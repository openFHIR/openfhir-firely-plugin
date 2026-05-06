using System.Text;
using System.Text.Json;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenFhirFirelyPlugin.Configuration;
using OpenFhirFirelyPlugin.OpenEhr;
using OpenFhirFirelyPlugin.OpenFhir;
using OpenFhirFirelyPlugin.Pix;
using Vonk.Core.Context;
using Vonk.Core.Context.Http;
using Task = System.Threading.Tasks.Task;

namespace OpenFhirFirelyPlugin.Middleware;

public class FhirQueryMiddleware
{
    private const string ResourceTypeKey = "_resourceType";
    private const string Wildcard = "*";
    private const string XReqIdHeader = "x-req-id";

    private readonly RequestDelegate _next;
    private readonly ILogger<FhirQueryMiddleware> _logger;

    private static readonly FhirJsonSerializer FhirSerializer = new();

    public FhirQueryMiddleware(RequestDelegate next, ILogger<FhirQueryMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext httpContext,
        IOptions<InterceptorOptions> interceptorOptions,
        OpenEhrCdrRegistry cdrRegistry,
        OpenFhirClient openFhirClient,
        PixManager pixManager)
    {
        if (!httpContext.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            await _next(httpContext);
            return;
        }

        var patientParam = httpContext.Request.Query["patient"].ToString();
        if (string.IsNullOrWhiteSpace(patientParam))
        {
            _logger.LogDebug("GET {Path} — no patient query param, passing through", httpContext.Request.Path);
            await _next(httpContext);
            return;
        }

        var resourceType = ResourceTypeFromPath(httpContext.Request.Path);
        var matchedRule = ResolveRule(httpContext.Request, resourceType, interceptorOptions.Value.FhirQueryFilter.Rules);

        if (matchedRule == null)
        {
            _logger.LogInformation("GET {Path} — no rule matched, passing through", httpContext.Request.Path);
            await _next(httpContext);
            return;
        }

        var patientId = patientParam.Contains('/')
            ? patientParam.Substring(patientParam.LastIndexOf('/') + 1)
            : patientParam;

        _logger.LogInformation("GET {Path} — rule matched (templateId='{TemplateId}'), intercepting for patient={PatientId}",
            httpContext.Request.Path, matchedRule.TemplateId, patientId);

        try
        {
            var vonkContext = httpContext.Vonk();
            var (request, args, response) = vonkContext.Parts();
            var incomingReqId = httpContext.Request.Headers[XReqIdHeader].ToString();
            var reqId = !string.IsNullOrWhiteSpace(incomingReqId) ? incomingReqId : Guid.NewGuid().ToString();

            var cdrHeader = httpContext.Request.Headers[Constants.TargetCdrHeader].ToString();
            var resolvedCdrName = cdrRegistry.ResolveName(cdrHeader);

            var ehrId = await pixManager.ResolveById(patientId, Constants.EhrIdSystem, resolvedCdrName, vonkContext)
                ?? throw new InvalidOperationException(
                    $"No EHR ID found for patient {patientId} on CDR '{resolvedCdrName}'");

            var fhirPath = BuildFhirPath(httpContext.Request, resourceType);

            var toAqlResponse = await openFhirClient.GetAql(
                new ToAqlRequest(matchedRule.TemplateId, ehrId, fhirPath), reqId);

            if (toAqlResponse.Aqls.Count == 0)
            {
                await WriteBundle(httpContext, EmptySearchBundle());
                return;
            }

            var cdrClient = cdrRegistry.Resolve(cdrHeader);
            var allRows = new List<JsonElement>();

            foreach (var aqlEntry in toAqlResponse.Aqls)
            {
                if (aqlEntry.Type == AqlType.COMPOSITION) continue;

                var openEhrResult = await cdrClient.QueryAql(aqlEntry.Aql);
                allRows.AddRange(ExtractArchetypeRows(openEhrResult));
            }

            if (allRows.Count == 0)
            {
                await WriteBundle(httpContext, EmptySearchBundle());
                return;
            }

            var fhirJson = await openFhirClient.ToFhir(allRows, reqId, matchedRule.TemplateId);
            var resultBundle = FhirJsonDeserializer.SYNTAXONLY.DeserializeResource(fhirJson) as Bundle
                               ?? throw new InvalidOperationException("toFhir did not return a Bundle");

            var searchBundle = EmptySearchBundle();
            foreach (var entry in resultBundle.Entry)
            {
                var r = entry.Resource;
                if (r == null || r.TypeName != resourceType) continue;
                if (string.IsNullOrEmpty(r.Id)) r.Id = Guid.NewGuid().ToString();
                searchBundle.AddResourceEntry(r, $"{r.TypeName}/{r.Id}");
            }
            searchBundle.Total = searchBundle.Entry.Count;
            
            // Mark the request as handled to prevent normal storage
            args.Handled();

            await WriteBundle(httpContext, searchBundle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Query interception failed for {Path} patient={PatientId}",
                httpContext.Request.Path, patientId);
            httpContext.Response.StatusCode = 500;
            httpContext.Response.ContentType = "application/fhir+json;charset=UTF-8";
            await httpContext.Response.WriteAsync(BuildOperationOutcomeJson(ex.Message));
        }
        // Do NOT call _next — request is fully handled
    }

    private QueryRule? ResolveRule(HttpRequest request, string? resourceType, List<QueryRule> rules)
    {
        foreach (var rule in rules)
        {
            if (RuleMatches(rule, request, resourceType))
                return rule;
        }
        return null;
    }

    internal bool RuleMatches(QueryRule rule, HttpRequest request, string? resourceType)
    {
        foreach (var criterion in rule.FhirQuery)
        {
            var expected = criterion.Value;
            if (criterion.Key == ResourceTypeKey)
            {
                if (resourceType == null) return false;
                if (expected != Wildcard && expected != resourceType) return false;
            }
            else
            {
                var paramValue = request.Query[criterion.Key].ToString();
                if (string.IsNullOrEmpty(paramValue)) return false;
                if (expected != Wildcard && expected != paramValue) return false;
            }
        }
        return true;
    }

    private static string? ResourceTypeFromPath(PathString path)
    {
        var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments == null || segments.Length == 0) return null;
        var last = segments[^1];
        return !string.IsNullOrEmpty(last) && char.IsUpper(last[0]) ? last : null;
    }

    private static string BuildFhirPath(HttpRequest request, string? resourceType)
    {
        var sb = new StringBuilder("/").Append(resourceType);
        var queryString = request.QueryString.Value;
        if (!string.IsNullOrWhiteSpace(queryString))
        {
            var filtered = queryString.TrimStart('?')
                .Split('&')
                .Where(p => !p.StartsWith("patient=", StringComparison.Ordinal))
                .ToArray();

            if (filtered.Length > 0)
                sb.Append('?').Append(string.Join('&', filtered));
        }
        return sb.ToString();
    }

    private static IReadOnlyList<JsonElement> ExtractArchetypeRows(string aqlResultJson)
    {
        using var doc = JsonDocument.Parse(aqlResultJson);
        var rows = doc.RootElement.GetProperty("rows");
        var result = new List<JsonElement>();

        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() > 0)
            {
                // Clone to survive doc disposal
                result.Add(row[0].Clone());
            }
        }
        return result;
    }

    private static Bundle EmptySearchBundle() => new()
    {
        Type = Bundle.BundleType.Searchset,
        Total = 0
    };

    private static async Task WriteBundle(HttpContext httpContext, Bundle bundle)
    {
        httpContext.Response.StatusCode = 200;
        httpContext.Response.ContentType = "application/fhir+json;charset=UTF-8";
        await httpContext.Response.WriteAsync(FhirSerializer.SerializeToString(bundle));
    }

    private static string BuildOperationOutcomeJson(string message)
    {
        var escaped = message.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        return $"{{\"resourceType\":\"OperationOutcome\",\"issue\":[{{\"severity\":\"error\",\"code\":\"exception\",\"diagnostics\":\"{escaped}\"}}]}}";
    }
}

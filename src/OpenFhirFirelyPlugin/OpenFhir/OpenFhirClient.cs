using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenFhirFirelyPlugin.Auth;
using OpenFhirFirelyPlugin.Configuration;
using OpenFhirFirelyPlugin.OpenEhr;

namespace OpenFhirFirelyPlugin.OpenFhir;

public class OpenFhirClient
{
    // openFHIR >= 3.0.0 FHIR-operations endpoints (server root, no /openfhir prefix)
    private const string ToOpenEhrPath = "/$toopenehr?format=canonical";
    private const string ToFhirPath = "/$tofhir";
    // toaql has no 3.0.0 replacement and stays on the legacy path
    private const string ToAqlPath = "/openfhir/toaql";
    private const string FhirJsonMediaType = "application/fhir+json";
    // Identifier system the engine puts on the Provenance entry it appends to every $tofhir Bundle
    private const string EngineProvenanceIdentifierSystem = "urn:openfhir:templateId";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OpenFhirOptions _options;
    private readonly ClientCredentialsTokenProvider? _tokenProvider;
    private readonly ILogger<OpenFhirClient> _logger;

    public OpenFhirClient(
        IHttpClientFactory httpClientFactory,
        IOptions<OpenFhirOptions> options,
        ILogger<OpenFhirClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;

        if (_options.OAuth2.IsConfigured())
            _tokenProvider = new ClientCredentialsTokenProvider(_options.OAuth2, httpClientFactory);
    }

    /// <summary>Converts a FHIR JSON resource to an openEHR composition via POST /$toopenehr.</summary>
    public async Task<string> Convert(string fhirJson, string reqId)
    {
        using var request = NewRequest(HttpMethod.Post, ToOpenEhrPath, reqId);
        request.Content = new StringContent(fhirJson, Encoding.UTF8, FhirJsonMediaType);

        using var httpClient = _httpClientFactory.CreateClient("OpenFhir");
        var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"OpenFHIR $toopenehr failed with status {(int)response.StatusCode}: {ExtractErrorDetail(body)}");

        _logger.LogInformation("OpenFHIR $toopenehr successful, status={Status}", (int)response.StatusCode);

        string? composition = null;
        using (var doc = JsonDocument.Parse(body))
        {
            if (doc.RootElement.TryGetProperty("parameter", out var parameters)
                && parameters.ValueKind == JsonValueKind.Array)
            {
                foreach (var parameter in parameters.EnumerateArray())
                {
                    if (!parameter.TryGetProperty("name", out var name)) continue;

                    if (name.ValueEquals("composition")
                        && parameter.TryGetProperty("valueString", out var valueString))
                    {
                        composition = valueString.GetString();
                    }
                    else if (name.ValueEquals("outcome")
                             && parameter.TryGetProperty("resource", out var outcome))
                    {
                        LogOperationOutcomeWarnings(outcome, "$toopenehr", reqId);
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(composition))
            throw new InvalidOperationException(
                "OpenFHIR $toopenehr returned no composition parameter in its Parameters response");

        return composition;
    }

    /// <summary>Gets AQL queries for the given FHIR path via /openfhir/toaql.</summary>
    public async Task<ToAqlResponse> GetAql(ToAqlRequest toAqlRequest, string reqId)
    {
        var payload = JsonSerializer.Serialize(toAqlRequest, JsonOptions);
        using var request = NewRequest(HttpMethod.Post, ToAqlPath, reqId);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var httpClient = _httpClientFactory.CreateClient("OpenFhir");
        var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"OpenFHIR toaql failed with status {(int)response.StatusCode}: {body}");

        _logger.LogInformation("OpenFHIR toaql successful, status={Status}, reqId={ReqId}",
            (int)response.StatusCode, reqId);

        return JsonSerializer.Deserialize<ToAqlResponse>(body, JsonOptions)
               ?? new ToAqlResponse();
    }

    /// <summary>Converts openEHR archetype rows to FHIR resources via POST /$tofhir.</summary>
    public async Task<string> ToFhir(IReadOnlyList<JsonElement> rows, string reqId, string? templateId,
        string? ehrId = null, string? patientReference = null)
    {
        var payload = BuildToFhirParameters(rows, templateId, ehrId, patientReference);

        using var request = NewRequest(HttpMethod.Post, ToFhirPath, reqId);
        request.Content = new StringContent(payload, Encoding.UTF8, FhirJsonMediaType);

        using var httpClient = _httpClientFactory.CreateClient("OpenFhir");
        var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"OpenFHIR $tofhir failed with status {(int)response.StatusCode}: {ExtractErrorDetail(body)}");

        _logger.LogInformation("OpenFHIR $tofhir successful, status={Status}, reqId={ReqId}",
            (int)response.StatusCode, reqId);

        return StripEngineEntries(body, reqId);
    }

    /// <summary>
    /// Builds the $tofhir Parameters request: composition (stringified archetype-rows array),
    /// optional templateId and context (ehr_id + patient reference).
    /// </summary>
    private static string BuildToFhirParameters(IReadOnlyList<JsonElement> rows, string? templateId,
        string? ehrId, string? patientReference)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("resourceType", "Parameters");
            writer.WriteStartArray("parameter");

            writer.WriteStartObject();
            writer.WriteString("name", "composition");
            writer.WriteString("valueString", JsonSerializer.Serialize(rows));
            writer.WriteEndObject();

            if (!string.IsNullOrWhiteSpace(templateId))
            {
                writer.WriteStartObject();
                writer.WriteString("name", "templateId");
                writer.WriteString("valueString", templateId);
                writer.WriteEndObject();
            }

            if (!string.IsNullOrWhiteSpace(ehrId) || !string.IsNullOrWhiteSpace(patientReference))
            {
                writer.WriteStartObject();
                writer.WriteString("name", "context");
                writer.WriteStartArray("part");

                if (!string.IsNullOrWhiteSpace(ehrId))
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", "ehr_id");
                    writer.WriteString("valueString", ehrId);
                    writer.WriteEndObject();
                }

                if (!string.IsNullOrWhiteSpace(patientReference))
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", "patient");
                    writer.WriteStartObject("valueReference");
                    writer.WriteString("reference", patientReference);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Removes the entries the 3.0.0 engine appends to every $tofhir Bundle — OperationOutcome entries
    /// (partial-failure reports, logged as warnings) and the engine's own marked Provenance entry —
    /// so callers see the same clinical-resources-only Bundle the legacy endpoint returned.
    /// Provenance resources produced by a mapping itself are kept.
    /// </summary>
    private string StripEngineEntries(string bundleJson, string reqId)
    {
        using var doc = JsonDocument.Parse(bundleJson);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("entry", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
            return bundleJson;

        var kept = new List<JsonElement>();
        var removedCount = 0;
        var removedMarkedProvenance = false;

        foreach (var entry in entries.EnumerateArray())
        {
            var resourceType = GetResourceType(entry);

            if (resourceType == "OperationOutcome")
            {
                removedCount++;
                if (entry.TryGetProperty("resource", out var outcome))
                    LogOperationOutcomeWarnings(outcome, "$tofhir", reqId);
                continue;
            }

            if (resourceType == "Provenance" && HasEngineProvenanceMarker(entry))
            {
                removedCount++;
                removedMarkedProvenance = true;
                continue;
            }

            kept.Add(entry);
        }

        // Fallback for an unmarked engine Provenance: the engine always appends one last,
        // so if none carried the marker and the trailing entry is a Provenance, drop it.
        if (!removedMarkedProvenance && kept.Count > 0
            && GetResourceType(kept[^1]) == "Provenance")
        {
            _logger.LogWarning(
                "OpenFHIR $tofhir: stripping unmarked trailing Provenance entry (engine marker not found), reqId={ReqId}",
                reqId);
            kept.RemoveAt(kept.Count - 1);
            removedCount++;
        }

        if (removedCount == 0)
            return bundleJson;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("entry"))
                {
                    writer.WriteStartArray("entry");
                    foreach (var entry in kept)
                        entry.WriteTo(writer);
                    writer.WriteEndArray();
                }
                else if (property.NameEquals("total")
                         && property.Value.ValueKind == JsonValueKind.Number
                         && property.Value.TryGetInt32(out var total))
                {
                    writer.WriteNumber("total", Math.Max(0, total - removedCount));
                }
                else
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? GetResourceType(JsonElement entry)
    {
        if (entry.TryGetProperty("resource", out var resource)
            && resource.ValueKind == JsonValueKind.Object
            && resource.TryGetProperty("resourceType", out var resourceType)
            && resourceType.ValueKind == JsonValueKind.String)
            return resourceType.GetString();
        return null;
    }

    /// <summary>
    /// True when the entry's Provenance has an entity with role "source" whose
    /// what.identifier.system is the engine's templateId marker.
    /// </summary>
    private static bool HasEngineProvenanceMarker(JsonElement entry)
    {
        if (!entry.TryGetProperty("resource", out var resource)
            || !resource.TryGetProperty("entity", out var entities)
            || entities.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var entity in entities.EnumerateArray())
        {
            if (entity.TryGetProperty("role", out var role) && role.ValueEquals("source")
                && entity.TryGetProperty("what", out var what)
                && what.TryGetProperty("identifier", out var identifier)
                && identifier.TryGetProperty("system", out var system)
                && system.ValueEquals(EngineProvenanceIdentifierSystem))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Extracts a readable message from an error body: OperationOutcome issue diagnostics
    /// when the body parses as one, the raw body otherwise (proxy HTML, plain text, empty).
    /// </summary>
    private static string ExtractErrorDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return body;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("resourceType", out var resourceType)
                || !resourceType.ValueEquals("OperationOutcome")
                || !root.TryGetProperty("issue", out var issues)
                || issues.ValueKind != JsonValueKind.Array)
                return body;

            var messages = new List<string>();
            foreach (var issue in issues.EnumerateArray())
            {
                if (issue.TryGetProperty("diagnostics", out var diagnostics)
                    && diagnostics.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(diagnostics.GetString()))
                {
                    messages.Add(diagnostics.GetString()!);
                }
                else if (issue.TryGetProperty("details", out var details)
                         && details.TryGetProperty("text", out var text)
                         && text.ValueKind == JsonValueKind.String
                         && !string.IsNullOrWhiteSpace(text.GetString()))
                {
                    messages.Add(text.GetString()!);
                }
            }

            return messages.Count > 0 ? string.Join("; ", messages) : body;
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private void LogOperationOutcomeWarnings(JsonElement outcome, string operation, string reqId)
    {
        if (!outcome.TryGetProperty("issue", out var issues) || issues.ValueKind != JsonValueKind.Array)
            return;

        foreach (var issue in issues.EnumerateArray())
        {
            var severity = issue.TryGetProperty("severity", out var s) ? s.GetString() : null;
            var code = issue.TryGetProperty("code", out var c) ? c.GetString() : null;
            var diagnostics = issue.TryGetProperty("diagnostics", out var d) ? d.GetString() : null;

            _logger.LogWarning(
                "OpenFHIR {Operation} reported issue: severity={Severity}, code={Code}, diagnostics={Diagnostics}, reqId={ReqId}",
                operation, severity, code, diagnostics, reqId);
        }
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path, string reqId)
    {
        var effectiveReqId = !string.IsNullOrWhiteSpace(reqId) ? reqId : Guid.NewGuid().ToString();
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("x-req-id", effectiveReqId);

        if (_tokenProvider != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokenProvider.GetToken());

        return request;
    }
}

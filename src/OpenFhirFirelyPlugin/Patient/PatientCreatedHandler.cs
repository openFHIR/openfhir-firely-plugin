using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenFhirFirelyPlugin.OpenEhr;
using OpenFhirFirelyPlugin.Pix;
using Vonk.Core.Common;
using Vonk.Core.Context;
using Vonk.Core.Context.Http;
using Vonk.Core.ElementModel;
using Vonk.Core.Repository;
using Vonk.Fhir;
using Task = System.Threading.Tasks.Task;

namespace OpenFhirFirelyPlugin.Patient;

public class PatientCreatedHandler
{
    private readonly RequestDelegate _next;
    private readonly ILogger<PatientCreatedHandler> _logger;

    public PatientCreatedHandler(RequestDelegate next, ILogger<PatientCreatedHandler> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext httpContext,
        OpenEhrCdrRegistry cdrRegistry,
        PixManager pixManager,
        IResourceChangeRepository changeRepository)
    {
        // Only intercept POST to /Patient
        if (!httpContext.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
            !IsPatientPath(httpContext.Request.Path))
        {
            await _next(httpContext);
            return;
        }

        // Buffer the response so we can patch it after _next
        var originalBody = httpContext.Response.Body;
        using var buffer = new MemoryStream();
        httpContext.Response.Body = buffer;

        try
        {
            await _next(httpContext);
        }
        finally
        {
            httpContext.Response.Body = originalBody;
        }

        buffer.Position = 0;

        if (httpContext.Response.StatusCode != 201)
        {
            await buffer.CopyToAsync(originalBody);
            return;
        }

        // Read the response body Vonk wrote
        var responseJson = await new StreamReader(buffer, Encoding.UTF8).ReadToEndAsync();

        JsonNode? doc;
        try { doc = JsonNode.Parse(responseJson); } catch { doc = null; }

        if (doc?["resourceType"]?.GetValue<string>() != "Patient")
        {
            await originalBody.WriteAsync(Encoding.UTF8.GetBytes(responseJson));
            return;
        }

        var patientId = doc["id"]?.GetValue<string>() ?? string.Empty;
        _logger.LogInformation("Patient {PatientId} created, provisioning EHR record", patientId);

        var vonkContext = httpContext.Vonk();
        var cdrHeader = httpContext.Request.Headers[Constants.TargetCdrHeader].ToString();
        var cdrNames = !string.IsNullOrWhiteSpace(cdrHeader)
            ? cdrHeader.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : new[] { (string?)null };

        IResource? lastUpdated = null;
        foreach (var cdrName in cdrNames)
        {
            var resolvedCdrName = cdrRegistry.ResolveName(cdrName);
            var cdrClient = cdrRegistry.Resolve(cdrName);
            try
            {
                lastUpdated = await pixManager.ProvisionEhrForPatient(patientId, cdrClient, resolvedCdrName, vonkContext);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "EHR provisioning failed for patient {PatientId} on CDR '{CdrName}' — attempting compensating delete",
                    patientId, resolvedCdrName);

                try
                {
                    var key = new ResourceKey("Patient", patientId);
                    await changeRepository.Delete(key, vonkContext.InformationModel);
                }
                catch (Exception deleteEx)
                {
                    _logger.LogError(deleteEx, "Compensating delete of patient {PatientId} also failed", patientId);
                }

                httpContext.Response.StatusCode = 500;
                httpContext.Response.ContentType = "application/fhir+json;charset=UTF-8";
                var errJson = BuildOperationOutcomeJson($"Failed to provision EHR in CDR '{resolvedCdrName}': {ex.Message}");
                await originalBody.WriteAsync(Encoding.UTF8.GetBytes(errJson));
                return;
            }
        }

        // Patch the identifier(s) from the stored resource into the JSON response
        if (lastUpdated != null)
        {
            var identifiers = ((ISourceNode)lastUpdated).Children("identifier").ToList();
            var identifierArray = doc["identifier"] as JsonArray ?? new JsonArray();

            // Add any identifiers from stored resource not already in the response JSON
            var existingSystems = identifierArray
                .Select(i => i?["system"]?.GetValue<string>())
                .ToHashSet();

            foreach (var idNode in identifiers)
            {
                var system = idNode.Children("system").FirstOrDefault()?.Text;
                if (system != null && existingSystems.Contains(system)) continue;

                var idObj = new JsonObject();
                foreach (var child in idNode.Children())
                    if (!string.IsNullOrEmpty(child.Text))
                        idObj[child.Name] = child.Text;

                // Handle nested assigner
                var assigner = idNode.Children("assigner").FirstOrDefault();
                if (assigner != null)
                {
                    var assignerObj = new JsonObject();
                    foreach (var ac in assigner.Children())
                        if (!string.IsNullOrEmpty(ac.Text))
                            assignerObj[ac.Name] = ac.Text;
                    idObj["assigner"] = assignerObj;
                }

                identifierArray.Add(idObj);
                existingSystems.Add(system);
            }

            doc["identifier"] = identifierArray;
        }

        var patchedBytes = Encoding.UTF8.GetBytes(doc.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        httpContext.Response.ContentLength = patchedBytes.Length;
        await originalBody.WriteAsync(patchedBytes);
    }

    private static bool IsPatientPath(PathString path)
    {
        var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments?.Length == 1 && segments[0] == "Patient";
    }

    private static string BuildOperationOutcomeJson(string message)
    {
        var escaped = message.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        return $"{{\"resourceType\":\"OperationOutcome\",\"issue\":[{{\"severity\":\"error\",\"code\":\"exception\",\"diagnostics\":\"{escaped}\"}}]}}";
    }
}
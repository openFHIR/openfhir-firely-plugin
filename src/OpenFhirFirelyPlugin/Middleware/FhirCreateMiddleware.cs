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

public class FhirCreateMiddleware
{
    private const string XReqIdHeader = "x-req-id";

    private readonly RequestDelegate _next;
    private readonly ILogger<FhirCreateMiddleware> _logger;

    // Per-resource-type FHIRPath expressions that may hold a patient reference
    private static readonly Dictionary<string, List<string>> PatientPaths = new()
    {
        ["Account"]                     = new() { "Account.subject" },
        ["AdverseEvent"]                = new() { "AdverseEvent.subject" },
        ["AllergyIntolerance"]          = new() { "AllergyIntolerance.patient", "AllergyIntolerance.recorder", "AllergyIntolerance.asserter" },
        ["Appointment"]                 = new() { "Appointment.participant.actor" },
        ["AppointmentResponse"]         = new() { "AppointmentResponse.actor" },
        ["AuditEvent"]                  = new() { "AuditEvent.patient", "AuditEvent.agent.who", "AuditEvent.entity.what" },
        ["Basic"]                       = new() { "Basic.subject", "Basic.author" },
        ["BodyStructure"]               = new() { "BodyStructure.patient" },
        ["CarePlan"]                    = new() { "CarePlan.subject", "CarePlan.activity.detail.performer" },
        ["CareTeam"]                    = new() { "CareTeam.subject", "CareTeam.participant.member" },
        ["ChargeItem"]                  = new() { "ChargeItem.subject" },
        ["Claim"]                       = new() { "Claim.patient", "Claim.payee.party" },
        ["ClaimResponse"]               = new() { "ClaimResponse.patient" },
        ["ClinicalImpression"]          = new() { "ClinicalImpression.subject" },
        ["Communication"]               = new() { "Communication.subject", "Communication.sender", "Communication.recipient" },
        ["CommunicationRequest"]        = new() { "CommunicationRequest.subject", "CommunicationRequest.sender", "CommunicationRequest.recipient", "CommunicationRequest.requester" },
        ["Composition"]                 = new() { "Composition.subject", "Composition.author", "Composition.attester.party" },
        ["Condition"]                   = new() { "Condition.subject", "Condition.asserter" },
        ["Consent"]                     = new() { "Consent.patient" },
        ["Coverage"]                    = new() { "Coverage.policyHolder", "Coverage.subscriber", "Coverage.beneficiary", "Coverage.payor" },
        ["CoverageEligibilityRequest"]  = new() { "CoverageEligibilityRequest.patient" },
        ["CoverageEligibilityResponse"] = new() { "CoverageEligibilityResponse.patient" },
        ["DetectedIssue"]               = new() { "DetectedIssue.patient" },
        ["DeviceRequest"]               = new() { "DeviceRequest.subject", "DeviceRequest.performer" },
        ["DeviceUseStatement"]          = new() { "DeviceUseStatement.subject" },
        ["DiagnosticReport"]            = new() { "DiagnosticReport.subject" },
        ["DocumentManifest"]            = new() { "DocumentManifest.subject", "DocumentManifest.author", "DocumentManifest.recipient" },
        ["DocumentReference"]           = new() { "DocumentReference.subject", "DocumentReference.author" },
        ["Encounter"]                   = new() { "Encounter.subject" },
        ["EnrollmentRequest"]           = new() { "EnrollmentRequest.candidate" },
        ["EpisodeOfCare"]               = new() { "EpisodeOfCare.patient" },
        ["ExplanationOfBenefit"]        = new() { "ExplanationOfBenefit.patient", "ExplanationOfBenefit.payee.party" },
        ["FamilyMemberHistory"]         = new() { "FamilyMemberHistory.patient" },
        ["Flag"]                        = new() { "Flag.subject" },
        ["Goal"]                        = new() { "Goal.subject" },
        ["Group"]                       = new() { "Group.member.entity" },
        ["ImagingStudy"]                = new() { "ImagingStudy.subject" },
        ["Immunization"]                = new() { "Immunization.patient" },
        ["ImmunizationEvaluation"]      = new() { "ImmunizationEvaluation.patient" },
        ["ImmunizationRecommendation"]  = new() { "ImmunizationRecommendation.patient" },
        ["Invoice"]                     = new() { "Invoice.subject", "Invoice.patient", "Invoice.recipient" },
        ["List"]                        = new() { "List.subject", "List.source" },
        ["MeasureReport"]               = new() { "MeasureReport.subject" },
        ["Media"]                       = new() { "Media.subject" },
        ["MedicationAdministration"]    = new() { "MedicationAdministration.patient", "MedicationAdministration.subject", "MedicationAdministration.performer.actor" },
        ["MedicationDispense"]          = new() { "MedicationDispense.subject", "MedicationDispense.patient", "MedicationDispense.receiver" },
        ["MedicationRequest"]           = new() { "MedicationRequest.subject" },
        ["MedicationStatement"]         = new() { "MedicationStatement.subject" },
        ["MolecularSequence"]           = new() { "MolecularSequence.patient" },
        ["NutritionOrder"]              = new() { "NutritionOrder.patient" },
        ["Observation"]                 = new() { "Observation.subject", "Observation.performer" },
        ["Patient"]                     = new() { "Patient.link.other" },
        ["Person"]                      = new() { "Person.link.target" },
        ["Procedure"]                   = new() { "Procedure.subject", "Procedure.performer.actor" },
        ["Provenance"]                  = new() { "Provenance.patient" },
        ["QuestionnaireResponse"]       = new() { "QuestionnaireResponse.subject", "QuestionnaireResponse.author" },
        ["RelatedPerson"]               = new() { "RelatedPerson.patient" },
        ["RequestGroup"]                = new() { "RequestGroup.subject", "RequestGroup.participant" },
        ["ResearchSubject"]             = new() { "ResearchSubject.individual" },
        ["RiskAssessment"]              = new() { "RiskAssessment.subject" },
        ["Schedule"]                    = new() { "Schedule.actor" },
        ["ServiceRequest"]              = new() { "ServiceRequest.subject", "ServiceRequest.performer" },
        ["Specimen"]                    = new() { "Specimen.subject" },
        ["SupplyDelivery"]              = new() { "SupplyDelivery.patient" },
        ["SupplyRequest"]               = new() { "SupplyRequest.subject" },
        ["VisionPrescription"]          = new() { "VisionPrescription.patient" },
    };

    private static readonly FhirJsonSerializer FhirSerializer = new();

    public FhirCreateMiddleware(RequestDelegate next, ILogger<FhirCreateMiddleware> logger)
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
        if (!httpContext.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            await _next(httpContext);
            return;
        }

        _logger.LogDebug("POST {Path} — evaluating for OpenEHR interception", httpContext.Request.Path);

        var cdrHeader = httpContext.Request.Headers[Constants.TargetCdrHeader].ToString();
        if (string.Equals(cdrHeader, "fhir", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("POST {Path} — X-OpenEhrCdr=fhir, passing through to Firely", httpContext.Request.Path);
            await _next(httpContext);
            return;
        }

        httpContext.Request.EnableBuffering();
        var body = await ReadBodyAsync(httpContext.Request);
        httpContext.Request.Body.Position = 0;

        Resource resource;
        try
        {
            resource = FhirJsonDeserializer.SYNTAXONLY.DeserializeResource(body);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "POST {Path} — body not parseable as FHIR, passing through", httpContext.Request.Path);
            await _next(httpContext);
            return;
        }

        var matchedProfile = ResolveMatchedProfile(resource, interceptorOptions.Value.FhirCreateFilter.InterceptedProfiles);
        if (matchedProfile == null)
        {
            _logger.LogInformation("POST {Path} — no intercepted profile matched, passing through",
                httpContext.Request.Path);
            await _next(httpContext);
            return;
        }

        _logger.LogInformation("POST {Path} — profile '{Profile}' matched, forwarding to OpenEHR CDR",
            httpContext.Request.Path, matchedProfile);

        var incomingReqId = httpContext.Request.Headers[XReqIdHeader].ToString();
        var reqId = !string.IsNullOrWhiteSpace(incomingReqId) ? incomingReqId : Guid.NewGuid().ToString();

        try
        {
            var vonkContext = httpContext.Vonk();
            var (request, args, response) = vonkContext.Parts();
            var cdrEntry = cdrRegistry.ResolveEntry(cdrHeader);
            var cdrClient = cdrRegistry.Resolve(cdrHeader);
            var resolvedCdrName = cdrEntry.Id;

            var patientId = ExtractPatientReferenceIdPart(resource);
            if (patientId == null)
                throw new InvalidOperationException(
                    $"Could not extract patient reference from resource {resource.TypeName}/{resource.Id}");

            var ehrId = await pixManager.ResolveById(patientId, Constants.EhrIdSystem, resolvedCdrName, vonkContext)
                        ?? (await pixManager.ProvisionEhrForPatient(patientId, cdrClient, resolvedCdrName, vonkContext)).Value!;

            var bundleJson = FhirSerializer.SerializeToString(ToBundle(resource));
            var openEhrPayload = await openFhirClient.Convert(bundleJson, reqId);
            var location = await cdrClient.Store(openEhrPayload, ehrId);

            httpContext.Response.StatusCode = 201;
            if (location != null)
                httpContext.Response.Headers["Location"] = location;
            // Mark the request as handled to prevent normal storage
            args.Handled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to forward resource to OpenEHR CDR");
            httpContext.Response.StatusCode = 500;
            httpContext.Response.ContentType = "application/fhir+json;charset=UTF-8";
            await httpContext.Response.WriteAsync(BuildOperationOutcomeJson(ex.Message));
        }
        // Do NOT call _next — request is fully handled
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private string? ResolveMatchedProfile(Resource resource, List<string> interceptedProfiles)
    {
        var direct = MatchedProfile(resource, interceptedProfiles);
        if (direct != null) return direct;

        if (resource is Bundle bundle)
        {
            return bundle.Entry
                .Select(e => e.Resource)
                .Where(r => r != null)
                .Select(r => MatchedProfile(r!, interceptedProfiles))
                .FirstOrDefault(p => p != null);
        }

        return null;
    }

    private static string? MatchedProfile(Resource resource, List<string> interceptedProfiles)
    {
        return resource.Meta?.Profile
            .FirstOrDefault(p => p != null && interceptedProfiles.Contains(p));
    }

    private string? ExtractPatientReferenceIdPart(Resource resource)
    {
        if (resource is Bundle bundle)
        {
            return bundle.Entry
                .Select(e => e.Resource)
                .Where(r => r != null)
                .Select(r => ExtractPatientReferenceIdPart(r!))
                .FirstOrDefault(id => id != null);
        }

        var resourceType = resource.TypeName;
        if (!PatientPaths.TryGetValue(resourceType, out var paths))
        {
            _logger.LogWarning("No patient path defined for resource type '{ResourceType}'", resourceType);
            return null;
        }

        // Use JSON to resolve patient references without FhirTerser dependency
        var json = FhirSerializer.SerializeToString(resource);
        using var doc = JsonDocument.Parse(json);

        foreach (var path in paths)
        {
            var patientId = TryExtractPatientIdFromPath(doc.RootElement, path, resourceType);
            if (patientId != null) return patientId;
        }

        return null;
    }

    private static string? TryExtractPatientIdFromPath(JsonElement root, string fhirPath, string resourceType)
    {
        // Convert FHIR path like "Composition.subject" to JSON navigation
        var segments = fhirPath.Split('.');
        // Skip the resource type prefix
        return NavigateJson(root, segments, 1);
    }

    private static string? NavigateJson(JsonElement element, string[] segments, int index)
    {
        if (index >= segments.Length)
        {
            // We're at a reference — look for "reference" property
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty("reference", out var refProp))
            {
                var refValue = refProp.GetString();
                if (refValue != null)
                {
                    // Check if it's a Patient reference
                    if (refValue.StartsWith("Patient/", StringComparison.Ordinal))
                        return refValue.Substring("Patient/".Length);
                    // May be a plain ID (no resource type prefix)
                    if (!refValue.Contains('/'))
                        return refValue;
                }
            }
            return null;
        }

        var segment = segments[index];

        if (element.ValueKind == JsonValueKind.Object)
        {
            // Convert PascalCase FHIR path segment to camelCase JSON property
            var jsonProp = char.ToLowerInvariant(segment[0]) + segment.Substring(1);
            if (element.TryGetProperty(jsonProp, out var child))
                return NavigateJson(child, segments, index + 1);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var result = NavigateJson(item, segments, index);
                if (result != null) return result;
            }
        }

        return null;
    }

    private static Bundle ToBundle(Resource resource)
    {
        if (resource is Bundle b) return b;
        var bundle = new Bundle { Type = Bundle.BundleType.Collection };
        bundle.AddResourceEntry(resource, null);
        return bundle;
    }

    private static string BuildOperationOutcomeJson(string message)
    {
        var escaped = message.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        return $"{{\"resourceType\":\"OperationOutcome\",\"issue\":[{{\"severity\":\"error\",\"code\":\"exception\",\"diagnostics\":\"{escaped}\"}}]}}";
    }
}

using System.Text.Json;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenFhirFirelyPlugin.OpenEhr;
using OpenFhirFirelyPlugin.OpenFhir;
using OpenFhirFirelyPlugin.Pix;
using Vonk.Core.Context;
using FhirPatient = Hl7.Fhir.Model.Patient;
using Task = System.Threading.Tasks.Task;

namespace OpenFhirFirelyPlugin.Ips;

public class IpsSummaryService
{
    private const string TemplateId = "International Patient Summary";
    private const string XReqIdHeader = "x-req-id";

    private static readonly List<string> FhirPaths = new()
    {
        "/AllergyIntolerance",
        "/Condition?verification-status=confirmed"
    };

    private readonly PixManager _pixManager;
    private readonly OpenEhrCdrRegistry _cdrRegistry;
    private readonly OpenFhirClient _openFhirClient;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<IpsSummaryService> _logger;

    public IpsSummaryService(
        PixManager pixManager,
        OpenEhrCdrRegistry cdrRegistry,
        OpenFhirClient openFhirClient,
        IHttpContextAccessor httpContextAccessor,
        ILogger<IpsSummaryService> logger)
    {
        _pixManager = pixManager;
        _cdrRegistry = cdrRegistry;
        _openFhirClient = openFhirClient;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task ExecuteSummary(IVonkContext ctx)
    {
        // Extract patient ID from instance-level arguments
        var patientId = ctx.Arguments.ResourceIdArgument().ArgumentValue;

        if (string.IsNullOrWhiteSpace(patientId))
        {
            await WriteOperationOutcome(400, OperationOutcome.IssueSeverity.Error,
                OperationOutcome.IssueType.Required, "Could not determine patient ID from request");
            return;
        }
        var (request, args, response) = ctx.Parts();
        var httpRequest = _httpContextAccessor.HttpContext?.Request;
        var incomingReqId = httpRequest?.Headers[XReqIdHeader].ToString() ?? string.Empty;
        var reqId = !string.IsNullOrWhiteSpace(incomingReqId) ? incomingReqId : Guid.NewGuid().ToString();

        _logger.LogInformation("$summary called for patient {PatientId}, reqId={ReqId}", patientId, reqId);

        var cdrHeader = httpRequest?.Headers[Constants.TargetCdrHeader].ToString();
        var resolvedCdrName = _cdrRegistry.ResolveName(cdrHeader);

        var ehrId = await _pixManager.ResolveById(patientId, Constants.EhrIdSystem, resolvedCdrName, ctx);
        if (ehrId == null)
        {
            await WriteOperationOutcome(404, OperationOutcome.IssueSeverity.Error,
                OperationOutcome.IssueType.NotFound,
                $"No EHR ID found for patient {patientId} on CDR '{resolvedCdrName}'");
            return;
        }

        var cdrClient = _cdrRegistry.Resolve(cdrHeader);
        var allRows = new List<JsonElement>();

        foreach (var fhirPath in FhirPaths)
        {
            var toAqlRequest = new ToAqlRequest(TemplateId, ehrId, fhirPath);
            var toAqlResponse = await _openFhirClient.GetAql(toAqlRequest, reqId);

            if (toAqlResponse.Aqls.Count == 0)
            {
                _logger.LogDebug("No AQLs returned for fhirPath={FhirPath}, skipping", fhirPath);
                continue;
            }

            foreach (var aqlEntry in toAqlResponse.Aqls)
            {
                if (aqlEntry.Type == AqlType.COMPOSITION) continue;

                var openEhrResult = await cdrClient.QueryAql(aqlEntry.Aql);
                allRows.AddRange(ExtractArchetypeRows(openEhrResult));
            }
        }

        Bundle bundle;

        if (allRows.Count == 0)
        {
            _logger.LogInformation("No archetype rows found for patient {PatientId}, returning empty IPS bundle", patientId);
            var patient = await LoadPatient(patientId, ctx);
            bundle = BuildEmptyBundle(patient);
        }
        else
        {
            _logger.LogInformation("Sending {Count} archetype rows to toFhir for patient {PatientId}",
                allRows.Count, patientId);
            var fhirJson = await _openFhirClient.ToFhir(allRows, reqId, TemplateId);
            _logger.LogInformation("toFhir raw response for patient {PatientId}: {FhirJson}", patientId, fhirJson);
            var deserializedBundle = FhirJsonDeserializer.SYNTAXONLY.DeserializeResource(fhirJson) as Bundle;

            var patient = await LoadPatient(patientId, ctx);

            if (deserializedBundle == null)
            {
                _logger.LogWarning("toFhir did not return a Bundle for patient {PatientId}, returning empty IPS bundle", patientId);
                bundle = BuildEmptyBundle(patient);
            }
            else
            {
                bundle = deserializedBundle;
                InjectPatient(bundle, patient);
                if (bundle.Entry.Count > 0)
                    bundle.Entry[0].FullUrl = "urn:uuid:" + Guid.NewGuid();
            }
        }
        
        _logger.LogInformation("$summary bundle for patient {PatientId} (entries={Count}): {BundleJson}",
            patientId, bundle.Entry.Count, new FhirJsonSerializer().SerializeToString(bundle));

        // Mark the request as handled to prevent normal storage
        args.Handled();
        await SendResponse(ctx, bundle);
    }
    
    private async Task SendResponse(IVonkContext vonkContext, Bundle bundle)
    {
        vonkContext.Response.HttpResult = 200;
        var httpResponse = _httpContextAccessor.HttpContext!.Response;
        httpResponse.StatusCode = 200;
        httpResponse.ContentType = "application/fhir+json;charset=UTF-8";
        await httpResponse.WriteAsync(new FhirJsonSerializer().SerializeToString(bundle));
    }

    private Task<FhirPatient> LoadPatient(string patientId, IVonkContext ctx)
    {
        // Return a minimal Patient — $summary should still work
        return System.Threading.Tasks.Task.FromResult(new FhirPatient { Id = patientId });
    }

    private static IReadOnlyList<JsonElement> ExtractArchetypeRows(string aqlResultJson)
    {
        using var doc = JsonDocument.Parse(aqlResultJson);
        var rows = doc.RootElement.GetProperty("rows");
        var result = new List<JsonElement>();

        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() > 0)
                result.Add(row[0].Clone());
        }
        return result;
    }

    private static Bundle BuildEmptyBundle(FhirPatient patient)
    {
        var patientUuid = Guid.NewGuid().ToString();
        var patientFullUrl = "urn:uuid:" + patientUuid;
        patient.Id = patientUuid;

        var composition = new Composition
        {
            Id = Guid.NewGuid().ToString(),
            Status = CompositionStatus.Final,
            Type = new CodeableConcept("http://loinc.org", "60591-5", "Patient summary Document"),
            Subject = new ResourceReference(patientFullUrl),
            DateElement = new FhirDateTime(DateTimeOffset.UtcNow),
            Title = "Patient Summary",
            Meta = new Meta
            {
                ProfileUri = ["http://hl7.org/fhir/uv/ips/StructureDefinition/Composition-uv-ips"]
            }
        };
        composition.Author.Add(new ResourceReference { Display = "openFHIR" });
        composition.Section.Add(EmptySection("Active Problems", "http://loinc.org", "11450-4", "Problem list Reported"));
        composition.Section.Add(EmptySection("Active Allergies and Intolerances", "http://loinc.org", "48765-2", "Allergies and Intolerances"));
        composition.Section.Add(EmptySection("Active Medication List", "http://loinc.org", "10160-0", "Medication List"));

        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Document,
            Timestamp = DateTimeOffset.UtcNow,
            Identifier = new Identifier("urn:oid:2.16.840.1.113883.3.72", Guid.NewGuid().ToString()),
            Meta = new Meta
            {
                ProfileUri = ["http://hl7.org/fhir/uv/ips/StructureDefinition/Bundle-uv-ips"]
            }
        };

        bundle.AddResourceEntry(composition, "urn:uuid:" + composition.Id);
        bundle.AddResourceEntry(patient, patientFullUrl);
        return bundle;
    }

    private static Composition.SectionComponent EmptySection(string title, string system, string code, string display)
    {
        return new Composition.SectionComponent
        {
            Title = title,
            Code = new CodeableConcept(system, code, display),
            Text = new Narrative
            {
                Status = Narrative.NarrativeStatus.Generated,
                Div = $"<div xmlns=\"http://www.w3.org/1999/xhtml\">{title}</div>"
            },
            EmptyReason = new CodeableConcept("http://terminology.hl7.org/CodeSystem/list-empty-reason", "unavailable")
        };
    }

    private static void InjectPatient(Bundle bundle, FhirPatient patient)
    {
        var patientUuid = Guid.NewGuid().ToString();
        var patientFullUrl = "urn:uuid:" + patientUuid;
        patient.Id = patientUuid;

        var patientEntry = new Bundle.EntryComponent
        {
            Resource = patient,
            FullUrl = patientFullUrl
        };

        // Insert Patient after first entry (Composition)
        if (bundle.Entry.Count >= 1)
            bundle.Entry.Insert(1, patientEntry);
        else
            bundle.Entry.Add(patientEntry);

        // Update subject references
        foreach (var entry in bundle.Entry)
        {
            switch (entry.Resource)
            {
                case Composition comp:
                    comp.Subject = new ResourceReference(patientFullUrl);
                    break;
                case Condition cond:
                    cond.Subject = new ResourceReference(patientFullUrl);
                    break;
                case AllergyIntolerance ai:
                    ai.Patient = new ResourceReference(patientFullUrl);
                    break;
            }
        }
    }

    private async Task WriteOperationOutcome(int statusCode, OperationOutcome.IssueSeverity severity,
        OperationOutcome.IssueType code, string diagnostics)
    {
        var outcome = new OperationOutcome();
        outcome.Issue.Add(new OperationOutcome.IssueComponent { Severity = severity, Code = code, Diagnostics = diagnostics });
        var httpResponse = _httpContextAccessor.HttpContext!.Response;
        httpResponse.StatusCode = statusCode;
        httpResponse.ContentType = "application/fhir+json;charset=UTF-8";
        await httpResponse.WriteAsync(new FhirJsonSerializer().SerializeToString(outcome));
    }
}

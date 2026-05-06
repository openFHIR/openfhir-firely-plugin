using Hl7.Fhir.ElementModel;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenFhirFirelyPlugin.OpenEhr;
using OpenFhirFirelyPlugin.Pix;
using Vonk.Core.Context;
using Vonk.Core.Repository;
using Vonk.Fhir;
using FhirPatient = Hl7.Fhir.Model.Patient;
using Task = System.Threading.Tasks.Task;

namespace OpenFhirFirelyPlugin.Patient;

public class PatientCreatedHandler
{
    private readonly OpenEhrCdrRegistry _cdrRegistry;
    private readonly PixManager _pixManager;
    private readonly IResourceChangeRepository _changeRepository;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<PatientCreatedHandler> _logger;

    public PatientCreatedHandler(
        OpenEhrCdrRegistry cdrRegistry,
        PixManager pixManager,
        IResourceChangeRepository changeRepository,
        IHttpContextAccessor httpContextAccessor,
        ILogger<PatientCreatedHandler> logger)
    {
        _cdrRegistry = cdrRegistry;
        _pixManager = pixManager;
        _changeRepository = changeRepository;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task OnPatientCreated(IVonkContext ctx)
    {
        if (ctx.Response.HttpResult != 201)
            return;

        if (ctx.Response.Payload?.ToPoco() is not FhirPatient patient)
            return;

        var patientId = patient.Id ?? string.Empty;
        _logger.LogInformation("Patient {PatientId} created, provisioning EHR record", patientId);

        var cdrHeader = _httpContextAccessor.HttpContext?.Request.Headers[Constants.TargetCdrHeader].ToString();
        var cdrNames = !string.IsNullOrWhiteSpace(cdrHeader)
            ? cdrHeader.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : new[] { (string?)null };

        foreach (var cdrName in cdrNames)
        {
            var resolvedCdrName = _cdrRegistry.ResolveName(cdrName);
            var cdrClient = _cdrRegistry.Resolve(cdrName);
            try
            {
                await _pixManager.ProvisionEhrForPatient(patientId, cdrClient, resolvedCdrName, ctx);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "EHR provisioning failed for patient {PatientId} on CDR '{CdrName}' — attempting compensating delete",
                    patientId, resolvedCdrName);

                try
                {
                    await _changeRepository.Delete(patient.Key(), ctx.InformationModel);
                }
                catch (Exception deleteEx)
                {
                    _logger.LogError(deleteEx, "Compensating delete of patient {PatientId} also failed", patientId);
                }

                ctx.Response.HttpResult = 500;
                ctx.Response.Outcome.AddIssue(new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Error, Code = OperationOutcome.IssueType.Exception,
                    Diagnostics = $"Failed to provision EHR in CDR '{resolvedCdrName}': {ex.Message}"
                });
                return;
            }
        }
    }
}
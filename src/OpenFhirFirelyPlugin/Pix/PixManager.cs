using Hl7.Fhir.ElementModel;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging;
using Vonk.Core.Common;
using Vonk.Core.Context;
using Vonk.Core.ElementModel;
using Vonk.Core.Repository;
using OpenFhirFirelyPlugin.OpenEhr;
using Vonk.Fhir;
using FhirPatient = Hl7.Fhir.Model.Patient;

namespace OpenFhirFirelyPlugin.Pix;

public class PixManager
{
    private readonly ISearchRepository _searchRepository;
    private readonly IResourceChangeRepository _changeRepository;
    private readonly ILogger<PixManager> _logger;

    public PixManager(
        ISearchRepository searchRepository,
        IResourceChangeRepository changeRepository,
        ILogger<PixManager> logger)
    {
        _searchRepository = searchRepository;
        _changeRepository = changeRepository;
        _logger = logger;
    }

    /// <summary>
    /// Finds the EHR ID for a patient by FHIR logical ID, scoped to the given CDR name.
    /// Returns null if no matching identifier exists.
    /// </summary>
    public async Task<string?> ResolveById(string patientId, string targetSystem, string cdrName, IVonkContext ctx)
    {
        _logger.LogInformation("PIX lookup by id={PatientId}, targetSystem={TargetSystem}, cdrName={CdrName}",
            patientId, targetSystem, cdrName);

        var patient = await FindPatientById(patientId, ctx);
        if (patient == null)
        {
            _logger.LogWarning("PIX lookup: patient {PatientId} not found", patientId);
            return null;
        }

        return ExtractIdentifier(patient, targetSystem, cdrName);
    }

    /// <summary>
    /// Finds the EHR ID for a patient by identifier system+value, scoped to the given CDR name.
    /// Returns null if no matching patient or identifier exists.
    /// </summary>
    public async Task<string?> ResolveByIdentifier(string? system, string value, string targetSystem, string cdrName, IVonkContext ctx)
    {
        _logger.LogInformation("PIX lookup by identifier system={System}, value={Value}, targetSystem={TargetSystem}, cdrName={CdrName}",
            system, value, targetSystem, cdrName);

        var searchArgs = new ArgumentCollection();
        searchArgs.AddArgument(new Argument(ArgumentSource.Internal, ArgumentNames.resourceType, "Patient"));
        var identifierValue = system != null ? $"{system}|{value}" : value;
        searchArgs.AddArgument(new Argument(ArgumentSource.Internal, "identifier", identifierValue));

        var opts = SearchOptions.Latest(ctx.ServerBase, informationModel: ctx.InformationModel).WithFullAccess();
        var result = await _searchRepository.Search(searchArgs, opts);

        var iResource = result.FirstOrDefault();
        if (iResource == null)
        {
            _logger.LogInformation("PIX lookup found no patient for identifier {System}|{Value}", system, value);
            return null;
        }

        return iResource.ToPoco() is not FhirPatient patient ? null : ExtractIdentifier(patient, targetSystem, cdrName);
    }

    /// <summary>
    /// Creates a new EHR in the CDR, stores the EHR ID on the patient as an identifier,
    /// and returns the updated IResource (with the new identifier already present).
    /// Operates directly on the ISourceNode to preserve the original InformationModel
    /// without a version-specific POCO round-trip.
    /// </summary>
    public async Task<IResource> ProvisionEhrForPatient(string patientId, OpenEhrCdrClient cdrClient, string cdrName, IVonkContext ctx)
    {
        var ehrId = await cdrClient.CreateEhr();
        _logger.LogInformation("EHR created for patient {PatientId}, ehrId={EhrId}, cdr={CdrName}",
            patientId, ehrId, cdrName);

        var originalIResource = await FindIResourceById(patientId, ctx)
            ?? throw new InvalidOperationException($"Patient {patientId} not found for EHR provisioning");

        // Build the identifier node directly as ISourceNode so we don't need a version-specific
        // POCO round-trip. Vonk.Fhir.R3/R5 ToIResource() rejects R4 POCOs from ToPoco().
        var identifierNode = SourceNode.Node("identifier",
            SourceNode.Valued("system", Constants.EhrIdSystem),
            SourceNode.Valued("value", ehrId),
            SourceNode.Node("assigner",
                SourceNode.Valued("display", cdrName)));

        var updatedSourceNode = originalIResource
            .ForceAdd("Patient", identifierNode)
            // Remove versionId so Vonk assigns a new one on Update
            .Remove("Patient.meta.versionId");

        var updatedIResource = updatedSourceNode.ToIResource(originalIResource.InformationModel);

        var storedIResource = await _changeRepository.Update(originalIResource.Key(), updatedIResource);

        _logger.LogInformation("Patient {PatientId} updated with ehrId={EhrId} for cdr={CdrName}",
            patientId, ehrId, cdrName);

        return storedIResource;
    }

    private async Task<FhirPatient?> FindPatientById(string patientId, IVonkContext ctx)
    {
        var iResource = await FindIResourceById(patientId, ctx);
        return iResource?.ToPoco() as FhirPatient;
    }

    private async Task<IResource?> FindIResourceById(string patientId, IVonkContext ctx)
    {
        var searchArgs = new ArgumentCollection();
        searchArgs.AddArgument(new Argument(ArgumentSource.Internal, ArgumentNames.resourceType, "Patient"));
        searchArgs.AddArgument(new Argument(ArgumentSource.Internal, "_id", patientId));

        var opts = SearchOptions.Latest(ctx.ServerBase, informationModel: ctx.InformationModel).WithFullAccess();
        var result = await _searchRepository.Search(searchArgs, opts);

        return result.FirstOrDefault();
    }

    private string? ExtractIdentifier(FhirPatient patient, string targetSystem, string cdrName)
    {
        return patient.Identifier
            .FirstOrDefault(id =>
                id.System == targetSystem &&
                id.Assigner?.Display == cdrName)
            ?.Value;
    }
}

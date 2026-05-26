using Hl7.Fhir.Model;

namespace OpenFhirFirelyPlugin;

/// <summary>
/// Sets the primary patient reference on any FHIR resource type.
/// "Primary" means the first direct patient/subject property on the resource itself
/// (mirrors the first entry in FhirCreateMiddleware.PatientPaths).
/// Uses reflection so no giant switch is needed and new resource types are handled automatically.
/// </summary>
public static class PatientReferenceHelper
{
    // Maps resource type name → the C# property name on the POCO that holds the primary patient ref.
    // Only the first (most canonical) patient-bearing property per type is listed.
    private static readonly Dictionary<string, string> PrimaryPatientProperty = new()
    {
        ["Account"]                     = "Subject",
        ["AdverseEvent"]                = "Subject",
        ["AllergyIntolerance"]          = "Patient",
        ["AppointmentResponse"]         = "Actor",
        ["AuditEvent"]                  = "Patient",
        ["Basic"]                       = "Subject",
        ["BodyStructure"]               = "Patient",
        ["CarePlan"]                    = "Subject",
        ["CareTeam"]                    = "Subject",
        ["ChargeItem"]                  = "Subject",
        ["Claim"]                       = "Patient",
        ["ClaimResponse"]               = "Patient",
        ["ClinicalImpression"]          = "Subject",
        ["Communication"]               = "Subject",
        ["CommunicationRequest"]        = "Subject",
        ["Composition"]                 = "Subject",
        ["Condition"]                   = "Subject",
        ["Consent"]                     = "Patient",
        ["CoverageEligibilityRequest"]  = "Patient",
        ["CoverageEligibilityResponse"] = "Patient",
        ["DetectedIssue"]               = "Patient",
        ["DeviceRequest"]               = "Subject",
        ["DeviceUseStatement"]          = "Subject",
        ["DiagnosticReport"]            = "Subject",
        ["DocumentManifest"]            = "Subject",
        ["DocumentReference"]           = "Subject",
        ["Encounter"]                   = "Subject",
        ["EnrollmentRequest"]           = "Candidate",
        ["EpisodeOfCare"]               = "Patient",
        ["ExplanationOfBenefit"]        = "Patient",
        ["FamilyMemberHistory"]         = "Patient",
        ["Flag"]                        = "Subject",
        ["Goal"]                        = "Subject",
        ["ImagingStudy"]                = "Subject",
        ["Immunization"]                = "Patient",
        ["ImmunizationEvaluation"]      = "Patient",
        ["ImmunizationRecommendation"]  = "Patient",
        ["Invoice"]                     = "Subject",
        ["List"]                        = "Subject",
        ["MeasureReport"]               = "Subject",
        ["Media"]                       = "Subject",
        ["MedicationAdministration"]    = "Subject",
        ["MedicationDispense"]          = "Subject",
        ["MedicationRequest"]           = "Subject",
        ["MedicationStatement"]         = "Subject",
        ["MolecularSequence"]           = "Patient",
        ["NutritionOrder"]              = "Patient",
        ["Observation"]                 = "Subject",
        ["Procedure"]                   = "Subject",
        ["Provenance"]                  = "Patient",
        ["QuestionnaireResponse"]       = "Subject",
        ["RelatedPerson"]               = "Patient",
        ["RequestGroup"]                = "Subject",
        ["ResearchSubject"]             = "Individual",
        ["RiskAssessment"]              = "Subject",
        ["ServiceRequest"]              = "Subject",
        ["Specimen"]                    = "Subject",
        ["SupplyDelivery"]              = "Patient",
        ["SupplyRequest"]               = "Subject",
        ["VisionPrescription"]          = "Patient",
    };

    public static void SetPrimaryPatientReference(Resource? resource, string patientFullUrl)
    {
        if (resource == null) return;
        if (!PrimaryPatientProperty.TryGetValue(resource.TypeName, out var propName)) return;

        var prop = resource.GetType().GetProperty(propName);
        if (prop == null || !prop.CanWrite) return;

        if (prop.PropertyType == typeof(ResourceReference))
            prop.SetValue(resource, new ResourceReference(patientFullUrl));
        else if (prop.PropertyType == typeof(DataType))
            prop.SetValue(resource, new ResourceReference(patientFullUrl));
    }
}

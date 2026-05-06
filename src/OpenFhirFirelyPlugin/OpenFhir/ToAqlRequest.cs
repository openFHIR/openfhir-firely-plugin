using System.Text.Json.Serialization;

namespace OpenFhirFirelyPlugin.OpenFhir;

public record ToAqlRequest(
    [property: JsonPropertyName("template")] string Template,
    [property: JsonPropertyName("ehrId")] string EhrId,
    [property: JsonPropertyName("fhirFullUrl")] string FhirFullUrl);

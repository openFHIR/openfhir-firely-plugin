using System.Text.Json.Serialization;

namespace OpenFhirFirelyPlugin.OpenFhir;

public record AqlRequest(
    [property: JsonPropertyName("q")] string Q);

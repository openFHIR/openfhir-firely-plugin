using System.Text.Json.Serialization;

namespace OpenFhirFirelyPlugin.OpenFhir;

public class ToAqlResponse
{
    [JsonPropertyName("aqls")]
    public List<AqlEntry>? Aqls { get; set; }
}

public class AqlEntry
{
    [JsonPropertyName("aql")]
    public string Aql { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public AqlType Type { get; set; }

    [JsonPropertyName("templateId")]
    public string? TemplateId { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AqlType
{
    COMPOSITION,
    ENTRY
}

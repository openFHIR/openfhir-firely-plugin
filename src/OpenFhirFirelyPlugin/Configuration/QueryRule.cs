namespace OpenFhirFirelyPlugin.Configuration;

public class QueryRule
{
    public string TemplateId { get; set; } = string.Empty;
    public Dictionary<string, string> FhirQuery { get; set; } = new();
}

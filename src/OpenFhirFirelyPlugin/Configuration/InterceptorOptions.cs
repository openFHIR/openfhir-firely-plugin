namespace OpenFhirFirelyPlugin.Configuration;

public class InterceptorOptions
{
    public string CdrsConfigFile { get; set; } = "/etc/cdrs.yml";
    public FhirCreateFilterOptions FhirCreateFilter { get; set; } = new();
    public FhirQueryFilterOptions FhirQueryFilter { get; set; } = new();
    public IpsOptions Ips { get; set; } = new();
}

public class IpsOptions
{
    public string TemplateId { get; set; } = "International Patient Summary";
}

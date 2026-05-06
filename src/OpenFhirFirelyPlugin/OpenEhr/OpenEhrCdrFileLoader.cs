using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenFhirFirelyPlugin.Configuration;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OpenFhirFirelyPlugin.OpenEhr;

public class OpenEhrCdrFileLoader
{
    private readonly string _configFilePath;
    private readonly ILogger<OpenEhrCdrFileLoader> _logger;

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public OpenEhrCdrFileLoader(IOptions<InterceptorOptions> options, ILogger<OpenEhrCdrFileLoader> logger)
    {
        _configFilePath = options.Value.CdrsConfigFile;
        _logger = logger;
    }

    /// <summary>Reads and returns the CDR list from disk. Never cached — hot-reload on every call.</summary>
    public List<CdrEntry> Load()
    {
        if (string.IsNullOrWhiteSpace(_configFilePath))
            throw new InvalidOperationException(
                "OpenFhirPlugin:Interceptor:CdrsConfigFile is not set. Provide a path to the CDR config YAML file.");

        if (!File.Exists(_configFilePath))
            throw new InvalidOperationException($"OpenEHR CDR config file not found: {_configFilePath}");

        try
        {
            var yaml = File.ReadAllText(_configFilePath);
            var entries = Deserializer.Deserialize<List<CdrEntry>>(yaml);

            if (entries == null || entries.Count == 0)
                throw new InvalidOperationException($"OpenEHR CDR config file contains no entries: {_configFilePath}");

            _logger.LogDebug("Loaded {Count} CDR entries from {Path}", entries.Count, _configFilePath);
            return entries;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse OpenEHR CDR config file '{_configFilePath}': {ex.Message}", ex);
        }
    }
}

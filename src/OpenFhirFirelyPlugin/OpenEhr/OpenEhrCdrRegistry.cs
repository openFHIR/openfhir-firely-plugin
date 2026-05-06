using Microsoft.Extensions.Logging;

namespace OpenFhirFirelyPlugin.OpenEhr;

public class OpenEhrCdrRegistry
{
    private readonly OpenEhrCdrFileLoader _fileLoader;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenEhrCdrRegistry> _logger;

    public OpenEhrCdrRegistry(
        OpenEhrCdrFileLoader fileLoader,
        IHttpClientFactory httpClientFactory,
        ILogger<OpenEhrCdrRegistry> logger)
    {
        _fileLoader = fileLoader;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Returns the canonical CDR id that <see cref="Resolve"/> would select.</summary>
    public string ResolveName(string? cdrId)
    {
        var entries = _fileLoader.Load();
        if (!string.IsNullOrWhiteSpace(cdrId))
        {
            foreach (var entry in entries)
            {
                if (cdrId == entry.Id)
                    return cdrId;
            }
        }
        return entries[0].Id;
    }

    /// <summary>Returns the <see cref="CdrEntry"/> for the given CDR id, falling back to first entry.</summary>
    public CdrEntry ResolveEntry(string? cdrId)
    {
        var entries = _fileLoader.Load();
        CdrEntry? match = null;

        if (!string.IsNullOrWhiteSpace(cdrId))
        {
            foreach (var entry in entries)
            {
                if (cdrId == entry.Id)
                {
                    match = entry;
                    break;
                }
            }
            if (match == null)
                _logger.LogWarning("Unknown CDR '{CdrId}' requested via {Header} header; falling back to first entry",
                    cdrId, Constants.TargetCdrHeader);
        }
        else
        {
            _logger.LogWarning("{Header} header absent; falling back to first configured CDR entry",
                Constants.TargetCdrHeader);
        }

        match ??= entries[0];
        _logger.LogDebug("Resolved CDR '{Id}' → {BaseUrl}", match.Id, match.BaseUrl);
        return match;
    }

    /// <summary>Returns a fresh <see cref="OpenEhrCdrClient"/> for the resolved CDR entry.</summary>
    public OpenEhrCdrClient Resolve(string? cdrId)
    {
        var entry = ResolveEntry(cdrId);
        return new OpenEhrCdrClient(entry, _httpClientFactory);
    }
}

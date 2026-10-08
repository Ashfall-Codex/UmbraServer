using MareSynchronosShared.Services;
using MareSynchronosShared.Utils.Configuration;

namespace MareSynchronosServer.Services;

public sealed class McdfShareStorage
{
    private readonly ILogger<McdfShareStorage> _logger;

    public string Directory { get; }
    public long MaxBytes { get; }

    public McdfShareStorage(ILogger<McdfShareStorage> logger, IConfigurationService<ServerConfiguration> configuration)
    {
        _logger = logger;
        var configured = configuration.GetValueOrDefault(nameof(ServerConfiguration.McdfStorageDirectory), string.Empty);
        Directory = string.IsNullOrWhiteSpace(configured) ? Path.Combine(AppContext.BaseDirectory, "mcdf_shares") : configured;
        var maxMiB = Math.Max(1, configuration.GetValueOrDefault(nameof(ServerConfiguration.McdfMaxSizeInMiB), 2048));
        MaxBytes = maxMiB * 1024L * 1024L;
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string GetPath(Guid shareId) => Path.Combine(Directory, $"{shareId:N}.bin");

    public string CreateTempPath(Guid shareId) => Path.Combine(Directory, $"{shareId:N}.{Guid.NewGuid():N}.tmp");

    public void Commit(string tempPath, Guid shareId) => File.Move(tempPath, GetPath(shareId), overwrite: true);

    public void Delete(Guid shareId) => TryDelete(GetPath(shareId));

    public void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete MCDF share file {Path}", path);
        }
    }

    /// <summary>Supprime les .bin sans partage en base et les .tmp abandonnés (uploads interrompus), au-delà de <paramref name="minAge"/>.</summary>
    public int SweepOrphans(IReadOnlySet<Guid> knownFileBackedShares, TimeSpan minAge)
    {
        var removed = 0;
        var threshold = DateTime.UtcNow - minAge;
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) > threshold) continue;

                var ext = Path.GetExtension(file);
                if (string.Equals(ext, ".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                    removed++;
                }
                else if (string.Equals(ext, ".bin", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id) || !knownFileBackedShares.Contains(id))
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not sweep MCDF storage file {Path}", file);
            }
        }
        return removed;
    }
}

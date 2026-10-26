using UmbraSync.API.Routes;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using MareSynchronosShared.Utils.Configuration;
using MareSynchronosStaticFilesServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MareSynchronosStaticFilesServer.Controllers;

[Route(MareFiles.Distribution)]
public class DistributionController : ControllerBase
{
    private readonly CachedFileProvider _cachedFileProvider;
    private readonly IConfigurationService<StaticFilesServerConfiguration> _configuration;

    public DistributionController(ILogger<DistributionController> logger, CachedFileProvider cachedFileProvider,
        IConfigurationService<StaticFilesServerConfiguration> configuration) : base(logger)
    {
        _cachedFileProvider = cachedFileProvider;
        _configuration = configuration;
    }

    [HttpGet(MareFiles.Distribution_Get)]
    [Authorize(Policy = "Internal")]
    public async Task<IActionResult> GetFile(string file)
    {
        // Le nom part aussi dans X-Accel-Redirect : jamais autre chose qu'un hash.
        if (file == null || !FileHash.IsAccepted(file)) return BadRequest();

        _logger.LogInformation($"GetFile:{MareUser}:{file}");

        var fi = await _cachedFileProvider.GetAndDownloadFile(file, HttpContext.RequestAborted);
        if (fi == null) return NotFound();

        if (_configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UseXAccelRedirect), false))
        {
            var prefix = _configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.XAccelRedirectPrefix));
            Response.Headers.Append("X-Accel-Redirect", Path.Combine(prefix, file));
            return Ok();
        }
        else
        {
            return PhysicalFile(fi.FullName, "application/octet-stream");
        }
    }

    [HttpPost("touch")]
    [Authorize(Policy = "Internal")]
    public IActionResult TouchFiles([FromBody] string[] files)
    {
        if (files == null) return BadRequest();

        var user = MareUser;
        _logger.LogInformation($"TouchFiles:{user}:{files.Length}");

        var hashes = files.Where(f => f != null && FileHash.IsAccepted(f)).ToArray();
        if (hashes.Length != files.Length)
            _logger.LogWarning("TouchFiles from {user}: {count} invalid hashes ignored", user, files.Length - hashes.Length);

        if (hashes.Length == 0)
            return Ok();

        _ = Task.Run(() => {
            try
            {
                foreach (var hash in hashes)
                    _cachedFileProvider.TouchColdHash(hash);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TouchFiles from {user} failed", user);
            }
        });

        return Ok();
    }
}

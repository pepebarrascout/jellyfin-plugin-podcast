using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Podcasts.Api;

/// <summary>
/// API controller for the Podcasts plugin.
/// Provides endpoints called from the dashboard configuration page:
/// - ValidateFeed: Checks if an RSS feed URL is valid before adding it to the configuration.
///
/// This controller is automatically discovered by Jellyfin's API routing.
/// The route is nested under the Plugins path for consistency with other Jellyfin plugin APIs.
/// </summary>
[ApiController]
[Route("Plugins/Podcasts")]
public class PodcastsApiController : ControllerBase
{
    private readonly ILogger<PodcastsApiController> _logger;
    private readonly PodcastService _podcastService;

    public PodcastsApiController(
        ILogger<PodcastsApiController> logger,
        PodcastService podcastService)
    {
        _logger = logger;
        _podcastService = podcastService;
    }

    /// <summary>
    /// Validates a podcast RSS feed URL.
    /// Checks that the URL is well-formed, accessible on the internet,
    /// contains valid RSS XML with audio enclosures, and is not already registered.
    /// Called from the config page before allowing the user to save a new feed.
    /// </summary>
    /// <param name="url">The RSS feed URL to validate.</param>
    /// <returns>200 with success=true if valid, or 200 with success=false and an error message.</returns>
    [HttpGet("ValidateFeed")]
    public async Task<ActionResult> ValidateFeed([FromQuery] string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Ok(new { success = false, error = "La URL del feed no puede estar vacía." });
        }

        try
        {
            var (isValid, errorMessage) = await _podcastService.ValidateFeedUrlAsync(url);
            return Ok(new { success = isValid, error = errorMessage ?? string.Empty });
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Error validating feed URL: {Url}", url);
            return Ok(new { success = false, error = $"Error interno: {ex.Message}" });
        }
    }
}

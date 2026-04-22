using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Podcasts.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Podcasts.Api;

/// <summary>
/// API controller for the Podcasts plugin.
/// Provides endpoints called from the dashboard configuration page for manual operations:
/// - UpdateAll: Triggers a manual update of all podcast feeds.
/// - UpdateFeed: Triggers a manual update of a specific feed by name.
/// These endpoints allow users to update feeds immediately without waiting for the scheduled time.
/// </summary>
[ApiController]
[Route("Plugins/Podcasts")]
public class PodcastsApiController : ControllerBase
{
    private readonly ILogger<PodcastsApiController> _logger;
    private readonly PodcastService _podcastService;

    /// <summary>
    /// Initializes a new instance of the <see cref="PodcastsApiController"/> class.
    /// </summary>
    public PodcastsApiController(
        ILogger<PodcastsApiController> logger,
        PodcastService podcastService)
    {
        _logger = logger;
        _podcastService = podcastService;
    }

    /// <summary>
    /// Triggers a manual update of all configured podcast feeds.
    /// Called from the "Actualizar Todos" button in the config page.
    /// Each feed is updated sequentially, downloading any new episodes available.
    /// </summary>
    [HttpPost("UpdateAll")]
    public async Task<ActionResult> UpdateAll()
    {
        try
        {
            var config = PodcastsPlugin.Instance?.Configuration;
            if (config == null || config.Feeds.Count == 0)
            {
                return Ok(new { success = false, error = "No hay podcasts configurados." });
            }

            _logger.LogInformation("Manual update triggered for all {Count} feeds", config.Feeds.Count);
            var updatedCount = 0;
            var errors = 0;

            foreach (var feed in config.Feeds.ToList())
            {
                try
                {
                    await _podcastService.UpdateFeedAsync(feed);
                    updatedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error manually updating feed {Name}", feed.Name);
                    errors++;
                }
            }

            // Save updated config (with new LastUpdateDate values)
            PodcastsPlugin.Instance?.SaveConfiguration();

            var message = $"Se actualizaron {updatedCount} podcast(s).";
            if (errors > 0)
            {
                message += $" {errors} error(es).";
            }

            return Ok(new { success = true, message = message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in UpdateAll endpoint");
            return Ok(new { success = false, error = $"Error interno: {ex.Message}" });
        }
    }

    /// <summary>
    /// Triggers a manual update of a specific podcast feed identified by its name.
    /// Called from the "Actualizar" button next to each feed in the config page.
    /// Downloads any new episodes available in the feed's RSS.
    /// </summary>
    /// <param name="name">The display name of the podcast feed to update.</param>
    [HttpPost("UpdateFeed")]
    public async Task<ActionResult> UpdateFeed([FromQuery] string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Ok(new { success = false, error = "Nombre de podcast no especificado." });
        }

        try
        {
            var config = PodcastsPlugin.Instance?.Configuration;
            if (config == null)
            {
                return Ok(new { success = false, error = "Configuración del plugin no disponible." });
            }

            var feed = config.Feeds.FirstOrDefault(f =>
                string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

            if (feed == null)
            {
                return Ok(new { success = false, error = $"No se encontró el podcast \"{name}\"." });
            }

            _logger.LogInformation("Manual update triggered for feed: {Name}", feed.Name);
            await _podcastService.UpdateFeedAsync(feed);
            PodcastsPlugin.Instance?.SaveConfiguration();

            return Ok(new { success = true, message = $"Podcast \"{feed.Name}\" actualizado correctamente." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in UpdateFeed endpoint for {Name}", name);
            return Ok(new { success = false, error = $"Error interno: {ex.Message}" });
        }
    }
}

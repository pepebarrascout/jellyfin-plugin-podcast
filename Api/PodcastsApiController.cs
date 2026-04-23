using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;
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
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="PodcastsApiController"/> class.
    /// </summary>
    public PodcastsApiController(
        ILogger<PodcastsApiController> logger,
        PodcastService podcastService,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _podcastService = podcastService;
        _httpClientFactory = httpClientFactory;
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

    /// <summary>
    /// Validates an RSS feed URL server-side by downloading and parsing the feed.
    /// Checks that the URL returns valid XML with RSS structure and at least one audio episode.
    /// This avoids CORS issues since the request is made from the server, not the browser.
    /// </summary>
    /// <param name="url">The RSS feed URL to validate.</param>
    [HttpPost("ValidateFeed")]
    public async Task<ActionResult> ValidateFeed([FromQuery] string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Ok(new { success = false, error = "URL de feed RSS no especificada." });
        }

        // Check for duplicate in existing feeds
        var config = PodcastsPlugin.Instance?.Configuration;
        if (config != null && config.Feeds != null)
        {
            var isDuplicate = config.Feeds.Any(f =>
                string.Equals(f.FeedUrl.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            if (isDuplicate)
            {
                return Ok(new { success = false, error = "Este feed RSS ya está registrado." });
            }
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            var response = await client.GetStringAsync(url);
            var doc = XDocument.Parse(response);

            // Check for RSS root element
            var rssElement = doc.Root;
            if (rssElement == null || rssElement.Name.LocalName != "rss")
            {
                return Ok(new { success = false, error = "La URL no contiene un feed RSS válido." });
            }

            var channel = rssElement.Element("channel");
            if (channel == null)
            {
                return Ok(new { success = false, error = "El feed RSS no contiene un elemento channel." });
            }

            // Check for at least one item with an audio enclosure
            var items = channel.Elements("item").ToList();
            var audioExtensions = new[] { ".mp3", ".m4a", ".ogg", ".opus", ".wav", ".flac", ".aac", ".wma", ".mp4", ".webm" };
            var hasAudio = false;
            var episodeCount = 0;

            foreach (var item in items)
            {
                var enclosure = item.Element("enclosure");
                if (enclosure != null)
                {
                    episodeCount++;
                    var encUrl = enclosure.Attribute("url")?.Value ?? string.Empty;
                    var mimeType = (enclosure.Attribute("type")?.Value ?? string.Empty).ToLowerInvariant();

                    if (mimeType.StartsWith("audio/") || audioExtensions.Any(ext => encUrl.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                    {
                        hasAudio = true;
                        break;
                    }
                }
            }

            if (!hasAudio)
            {
                return Ok(new { success = false, error = "El feed RSS no contiene episodios de audio válidos." });
            }

            _logger.LogInformation("Feed RSS validado exitosamente: {Url} con {Count} episodios", url, items.Count);
            return Ok(new { success = true, message = "Feed RSS valido. Listo para agregar." });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Error HTTP al validar feed: {Url}", url);
            return Ok(new { success = false, error = $"No se pudo conectar al feed: HTTP {ex.StatusCode}" });
        }
        catch (TaskCanceledException)
        {
            return Ok(new { success = false, error = "Tiempo de espera agotado al conectar con el feed." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al validar feed: {Url}", url);
            return Ok(new { success = false, error = $"Error al procesar el feed: {ex.Message}" });
        }
    }
}

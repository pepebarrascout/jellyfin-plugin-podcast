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
/// - UpdateAll: Starts a background update of all feeds (returns immediately).
/// - UpdateFeed: Triggers a manual update of a specific feed by name.
/// - GeneratePlaylist: Triggers manual playlist generation.
/// - DeleteListened: Deletes all listened episodes immediately.
/// - ExportOpml: Generates and returns an OPML file with all configured feeds.
/// - ImportOpml: Imports podcast feeds from an OPML file.
/// - ValidateFeed: Server-side RSS feed URL validation.
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
    /// Starts a background update of all configured podcast feeds.
    /// Returns immediately with a success message - the update runs in the background.
    /// This prevents HTTP timeouts when updating many feeds with large episodes.
    /// Called from the "Actualizar Todos" button in the config page.
    /// </summary>
    [HttpPost("UpdateAll")]
    public ActionResult UpdateAll()
    {
        try
        {
            var config = PodcastsPlugin.Instance?.Configuration;
            if (config == null || config.Feeds.Count == 0)
            {
                return Ok(new { success = false, error = "No hay podcasts configurados." });
            }

            _logger.LogInformation("Manual update triggered for all {Count} feeds (background)", config.Feeds.Count);

            // Run the update in a fire-and-forget background task
            _ = Task.Run(async () =>
            {
                try
                {
                    await _podcastService.UpdateAllFeedsAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Background update failed");
                }
            });

            return Ok(new { success = true, message = $"Actualizacion de {config.Feeds.Count} podcast(s) iniciada en segundo plano. Los episodios se descargaran progresivamente." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting UpdateAll");
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
                return Ok(new { success = false, error = "Configuracion del plugin no disponible." });
            }

            var feed = config.Feeds.FirstOrDefault(f =>
                string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

            if (feed == null)
            {
                return Ok(new { success = false, error = $"No se encontro el podcast \"{name}\"." });
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
    /// Triggers a manual generation of the auto-playlist.
    /// Called from the "Generar playlist" button in the config page.
    /// </summary>
    [HttpPost("GeneratePlaylist")]
    public async Task<ActionResult> GeneratePlaylist()
    {
        try
        {
            _logger.LogInformation("Manual playlist generation triggered");
            await _podcastService.GenerateAutoPlaylistAsync();
            return Ok(new { success = true, message = "Lista de reproduccion generada correctamente." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GeneratePlaylist endpoint");
            return Ok(new { success = false, error = $"Error interno: {ex.Message}" });
        }
    }

    /// <summary>
    /// Immediately deletes all listened podcast episode files.
    /// Called from the "Borrar escuchados" button in the config page.
    /// Unlike the scheduled auto-delete (which waits 2 days), this deletes immediately.
    /// </summary>
    [HttpPost("DeleteListened")]
    public ActionResult DeleteListened()
    {
        try
        {
            _logger.LogInformation("Manual delete-listened triggered");
            var (deletedCount, message) = _podcastService.DeleteListenedEpisodes();
            return Ok(new { success = true, message = message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in DeleteListened endpoint");
            return Ok(new { success = false, error = $"Error interno: {ex.Message}" });
        }
    }

    /// <summary>
    /// Exports all configured podcast feeds as an OPML file.
    /// The OPML file can be used to import subscriptions into other podcast apps
    /// or to back up the current configuration.
    /// Called from the "Exportar OPML" button in the config page.
    /// </summary>
    [HttpGet("ExportOpml")]
    public ActionResult ExportOpml()
    {
        try
        {
            _logger.LogInformation("OPML export triggered");
            var opmlContent = _podcastService.GenerateOpml();
            var bytes = System.Text.Encoding.UTF8.GetBytes(opmlContent);
            return File(bytes, "application/xml", "podcasts.opml");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ExportOpml endpoint");
            return Ok(new { success = false, error = $"Error interno: {ex.Message}" });
        }
    }

    /// <summary>
    /// Imports podcast feeds from an OPML file.
    /// Feeds that already exist (by URL) are skipped.
    /// New feeds are added with default settings (Daily frequency, AfterTwoDays auto-delete).
    /// Called from the "Importar OPML" button in the config page.
    /// </summary>
    [HttpPost("ImportOpml")]
    public async Task<ActionResult> ImportOpml()
    {
        try
        {
            var file = Request.Form.Files.FirstOrDefault();
            if (file == null || file.Length == 0)
            {
                return Ok(new { success = false, error = "No se selecciono ningun archivo OPML." });
            }

            if (!file.FileName.EndsWith(".opml", StringComparison.OrdinalIgnoreCase) &&
                !file.FileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                return Ok(new { success = false, error = "El archivo debe ser un archivo OPML (.opml o .xml)." });
            }

            using var reader = new StreamReader(file.OpenReadStream());
            var opmlContent = await reader.ReadToEndAsync();

            _logger.LogInformation("OPML import triggered, file size: {Size} bytes", file.Length);
            var importedCount = _podcastService.ImportFromOpml(opmlContent);

            if (importedCount > 0)
            {
                return Ok(new { success = true, message = $"Se importaron {importedCount} podcast(s) correctamente." });
            }
            else
            {
                return Ok(new { success = false, error = "No se encontraron nuevos podcasts en el archivo OPML (ya existen todos)." });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ImportOpml endpoint");
            return Ok(new { success = false, error = $"Error al importar: {ex.Message}" });
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
                return Ok(new { success = false, error = "Este feed RSS ya esta registrado." });
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
                return Ok(new { success = false, error = "La URL no contiene un feed RSS valido." });
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
                return Ok(new { success = false, error = "El feed RSS no contiene episodios de audio validos." });
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

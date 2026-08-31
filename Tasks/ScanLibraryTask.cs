using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Podcasts.Tasks;

/// <summary>
/// Jellyfin Scheduled Task: Forces Jellyfin to scan the podcast folder for new files
/// and add them to its database.
/// Appears in Dashboard > Scheduled Tasks under the "Podcasts" category.
/// The user configures the schedule from Jellyfin's UI.
/// No default trigger is provided - the user must add a schedule manually.
/// 
/// This is useful after the "Update feeds" task downloads new episodes,
/// to ensure Jellyfin detects them and adds them to the library immediately.
/// </summary>
public class ScanLibraryTask : IScheduledTask
{
    private readonly PodcastService _podcastService;
    private readonly ILogger<ScanLibraryTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScanLibraryTask"/> class.
    /// </summary>
    public ScanLibraryTask(PodcastService podcastService, ILogger<ScanLibraryTask> logger)
    {
        _podcastService = podcastService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Escanear biblioteca de podcasts";

    /// <inheritdoc />
    public string Key => "PodcastsScanLibrary";

    /// <inheritdoc />
    public string Description => "Fuerza a Jellyfin a escanear la carpeta de podcasts para detectar archivos nuevos y agregarlos a la base de datos. Se recomienda ejecutar despues de actualizar los feeds RSS.";

    /// <inheritdoc />
    public string Category => "Podcasts";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Scheduled task: Scanning podcast library...");

        try
        {
            await _podcastService.ScanPodcastLibraryAsync(progress);

            _logger.LogInformation("Scheduled task: Library scan completed");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Scheduled task: Library scan was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled task: Error scanning library");
            throw;
        }
    }
}

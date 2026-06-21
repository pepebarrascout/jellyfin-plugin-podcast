using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Podcasts.Tasks;

/// <summary>
/// Jellyfin Scheduled Task: Processes auto-deletion of listened podcast episodes.
/// Appears in Dashboard > Scheduled Tasks under the "Podcasts" category.
/// The user configures the schedule from Jellyfin's UI.
/// No default trigger is provided - the user must add a schedule manually.
/// Episodes are deleted 2 days after being listened to (for feeds with AfterTwoDays option).
/// </summary>
public class AutoDeleteTask : IScheduledTask
{
    private readonly PodcastService _podcastService;
    private readonly ILogger<AutoDeleteTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoDeleteTask"/> class.
    /// </summary>
    public AutoDeleteTask(PodcastService podcastService, ILogger<AutoDeleteTask> logger)
    {
        _podcastService = podcastService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Procesar auto-borrado de podcasts";

    /// <inheritdoc />
    public string Key => "PodcastsAutoDelete";

    /// <inheritdoc />
    public string Description => "Borra episodios de podcasts que fueron escuchados hace mas de 2 dias. Solo afecta feeds configurados con la opcion de auto-borrado activada. Los episodios borrados se registran para evitar re-descargas durante 6 meses.";

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
        _logger.LogInformation("Scheduled task: Processing auto-delete...");

        try
        {
            progress.Report(10);
            await _podcastService.ProcessAutoDeleteAsync();
            progress.Report(100);

            _logger.LogInformation("Scheduled task: Auto-delete processing completed");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Scheduled task: Auto-delete was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled task: Error processing auto-delete");
            throw;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emby.TrailerDownloader.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace Emby.TrailerDownloader.ScheduledTasks
{
    /// <summary>
    /// Safety net for the "item added" hook: scans the whole library for movies/series without a local trailer
    /// (existing library, items added while the plugin was off, and earlier failures whose back-off has expired).
    /// </summary>
    public class DownloadMissingTrailersTask : IScheduledTask
    {
        private readonly TrailerService _service;

        public DownloadMissingTrailersTask(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            IJsonSerializer json,
            ILogManager logManager,
            IApplicationPaths appPaths,
            IFfmpegManager ffmpegManager)
        {
            _service = TrailerService.GetOrCreate(libraryManager, providerManager, fileSystem, json, logManager, appPaths, ffmpegManager);
        }

        public string Name => "Download missing trailers";

        public string Key => "TrailerDownloaderMissingTrailers";

        public string Description =>
            "Downloads trailers for every movie and series that doesn't have a local trailer yet. Items that failed recently are retried with back-off.";

        public string Category => Plugin.PluginName;

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress) =>
            RunScanAsync(_service, false, cancellationToken, progress);

        internal static async Task RunScanAsync(
            TrailerService service, bool ignoreBackoff, CancellationToken cancellationToken, IProgress<double> progress)
        {
            var items = service.GetCandidateItems();
            int downloaded = 0, failed = 0, existing = 0;

            for (var i = 0; i < items.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(100.0 * i / Math.Max(1, items.Count));

                try
                {
                    switch (await service.ProcessAsync(items[i], ignoreBackoff, true, cancellationToken).ConfigureAwait(false))
                    {
                        case TrailerOutcome.Downloaded: downloaded++; break;
                        case TrailerOutcome.Failed: failed++; break;
                        case TrailerOutcome.AlreadyHasTrailer: existing++; break;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    service.Logger.ErrorException("Error processing {0}", ex, items[i].Name);
                }
            }

            progress.Report(100);
            service.Logger.Info("Trailer scan finished: {0} downloaded, {1} failed, {2} already had trailers, {3} checked",
                downloaded, failed, existing, items.Count);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks }
        };
    }
}

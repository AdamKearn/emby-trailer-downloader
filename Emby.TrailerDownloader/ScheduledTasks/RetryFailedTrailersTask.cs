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
    /// Manual task: forget every recorded failure and retry them all right now, e.g. after adding a proxy or cookies.
    /// </summary>
    public class RetryFailedTrailersTask : IScheduledTask
    {
        private readonly TrailerService _service;

        public RetryFailedTrailersTask(
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

        public string Name => "Retry all failed trailers now";

        public string Key => "TrailerDownloaderRetryFailed";

        public string Description =>
            "Clears the failure/back-off history and immediately retries every movie and series that is still missing a trailer. Run this after changing proxy or cookie settings.";

        public string Category => Plugin.PluginName;

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _service.Failures.ClearAll();
            return DownloadMissingTrailersTask.RunScanAsync(_service, true, cancellationToken, progress);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    }
}

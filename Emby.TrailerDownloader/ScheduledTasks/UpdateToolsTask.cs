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
    /// YouTube changes often and old yt-dlp builds stop working, so keep the managed copy up to date.
    /// </summary>
    public class UpdateToolsTask : IScheduledTask
    {
        private readonly TrailerService _service;

        public UpdateToolsTask(
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

        public string Name => "Update yt-dlp";

        public string Key => "TrailerDownloaderUpdateTools";

        public string Description => "Downloads the latest yt-dlp (and Deno, if enabled) used to fetch trailers.";

        public string Category => Plugin.PluginName;

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            progress.Report(0);
            await _service.UpdateToolsAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(100);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(2).Ticks }
        };
    }
}

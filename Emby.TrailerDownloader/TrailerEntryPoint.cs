using System;
using System.Threading;
using System.Threading.Tasks;
using Emby.TrailerDownloader.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.TrailerDownloader
{
    /// <summary>
    /// Listens for new library items and queues a trailer download for the owning movie or series, then works
    /// through the queue (which <see cref="TrailerRefreshProvider"/> also feeds when a user clicks "Refresh metadata").
    /// Seasons and episodes collapse onto their series, so adding a whole season only queues one download.
    /// </summary>
    public class TrailerEntryPoint : IServerEntryPoint
    {
        private const int MaxNotReadyPasses = 3;
        private static readonly TimeSpan NotReadyRetryDelay = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

        private readonly ILibraryManager _libraryManager;
        private readonly TrailerService _service;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public TrailerEntryPoint(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            IJsonSerializer json,
            ILogManager logManager,
            IApplicationPaths appPaths,
            IFfmpegManager ffmpegManager)
        {
            _libraryManager = libraryManager;
            _service = TrailerService.GetOrCreate(libraryManager, providerManager, fileSystem, json, logManager, appPaths, ffmpegManager);
            _logger = _service.Logger;
        }

        public void Run()
        {
            _libraryManager.ItemAdded += OnItemAdded;
            Task.Run(() => WorkerLoopAsync(_cts.Token));
            Task.Run(async () =>
            {
                try
                {
                    await _service.EnsureToolsAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Failed to install yt-dlp / Deno on startup; will retry on the next download", ex);
                }
            });
        }

        private void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            try
            {
                if (!Plugin.Instance.Configuration.EnableOnItemAdded) return;

                var target = _service.ResolveTarget(e.Item);
                if (target == null) return;

                var delay = TimeSpan.FromMinutes(Math.Max(0, Plugin.Instance.Configuration.ProcessDelayMinutes));
                _service.Enqueue(target, delay, false, false);
                _logger.Debug("Queued trailer check for {0} (triggered by {1})", target.Name, e.Item.Name);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error handling new item {0}", ex, e.Item?.Name);
            }
        }

        private async Task WorkerLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _service.WaitForQueueAsync(PollInterval, cancellationToken).ConfigureAwait(false);

                    foreach (var entry in _service.TakeDueItems())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var job = entry.Value;

                        var item = _service.GetItem(entry.Key);
                        if (item == null) continue;

                        var allowWithoutMetadata = job.Manual || job.Passes >= MaxNotReadyPasses;
                        var outcome = await _service
                            .ProcessAsync(item, job.Manual, allowWithoutMetadata, cancellationToken, job.Force)
                            .ConfigureAwait(false);

                        if (outcome == TrailerOutcome.NotReady)
                        {
                            // Emby hasn't fetched trailer URLs / TMDB id yet - check again later.
                            _service.Enqueue(item, NotReadyRetryDelay, false, false, job.Passes + 1);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Trailer queue error", ex);
                }
            }
        }

        public void Dispose()
        {
            _libraryManager.ItemAdded -= OnItemAdded;
            _cts.Cancel();
        }
    }
}

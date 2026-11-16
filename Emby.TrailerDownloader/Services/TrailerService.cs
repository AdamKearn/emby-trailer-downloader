using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.TrailerDownloader.Services
{
    public enum TrailerOutcome
    {
        AlreadyHasTrailer,
        Downloaded,
        NotReady,
        Skipped,
        Failed
    }

    /// <summary>
    /// Core logic shared by the "item added" hook and the scheduled tasks. A single instance is shared so that
    /// only one download (or tool update) runs at a time.
    /// </summary>
    public class TrailerService
    {
        private static readonly object CreateLock = new object();
        private static TrailerService _instance;

        private static readonly HashSet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v", ".wmv", ".ts", ".m2ts", ".mpg", ".mpeg", ".flv", ".iso"
        };

        private readonly SemaphoreSlim _workLock = new SemaphoreSlim(1, 1);
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;
        private readonly TmdbClient _tmdb;
        private readonly string _tempRoot;

        private TrailerService(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            IJsonSerializer json,
            ILogManager logManager,
            IApplicationPaths appPaths,
            IFfmpegManager ffmpegManager)
        {
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _logger = logManager.GetLogger("TrailerDownloader");

            var dataDir = Path.Combine(appPaths.PluginConfigurationsPath, "TrailerDownloader");
            Directory.CreateDirectory(dataDir);
            _tempRoot = Path.Combine(dataDir, "temp");

            string ffmpeg = null;
            try
            {
                ffmpeg = ffmpegManager?.FfmpegConfiguration?.EncoderPath;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Could not determine Emby's ffmpeg path", ex);
            }

            Tools = new ToolManager(_logger, dataDir, ffmpeg);
            Failures = new FailureStore(json, _logger, dataDir);
            _tmdb = new TmdbClient(json, _logger);
        }

        public ToolManager Tools { get; }

        public FailureStore Failures { get; }

        public ILogger Logger => _logger;

        public static TrailerService GetOrCreate(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            IJsonSerializer json,
            ILogManager logManager,
            IApplicationPaths appPaths,
            IFfmpegManager ffmpegManager)
        {
            lock (CreateLock)
            {
                return _instance ?? (_instance = new TrailerService(
                    libraryManager, providerManager, fileSystem, json, logManager, appPaths, ffmpegManager));
            }
        }

        // ------------------------------------------------------------------ item selection

        /// <summary>
        /// Maps a newly added item to the item that should own the trailer: a movie keeps its own, while
        /// seasons and episodes are redirected to their series so a show only ever gets one trailer.
        /// </summary>
        public BaseItem ResolveTarget(BaseItem item)
        {
            if (item == null || item.IsVirtualItem) return null;

            var config = Plugin.Instance.Configuration;
            switch (item)
            {
                case Movie movie:
                    return config.EnableMovies ? movie : null;
                case Series series:
                    return config.EnableSeries ? series : null;
                case Season season:
                    return config.EnableSeries ? season.Series : null;
                case Episode episode:
                    return config.EnableSeries ? episode.Series : null;
                default:
                    return null;
            }
        }

        public List<BaseItem> GetCandidateItems()
        {
            var config = Plugin.Instance.Configuration;
            var types = new List<string>();
            if (config.EnableMovies) types.Add(nameof(Movie));
            if (config.EnableSeries) types.Add(nameof(Series));
            if (types.Count == 0) return new List<BaseItem>();

            return _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = types.ToArray(),
                    Recursive = true,
                    IsVirtualItem = false
                })
                .Where(i => !string.IsNullOrEmpty(i.Path))
                .ToList();
        }

        public BaseItem GetItem(long internalId) => _libraryManager.GetItemById(internalId);

        // ------------------------------------------------------------------ queue

        /// <summary>A queued trailer job. Immutable so it can be swapped atomically in the dictionary.</summary>
        public sealed class QueuedItem
        {
            public QueuedItem(DateTime dueUtc, int passes, bool manual, bool force)
            {
                DueUtc = dueUtc;
                Passes = passes;
                Manual = manual;
                Force = force;
            }

            public DateTime DueUtc { get; }

            /// <summary>How many times it was put back because Emby hadn't fetched metadata yet.</summary>
            public int Passes { get; }

            /// <summary>Requested by a user (Refresh metadata): skips back-off and the metadata wait.</summary>
            public bool Manual { get; }

            /// <summary>Download even if a trailer already exists.</summary>
            public bool Force { get; }
        }

        private readonly ConcurrentDictionary<long, QueuedItem> _queue = new ConcurrentDictionary<long, QueuedItem>();
        private readonly SemaphoreSlim _queueSignal = new SemaphoreSlim(0);

        /// <summary>
        /// Queues a movie/series. Duplicates merge into one job, so a whole season of new episodes
        /// still means one download for the series. A manual request upgrades an existing automatic one.
        /// </summary>
        public void Enqueue(BaseItem target, TimeSpan delay, bool manual, bool force, int passes = 0)
        {
            var job = new QueuedItem(DateTime.UtcNow + delay, passes, manual, force);
            _queue.AddOrUpdate(
                target.InternalId,
                job,
                (_, existing) => manual
                    ? new QueuedItem(
                        job.DueUtc < existing.DueUtc ? job.DueUtc : existing.DueUtc,
                        existing.Passes,
                        true,
                        force || existing.Force)
                    : existing);
            _queueSignal.Release();
        }

        /// <summary>Waits until something is queued or the timeout passes.</summary>
        public Task WaitForQueueAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            _queueSignal.WaitAsync(timeout, cancellationToken);

        /// <summary>Removes and returns every job that is due now.</summary>
        public List<KeyValuePair<long, QueuedItem>> TakeDueItems()
        {
            var now = DateTime.UtcNow;
            var due = new List<KeyValuePair<long, QueuedItem>>();
            foreach (var entry in _queue.Where(p => p.Value.DueUtc <= now).ToList())
            {
                if (_queue.TryRemove(entry.Key, out var job)) due.Add(new KeyValuePair<long, QueuedItem>(entry.Key, job));
            }

            return due;
        }

        public bool HasMetadataForDownload(BaseItem item) =>
            (item.RemoteTrailers != null && item.RemoteTrailers.Length > 0) ||
            !string.IsNullOrEmpty(item.GetProviderId(MetadataProviders.Tmdb));

        // ------------------------------------------------------------------ local trailer detection

        public bool HasLocalTrailer(BaseItem item)
        {
            if (item.LocalTrailerCount > 0) return true;

            var folder = GetItemFolder(item);
            if (folder == null || !Directory.Exists(folder)) return false;

            var trailersDir = Path.Combine(folder, "trailers");
            if (Directory.Exists(trailersDir) && Directory.EnumerateFiles(trailersDir).Any(IsVideoFile)) return true;

            if (item is Movie && File.Exists(item.Path))
            {
                var baseName = Path.GetFileNameWithoutExtension(item.Path);
                var shared = FolderHasOtherVideos(folder, item.Path);
                foreach (var file in Directory.EnumerateFiles(folder).Where(IsVideoFile))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (name.Equals(baseName + "-trailer", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals(baseName + ".trailer", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (!shared && (name.Equals("trailer", StringComparison.OrdinalIgnoreCase) ||
                                    name.EndsWith("-trailer", StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static string GetItemFolder(BaseItem item)
        {
            if (string.IsNullOrEmpty(item.Path)) return null;
            return Directory.Exists(item.Path) ? item.Path : Path.GetDirectoryName(item.Path);
        }

        private static bool IsVideoFile(string path) => VideoExtensions.Contains(Path.GetExtension(path));

        /// <summary>True when the movie shares its folder with other movies (e.g. all movies dumped in one folder).</summary>
        private static bool FolderHasOtherVideos(string folder, string moviePath)
        {
            var baseName = Path.GetFileNameWithoutExtension(moviePath);
            return Directory.EnumerateFiles(folder)
                .Where(IsVideoFile)
                .Select(Path.GetFileNameWithoutExtension)
                .Any(n => !n.StartsWith(baseName, StringComparison.OrdinalIgnoreCase) &&
                          !n.EndsWith("-trailer", StringComparison.OrdinalIgnoreCase) &&
                          !n.EndsWith("-sample", StringComparison.OrdinalIgnoreCase) &&
                          !n.Equals("trailer", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Where the downloaded trailer goes (without extension).</summary>
        private static string GetTrailerTargetWithoutExtension(BaseItem item)
        {
            var folder = GetItemFolder(item);
            if (item is Movie && File.Exists(item.Path) && FolderHasOtherVideos(folder, item.Path))
            {
                // Movie isn't in its own folder: use the "<movie>-trailer.ext" convention next to the file.
                return Path.Combine(folder, Path.GetFileNameWithoutExtension(item.Path) + "-trailer");
            }

            var title = item.ProductionYear.HasValue ? $"{item.Name} ({item.ProductionYear})" : item.Name;
            return Path.Combine(folder, "trailers", SanitiseFileName(title) + " - Trailer");
        }

        private static string SanitiseFileName(string name)
        {
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { ':', '*', '?', '"', '<', '>', '|', '/', '\\' };
            var sb = new StringBuilder(name.Length);
            foreach (var c in name) sb.Append(invalid.Contains(c) ? ' ' : c);
            return sb.ToString().Trim().TrimEnd('.');
        }

        private static string Describe(BaseItem item) =>
            $"[{(item is Series ? "TV" : "Movie")}] {item.Name}{(item.ProductionYear.HasValue ? $" ({item.ProductionYear})" : string.Empty)}";

        // ------------------------------------------------------------------ processing

        public async Task EnsureToolsAsync(CancellationToken cancellationToken)
        {
            await _workLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Tools.EnsureInstalledAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _workLock.Release();
            }
        }

        public async Task UpdateToolsAsync(CancellationToken cancellationToken)
        {
            await _workLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Tools.UpdateAllAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _workLock.Release();
            }
        }

        /// <param name="item">A movie or series.</param>
        /// <param name="ignoreBackoff">Retry immediately even if the item failed recently.</param>
        /// <param name="allowWithoutMetadata">If false and Emby hasn't fetched trailer URLs / a TMDB id yet, return NotReady.</param>
        /// <param name="force">Download even if the item already has a trailer (replaces the one this plugin saved).</param>
        public async Task<TrailerOutcome> ProcessAsync(
            BaseItem item, bool ignoreBackoff, bool allowWithoutMetadata, CancellationToken cancellationToken, bool force = false)
        {
            await _workLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ProcessLockedAsync(item, ignoreBackoff, allowWithoutMetadata, force, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // e.g. no permission to write into the media folder, or tools couldn't be installed.
                _logger.ErrorException("Trailer download failed for {0}", ex, Describe(item));
                Failures.RecordFailure(item.InternalId, Describe(item), ex.Message);
                return TrailerOutcome.Failed;
            }
            finally
            {
                _workLock.Release();
            }
        }

        private async Task<TrailerOutcome> ProcessLockedAsync(
            BaseItem item, bool ignoreBackoff, bool allowWithoutMetadata, bool force, CancellationToken cancellationToken)
        {
            if (GetItemFolder(item) == null) return TrailerOutcome.Skipped;

            if (!force && HasLocalTrailer(item))
            {
                Failures.Clear(item.InternalId);
                return TrailerOutcome.AlreadyHasTrailer;
            }

            if (!allowWithoutMetadata && !HasMetadataForDownload(item)) return TrailerOutcome.NotReady;

            if (!Failures.ShouldAttempt(item.InternalId, ignoreBackoff)) return TrailerOutcome.Skipped;

            await Tools.EnsureInstalledAsync(cancellationToken).ConfigureAwait(false);

            var candidates = await GetCandidateUrlsAsync(item, cancellationToken).ConfigureAwait(false);
            var config = Plugin.Instance.Configuration;
            if (candidates.Count == 0 && !config.EnableSearchFallback)
            {
                _logger.Info("No trailer URLs known for {0}", Describe(item));
                Failures.RecordFailure(item.InternalId, Describe(item), "No trailer URLs known");
                return TrailerOutcome.Failed;
            }

            _logger.Info("Downloading trailer for {0} ({1} candidate URL(s))", Describe(item), candidates.Count);

            var tempDir = Path.Combine(_tempRoot, item.InternalId.ToString());
            string lastError = null;
            try
            {
                foreach (var url in candidates)
                {
                    var result = await TryDownloadWithProxyFallbackAsync(url, tempDir, false, cancellationToken).ConfigureAwait(false);
                    if (result.File != null) return await FinishAsync(item, result.File, cancellationToken).ConfigureAwait(false);
                    lastError = result.Error;
                }

                if (config.EnableSearchFallback)
                {
                    var query = $"ytsearch5:{item.Name} {item.ProductionYear} official trailer".Replace("  ", " ");
                    _logger.Info("All known trailer URLs failed for {0}; searching YouTube", Describe(item));
                    var result = await TryDownloadWithProxyFallbackAsync(query, tempDir, true, cancellationToken).ConfigureAwait(false);
                    if (result.File != null) return await FinishAsync(item, result.File, cancellationToken).ConfigureAwait(false);
                    lastError = result.Error ?? lastError;
                }
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }

            _logger.Error("Could not download a trailer for {0}. Last error: {1}", Describe(item), lastError ?? "unknown");
            Failures.RecordFailure(item.InternalId, Describe(item), lastError);
            return TrailerOutcome.Failed;
        }

        private async Task<List<string>> GetCandidateUrlsAsync(BaseItem item, CancellationToken cancellationToken)
        {
            var urls = new List<string>();
            if (item.RemoteTrailers != null)
            {
                urls.AddRange(item.RemoteTrailers.Where(u => !string.IsNullOrWhiteSpace(u)));
            }

            var tmdbId = item.GetProviderId(MetadataProviders.Tmdb);
            urls.AddRange(await _tmdb.GetTrailerUrlsAsync(tmdbId, item is Series, null, cancellationToken).ConfigureAwait(false));

            return urls
                .Select(NormaliseUrl)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string NormaliseUrl(string url)
        {
            url = url.Trim();
            // Emby sometimes stores the "plugin://plugin.video.youtube/?action=play_video&videoid=X" Kodi form.
            const string kodiPrefix = "videoid=";
            if (url.StartsWith("plugin://", StringComparison.OrdinalIgnoreCase) && url.Contains(kodiPrefix))
            {
                var id = url.Substring(url.IndexOf(kodiPrefix, StringComparison.Ordinal) + kodiPrefix.Length).Split('&')[0];
                return "https://www.youtube.com/watch?v=" + id;
            }

            return url;
        }

        private async Task<TrailerOutcome> FinishAsync(BaseItem item, string downloadedFile, CancellationToken cancellationToken)
        {
            var targetBase = GetTrailerTargetWithoutExtension(item);
            var targetDir = Path.GetDirectoryName(targetBase);
            var target = targetBase + Path.GetExtension(downloadedFile);
            Directory.CreateDirectory(targetDir);

            // Replace a trailer this plugin saved earlier (possibly with a different extension).
            // Trailers with other names (e.g. added by hand) are left alone.
            foreach (var old in Directory.EnumerateFiles(targetDir, Path.GetFileName(targetBase) + ".*").Where(IsVideoFile).ToList())
            {
                File.Delete(old);
            }

            File.Move(downloadedFile, target);

            _logger.Info("Saved trailer for {0} to {1}", Describe(item), target);
            Failures.Clear(item.InternalId);

            await RescanItemAsync(item, targetDir, cancellationToken).ConfigureAwait(false);
            return TrailerOutcome.Downloaded;
        }

        /// <summary>
        /// Rescans just this movie/series so Emby registers the new trailer straight away - no library scan.
        /// </summary>
        private async Task RescanItemAsync(BaseItem item, string trailerDir, CancellationToken cancellationToken)
        {
            var itemFolder = GetItemFolder(item);
            var options = new MetadataRefreshOptions(_fileSystem)
            {
                // Re-read these folders from disk instead of Emby's cached directory listing,
                // otherwise the refresh may not notice the file we just wrote.
                RefreshPaths = new[] { itemFolder, trailerDir }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                // Default = keep existing metadata/images, only fill gaps and pick up file changes (incl. extras).
                MetadataRefreshMode = MetadataRefreshMode.Default,
                ImageRefreshMode = MetadataRefreshMode.Default,
                ReplaceAllMetadata = false,
                ReplaceAllImages = false,
                Recursive = false,
                ForceSave = true,
                // Marks this refresh as ours so TrailerRefreshProvider doesn't treat it as a user request.
                IsAutomated = true
            };

            try
            {
                if (item is Series)
                {
                    // Series are folders: validate only the series folder itself (not seasons/episodes).
                    await item.ValidateChildren(new Progress<double>(), cancellationToken, options, false).ConfigureAwait(false);
                }

                await item.RefreshMetadata(options, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Trailer saved, but rescanning {0} failed; it will be picked up by the next library scan", ex, Describe(item));
                return;
            }

            var refreshed = GetItem(item.InternalId) ?? item;
            if (refreshed.LocalTrailerCount > 0)
            {
                _logger.Info("Emby detected the trailer for {0}", Describe(item));
                return;
            }

            // Fall back to Emby's refresh queue (still just this item) in case the direct refresh was skipped.
            _logger.Warn("Trailer for {0} not detected after rescan; queueing another refresh of this item", Describe(item));
            try
            {
                _providerManager.QueueRefresh(item.InternalId, options, RefreshPriority.High);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Queueing a refresh for {0} failed", ex, Describe(item));
            }
        }

        // ------------------------------------------------------------------ yt-dlp

        private sealed class AttemptResult
        {
            public string File { get; set; }
            public string Error { get; set; }
        }

        private async Task<AttemptResult> TryDownloadWithProxyFallbackAsync(
            string url, string tempDir, bool isSearch, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            var proxy = string.IsNullOrWhiteSpace(config.ProxyUrl) ? null : config.ProxyUrl.Trim();
            var alwaysProxy = proxy != null && string.Equals(config.ProxyMode, "Always", StringComparison.OrdinalIgnoreCase);

            var first = await RunYtDlpAsync(url, tempDir, isSearch, alwaysProxy ? proxy : null, cancellationToken).ConfigureAwait(false);
            if (first.File != null || proxy == null || alwaysProxy) return first;

            _logger.Info("Direct download failed ({0}); retrying through proxy", first.Error);
            return await RunYtDlpAsync(url, tempDir, isSearch, proxy, cancellationToken).ConfigureAwait(false);
        }

        private async Task<AttemptResult> RunYtDlpAsync(
            string url, string tempDir, bool isSearch, string proxy, CancellationToken cancellationToken)
        {
            TryDeleteDirectory(tempDir);
            Directory.CreateDirectory(tempDir);

            var config = Plugin.Instance.Configuration;
            var height = config.MaxResolution > 0 ? config.MaxResolution : 1080;
            var ffmpeg = Tools.FfmpegPath;

            var args = new List<string>
            {
                "--no-playlist",
                "--no-progress",
                "--no-mtime",
                "--no-part",
                "--restrict-filenames",
                "--socket-timeout", "30",
                "--retries", "5",
                "--fragment-retries", "5",
                "--extractor-retries", "3",
                "-o", Path.Combine(tempDir, "trailer.%(ext)s")
            };

            if (ffmpeg != null)
            {
                // With ffmpeg we can merge the best separate video + audio streams.
                args.AddRange(new[]
                {
                    "-f", $"bv*[height<={height}]+ba/b[height<={height}]/bv*+ba/b",
                    "--merge-output-format", "mp4",
                    "--ffmpeg-location", ffmpeg
                });
            }
            else
            {
                // No ffmpeg: only formats that already contain both audio and video (usually up to 720p).
                args.AddRange(new[] { "-f", $"b[height<={height}]/b" });
            }

            var deno = Tools.DenoPath;
            if (deno != null) args.AddRange(new[] { "--js-runtimes", "deno:" + deno });

            if (!string.IsNullOrWhiteSpace(config.CookiesFile)) args.AddRange(new[] { "--cookies", config.CookiesFile.Trim() });
            if (proxy != null) args.AddRange(new[] { "--proxy", proxy });

            if (isSearch)
            {
                args.AddRange(new[]
                {
                    "--match-filters", "duration<=600 & title~='(?i)(trailer|teaser)'",
                    "--max-downloads", "1"
                });
            }

            args.AddRange(ProcessRunner.SplitArguments(config.ExtraYtDlpArgs));
            args.Add("--");
            args.Add(url);

            ProcessResult result;
            try
            {
                result = await ProcessRunner.RunAsync(Tools.YtDlpPath, args, TimeSpan.FromMinutes(20), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new AttemptResult { Error = "Could not start yt-dlp: " + ex.Message };
            }

            var file = Directory.EnumerateFiles(tempDir, "trailer.*")
                .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) &&
                            !f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
                .Where(IsVideoFile)
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();

            // Exit code 101 = stopped because --max-downloads was reached, i.e. success for searches.
            if (file != null && (result.ExitCode == 0 || result.ExitCode == 101)) return new AttemptResult { File = file };

            var error = result.TimedOut ? "yt-dlp timed out" : ExtractError(result);
            if (error.IndexOf("not a bot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                error.IndexOf("Sign in to confirm", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _logger.Warn("YouTube is asking this server to sign in. Setting a cookies file and/or a proxy in the plugin settings usually fixes this.");
            }

            _logger.Debug("yt-dlp failed for {0}{1}: {2}", url, proxy != null ? " (via proxy)" : string.Empty, error);
            return new AttemptResult { Error = error };
        }

        private static string ExtractError(ProcessResult result)
        {
            var errorLine = (result.StdErr ?? string.Empty)
                .Split('\n')
                .Select(l => l.Trim())
                .LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase));

            if (errorLine != null) return errorLine;
            if (result.ExitCode == 0 || result.ExitCode == 101) return "No matching trailer found";
            return $"yt-dlp exited with code {result.ExitCode}";
        }

        private static void TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                // Best effort.
            }
        }
    }
}

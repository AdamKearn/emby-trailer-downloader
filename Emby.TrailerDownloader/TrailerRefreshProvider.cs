using System;
using System.Threading;
using System.Threading.Tasks;
using Emby.TrailerDownloader.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.TrailerDownloader
{
    /// <summary>
    /// Emby has no API for adding entries to an item's "..." menu, so the plugin hooks into the
    /// "Refresh metadata" entry that is already there:
    ///  - Refresh metadata on a movie/series without a trailer  -> download it now (ignores back-off).
    ///  - Refresh metadata with "Replace all metadata" ticked   -> download again and replace the plugin's trailer.
    /// Only user-started refreshes count; library scans and the plugin's own refreshes are ignored.
    /// </summary>
    public class TrailerRefreshProvider : ICustomMetadataProvider<Movie>, ICustomMetadataProvider<Series>, IForcedProvider
    {
        // Let the refresh that triggered us finish saving (it may be fetching new trailer URLs) before we start.
        private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(20);

        private readonly TrailerService _service;

        public TrailerRefreshProvider(
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

        public string Name => Plugin.PluginName;

        public Task<ItemUpdateType> FetchAsync(MetadataResult<Movie> itemResult, MetadataRefreshOptions options, LibraryOptions libraryOptions, CancellationToken cancellationToken) =>
            Task.FromResult(Handle(itemResult.Item, options));

        public Task<ItemUpdateType> FetchAsync(MetadataResult<Series> itemResult, MetadataRefreshOptions options, LibraryOptions libraryOptions, CancellationToken cancellationToken) =>
            Task.FromResult(Handle(itemResult.Item, options));

        private ItemUpdateType Handle(BaseItem item, MetadataRefreshOptions options)
        {
            try
            {
                if (options == null || options.IsAutomated) return ItemUpdateType.None;

                var target = _service.ResolveTarget(item);
                if (target == null) return ItemUpdateType.None;

                var force = options.ReplaceAllMetadata;
                if (!force && _service.HasLocalTrailer(target)) return ItemUpdateType.None;

                _service.Enqueue(target, StartDelay, true, force);
                _service.Logger.Info("{0} trailer for {1} (requested via Refresh metadata)",
                    force ? "Re-downloading" : "Downloading", target.Name);
            }
            catch (Exception ex)
            {
                _service.Logger.ErrorException("Could not queue trailer for {0}", ex, item?.Name);
            }

            // Nothing on the item itself changes; the trailer is saved later by the queue worker.
            return ItemUpdateType.None;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using Emby.TrailerDownloader.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.TrailerDownloader
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage() =>
            GetType().Assembly.GetManifestResourceStream(GetType().Namespace + ".thumb.png");

        public const string PluginName = "Trailer Downloader";
        public static readonly Guid PluginGuid = new Guid("5119c78b-fe33-4f70-90b9-6cccf1b59b7a");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin Instance { get; private set; }

        public override string Name => PluginName;

        public override Guid Id => PluginGuid;

        public override string Description =>
            "Automatically downloads trailers for new movies and TV series and stores them locally next to your media.";

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "trailerdownloader",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                    IsMainConfigPage = true
                },
                new PluginPageInfo
                {
                    Name = "trailerdownloaderjs",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js"
                }
            };
        }
    }
}

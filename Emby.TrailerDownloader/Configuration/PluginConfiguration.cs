using MediaBrowser.Model.Plugins;

namespace Emby.TrailerDownloader.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        // ---- When to download ----

        /// <summary>Download a trailer as soon as a new movie / series / season / episode is added.</summary>
        public bool EnableOnItemAdded { get; set; } = true;

        /// <summary>Minutes to wait after an item is added so Emby can fetch its metadata (trailer URLs, TMDB id).</summary>
        public int ProcessDelayMinutes { get; set; } = 2;

        public bool EnableMovies { get; set; } = true;

        public bool EnableSeries { get; set; } = true;

        // ---- Where trailers come from ----

        /// <summary>Optional TMDB API key (v3 key or v4 read token). Adds extra trailer URLs in several languages.</summary>
        public string TmdbApiKey { get; set; } = string.Empty;

        /// <summary>Two-letter language code preferred when picking trailers from TMDB.</summary>
        public string PreferredLanguage { get; set; } = "en";

        /// <summary>When all known trailer URLs fail, search YouTube for "&lt;name&gt; &lt;year&gt; official trailer".</summary>
        public bool EnableSearchFallback { get; set; } = true;

        // ---- Download behaviour ----

        public int MaxResolution { get; set; } = 1080;

        /// <summary>Proxy for yt-dlp, e.g. socks5://user:pass@host:1080. Used to get around region blocks.</summary>
        public string ProxyUrl { get; set; } = string.Empty;

        /// <summary>"Fallback" = try direct first then retry through the proxy; "Always" = always use the proxy.</summary>
        public string ProxyMode { get; set; } = "Fallback";

        /// <summary>Optional Netscape-format cookies.txt passed to yt-dlp (helps with age-restricted / bot-checked videos).</summary>
        public string CookiesFile { get; set; } = string.Empty;

        /// <summary>Extra raw arguments appended to every yt-dlp call.</summary>
        public string ExtraYtDlpArgs { get; set; } = string.Empty;

        // ---- Tooling ----

        /// <summary>"nightly" or "stable".</summary>
        public string YtDlpChannel { get; set; } = "nightly";

        /// <summary>Use this yt-dlp binary instead of the one the plugin manages.</summary>
        public string YtDlpPathOverride { get; set; } = string.Empty;

        /// <summary>Use this ffmpeg binary instead of the one bundled with Emby.</summary>
        public string FfmpegPathOverride { get; set; } = string.Empty;

        /// <summary>Download Deno, the JavaScript runtime yt-dlp needs to fully support YouTube.</summary>
        public bool ManageDeno { get; set; } = true;

        // ---- Retries ----

        /// <summary>Give up on an item after this many failed attempts (0 = never give up).</summary>
        public int MaxAttempts { get; set; } = 10;
    }
}

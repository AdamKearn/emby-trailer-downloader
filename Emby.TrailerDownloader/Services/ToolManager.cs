using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.TrailerDownloader.Services
{
    /// <summary>
    /// Installs and updates the external tools the plugin needs (yt-dlp, Deno) inside the plugin's data
    /// folder, and finds ffmpeg, so nothing has to be installed on the server by hand.
    /// </summary>
    public class ToolManager
    {
        private static readonly HttpClient Http = CreateHttpClient();

        private readonly ILogger _logger;
        private readonly string _toolsDir;
        private readonly string _ffmpegFromEmby;

        public ToolManager(ILogger logger, string dataDir, string ffmpegFromEmby)
        {
            _logger = logger;
            _toolsDir = Path.Combine(dataDir, "tools");
            _ffmpegFromEmby = ffmpegFromEmby;
            Directory.CreateDirectory(_toolsDir);
        }

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        private static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        private static bool IsMusl =>
            IsLinux && Directory.Exists("/lib") && Directory.GetFiles("/lib", "ld-musl-*").Length > 0;

        private string ManagedYtDlpPath => Path.Combine(_toolsDir, IsWindows ? "yt-dlp.exe" : "yt-dlp");

        private string ManagedDenoPath => Path.Combine(_toolsDir, IsWindows ? "deno.exe" : "deno");

        public string YtDlpPath
        {
            get
            {
                var overridePath = Plugin.Instance.Configuration.YtDlpPathOverride;
                return string.IsNullOrWhiteSpace(overridePath) ? ManagedYtDlpPath : overridePath.Trim();
            }
        }

        public string DenoPath =>
            Plugin.Instance.Configuration.ManageDeno && File.Exists(ManagedDenoPath) ? ManagedDenoPath : null;

        public string FfmpegPath
        {
            get
            {
                var overridePath = Plugin.Instance.Configuration.FfmpegPathOverride;
                if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath.Trim();
                if (!string.IsNullOrWhiteSpace(_ffmpegFromEmby) && File.Exists(_ffmpegFromEmby)) return _ffmpegFromEmby;

                var bundled = Path.Combine(AppContext.BaseDirectory, IsWindows ? "ffmpeg.exe" : "ffmpeg");
                return File.Exists(bundled) ? bundled : null; // null => let yt-dlp look on PATH
            }
        }

        /// <summary>Installs anything missing. Cheap when everything is already there.</summary>
        public async Task EnsureInstalledAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(Plugin.Instance.Configuration.YtDlpPathOverride) && !File.Exists(ManagedYtDlpPath))
            {
                await UpdateYtDlpAsync(cancellationToken).ConfigureAwait(false);
            }

            if (Plugin.Instance.Configuration.ManageDeno && !File.Exists(ManagedDenoPath))
            {
                await UpdateDenoAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task UpdateAllAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(Plugin.Instance.Configuration.YtDlpPathOverride))
            {
                await UpdateYtDlpAsync(cancellationToken).ConfigureAwait(false);
            }

            if (Plugin.Instance.Configuration.ManageDeno)
            {
                await UpdateDenoAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task UpdateYtDlpAsync(CancellationToken cancellationToken)
        {
            var asset = GetYtDlpAssetName();
            if (asset == null)
            {
                _logger.Error("No prebuilt yt-dlp is available for this platform ({0} {1}). Set 'yt-dlp path' in the plugin settings.",
                    RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture);
                return;
            }

            var repo = string.Equals(Plugin.Instance.Configuration.YtDlpChannel, "stable", StringComparison.OrdinalIgnoreCase)
                ? "yt-dlp/yt-dlp"
                : "yt-dlp/yt-dlp-nightly-builds";
            var url = $"https://github.com/{repo}/releases/latest/download/{asset}";

            var tempPath = ManagedYtDlpPath + ".new";
            await DownloadFileAsync(url, tempPath, cancellationToken).ConfigureAwait(false);
            await MakeExecutableAsync(tempPath, cancellationToken).ConfigureAwait(false);

            var version = await GetVersionAsync(tempPath, "--version", cancellationToken).ConfigureAwait(false);
            if (version == null)
            {
                File.Delete(tempPath);
                throw new InvalidOperationException("Downloaded yt-dlp binary (" + asset + ") failed to run on this server.");
            }

            ReplaceFile(tempPath, ManagedYtDlpPath);
            _logger.Info("yt-dlp {0} installed from {1}", version, repo);
        }

        public async Task UpdateDenoAsync(CancellationToken cancellationToken)
        {
            var asset = GetDenoAssetName();
            if (asset == null)
            {
                _logger.Warn("No prebuilt Deno is available for this platform; YouTube downloads may be limited. " +
                             "Install deno on the server PATH if you see 'JavaScript runtime' errors.");
                return;
            }

            var url = "https://github.com/denoland/deno/releases/latest/download/" + asset;
            var zipPath = Path.Combine(_toolsDir, asset);
            var extractDir = Path.Combine(_toolsDir, "deno-extract");

            await DownloadFileAsync(url, zipPath, cancellationToken).ConfigureAwait(false);
            try
            {
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                ZipFile.ExtractToDirectory(zipPath, extractDir);
                var exe = Directory.GetFiles(extractDir, IsWindows ? "deno.exe" : "deno", SearchOption.AllDirectories).FirstOrDefault()
                          ?? throw new InvalidOperationException("Deno archive did not contain the deno binary.");

                await MakeExecutableAsync(exe, cancellationToken).ConfigureAwait(false);
                var version = await GetVersionAsync(exe, "--version", cancellationToken).ConfigureAwait(false);
                if (version == null)
                {
                    _logger.Warn("Downloaded Deno failed to run on this server; continuing without it.");
                    return;
                }

                ReplaceFile(exe, ManagedDenoPath);
                _logger.Info("Deno installed: {0}", version.Split('\n')[0].Trim());
            }
            finally
            {
                TryDelete(zipPath);
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            }
        }

        private static string GetYtDlpAssetName()
        {
            var arch = RuntimeInformation.OSArchitecture;
            if (IsWindows)
            {
                switch (arch)
                {
                    case Architecture.X64: return "yt-dlp.exe";
                    case Architecture.X86: return "yt-dlp_x86.exe";
                    case Architecture.Arm64: return "yt-dlp_arm64.exe";
                    default: return null;
                }
            }

            if (IsMac) return "yt-dlp_macos";

            if (IsLinux)
            {
                var musl = IsMusl;
                switch (arch)
                {
                    case Architecture.X64: return musl ? "yt-dlp_musllinux" : "yt-dlp_linux";
                    case Architecture.Arm64: return musl ? "yt-dlp_musllinux_aarch64" : "yt-dlp_linux_aarch64";
                    case Architecture.Arm: return "yt-dlp_linux_armv7l";
                    default: return null;
                }
            }

            return null;
        }

        private static string GetDenoAssetName()
        {
            var arch = RuntimeInformation.OSArchitecture;
            if (IsWindows && arch == Architecture.X64) return "deno-x86_64-pc-windows-msvc.zip";
            if (IsMac) return arch == Architecture.Arm64 ? "deno-aarch64-apple-darwin.zip" : "deno-x86_64-apple-darwin.zip";
            if (IsLinux && !IsMusl)
            {
                if (arch == Architecture.X64) return "deno-x86_64-unknown-linux-gnu.zip";
                if (arch == Architecture.Arm64) return "deno-aarch64-unknown-linux-gnu.zip";
            }

            return null;
        }

        private async Task DownloadFileAsync(string url, string destination, CancellationToken cancellationToken)
        {
            _logger.Info("Downloading {0}", url);
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task<string> GetVersionAsync(string exe, string arg, CancellationToken cancellationToken)
        {
            try
            {
                var result = await ProcessRunner.RunAsync(exe, new[] { arg }, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
                return result.ExitCode == 0 ? result.StdOut.Trim() : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Failed to run {0}", ex, exe);
                return null;
            }
        }

        private static async Task MakeExecutableAsync(string path, CancellationToken cancellationToken)
        {
            if (IsWindows) return;
            await ProcessRunner.RunAsync("chmod", new[] { "+x", path }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }

        private static void ReplaceFile(string source, string destination)
        {
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(source, destination);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // ignore
            }
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Emby-TrailerDownloader/3.0");
            return client;
        }
    }
}

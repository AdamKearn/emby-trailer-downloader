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
        private readonly Func<string> _ffmpegFromEmby;
        private readonly Func<string> _ffprobeFromEmby;
        private string _lastLoggedFfmpeg;

        public ToolManager(ILogger logger, string dataDir, Func<string> ffmpegFromEmby, Func<string> ffprobeFromEmby)
        {
            _logger = logger;
            _toolsDir = Path.Combine(dataDir, "tools");
            _ffmpegFromEmby = ffmpegFromEmby;
            _ffprobeFromEmby = ffprobeFromEmby;
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

                var fromEmby = _ffmpegFromEmby?.Invoke();
                if (!string.IsNullOrWhiteSpace(fromEmby) && File.Exists(fromEmby)) return fromEmby;

                // Emby's own layouts: ffmpeg next to the server (Windows), or <app>/system/EmbyServer with
                // <app>/bin/ffmpeg (Linux packages and Docker images, e.g. /app/emby/bin/ffmpeg).
                var exe = IsWindows ? "ffmpeg.exe" : "ffmpeg";
                var serverDir = AppContext.BaseDirectory;
                var candidates = new[]
                {
                    Path.Combine(serverDir, exe),
                    Path.GetFullPath(Path.Combine(serverDir, "..", "bin", exe))
                };

                return candidates.FirstOrDefault(File.Exists); // null => no ffmpeg
            }
        }

        private string FfprobePath(string ffmpeg)
        {
            if (UsingEmbyFfmpeg)
            {
                var fromEmby = _ffprobeFromEmby?.Invoke();
                if (!string.IsNullOrWhiteSpace(fromEmby) && File.Exists(fromEmby)) return fromEmby;
            }

            var sibling = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? string.Empty, IsWindows ? "ffprobe.exe" : "ffprobe");
            return File.Exists(sibling) ? sibling : null;
        }

        private bool UsingEmbyFfmpeg => string.IsNullOrWhiteSpace(Plugin.Instance.Configuration.FfmpegPathOverride);

        /// <summary>
        /// The value to pass to yt-dlp's --ffmpeg-location, or null if no ffmpeg was found.
        /// Emby's Linux ffmpeg (e.g. /app/emby/bin/ffmpeg) uses a *relative* program interpreter
        /// ("lib/ld-linux-x86-64.so.2"), so it only starts when the working directory is Emby's app folder -
        /// Emby's own emby-ffmpeg launcher does "cd $APP_DIR" for this reason. yt-dlp is also started without
        /// Emby's LD_LIBRARY_PATH (see ProcessRunner). So we point yt-dlp at a folder of tiny wrapper scripts that
        /// recreate Emby's launch environment for ffmpeg/ffprobe only.
        /// </summary>
        public async Task<string> GetFfmpegLocationForYtDlpAsync(CancellationToken cancellationToken)
        {
            var ffmpeg = FfmpegPath;
            if (ffmpeg != _lastLoggedFfmpeg)
            {
                _lastLoggedFfmpeg = ffmpeg;
                if (ffmpeg == null)
                {
                    _logger.Warn("ffmpeg not found - trailers are limited to formats that already include audio (often only 360p). " +
                                 "Set 'ffmpeg path' in the plugin settings to fix this.");
                }
                else
                {
                    _logger.Info("Using ffmpeg: {0}", ffmpeg);
                }
            }

            if (ffmpeg == null) return null;

            if (IsWindows || !UsingEmbyFfmpeg || !File.Exists("/bin/sh"))
            {
                return ffmpeg;
            }

            var libraryPath = ProcessRunner.InheritedLibraryPath;

            try
            {
                var wrapperDir = Path.Combine(_toolsDir, "ffmpeg-wrapper");
                Directory.CreateDirectory(wrapperDir);
                await WriteWrapperAsync(Path.Combine(wrapperDir, "ffmpeg"), ffmpeg, libraryPath, cancellationToken).ConfigureAwait(false);

                var ffprobe = FfprobePath(ffmpeg);
                if (ffprobe != null)
                {
                    await WriteWrapperAsync(Path.Combine(wrapperDir, "ffprobe"), ffprobe, libraryPath, cancellationToken).ConfigureAwait(false);
                }

                return wrapperDir;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Could not create the ffmpeg wrapper; passing Emby's ffmpeg to yt-dlp directly", ex);
                return ffmpeg;
            }
        }

        private async Task WriteWrapperAsync(string wrapperPath, string target, string libraryPath, CancellationToken cancellationToken)
        {
            string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

            // Emby's layout is <app>/bin/ffmpeg + <app>/lib/..., and ffmpeg must run from <app>.
            var appDir = Path.GetDirectoryName(Path.GetDirectoryName(target));
            var script = "#!/bin/sh\n" +
                         "# Generated by the Trailer Downloader plugin: runs Emby's ffmpeg the way Emby itself does.\n" +
                         (string.IsNullOrEmpty(appDir) ? string.Empty : "cd " + Quote(appDir) + " || exit 1\n") +
                         (string.IsNullOrEmpty(libraryPath) ? string.Empty : "export LD_LIBRARY_PATH=" + Quote(libraryPath) + "\n") +
                         "exec " + Quote(target) + " \"$@\"\n";

            if (!File.Exists(wrapperPath) || File.ReadAllText(wrapperPath) != script)
            {
                File.WriteAllText(wrapperPath, script);
            }

            await MakeExecutableAsync(wrapperPath, cancellationToken).ConfigureAwait(false);
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
                if (result.ExitCode == 0) return result.StdOut.Trim();

                _logger.Error("{0} {1} failed: {2}. Output: {3}", exe, arg, DescribeExitCode(result.ExitCode), (result.StdErr ?? string.Empty).Trim());
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorException(
                    "Failed to run {0}. If this says access/permission denied, the file could not be made executable " +
                    "or its folder is on a filesystem mounted with 'noexec'.", ex, exe);
                return null;
            }
        }

        /// <summary>Turns an exit code into something readable, e.g. 139 => "crashed (signal 11, segmentation fault)".</summary>
        public static string DescribeExitCode(int exitCode)
        {
            if (IsWindows || exitCode <= 128 || exitCode > 128 + 64) return "exit code " + exitCode;

            var signal = exitCode - 128;
            string name;
            switch (signal)
            {
                case 4: name = "illegal instruction - binary not built for this CPU"; break;
                case 6: name = "aborted"; break;
                case 9: name = "killed - possibly out of memory"; break;
                case 11: name = "segmentation fault"; break;
                default: name = "signal"; break;
            }

            return $"crashed (exit code {exitCode}, signal {signal}: {name})";
        }

        // chmod via .NET's own native shim (shipped with every .NET Core runtime, works on glibc, musl and macOS).
        // Doing it in-process avoids depending on a 'chmod' binary that may be missing or broken in the container.
        [DllImport("libSystem.Native", EntryPoint = "SystemNative_ChMod", SetLastError = true)]
        private static extern int SystemNativeChMod(string path, int mode);

        private const int Mode0755 = 0x1ED; // rwxr-xr-x

        private async Task MakeExecutableAsync(string path, CancellationToken cancellationToken)
        {
            if (IsWindows) return;

            try
            {
                if (SystemNativeChMod(path, Mode0755) == 0) return;
                _logger.Warn("chmod 755 {0} failed (errno {1}); trying /bin/chmod", path, Marshal.GetLastWin32Error());
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                _logger.Debug("Native chmod unavailable ({0}); using the chmod command", ex.Message);
            }

            var chmod = File.Exists("/bin/chmod") ? "/bin/chmod" : "chmod";
            var result = await ProcessRunner.RunAsync(chmod, new[] { "755", path }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Could not make {path} executable: chmod {DescribeExitCode(result.ExitCode)} {result.StdErr?.Trim()}");
            }
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

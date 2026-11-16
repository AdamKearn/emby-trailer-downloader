using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.TrailerDownloader.Services
{
    public sealed class ProcessResult
    {
        public int ExitCode { get; set; }
        public string StdOut { get; set; }
        public string StdErr { get; set; }
        public bool TimedOut { get; set; }
    }

    public static class ProcessRunner
    {
        public static async Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> args,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = BuildArguments(args),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
                process.Exited += (_, __) => exited.TrySetResult(true);

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var timedOut = false;
                using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeoutCts.CancelAfter(timeout);
                    var cancelled = new TaskCompletionSource<bool>();
                    using (timeoutCts.Token.Register(() => cancelled.TrySetResult(true)))
                    {
                        var finished = await Task.WhenAny(exited.Task, cancelled.Task).ConfigureAwait(false);
                        if (finished != exited.Task)
                        {
                            timedOut = !cancellationToken.IsCancellationRequested;
                            TryKill(process);
                        }
                    }
                }

                // Let the async readers drain.
                process.WaitForExit(5000);
                cancellationToken.ThrowIfCancellationRequested();

                return new ProcessResult
                {
                    ExitCode = process.HasExited ? process.ExitCode : -1,
                    StdOut = stdout.ToString(),
                    StdErr = stderr.ToString(),
                    TimedOut = timedOut
                };
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
                // Process already gone.
            }
        }

        /// <summary>
        /// Quotes arguments using the MSVC rules, which .NET also uses to split the string on Linux/macOS.
        /// </summary>
        public static string BuildArguments(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            foreach (var arg in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                AppendQuoted(sb, arg ?? string.Empty);
            }

            return sb.ToString();
        }

        private static void AppendQuoted(StringBuilder sb, string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                sb.Append(arg);
                return;
            }

            sb.Append('"');
            for (var i = 0; i < arg.Length; i++)
            {
                var backslashes = 0;
                while (i < arg.Length && arg[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i == arg.Length)
                {
                    sb.Append('\\', backslashes * 2);
                    break;
                }

                if (arg[i] == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(arg[i]);
                }
            }

            sb.Append('"');
        }

        /// <summary>Splits a user-supplied argument string, honouring double quotes.</summary>
        public static List<string> SplitArguments(string commandLine)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(commandLine)) return result;

            var current = new StringBuilder();
            var inQuotes = false;
            var hasToken = false;
            foreach (var c in commandLine)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    hasToken = true;
                }
                else if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    if (hasToken) result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                else
                {
                    current.Append(c);
                    hasToken = true;
                }
            }

            if (hasToken) result.Add(current.ToString());
            return result;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace RvtMcp.Plugin.Views.DrawingReview
{
    internal sealed record PlanAgentJob(int Page, DrawingRole Role, PlanAgentExchange Exchange);
    internal sealed record PlanAgentOutcome(int Page, DrawingMark[] Marks, string Error);

    internal static class PlanAgentRunner
    {
        private const string AgentResource = "RvtMcp.DrawingReview.Agent.py";

        public static async Task<DrawingMark[]> AnalyzeAsync(PlanAgentExchange exchange, IReadOnlyList<byte[]> references,
            IProgress<string> progress, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            string python = FindPython() ?? throw new InvalidOperationException("Python is required. Install Python 3.10 or later and reopen Revit.");
            var work = Directory.CreateTempSubdirectory("rvtmcp-plan-agent-");
            try
            {
                string script = Path.Combine(work.FullName, "drawing-review-agent.py");
                await using (var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream(AgentResource)
                    ?? throw new InvalidOperationException("The drawing agent is missing from this build. Rebuild the Revit 2027 add-in."))
                await using (var file = File.Create(script))
                    await embedded.CopyToAsync(file, cancellation);

                string input = Path.Combine(work.FullName, "plan.zip");
                string output = Path.Combine(work.FullName, "proposals.json");
                exchange.Export(input);
                var info = new ProcessStartInfo(python)
                {
                    WorkingDirectory = work.FullName, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                foreach (var value in new[] { "-u", script, input, "-o", output })
                    info.ArgumentList.Add(value);
                for (int i = 0; i < references.Count; i++)
                {
                    string path = Path.Combine(work.FullName, $"reference-{i + 1}.png");
                    await File.WriteAllBytesAsync(path, references[i], cancellation);
                    info.ArgumentList.Add("--reference");
                    info.ArgumentList.Add(path);
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromMinutes(8));
                timeout.Token.ThrowIfCancellationRequested();
                using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the drawing agent.");
                var stdout = ReportAsync(process.StandardOutput, progress);
                var stderr = process.StandardError.ReadToEndAsync();
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                    await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token);
                    var error = await stderr;
                    timeout.Token.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException(UserError(error));
                    return exchange.Import(output);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
                    {
                        DiagnosticLog.Write("DrawingReview", "Could not confirm termination of the drawing agent process.");
                    }
                    if (!cancellation.IsCancellationRequested)
                        throw new InvalidOperationException("The Codex analysis timed out. Check the connection and try again.");
                    throw;
                }
            }
            finally
            {
                try { work.Delete(recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLog.Write("DrawingReview", "Could not remove a temporary drawing agent directory.");
                }
            }
        }

        private static async Task ReportAsync(StreamReader reader, IProgress<string> progress)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("Finding the plan region", StringComparison.Ordinal))
                    progress.Report("Finding the plan region…");
                if (line.StartsWith("Finding the drawing region", StringComparison.Ordinal))
                    progress.Report("Finding the drawing region and orientation…");
                if (line.StartsWith("Identifying visible walls", StringComparison.Ordinal))
                    progress.Report("Identifying visible walls, doors and windows…");
                if (line.StartsWith("Reading reference heights", StringComparison.Ordinal))
                    progress.Report("Reading heights, openings and construction notes…");
            }
        }

        private static string UserError(string stderr)
        {
            if (stderr.Contains("No module named 'PIL'", StringComparison.OrdinalIgnoreCase))
                return "Install Pillow once with py -m pip install \"Pillow>=10.1\" and try again.";
            var detail = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            return string.IsNullOrEmpty(detail)
                ? "The agent could not analyze the plan. Check that Codex CLI is installed and signed in."
                : detail[..Math.Min(detail.Length, 430)];
        }

        private static string FindPython()
        {
            var userPrograms = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Append(Path.Combine(userPrograms, "Programs", "Python", "Launcher"))
                .Append(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
                .Select(dir => Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"')));
            foreach (var name in new[] { "py.exe", "python.exe" })
                foreach (string dir in directories.Where(Path.IsPathFullyQualified))
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }
            return null;
        }
    }
}

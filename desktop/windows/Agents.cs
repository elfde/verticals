// Running a command-line tool (claude, codex, py) the way a terminal would find it, without a window.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Verticals
{
    static class Agents
    {
        /// The tool's full path from PATH and PATHEXT, as cmd finds it: claude.exe, or an npm shim like codex.cmd.
        public static string Which(string tool, IDictionary<string, string> env = null)
        {
            string path = null;
            if (env != null) env.TryGetValue("PATH", out path);
            path = path ?? Environment.GetEnvironmentVariable("PATH") ?? "";
            var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';');
            foreach (var dir in path.Split(';').Where(d => d.Trim().Length > 0))
                foreach (var ext in extensions)
                {
                    try
                    {
                        var candidate = Path.Combine(dir.Trim().Trim('"'), tool + ext.ToLowerInvariant());
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch (ArgumentException) { }
                }
            return null;
        }

        /// Exit code and output together; 127 when the tool is not installed, as a shell says.
        public static (int, string) Run(string tool, string[] args, IDictionary<string, string> env, int timeoutMs = 60000)
        {
            var exe = Which(tool, env);
            if (exe == null) return (127, "");
            var line = string.Join(" ", args.Select(AppLinks.Quote));
            ProcessStartInfo start;
            if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                // A batch file runs through cmd; /s keeps the whole quoted line as it is.
                start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                                             "/d /s /c \"" + AppLinks.Quote(exe) + " " + line + "\"");
            else
                start = new ProcessStartInfo(exe, line);
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = start.RedirectStandardError = true;
            start.StandardOutputEncoding = start.StandardErrorEncoding = Encoding.UTF8;
            if (env != null) foreach (var pair in env) start.EnvironmentVariables[pair.Key] = pair.Value;
            try
            {
                using (var process = Process.Start(start))
                {
                    var output = new StringBuilder();
                    process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                    process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch (Exception) { }
                        return (1, $"{tool} did not answer in {timeoutMs / 1000} seconds.");
                    }
                    process.WaitForExit();
                    lock (output) return (process.ExitCode, output.ToString().Trim());
                }
            }
            catch (Exception e) { return (127, e.Message); }
        }
    }
}

// The services behind the window: desktop/launcher.py (PostgreSQL, the API, MCP, the UI gateway and the agent chat), started
// with Python unless Verticals is already running, asked whether it is healthy, and stopped the way Ctrl-C stops it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Verticals
{
    sealed class Backend
    {
        public readonly string Root;        // the repository, or the app's folder that mirrors it
        public readonly string StateDir;
        public readonly int UIPort;
        public Uri UI => new Uri($"http://127.0.0.1:{UIPort}/");
        public string MCPURL => $"http://127.0.0.1:{MCPPort}/mcp";
        public string LogPath => Path.Combine(StateDir, "app.log");
        public string HeadersFile => Path.Combine(StateDir, "mcp-headers.json");
        public string Python { get; private set; }
        public Process Process { get; private set; }
        public bool Running => Process != null && !Process.HasExited;
        /// An installed app: Python and PostgreSQL ship beside the exe. A build in the repository has neither.
        public bool Bundled => Root != null && File.Exists(Path.Combine(Root, "python", "python.exe")) && Directory.Exists(Path.Combine(Root, "site"));
        public event Action<int> Exited;
        readonly int MCPPort;
        StreamWriter log;

        public Backend(string stateDir)
        {
            Root = FindRoot(AppDomain.CurrentDomain.BaseDirectory);
            StateDir = stateDir;
            UIPort = Port("UI", 8288);
            MCPPort = Port("MCP", 8281);
        }

        // The launcher's own variables move its ports; the window follows the same ones.
        static int Port(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable($"VERTICALS_DESKTOP_{name}_PORT"), out var port) ? port : fallback;

        // The exe is built into desktop/dist/windows; the folder above it that holds desktop/launcher.py is the root.
        static string FindRoot(string start)
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "desktop", "launcher.py"))) return dir.FullName;
            return null;
        }

        // ---------------------------------------------------------------- health

        public Task<bool> Healthy() => Task.Run(() =>
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(new Uri(UI, "healthz"));
                request.Timeout = request.ReadWriteTimeout = 1000;
                request.Proxy = null;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                    return response.StatusCode == HttpStatusCode.OK && reader.ReadToEnd().Contains("\"db\":\"ok\"");
            }
            catch (Exception) { return false; }
        });

        // ---------------------------------------------------------------- start

        /// Null when it started, else what went wrong, for the window to say.
        public string Start(IDictionary<string, string> agentEnvironment)
        {
            if (Root == null) return "This build is incomplete: desktop\\launcher.py is not in any folder above Verticals.exe.";
            Python = FindPython();
            if (Python == null)
                return "Python 3.12 is required: install it (py -3.12 must work), or set VERTICALS_PYTHON.";
            var start = new ProcessStartInfo(Python, string.Join(" ", new[] { "-X", "utf8", "-u", Path.Combine(Root, "desktop", "launcher.py") }.Select(AppLinks.Quote)))
            {
                WorkingDirectory = Root,
                UseShellExecute = false,
                // A console of its own, never shown: Stop sends its Ctrl-C there.
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var pair in agentEnvironment) start.EnvironmentVariables[pair.Key] = pair.Value;
            start.EnvironmentVariables["VERTICALS_DESKTOP_STATE"] = StateDir;
            if (Bundled)
            {
                // The installed app carries Python with every dependency and PostgreSQL (desktop/windows/bundle.py), as
                // Verticals.app does: no virtual environment to build, and nothing written into the app's folder.
                start.EnvironmentVariables["VERTICALS_DESKTOP_PYTHONPATH"] = Root + ";" + Path.Combine(Root, "site");
                var bindir = Path.Combine(Root, "pg", "BINDIR");
                start.EnvironmentVariables["VERTICALS_PG_BIN"] = File.Exists(bindir) ? Path.Combine(Root, "pg", File.ReadAllText(bindir).Trim()) : "";
                start.EnvironmentVariables["PYTHONDONTWRITEBYTECODE"] = "1";
                start.EnvironmentVariables["PYTHONNOUSERSITE"] = "1";
            }
            Directory.CreateDirectory(StateDir);
            log = new StreamWriter(new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => Write(e.Data);
            process.ErrorDataReceived += (_, e) => Write(e.Data);
            process.Exited += (_, __) => Exited?.Invoke(process.ExitCode);
            try { process.Start(); }
            catch (Exception e) { return "Could not start Verticals: " + e.Message; }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Process = process;
            return null;
        }

        void Write(string line)
        {
            if (line == null) return;
            lock (this) { try { log?.WriteLine(line); } catch (Exception) { } }
        }

        // The app's own Python once one ships beside it, then VERTICALS_PYTHON, the repository's .venv, and the py launcher's 3.12.
        public string FindPython()
        {
            foreach (var candidate in new[] {
                Path.Combine(Root, "python", "python.exe"),
                Environment.GetEnvironmentVariable("VERTICALS_PYTHON"),
                Path.Combine(Root, ".venv", "Scripts", "python.exe"),
            })
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate)) return candidate;
            var (code, output) = Agents.Run("py", new[] { "-3.12", "-c", "import sys; print(sys.executable)" }, null, 15000);
            return code == 0 && File.Exists(output.Trim()) ? output.Trim() : null;
        }

        // ---------------------------------------------------------------- stop

        /// Ctrl-C in the launcher's console stops it as it stops in a terminal: the services end, PostgreSQL shuts down cleanly.
        /// What is still running after `wait` is ended by force; a database left behind is reused by the next start.
        public void Stop(TimeSpan wait)
        {
            var process = Process;
            if (process == null || process.HasExited) return;
            if (SendCtrlC(process.Id) && process.WaitForExit((int)wait.TotalMilliseconds)) return;
            Kill(process.Id);
            process.WaitForExit(5000);
        }

        static bool SendCtrlC(int pid)
        {
            lock (typeof(Backend))
            {
                FreeConsole();
                if (!AttachConsole((uint)pid)) return false;
                // The event goes to every process on that console, this one included while attached: ignore it here.
                SetConsoleCtrlHandler(IntPtr.Zero, true);
                var sent = GenerateConsoleCtrlEvent(0 /* CTRL_C_EVENT */, 0);
                Thread.Sleep(100);
                FreeConsole();
                return sent;
            }
        }

        static void Kill(int pid)
        {
            try
            {
                using (var taskkill = Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {pid}") { CreateNoWindow = true, UseShellExecute = false }))
                    taskkill?.WaitForExit(10000);
            }
            catch (Exception) { }
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeConsole();
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint group);
    }
}

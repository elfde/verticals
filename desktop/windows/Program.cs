// Verticals.exe: a native window (WebView2) around the desktop launcher, the Windows counterpart of desktop/macos/Verticals.swift.
// On launch it starts desktop/launcher.py with Python (PostgreSQL, API, MCP, UI gateway) unless Verticals is already running,
// waits until it is healthy, and shows the UI. Closing the window keeps everything running behind the tray icon, so agents
// keep the board and MCP; Quit stops what it started. One copy runs at a time: a second start, or a `verticals://goal/<id>`
// link opened anywhere, hands its request to the running one.
//
//   Verticals.exe                       open the window
//   Verticals.exe --hidden              tray only (what Open at Login starts)
//   Verticals.exe verticals://goal/<id> open that task
//   Verticals.exe --quit                quit the running copy as its Quit does, and return once it has stopped
//   Verticals.exe --state-dir <path>    keep the data elsewhere (tests), else VERTICALS_DESKTOP_STATE, else %LOCALAPPDATA%\Verticals
using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Verticals
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            var stateDir = Option(args, "--state-dir")
                ?? Environment.GetEnvironmentVariable("VERTICALS_DESKTOP_STATE")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Verticals");
            stateDir = Path.GetFullPath(stateDir);
            var link = args.FirstOrDefault(a => a.StartsWith("verticals:", StringComparison.OrdinalIgnoreCase));
            var quit = args.Contains("--quit");
            var request = quit ? "quit" : link ?? "show";

            // One copy per state folder: a second start hands its request over and ends.
            var name = "Verticals.Desktop." + Hash(stateDir.ToLowerInvariant());
            using (var mutex = new Mutex(true, @"Local\" + name, out var first))
            {
                if (!first)
                {
                    AllowSetForegroundWindow(-1);  // the running copy may bring its window forward
                    Forward(name, request);
                    // --quit returns once the running copy has stopped everything and let go of the folder.
                    if (quit) try { mutex.WaitOne(TimeSpan.FromSeconds(60)); } catch (AbandonedMutexException) { }
                    return;
                }
                if (quit) return;  // nothing to quit
                SetCurrentProcessExplicitAppUserModelID("Verticals.Desktop");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var window = new MainWindow(stateDir, startHidden: args.Contains("--hidden"), link: link);
                Listen(name, window);
                Application.Run(new ApplicationContext());
                mutex.ReleaseMutex();
            }
        }

        static string Option(string[] args, string name)
        {
            var at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        }

        static string Hash(string text)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Take(8).Select(b => b.ToString("x2")));
        }

        static void Forward(string name, string request)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out))
                {
                    pipe.Connect(3000);
                    var bytes = Encoding.UTF8.GetBytes(request);
                    pipe.Write(bytes, 0, bytes.Length);
                }
            }
            catch (Exception) { }  // the running copy is busy quitting
        }

        static void Listen(string name, MainWindow window)
        {
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1))
                        {
                            pipe.WaitForConnection();
                            using (var reader = new StreamReader(pipe, Encoding.UTF8))
                            {
                                var request = reader.ReadToEnd();
                                if (request.Length > 4096) continue;
                                window.BeginInvoke(new Action(() => window.HandleRequest(request)));
                            }
                        }
                    }
                    catch (Exception) { Thread.Sleep(200); }
                }
            }) { IsBackground = true };
            thread.Start();
        }

        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    }

    /// What the app remembers between launches, in HKCU\Software\Verticals (macOS keeps the same in its defaults).
    static class Settings
    {
        const string Key = @"Software\Verticals";

        public static object Get(string name)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(Key)) return key?.GetValue(name);
        }

        public static void Set(string name, object value)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(Key))
            {
                if (value == null) key.DeleteValue(name, false);
                else key.SetValue(name, value is bool b ? (b ? 1 : 0) : value);
            }
        }

        public static bool Flag(string name) => Get(name) is int i && i != 0;
    }

    /// The `verticals:` links and Open at Login live in the user's part of the registry: no admin rights, nothing system-wide.
    static class Shell
    {
        static string Exe => Application.ExecutablePath;
        const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        /// `verticals://goal/<id>` opens this exe, which hands it to the running copy. Written at each start, so the copy
        /// that ran last is the one links open.
        public static void RegisterLinks()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\verticals"))
                {
                    key.SetValue("", "URL:Verticals link");
                    key.SetValue("URL Protocol", "");
                    using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{Exe}\",0");
                    using (var command = key.CreateSubKey(@"shell\open\command")) command.SetValue("", $"\"{Exe}\" \"%1\"");
                }
            }
            catch (Exception) { }
        }

        public static bool OpensAtLogin
        {
            get { using (var key = Registry.CurrentUser.OpenSubKey(Run)) return key?.GetValue("Verticals") is string; }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(Run))
                {
                    if (value) key.SetValue("Verticals", $"\"{Exe}\" --hidden");
                    else key.DeleteValue("Verticals", false);
                }
            }
        }

        /// Task Manager's Startup tab can switch the login item off without removing it (an odd first byte).
        public static bool LoginTurnedOffInTaskManager
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(Approved))
                    return key?.GetValue("Verticals") is byte[] data && data.Length > 0 && (data[0] & 1) == 1;
            }
        }

        public static void Open(string target)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception) { }
        }
    }
}

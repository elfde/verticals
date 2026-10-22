// The window with the board (WebView2) and the tray icon with its menu: what desktop/macos/Verticals.swift does on a Mac,
// done the way Windows does it. The window and the menu say Verticals, as the macOS app still does.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace Verticals
{
    sealed class MainWindow : Form
    {
        const string WebView2Download = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        readonly Backend backend;
        readonly bool startHidden;
        readonly WebView2 web;
        readonly Label status;
        readonly NotifyIcon tray;
        bool serving, quitting, loading, webReady, showsLog;
        string statusLink;
        // A task's link that came while the board was starting is where it opens.
        string pendingTask;
        Icon trayIcon;

        public MainWindow(string stateDir, bool startHidden, string link)
        {
            backend = new Backend(stateDir);
            this.startHidden = startHidden;
            pendingTask = AppLinks.TaskID(link);

            Text = "Verticals";
            Icon = AppIcon();
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;

            status = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 11f),
                Padding = new Padding(24),
                Text = "Starting Verticals…",
            };
            status.Click += (_, __) =>
            {
                if (statusLink != null) Shell.Open(statusLink);
                else if (showsLog && File.Exists(backend.LogPath)) Shell.Open(backend.LogPath);
            };
            web = new WebView2 { Dock = DockStyle.Fill, Visible = false };
            Controls.Add(web);
            Controls.Add(status);

            tray = new NotifyIcon { Text = "Verticals", ContextMenu = new ContextMenu() };
            tray.ContextMenu.Popup += (_, __) => BuildMenu(tray.ContextMenu);
            tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindowFront(); };

            CreateHandle();
            ApplyTheme();
            PlaceWindow();
            SystemEvents.UserPreferenceChanged += ThemeChanged;
            tray.Visible = true;
            Shell.RegisterLinks();
            if (!startHidden) Show();
            Boot();
        }

        // ---------------------------------------------------------------- start

        async void Boot()
        {
            var webTask = StartWeb();
            if (await backend.Healthy()) ShowUI();
            else StartBackend();
            await webTask;
        }

        async Task StartWeb()
        {
            try { CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch (WebView2RuntimeNotFoundException)
            {
                // The board and MCP still start for the agents; only the window waits for WebView2.
                statusLink = WebView2Download;
                status.Cursor = Cursors.Hand;
                status.Text = "Verticals shows the board with Microsoft Edge WebView2, which is not on this computer.\n\n" +
                              "Click here to download it, install it, then open Verticals again.";
                return;
            }
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(backend.StateDir, "WebView2"));
                await web.EnsureCoreWebView2Async(environment);
            }
            catch (Exception e)
            {
                Fail("Could not open the board's window: " + e.Message);
                return;
            }
            var core = web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            if (double.TryParse(Settings.Get("PageZoom") as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var zoom)
                && zoom >= 0.25 && zoom <= 5)
                web.ZoomFactor = zoom;
            web.ZoomFactorChanged += (_, __) => Settings.Set("PageZoom", web.ZoomFactor.ToString("R", CultureInfo.InvariantCulture));
            core.NavigationStarting += NavigationStarting;
            core.NavigationCompleted += (_, __) => loading = false;
            core.NewWindowRequested += NewWindowRequested;
            core.LaunchingExternalUriScheme += (_, e) => { e.Cancel = true; OpenElsewhere(e.Uri); };
            core.ContextMenuRequested += ContextMenuRequested;
            core.ProcessFailed += (_, e) =>
            {
                if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited && serving) web.Reload();
            };
            webReady = true;
            if (serving) Navigate();
        }

        void StartBackend()
        {
            // Only a state folder without a database is a first launch; every other start just starts.
            var first = !Directory.Exists(Path.Combine(backend.StateDir, "postgres"));
            var error = backend.Start(AgentEnvironment());
            if (error != null) { Fail(error); return; }
            backend.Exited += code => BeginInvoke(new Action(() =>
            {
                if (!quitting) Fail($"Verticals stopped (exit {code}). See {backend.LogPath}.");
            }));
            SetStatus(first ? "Starting Verticals… (the first launch sets up the database, about a minute)" : "Starting Verticals…");
            WaitForBackend();
        }

        async void WaitForBackend()
        {
            // The first start from a repository also builds the UI and Python's packages: give it five minutes.
            for (var attempt = 0; attempt < 600; attempt++)
            {
                if (await backend.Healthy()) { ShowUI(); return; }
                if (!backend.Running) return;  // Exited says why
                await Task.Delay(500);
            }
            Fail($"Verticals did not start. See {backend.LogPath}.");
        }

        void ShowUI()
        {
            serving = true;
            showsLog = false;
            RefreshTrayIcon();
            if (!webReady) return;  // StartWeb shows it once WebView2 is up
            Navigate();
        }

        void Navigate()
        {
            status.Visible = false;
            web.Visible = true;
            var task = pendingTask != null ? AppLinks.BoardURL(pendingTask, backend.UI) : null;
            pendingTask = null;
            loading = true;
            web.CoreWebView2.Navigate((task ?? backend.UI).AbsoluteUri);
        }

        void Fail(string message)
        {
            serving = false;
            RefreshTrayIcon();
            showsLog = message.Contains(backend.LogPath);
            status.Cursor = showsLog ? Cursors.Hand : Cursors.Default;
            SetStatus(message);
        }

        void SetStatus(string message)
        {
            web.Visible = false;
            status.Visible = true;
            status.Text = message;
        }

        // ---------------------------------------------------------------- a task's link

        /// A request a second start handed over: "show", "quit", or a `verticals://goal/<id>` link.
        public void HandleRequest(string request)
        {
            if (request == "quit") { Quit(); return; }
            var id = AppLinks.TaskID(request);
            if (id != null) OpenTask(id); else ShowWindowFront();
        }

        void OpenTask(string id)
        {
            pendingTask = id;
            ShowWindowFront();
            if (!serving || !webReady) return;  // ShowUI opens it
            pendingTask = null;
            var current = web.Source;
            if (current != null && SameOrigin(current) && !loading)
                // On the board's own page only the part after `#` changes: the page opens the task, and Back returns.
                _ = web.CoreWebView2.ExecuteScriptAsync("location.hash = " + AppLinks.Json(AppLinks.Fragment(id)));
            else
                web.CoreWebView2.Navigate(AppLinks.BoardURL(id, backend.UI).AbsoluteUri);
        }

        bool SameOrigin(Uri uri) => uri.Host == backend.UI.Host && uri.Port == backend.UI.Port;

        // ---------------------------------------------------------------- the page

        void NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            var id = AppLinks.TaskID(e.Uri);
            if (id != null) { e.Cancel = true; OpenTask(id); return; }
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
            {
                var web = uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
                // A link that leaves the app opens in the default browser; a mail link in the mail app.
                if ((web && !SameOrigin(uri) && e.IsUserInitiated) || uri.Scheme == Uri.UriSchemeMailto)
                {
                    e.Cancel = true;
                    OpenElsewhere(e.Uri);
                    return;
                }
            }
            loading = true;
        }

        // target=_blank and window.open: the app has one window, so these open elsewhere too.
        void NewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            OpenElsewhere(e.Uri);
        }

        void OpenElsewhere(string link)
        {
            var id = AppLinks.TaskID(link);
            if (id != null) { OpenTask(id); return; }
            if (Uri.TryCreate(link, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto))
                Shell.Open(uri.AbsoluteUri);
        }

        // The page's own menu stays a text menu: what a browser adds about pages, printing and saving goes.
        static readonly HashSet<string> BrowserItems = new HashSet<string> {
            "back", "forward", "reload", "saveAs", "print", "createQrCode", "share", "webCapture", "inspectElement",
            "openLinkInNewWindow", "saveLinkAs", "saveImageAs", "copyImageLocation", "openImageInNewWindow",
        };

        void ContextMenuRequested(object sender, CoreWebView2ContextMenuRequestedEventArgs e)
        {
            var items = e.MenuItems;
            for (var i = items.Count - 1; i >= 0; i--)
                if (BrowserItems.Contains(items[i].Name)) items.RemoveAt(i);
            // Separators left at the ends or side by side.
            for (var i = items.Count - 1; i >= 0; i--)
            {
                var separator = items[i].Kind == CoreWebView2ContextMenuItemKind.Separator;
                if (separator && (i == 0 || i == items.Count - 1 || items[i - 1].Kind == CoreWebView2ContextMenuItemKind.Separator))
                    items.RemoveAt(i);
            }
            if (items.Count == 0) e.Handled = true;
        }

        // ---------------------------------------------------------------- window

        void ShowWindowFront()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) ShowWindow(Handle, 9 /* SW_RESTORE */);
            Activate();
            SetForegroundWindow(Handle);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (quitting) { base.OnFormClosing(e); return; }
            if (e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing)
            {
                // Signing out or shutting down: Windows gives a few seconds, so the database stops fast.
                quitting = true;
                SaveWindow();
                backend.Stop(TimeSpan.FromSeconds(5));
                tray.Visible = false;
                base.OnFormClosing(e);
                Application.Exit();
                return;
            }
            // Closing the window keeps Verticals running behind the tray icon, for the agents.
            e.Cancel = true;
            SaveWindow();
            Hide();
        }

        async void Quit()
        {
            if (quitting) return;
            quitting = true;
            SaveWindow();
            await SavePage();
            if (backend.Running)
            {
                // Ctrl-C makes the launcher stop the services and shut PostgreSQL down cleanly.
                if (Visible) SetStatus("Stopping Verticals…");
                tray.Text = "Stopping Verticals…";
                await Task.Run(() => backend.Stop(TimeSpan.FromSeconds(20)));
            }
            tray.Visible = false;
            tray.Dispose();
            SystemEvents.UserPreferenceChanged -= ThemeChanged;
            Application.Exit();
        }

        // Edits still waiting out their debounce reach the API before it stops (web/src/lib/beforeQuit.ts).
        async Task SavePage()
        {
            if (!webReady || !serving || !web.Visible) return;
            var save = web.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                "{\"expression\":\"window.verticalsBeforeQuit?.()\",\"awaitPromise\":true}");
            await Task.WhenAny(save, Task.Delay(3000));
        }

        // Where the window was, at the size it had; the first launch centres a 1320 x 860 window like the Mac's.
        void PlaceWindow()
        {
            MinimumSize = Scale(new Size(480, 400));
            var saved = (Settings.Get("WindowBounds") as string)?.Split(',').Select(s => int.TryParse(s, out var n) ? n : (int?)null).ToArray();
            if (saved != null && saved.Length == 4 && saved.All(n => n.HasValue))
            {
                var bounds = new Rectangle(saved[0].Value, saved[1].Value, saved[2].Value, saved[3].Value);
                if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
                {
                    Bounds = bounds;
                    if (Settings.Flag("WindowMaximized")) WindowState = FormWindowState.Maximized;
                    return;
                }
            }
            var area = Screen.PrimaryScreen.WorkingArea;
            var size = Scale(new Size(1320, 860));
            size = new Size(Math.Min(size.Width, area.Width), Math.Min(size.Height, area.Height));
            Bounds = new Rectangle(area.Left + (area.Width - size.Width) / 2, area.Top + (area.Height - size.Height) / 2, size.Width, size.Height);
        }

        void SaveWindow()
        {
            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            Settings.Set("WindowBounds", $"{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}");
            Settings.Set("WindowMaximized", WindowState == FormWindowState.Maximized);
        }

        Size Scale(Size size) => new Size(size.Width * DeviceDpi / 96, size.Height * DeviceDpi / 96);

        // ---------------------------------------------------------------- appearance

        // The page follows Windows' app mode by itself (prefers-color-scheme); the frame, the background before the page and
        // the tray icon follow it here: apps in Settings → Personalization → Colors, the taskbar in its own mode there.
        static bool AppsDark => Personalize("AppsUseLightTheme") == 0;
        static bool TaskbarDark => Personalize("SystemUsesLightTheme") == 0;

        static int Personalize(string name)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                return key?.GetValue(name) is int value ? value : 1;
        }

        void ThemeChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.VisualStyle)
                BeginInvoke(new Action(ApplyTheme));
        }

        void ApplyTheme()
        {
            var dark = AppsDark;
            // --color-bg in web/src/theme.css and the design system: the page's own background, so nothing flashes.
            var background = dark ? Color.FromArgb(0x1c, 0x1c, 0x1e) : Color.White;
            BackColor = status.BackColor = web.DefaultBackgroundColor = background;
            status.ForeColor = dark ? Color.FromArgb(0x9a, 0x9a, 0xa0) : Color.FromArgb(0x6e, 0x6e, 0x73);
            var on = dark ? 1 : 0;
            if (DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(Handle, 19 /* the same before Windows 10 20H1 */, ref on, sizeof(int));
            MenuTheme(TaskbarDark);
            RefreshTrayIcon();
        }

        static Icon AppIcon()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AppIcon.ico"))
                return new Icon(stream);
        }

        void RefreshTrayIcon()
        {
            var old = trayIcon;
            trayIcon = Mascot(SystemInformation.SmallIconSize.Width, TaskbarDark ? Color.White : Color.Black, dimmed: !serving);
            tray.Icon = trayIcon;
            if (old != null) { DestroyIcon(old.Handle); old.Dispose(); }
        }

        /// The app icon's mascot for the tray: a disc with its line cut out, white on a dark taskbar and black on a light one,
        /// faint while the board is not up (macOS dims its menu bar icon the same way).
        static Icon Mascot(int size, Color colour, bool dimmed)
        {
            using (var bitmap = new Bitmap(size, size))
            {
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    var unit = size / 18f;  // the macOS template's 18-point grid
                    using (var brush = new SolidBrush(Color.FromArgb(dimmed ? 110 : 255, colour)))
                        g.FillEllipse(brush, 2 * unit, 2 * unit, 14 * unit, 14 * unit);
                    g.CompositingMode = CompositingMode.SourceCopy;
                    var width = Math.Max(1.6f * unit, 1.5f);
                    using (var line = RoundedRect(new RectangleF(9 * unit - width / 2, 5.6f * unit, width, 6.8f * unit), width / 2))
                    using (var clear = new SolidBrush(Color.Transparent))
                        g.FillPath(clear, line);
                }
                return Icon.FromHandle(bitmap.GetHicon());
            }
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ---------------------------------------------------------------- tray menu

        // The tray menu is Windows' own menu (ContextMenu builds a native one), as the menu bar's is macOS's own: the system's
        // font, spacing, corners and ticks, and its light or dark look from the taskbar's mode, which the tray belongs to.
        void BuildMenu(ContextMenu menu)
        {
            menu.MenuItems.Clear();
            MenuItem Item(string title, Action action)
            {
                var item = new MenuItem(title);
                if (action != null) item.Click += (_, __) => action(); else item.Enabled = false;
                return item;
            }
            var open = Item("Open Verticals", ShowWindowFront);
            open.DefaultItem = true;  // bold, and what a click on the icon does
            menu.MenuItems.Add(open);
            menu.MenuItems.Add("-");
            menu.MenuItems.Add(Item(serving ? "Board and MCP are running" : backend.Running ? "Starting…" : "Stopped, see app.log", null));
            // The click is the consent: nothing is written into Claude Code's or Codex's settings before it. The tick says it was done here.
            var ready = serving && File.Exists(backend.HeadersFile);
            var claude = Item("Use Verticals in Claude Code", ready ? (Action)ConnectClaude : null);
            claude.Checked = Settings.Flag("InClaudeCode");
            menu.MenuItems.Add(claude);
            var codex = Item("Use Verticals in Codex", ready ? (Action)ConnectCodex : null);
            codex.Checked = Settings.Flag("InCodex");
            menu.MenuItems.Add(codex);
            var login = Item("Open at Login", ToggleLogin);
            login.Checked = Shell.OpensAtLogin && !Shell.LoginTurnedOffInTaskManager;
            if (Shell.OpensAtLogin && Shell.LoginTurnedOffInTaskManager) login.Text = "Open at Login (allow in Task Manager)";
            menu.MenuItems.Add(login);
            menu.MenuItems.Add("-");
            menu.MenuItems.Add(Item("Quit Verticals", Quit));
        }

        /// Native menus are drawn light unless the app opts in to dark ones: uxtheme's SetPreferredAppMode and FlushMenuThemes,
        /// which have no names, only ordinals (Windows 10 1903 and later; elsewhere the menu stays light).
        static void MenuTheme(bool dark)
        {
            try
            {
                SetPreferredAppMode(dark ? 2 /* ForceDark */ : 3 /* ForceLight */);
                FlushMenuThemes();
            }
            catch (EntryPointNotFoundException) { }
        }

        [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
        [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();

        void ToggleLogin()
        {
            if (Shell.OpensAtLogin && Shell.LoginTurnedOffInTaskManager) { Shell.Open("taskmgr.exe"); return; }
            Shell.OpensAtLogin = !Shell.OpensAtLogin;
        }

        void ConnectClaude() =>
            Connect("Claude Code", "claude", "InClaudeCode", AppLinks.ClaudeRemoveArguments,
                    AppLinks.ClaudeAddArguments(backend.MCPURL, backend.HeadersFile),
                    $"New Claude Code sessions in any folder can use the board through the {AppLinks.ClaudeServer} MCP server while Verticals runs.");

        void ConnectCodex()
        {
            var python = backend.Python ?? backend.FindPython();
            if (python == null) { Say("Couldn't connect Codex", "Codex starts Verticals' relay with Python 3.12, which isn't found."); return; }
            Connect("Codex", "codex", "InCodex", AppLinks.CodexRemoveArguments,
                    AppLinks.CodexAddArguments(python, Path.Combine(backend.Root, "desktop", "chat", "mcp_proxy.py"), backend.MCPURL, backend.HeadersFile),
                    $"New Codex sessions can use the board through the {AppLinks.CodexServer} MCP server while Verticals runs.");
        }

        // Replaces an earlier connection under the same name, then says what happened; the tick is kept for the menu.
        async void Connect(string name, string tool, string key, string[] remove, string[] add, string done)
        {
            var env = AgentEnvironment();
            var (code, output) = await Task.Run(() =>
            {
                Agents.Run(tool, remove, env);
                return Agents.Run(tool, add, env);
            });
            Settings.Set(key, code == 0);
            if (code == 0) Say($"{name} is connected", done);
            else Say($"Couldn't connect {name}", code == 127 ? $"The {tool} command isn't installed." : output);
        }

        void Say(string title, string text)
        {
            MessageBox.Show(Visible ? this : null, text, title, MessageBoxButtons.OK, MessageBoxIcon.None);
        }

        // Whose sign-in Claude Code and Codex use: `agents.env` in the state folder (AppLinks.AgentEnvironment).
        Dictionary<string, string> AgentEnvironment()
        {
            var file = Path.Combine(backend.StateDir, "agents.env");
            try
            {
                return File.Exists(file)
                    ? AppLinks.AgentEnvironment(File.ReadAllText(file), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                    : new Dictionary<string, string>();
            }
            catch (IOException) { return new Dictionary<string, string>(); }
        }

        // ---------------------------------------------------------------- Win32

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    }
}

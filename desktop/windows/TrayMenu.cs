// The tray icon's menu, drawn by the app: a light or dark panel with Windows' rounded corners and shadow, tall rows,
// separators across the whole width and a narrow column for ticks. It closes on a choice, a click elsewhere or Esc;
// the arrows and Enter work as in a menu.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Verticals
{
    sealed class TrayMenu : Form
    {
        public sealed class Entry
        {
            public string Text;
            public Action Action;      // null: a line that only says something (greyed)
            public bool Checked;
            public bool Separator;
            public static Entry Line() => new Entry { Separator = true };
        }

        // Sizes in 96-dpi pixels, scaled to the monitor the menu opens on.
        const int Pad = 6, Row = 30, SeparatorGap = 7, TickColumn = 34, RightPad = 28, MinWidth = 220, FontPx = 14;

        List<Entry> entries = new List<Entry>();
        List<Rectangle> rows = new List<Rectangle>();
        int hover = -1;
        float scale = 1;
        bool dark;
        Font font, glyphs;

        public TrayMenu()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            KeyPreview = true;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                p.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW: no taskbar button, no Alt-Tab
                p.ClassStyle |= 0x20000;  // CS_DROPSHADOW, where Windows draws no rounded frame of its own
                return p;
            }
        }

        public void Open(IEnumerable<Entry> items, Point at, bool darkMode)
        {
            entries = items.ToList();
            dark = darkMode;
            var screen = Screen.FromPoint(at);
            scale = MonitorDpi(at) / 96f;
            font?.Dispose();
            glyphs?.Dispose();
            font = new Font("Segoe UI", FontPx * scale, GraphicsUnit.Pixel);
            glyphs = new Font(Environment.OSVersion.Version.Build >= 22000 ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", 12 * scale, GraphicsUnit.Pixel);

            // Rows from top to bottom, and the width the longest text needs.
            rows.Clear();
            var y = S(Pad);
            var text = 0;
            foreach (var e in entries)
            {
                var h = e.Separator ? S(SeparatorGap * 2 + 1) : S(Row);
                rows.Add(new Rectangle(0, y, 0, h));
                y += h;
                if (!e.Separator) text = Math.Max(text, TextRenderer.MeasureText(e.Text, font, Size.Empty, TextFormatFlags.NoPadding).Width);
            }
            var width = Math.Max(S(MinWidth), S(TickColumn) + text + S(RightPad));
            for (var i = 0; i < rows.Count; i++) rows[i] = new Rectangle(0, rows[i].Y, width, rows[i].Height);
            var size = new Size(width, y + S(Pad));

            // Above and to the left of the pointer, as the taskbar's own menus open, kept on the screen.
            var area = screen.WorkingArea;
            var x = Math.Max(area.Left, Math.Min(at.X - size.Width, area.Right - size.Width));
            var top = at.Y - size.Height;
            if (top < area.Top) top = Math.Min(at.Y, area.Bottom - size.Height);
            Bounds = new Rectangle(x, Math.Max(area.Top, top), size.Width, size.Height);
            BackColor = Colours.Back;
            hover = -1;
            Show();
            Frame();
            Activate();
            SetForegroundWindow(Handle);
        }

        int S(int px) => (int)Math.Round(px * scale);

        (Color Back, Color Text, Color Muted, Color Line, Color Hover) Colours => dark
            ? (Color.FromArgb(0x2b, 0x2b, 0x2d), Color.FromArgb(0xf2, 0xf2, 0xf2), Color.FromArgb(0x9b, 0x9b, 0xa0), Color.FromArgb(0x3c, 0x3c, 0x40), Color.FromArgb(0x3a, 0x3a, 0x3e))
            : (Color.White, Color.FromArgb(0x1b, 0x1b, 0x1f), Color.FromArgb(0x6e, 0x6e, 0x76), Color.FromArgb(0xe3, 0xe7, 0xee), Color.FromArgb(0xf1, 0xf3, 0xf6));

        // Windows 11 rounds the corners and draws the shadow and a hairline border; Windows 10 keeps a square panel.
        void Frame()
        {
            var round = 2;  // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            var border = ColorTranslator.ToWin32(dark ? Color.FromArgb(0x45, 0x45, 0x4a) : Color.FromArgb(0xd9, 0xdd, 0xe3));
            DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var c = Colours;
            var g = e.Graphics;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var r = rows[i];
                if (entry.Separator)
                {
                    using (var pen = new Pen(c.Line, Math.Max(1, scale))) g.DrawLine(pen, 0, r.Y + r.Height / 2, r.Width, r.Y + r.Height / 2);
                    continue;
                }
                if (i == hover && entry.Action != null)
                    using (var brush = new SolidBrush(c.Hover)) g.FillRectangle(brush, r);
                var colour = entry.Action != null ? c.Text : c.Muted;
                if (entry.Checked)
                    TextRenderer.DrawText(g, "", glyphs, new Rectangle(0, r.Y, S(TickColumn), r.Height), colour,
                                          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, entry.Text, font, new Rectangle(S(TickColumn), r.Y, r.Width - S(TickColumn), r.Height), colour,
                                      TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
        }

        int RowAt(Point p) => rows.FindIndex(r => r.Contains(p));

        bool Choosable(int i) => i >= 0 && i < entries.Count && !entries[i].Separator && entries[i].Action != null;

        void Hover(int i)
        {
            if (i == hover) return;
            hover = i;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e) => Hover(Choosable(RowAt(e.Location)) ? RowAt(e.Location) : -1);

        protected override void OnMouseLeave(EventArgs e) => Hover(-1);

        protected override void OnMouseUp(MouseEventArgs e)
        {
            var i = RowAt(e.Location);
            if (Choosable(i)) Choose(i);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Hide();
            else if (e.KeyCode == Keys.Enter && Choosable(hover)) Choose(hover);
            else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up) Step(e.KeyCode == Keys.Down ? 1 : -1);
        }

        void Step(int direction)
        {
            for (int i = hover, n = 0; n < entries.Count; n++)
            {
                i = (i + direction + entries.Count) % entries.Count;
                if (Choosable(i)) { Hover(i); return; }
            }
        }

        void Choose(int i)
        {
            var action = entries[i].Action;
            Hide();
            BeginInvoke(action);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            Hide();
        }

        static int MonitorDpi(Point at)
        {
            try
            {
                var monitor = MonitorFromPoint(at, 2 /* MONITOR_DEFAULTTONEAREST */);
                return GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? (int)dpi : 96;
            }
            catch (DllNotFoundException) { return 96; }
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point pt, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    }
}

// The tray icon's menu, drawn by the app: a light or dark panel with continuous corners (a superellipse, the family of the
// app icon's tile) and a soft shadow of its own, tall rows, separators across the whole width and a narrow column for
// ticks. The window is layered, so the shadow and the corners blend with whatever is behind. It closes on a choice, a
// click elsewhere or Esc; the arrows and Enter work as in a menu.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
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
        // The corner: a superellipse |x|^n + |y|^n = 1 over this many pixels; n = 2 would be a circle, higher is squarer.
        const float Corner = 20, CornerPower = 4.2f;
        // The shadow: a wide soft one below the panel and a tight one at its edge, in 96-dpi pixels.
        const int ShadowBlur = 24, ShadowDrop = 10, ContactBlur = 3, ShadowMargin = 36;

        List<Entry> entries = new List<Entry>();
        List<Rectangle> rows = new List<Rectangle>();
        Rectangle panel;
        int hover = -1;
        float scale = 1;
        bool dark;
        Font font, glyphs;
        Bitmap shadow;  // drawn once per opening; rows are drawn over it on each hover

        public TrayMenu()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                p.ExStyle |= 0x80 | 0x80000;  // WS_EX_TOOLWINDOW (no taskbar button, no Alt-Tab), WS_EX_LAYERED
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

            // Rows from top to bottom inside the panel, and the width the longest text needs.
            var margin = S(ShadowMargin);
            var heights = entries.Select(e => e.Separator ? S(SeparatorGap * 2 + 1) : S(Row)).ToList();
            int text;
            using (var measure = Graphics.FromHwnd(IntPtr.Zero))
                text = entries.Where(e => !e.Separator)
                              .Select(e => (int)Math.Ceiling(measure.MeasureString(e.Text, font, PointF.Empty, StringFormat.GenericTypographic).Width))
                              .DefaultIfEmpty(0).Max();
            var width = Math.Max(S(MinWidth), S(TickColumn) + text + S(RightPad));
            panel = new Rectangle(margin, margin, width, S(Pad) * 2 + heights.Sum());
            rows.Clear();
            var y = panel.Top + S(Pad);
            foreach (var h in heights) { rows.Add(new Rectangle(panel.Left, y, width, h)); y += h; }

            // The panel above and to the left of the pointer, as the taskbar's own menus open, kept on the screen; the
            // window is larger by the shadow's margin all round.
            var area = screen.WorkingArea;
            var x = Math.Max(area.Left, Math.Min(at.X - panel.Width, area.Right - panel.Width));
            var top = at.Y - panel.Height;
            if (top < area.Top) top = Math.Min(at.Y, area.Bottom - panel.Height);
            Bounds = new Rectangle(x - margin, Math.Max(area.Top, top) - margin, panel.Width + 2 * margin, panel.Height + 2 * margin);

            shadow?.Dispose();
            shadow = DrawShadow();
            hover = -1;
            Show();
            NoFrame();
            Render();
            Activate();
            SetForegroundWindow(Handle);
        }

        int S(float px) => (int)Math.Round(px * scale);

        (Color Back, Color Text, Color Muted, Color Line, Color Hover, int Shadow, int Contact) Colours => dark
            ? (Color.FromArgb(0x2b, 0x2b, 0x2d), Color.FromArgb(0xf2, 0xf2, 0xf2), Color.FromArgb(0x9b, 0x9b, 0xa0), Color.FromArgb(0x3c, 0x3c, 0x40), Color.FromArgb(0x3a, 0x3a, 0x3e), 150, 110)
            : (Color.White, Color.FromArgb(0x1b, 0x1b, 0x1f), Color.FromArgb(0x6e, 0x6e, 0x76), Color.FromArgb(0xe3, 0xe7, 0xee), Color.FromArgb(0xf1, 0xf3, 0xf6), 56, 30);

        // ---------------------------------------------------------------- drawing

        /// The panel's outline: straight sides joined by superellipse corners, which bend in gradually instead of meeting
        /// an arc at a crease.
        GraphicsPath Outline(RectangleF r)
        {
            var radius = Math.Min(S(Corner), Math.Min(r.Width, r.Height) / 2);
            var path = new GraphicsPath();
            var points = new List<PointF>();
            const int steps = 24;
            // Each corner from the end of one side to the start of the next, clockwise from the top right.
            var corners = new[] { (cx: r.Right - radius, cy: r.Top + radius, sx: 1, sy: -1, from: 0),
                                  (cx: r.Right - radius, cy: r.Bottom - radius, sx: 1, sy: 1, from: 1),
                                  (cx: r.Left + radius, cy: r.Bottom - radius, sx: -1, sy: 1, from: 0),
                                  (cx: r.Left + radius, cy: r.Top + radius, sx: -1, sy: -1, from: 1) };
            foreach (var c in corners)
                for (var i = 0; i <= steps; i++)
                {
                    var t = (c.from == 0 ? (double)i / steps : 1 - (double)i / steps) * Math.PI / 2;
                    // A rounding below zero at the ends would make the power NaN.
                    var px = (float)Math.Pow(Math.Max(0, Math.Sin(t)), 2 / CornerPower);
                    var py = (float)Math.Pow(Math.Max(0, Math.Cos(t)), 2 / CornerPower);
                    points.Add(new PointF(c.cx + c.sx * px * radius, c.cy + c.sy * py * radius));
                }
            path.AddPolygon(points.ToArray());
            return path;
        }

        /// The two shadows, blurred from the panel's shape once per opening (premultiplied black).
        Bitmap DrawShadow()
        {
            var c = Colours;
            var size = Size;
            var wide = Blurred(size, new RectangleF(panel.X, panel.Y + S(ShadowDrop), panel.Width, panel.Height), S(ShadowBlur));
            var tight = Blurred(size, new RectangleF(panel.X, panel.Y + Math.Max(1, S(1)), panel.Width, panel.Height), S(ContactBlur));
            var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            var data = bitmap.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            var pixels = new byte[data.Stride * size.Height];
            for (var i = 0; i < wide.Length; i++)
            {
                var a = 1 - (1 - wide[i] * c.Shadow / 255f) * (1 - tight[i] * c.Contact / 255f);
                pixels[(i / size.Width) * data.Stride + (i % size.Width) * 4 + 3] = (byte)Math.Round(a * 255);
            }
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            bitmap.UnlockBits(data);
            return bitmap;
        }

        /// The shape's coverage (0 to 1) blurred by three box passes each way, close to a Gaussian.
        float[] Blurred(Size size, RectangleF shape, int blur)
        {
            var coverage = new float[size.Width * size.Height];
            using (var mask = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(mask))
                using (var path = Outline(shape))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillPath(Brushes.Black, path);
                }
                var data = mask.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var bytes = new byte[data.Stride * size.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                mask.UnlockBits(data);
                for (var y = 0; y < size.Height; y++)
                    for (var x = 0; x < size.Width; x++)
                        coverage[y * size.Width + x] = bytes[y * data.Stride + x * 4 + 3] / 255f;
            }
            var radius = Math.Max(1, blur / 3);
            for (var pass = 0; pass < 3; pass++)
            {
                BoxBlur(coverage, size.Width, size.Height, radius, horizontal: true);
                BoxBlur(coverage, size.Width, size.Height, radius, horizontal: false);
            }
            return coverage;
        }

        static void BoxBlur(float[] values, int width, int height, int radius, bool horizontal)
        {
            int lines = horizontal ? height : width, length = horizontal ? width : height;
            var line = new float[length];
            for (var l = 0; l < lines; l++)
            {
                for (var i = 0; i < length; i++) line[i] = values[horizontal ? l * width + i : i * width + l];
                float sum = 0;
                for (var i = -radius; i <= radius; i++) sum += i >= 0 && i < length ? line[i] : 0;
                for (var i = 0; i < length; i++)
                {
                    values[horizontal ? l * width + i : i * width + l] = sum / (2 * radius + 1);
                    var add = i + radius + 1;
                    var drop = i - radius;
                    sum += (add < length ? line[add] : 0) - (drop >= 0 ? line[drop] : 0);
                }
            }
        }

        /// The shadow, the panel and its rows into one bitmap, handed to Windows with its alpha (UpdateLayeredWindow).
        void Render()
        {
            var c = Colours;
            using (var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(bitmap))
                using (var outline = Outline(panel))
                {
                    g.DrawImageUnscaled(shadow, 0, 0);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var back = new SolidBrush(c.Back)) g.FillPath(back, outline);
                    g.SetClip(outline);
                    g.SmoothingMode = SmoothingMode.None;
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;  // over the opaque panel, so ClearType holds
                    var format = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                    var centred = new StringFormat(format) { Alignment = StringAlignment.Center };
                    for (var i = 0; i < entries.Count; i++)
                    {
                        var entry = entries[i];
                        var r = rows[i];
                        if (entry.Separator)
                        {
                            using (var line = new SolidBrush(c.Line)) g.FillRectangle(line, r.X, r.Y + r.Height / 2, r.Width, Math.Max(1, S(1)));
                            continue;
                        }
                        if (i == hover && entry.Action != null)
                            using (var brush = new SolidBrush(c.Hover)) g.FillRectangle(brush, r);
                        using (var brush = new SolidBrush(entry.Action != null ? c.Text : c.Muted))
                        {
                            if (entry.Checked)
                            {
                                // An icon font in ClearType comes out with coloured fringes: the tick is smoothed in grey.
                                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                                g.DrawString("", glyphs, brush, new RectangleF(r.X, r.Y, S(TickColumn), r.Height), centred);
                                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                            }
                            g.DrawString(entry.Text, font, brush, new RectangleF(r.X + S(TickColumn), r.Y, r.Width - S(TickColumn) - S(RightPad) / 2, r.Height), format);
                        }
                    }
                }
                Present(bitmap);
            }
        }

        void Present(Bitmap bitmap)
        {
            var screen = GetDC(IntPtr.Zero);
            var memory = CreateCompatibleDC(screen);
            var hbitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            var old = SelectObject(memory, hbitmap);
            try
            {
                var size = new Size(bitmap.Width, bitmap.Height);
                var source = Point.Empty;
                var position = Location;
                var blend = new Blend { Op = 0, Flags = 0, Alpha = 255, Format = 1 /* AC_SRC_ALPHA */ };
                UpdateLayeredWindow(Handle, screen, ref position, ref size, memory, ref source, 0, ref blend, 2 /* ULW_ALPHA */);
            }
            finally
            {
                SelectObject(memory, old);
                DeleteObject(hbitmap);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        // Windows 11 would round a popup and frame it; this one has its own corners and shadow.
        void NoFrame()
        {
            var none = 1;  // DWMWCP_DONOTROUND
            DwmSetWindowAttribute(Handle, 33, ref none, sizeof(int));
        }

        // ---------------------------------------------------------------- the hand

        int RowAt(Point p) => rows.FindIndex(r => r.Contains(p));

        bool Choosable(int i) => i >= 0 && i < entries.Count && !entries[i].Separator && entries[i].Action != null;

        void Hover(int i)
        {
            if (i == hover) return;
            hover = i;
            if (Visible) Render();
        }

        protected override void OnMouseMove(MouseEventArgs e) => Hover(Choosable(RowAt(e.Location)) ? RowAt(e.Location) : -1);

        protected override void OnMouseLeave(EventArgs e) => Hover(-1);

        protected override void OnMouseUp(MouseEventArgs e)
        {
            var i = RowAt(e.Location);
            if (Choosable(i)) Choose(i);
            else if (!panel.Contains(e.Location)) Hide();  // the shadow is outside the menu
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

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct Blend { public byte Op, Flags, Alpha, Format; }

        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref Point position, ref Size size, IntPtr src, ref Point source, int key, ref Blend blend, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point pt, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    }
}

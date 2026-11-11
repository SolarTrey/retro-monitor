using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace RetroMonitor
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MonitorForm());
        }
    }

    // ------------------------------------------------------------------
    //  Video probing (Windows property system first, ffprobe as fallback)
    // ------------------------------------------------------------------
    class VideoInfo
    {
        public uint Width;
        public uint Height;
        public double Fps;
        public bool HasResolution { get { return Width > 0 && Height > 0; } }
    }

    static class Prober
    {
        static readonly Guid VideoFmt = new Guid("64440491-4C8B-11D1-8B70-080036B11A03");

        [StructLayout(LayoutKind.Sequential)]
        struct PROPERTYKEY { public Guid fmtid; public uint pid; }

        [StructLayout(LayoutKind.Sequential)]
        struct PROPVARIANT
        {
            public ushort vt;
            public ushort r1;
            public ushort r2;
            public ushort r3;
            public ulong val1;
            public ulong val2;
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint cProps);
            [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
            [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
            [PreserveSig] int Commit();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHGetPropertyStoreFromParsingName(
            string pszPath, IntPtr pbc, int flags, ref Guid riid, out IPropertyStore ppv);

        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PROPVARIANT pvar);

        static uint ReadUInt(IPropertyStore s, uint pid)
        {
            PROPERTYKEY k = new PROPERTYKEY();
            k.fmtid = VideoFmt;
            k.pid = pid;
            PROPVARIANT pv;
            if (s.GetValue(ref k, out pv) != 0) return 0;
            uint res = 0;
            if (pv.vt == 19 || pv.vt == 3) res = (uint)(pv.val1 & 0xFFFFFFFFUL);
            PropVariantClear(ref pv);
            return res;
        }

        static VideoInfo ProbeShell(string path)
        {
            VideoInfo v = new VideoInfo();
            IPropertyStore store = null;
            try
            {
                Guid iid = typeof(IPropertyStore).GUID;
                SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out store);
                v.Width = ReadUInt(store, 3);   // System.Video.FrameWidth
                v.Height = ReadUInt(store, 4);  // System.Video.FrameHeight
                uint rate = ReadUInt(store, 6); // System.Video.FrameRate (frames per 1000 s)
                v.Fps = rate / 1000.0;
            }
            catch { }
            finally
            {
                if (store != null) { try { Marshal.ReleaseComObject(store); } catch { } }
            }
            return v;
        }

        static string FindFfprobe()
        {
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffprobe.exe");
            return File.Exists(local) ? local : "ffprobe";
        }

        static void ProbeFfprobe(string path, VideoInfo v)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(FindFfprobe(),
                    "-v error -select_streams v:0 -show_entries stream=width,height,r_frame_rate -of csv=p=0 \"" + path + "\"");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } }
                    string[] lines = o.Trim().Split('\n');
                    if (lines.Length == 0) return;
                    string[] parts = lines[0].Trim().Split(',');
                    if (parts.Length < 3) return;
                    uint w, h;
                    if (uint.TryParse(parts[0], out w) && v.Width == 0) v.Width = w;
                    if (uint.TryParse(parts[1], out h) && v.Height == 0) v.Height = h;
                    if (v.Fps <= 0)
                    {
                        string[] fr = parts[2].Trim().Split('/');
                        double n, d = 1;
                        if (fr.Length >= 1 && double.TryParse(fr[0], NumberStyles.Float, CultureInfo.InvariantCulture, out n))
                        {
                            if (fr.Length == 2) double.TryParse(fr[1], NumberStyles.Float, CultureInfo.InvariantCulture, out d);
                            if (d > 0) v.Fps = n / d;
                        }
                    }
                }
            }
            catch { }
        }

        public static VideoInfo Probe(string path)
        {
            VideoInfo v = ProbeShell(path);
            if (!v.HasResolution || v.Fps <= 0) ProbeFfprobe(path, v);
            return v;
        }
    }

    // ------------------------------------------------------------------
    //  The monitor widget (photo skin + live phosphor screen)
    // ------------------------------------------------------------------
    class MonitorForm : Form
    {
        enum State { Idle, Hover, Scanning, Result, Error }
        enum Power { Off, Booting, On, ShuttingDown }

        const double BootSec = 5.0;
        const double ShutSec = 0.85;
        static readonly Rectangle SwitchRect = new Rectangle(296, 185, 34, 21);
        bool pressed;

        Power pw = Power.Off;
        long pwMs;
        Stopwatch clock = Stopwatch.StartNew();
        Bitmap noise = new Bitmap(78, 58, PixelFormat.Format32bppArgb);
        Random rng = new Random();

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize,
            IntPtr hdcSrc, ref Point pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);

        // Outline of the glass inside skin.png (pixel coordinates in the skin image)
        static readonly Point[] ScreenPts = new Point[] { new Point(258, 71), new Point(260, 80), new Point(262, 92), new Point(262, 151), new Point(259, 174), new Point(256, 185), new Point(254, 188), new Point(224, 194), new Point(178, 196), new Point(143, 195), new Point(116, 192), new Point(102, 190), new Point(95, 185), new Point(92, 154), new Point(92, 91), new Point(93, 80), new Point(96, 72), new Point(105, 68), new Point(114, 67), new Point(244, 67), new Point(253, 68) };

        State state = State.Idle;
        State prevState = State.Idle;
        string fileName = "";
        string errorText = "";
        VideoInfo info;
        int tick;
        int scanId;
        DateTime scanStart;
        const double ScanSeconds = 1.6;

        bool dragging;
        Point dragOffset;

        const int PX = 1;
        Bitmap skin, layer, small;
        GraphicsPath screenPath;
        RectangleF sb;
        Font fontMain, fontSmall;
        StringFormat sf;
        System.Windows.Forms.Timer timer;
        ContextMenuStrip menu;

        static readonly Color Green = Color.FromArgb(185, 255, 195);
        static readonly Color GreenDim = Color.FromArgb(70, 200, 95);

        static Bitmap LoadSkin()
        {
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("skin.png");
            if (s == null)
            {
                string f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "skin.png");
                if (!File.Exists(f)) throw new FileNotFoundException("skin.png is missing");
                s = File.OpenRead(f);
            }
            using (s)
            using (Image tmp = Image.FromStream(s))
            {
                Bitmap b = new Bitmap(tmp.Width, tmp.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(tmp, 0, 0, tmp.Width, tmp.Height);
                }
                return b;
            }
        }

        public MonitorForm()
        {
            skin = LoadSkin();
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ClientSize = skin.Size;
            ShowInTaskbar = true;
            Text = "Retro Monitor";
            AllowDrop = true;

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - skin.Width - 30, wa.Top + 50);

            screenPath = new GraphicsPath();
            screenPath.AddPolygon(ScreenPts);
            sb = screenPath.GetBounds();
            layer = new Bitmap((int)Math.Ceiling(sb.Width / PX) + 1, (int)Math.Ceiling(sb.Height / PX) + 1, PixelFormat.Format32bppArgb);
            small = new Bitmap(Math.Max(8, layer.Width / 3), Math.Max(8, layer.Height / 3), PixelFormat.Format32bppArgb);

            string fam = "Courier New";
            foreach (FontFamily f in FontFamily.Families)
                if (f.Name == "Consolas") { fam = "Consolas"; break; }
            fontMain = new Font(fam, 18f, FontStyle.Bold, GraphicsUnit.Pixel);
            fontSmall = new Font(fam, 12f, FontStyle.Bold, GraphicsUnit.Pixel);

            sf = (StringFormat)StringFormat.GenericTypographic.Clone();
            sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;

            menu = new ContextMenuStrip();
            ToolStripMenuItem top = new ToolStripMenuItem("Always on top");
            top.CheckOnClick = true;
            top.CheckedChanged += delegate { TopMost = top.Checked; };
            ToolStripMenuItem clear = new ToolStripMenuItem("Clear screen");
            clear.Click += delegate { scanId++; state = State.Idle; Render(); };
            ToolStripMenuItem exit = new ToolStripMenuItem("Power off (exit)");
            exit.Click += delegate { Close(); };
            menu.Items.Add(top);
            menu.Items.Add(clear);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exit);
            ContextMenuStrip = menu;

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 40;
            timer.Tick += delegate
            {
                int nt = (int)(clock.ElapsedMilliseconds / 150);
                double t = (clock.ElapsedMilliseconds - pwMs) / 1000.0;
                if (pw == Power.Booting && t >= BootSec) { pw = Power.On; state = State.Idle; }
                else if (pw == Power.ShuttingDown && t >= ShutSec) { pw = Power.Off; state = State.Idle; }
                bool anim = pw == Power.Booting || pw == Power.ShuttingDown;
                if (anim || nt != tick) { tick = nt; Render(); }
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80000; // WS_EX_LAYERED
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Render();
            timer.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            base.OnFormClosed(e);
        }

        // ---------------- dragging the whole widget ----------------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (SwitchRect.Contains(e.Location)) { pressed = true; TogglePower(); return; }
                dragging = true; dragOffset = e.Location;
            }
            base.OnMouseDown(e);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
                Location = new Point(Cursor.Position.X - dragOffset.X, Cursor.Position.Y - dragOffset.Y);
            else
                Cursor = SwitchRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            dragging = false;
            if (pressed) { pressed = false; Render(); }
            base.OnMouseUp(e);
        }

        // ---------------- drag & drop of files ----------------
        protected override void OnDragEnter(DragEventArgs e)
        {
            if (pw == Power.On && state != State.Scanning && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
                if (state != State.Hover) prevState = state;
                state = State.Hover;
                Render();
            }
            else e.Effect = DragDropEffects.None;
            base.OnDragEnter(e);
        }

        protected override void OnDragLeave(EventArgs e)
        {
            if (state == State.Hover) { state = prevState; Render(); }
            base.OnDragLeave(e);
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) { state = prevState; Render(); return; }
            StartScan(files[0]);
            base.OnDragDrop(e);
        }

        void StartScan(string path)
        {
            if (Directory.Exists(path) || !File.Exists(path))
            {
                fileName = Path.GetFileName(path);
                errorText = "not a file";
                state = State.Error;
                Render();
                return;
            }

            fileName = Path.GetFileName(path);
            state = State.Scanning;
            scanStart = DateTime.Now;
            int id = ++scanId;
            Render();

            ThreadPool.QueueUserWorkItem(delegate
            {
                VideoInfo v = null;
                try { v = Prober.Probe(path); } catch { }
                double left = ScanSeconds - (DateTime.Now - scanStart).TotalSeconds;
                if (left > 0) Thread.Sleep((int)(left * 1000));
                try
                {
                    if (!IsHandleCreated || IsDisposed) return;
                    BeginInvoke(new Action(delegate
                    {
                        if (id != scanId) return;
                        if (v != null && v.HasResolution)
                        {
                            info = v;
                            state = State.Result;
                        }
                        else
                        {
                            errorText = "no video";
                            state = State.Error;
                        }
                        Render();
                    }));
                }
                catch { }
            });
        }

        // ---------------- drawing ----------------
        void Render()
        {
            if (!IsHandleCreated) return;
            using (Bitmap bmp = new Bitmap(skin.Width, skin.Height, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                g.DrawImage(skin, 0, 0, skin.Width, skin.Height);
                DrawScreen(g);
                DrawSwitch(g);
                SetBitmap(bmp);
            }
        }

        class Line
        {
            public string Text;
            public Color Color;
            public Font Font;
            public Line(string t, Color c, Font f) { Text = t; Color = c; Font = f; }
        }

        void DrawScreen(Graphics g)
        {
            PointF c = new PointF(sb.X + sb.Width / 2, sb.Y + sb.Height / 2);

            // faint light spill onto the bezel
            float[] widths = new float[] { 14f, 9f, 5f };
            foreach (float w in widths)
                using (Pen sp = new Pen(Color.FromArgb(9, 70, 255, 110), w))
                {
                    sp.LineJoin = LineJoin.Round;
                    g.DrawPath(sp, screenPath);
                }

            GraphicsState gs = g.Save();
            g.SetClip(screenPath, CombineMode.Replace);

            // dim phosphor glow behind the text
            using (PathGradientBrush pb = new PathGradientBrush(screenPath))
            {
                pb.CenterPoint = new PointF(c.X, c.Y - 3);
                pb.CenterColor = Color.FromArgb(11, 48, 24);
                pb.SurroundColors = new Color[] { Color.FromArgb(0, 3, 1) };
                pb.FocusScales = new PointF(0.4f, 0.4f);
                g.FillPath(pb, screenPath);
            }

            // text on its own layer so it can bloom
            using (Graphics lg = Graphics.FromImage(layer))
            {
                lg.Clear(Color.Transparent);
                lg.SmoothingMode = SmoothingMode.None;
                lg.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
                DrawContent(lg, new RectangleF(0, 0, layer.Width, layer.Height));
            }
            using (Graphics sg = Graphics.FromImage(small))
            {
                sg.Clear(Color.Transparent);
                sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                sg.DrawImage(layer, new Rectangle(0, 0, small.Width, small.Height));
            }
            Rectangle dst = new Rectangle((int)Math.Floor(sb.X), (int)Math.Floor(sb.Y), layer.Width * PX, layer.Height * PX);
            float flick = 0.94f + 0.06f * (float)Math.Sin(tick * 1.7);

            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            using (ImageAttributes ia = new ImageAttributes())
            {
                ColorMatrix cm = new ColorMatrix();
                cm.Matrix33 = 0.45f * flick;
                ia.SetColorMatrix(cm);
                Rectangle bloom = new Rectangle(dst.X - 2, dst.Y - 2, dst.Width + 4, dst.Height + 4);
                g.DrawImage(small, bloom, 0, 0, small.Width, small.Height, GraphicsUnit.Pixel, ia);
            }

            // faint scanlines under the text so letters stay solid
            using (Brush sl = new SolidBrush(Color.FromArgb(26, 0, 0, 0)))
                for (float y = sb.Y; y < sb.Bottom; y += 3)
                    g.FillRectangle(sl, sb.X, y, sb.Width, 1);

            // chunky pixel text with a slight CRT wobble (drawn in thin strips)
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            int strip = 3; // rows per wobble strip
            for (int ly = 0; ly < layer.Height; ly += strip)
            {
                int hh = Math.Min(strip, layer.Height - ly);
                double wob = Math.Sin(ly * 0.45 + tick * 0.35) * 0.9;
                if ((tick % 23) == 0 && ly > layer.Height / 3 && ly < layer.Height / 3 + 6) wob += 2.5; // rare glitch
                int off = (int)Math.Round(wob);
                g.DrawImage(layer, new Rectangle(dst.X + off, dst.Y + ly * PX, layer.Width * PX, hh * PX),
                    0, ly, layer.Width, hh, GraphicsUnit.Pixel);
            }
            g.PixelOffsetMode = PixelOffsetMode.Default;

            PowerFx(g);

            // curved-glass vignette
            using (PathGradientBrush vb = new PathGradientBrush(screenPath))
            {
                vb.CenterPoint = c;
                vb.CenterColor = Color.FromArgb(0, 0, 0, 0);
                vb.SurroundColors = new Color[] { Color.FromArgb(150, 0, 0, 0) };
                vb.FocusScales = new PointF(0.7f, 0.7f);
                g.FillPath(vb, screenPath);
            }

            // glass reflection
            using (GraphicsPath sheen = new GraphicsPath())
            {
                sheen.AddPolygon(new PointF[] {
                    new PointF(sb.X, sb.Y), new PointF(sb.X + sb.Width * 0.55f, sb.Y), new PointF(sb.X, sb.Y + sb.Height * 0.6f) });
                using (LinearGradientBrush shb = new LinearGradientBrush(
                    new RectangleF(sb.X, sb.Y, sb.Width * 0.55f, sb.Height * 0.6f),
                    Color.FromArgb(30, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 45f))
                    g.FillPath(shb, sheen);
            }
            g.Restore(gs);

            // clean dark lip around the glass
            using (Pen rim = new Pen(Color.FromArgb(235, 5, 9, 7), 2.4f))
            {
                rim.LineJoin = LineJoin.Round;
                g.DrawPath(rim, screenPath);
            }
        }

        static double Ease(double u)
        {
            u = Math.Max(0, Math.Min(1, u));
            return 1 - (1 - u) * (1 - u);
        }

        void TogglePower()
        {
            scanId++;
            if (pw == Power.Off || pw == Power.ShuttingDown)
            {
                pw = Power.Booting;
                state = State.Idle;
            }
            else
            {
                pw = Power.ShuttingDown;
            }
            pwMs = clock.ElapsedMilliseconds;
            Render();
        }

        // CRT turn-on / turn-off effects, drawn inside the glass clip
        void PowerFx(Graphics g)
        {
            if (pw == Power.On) return;
            float cx = sb.X + sb.Width / 2, cy = sb.Y + sb.Height / 2;
            using (Brush blk = new SolidBrush(Color.FromArgb(255, 2, 5, 3)))
            {
                if (pw == Power.Off)
                {
                    g.FillRectangle(blk, sb.X - 1, sb.Y - 1, sb.Width + 2, sb.Height + 2);
                    return;
                }

                double t = (clock.ElapsedMilliseconds - pwMs) / 1000.0;
                double wf = 1, hf = 1, flash = 0, dot = 1;

                if (pw == Power.Booting)
                {
                    if (t < 0.25) { wf = Ease(t / 0.25); hf = 0; flash = 1; }
                    else if (t < 0.75) { double u = (t - 0.25) / 0.5; hf = Ease(u); flash = 1 - u; }

                    // static burst
                    if (t >= 0.6 && t < 1.5)
                    {
                        double lvl = t < 0.9 ? 1.0 : 1.0 - (t - 0.9) / 0.6;
                        for (int y = 0; y < noise.Height; y++)
                            for (int x = 0; x < noise.Width; x++)
                            {
                                int v = rng.Next(256);
                                noise.SetPixel(x, y, Color.FromArgb((int)(v * 0.75 * lvl), v / 3, v, v / 3));
                            }
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImage(noise, new RectangleF(sb.X, sb.Y, sb.Width, sb.Height));
                        g.PixelOffsetMode = PixelOffsetMode.Default;
                    }
                }
                else
                {
                    if (t < 0.28) { double u = t / 0.28; hf = 1 - Ease(u); flash = u; }
                    else if (t < 0.55) { double u = (t - 0.28) / 0.27; hf = 0; wf = 1 - Ease(u); flash = 1; }
                    else { hf = 0; wf = 0.02; flash = 1; dot = Math.Max(0, 1 - (t - 0.55) / 0.3); }
                }

                float bh = (float)Math.Max(2, hf * sb.Height);
                float bw = (float)Math.Max(2, wf * sb.Width);
                RectangleF band = new RectangleF(cx - bw / 2, cy - bh / 2, bw, bh);

                if (hf < 1 || wf < 1)
                {
                    // everything outside the band is dark
                    g.FillRectangle(blk, sb.X - 1, sb.Y - 1, sb.Width + 2, band.Y - sb.Y + 1);
                    g.FillRectangle(blk, sb.X - 1, band.Bottom, sb.Width + 2, sb.Bottom - band.Bottom + 1);
                    g.FillRectangle(blk, sb.X - 1, band.Y, band.X - sb.X + 1, band.Height);
                    g.FillRectangle(blk, band.Right, band.Y, sb.Right - band.Right + 1, band.Height);
                }

                int fa = (int)(Math.Min(1.0, flash) * dot * 235);
                if (fa > 0)
                {
                    for (int k = 3; k >= 1; k--)
                        using (Brush hb = new SolidBrush(Color.FromArgb((int)(fa * 0.12), 120, 255, 150)))
                            g.FillRectangle(hb, band.X - k * 2, band.Y - k * 2, band.Width + k * 4, band.Height + k * 4);
                    using (Brush fb = new SolidBrush(Color.FromArgb(fa, 200, 255, 215)))
                        g.FillRectangle(fb, band);
                }
            }
        }

        static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static Color Lerp(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                                  (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // 0 = rocked to OFF (left side pressed in), 1 = rocked to ON (right side pressed in)
        float RockPos()
        {
            double t = Math.Min(1.0, (clock.ElapsedMilliseconds - pwMs) / 180.0);
            double e = t * t * (3 - 2 * t);
            if (pw == Power.Booting) return (float)e;
            if (pw == Power.On) return 1f;
            if (pw == Power.ShuttingDown) return (float)(1 - e);
            return 0f;
        }

        Bitmap swBmp = new Bitmap(232, 184, PixelFormat.Format32bppArgb);

        // the rocker is drawn 4x larger off-screen and scaled down for smooth, detailed edges
        void DrawSwitch(Graphics g)
        {
            using (Graphics h = Graphics.FromImage(swBmp))
            {
                h.Clear(Color.Transparent);
                h.SmoothingMode = SmoothingMode.AntiAlias;
                h.PixelOffsetMode = PixelOffsetMode.HighQuality;
                h.ScaleTransform(4f, 4f);
                h.TranslateTransform(-284f, -174f);
                DrawRocker(h);
            }
            GraphicsState st = g.Save();
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(swBmp, new RectangleF(284f, 174f, 58f, 46f), new RectangleF(0f, 0f, 232f, 184f), GraphicsUnit.Pixel);
            g.Restore(st);
        }

        void DrawRocker(Graphics g)
        {
            float s = RockPos();
            RectangleF btn = new RectangleF(301.15f, 190.95f, 23.5f, 9.95f);
            float x0 = btn.X, x1 = btn.Right, y0 = btn.Y, y1 = btn.Bottom;
            float midX = (x0 + x1) / 2f, midY = (y0 + y1) / 2f;

            // amber light spilling over the black housing (kept inside its border)
            GraphicsState g0 = g.Save();
            using (GraphicsPath hc = RoundRect(new RectangleF(297.4f, 187.7f, 31.2f, 16.5f), 1.6f))
                g.SetClip(hc, CombineMode.Replace);
            if (s > 0.02f)
            {
                double pulse = 0.94 + 0.06 * Math.Sin(clock.ElapsedMilliseconds / 260.0);
                for (int i = 9; i >= 1; i--)
                {
                    float e = 0.6f + i * 1.55f;
                    RectangleF gr = new RectangleF(btn.X - e, btn.Y - e, btn.Width + 2 * e, btn.Height + 2 * e);
                    using (GraphicsPath gp = RoundRect(gr, 2f + e))
                    using (Brush gb = new SolidBrush(Color.FromArgb((int)(9 * pulse * s), 255, 150, 30)))
                        g.FillPath(gb, gp);
                }
            }
            g.Restore(g0);

            // everything else stays inside the black window
            using (GraphicsPath wc = RoundRect(new RectangleF(300.95f, 190.75f, 23.9f, 10.3f), 0.8f))
                g.SetClip(wc, CombineMode.Replace);

            // rocker geometry: a narrow side wall on the raised end, two lens planes meeting at a ridge
            float rl = s;                 // ON: left end raised, dash side pressed in
            float rr = 1f - s;            // OFF: right end raised, O side pressed in
            float wall = 3.1f;
            float stripL = wall * rl, stripR = wall * rr;
            float ridge = midX + (rl - rr) * 1.5f;
            RectangleF planeA = new RectangleF(x0 + stripL, y0, ridge - (x0 + stripL), btn.Height);
            RectangleF planeB = new RectangleF(ridge, y0, (x1 - stripR) - ridge, btn.Height);

            Color onRaisedT = Color.FromArgb(238, 255, 232, 160), onRaisedB = Color.FromArgb(238, 255, 182, 56);
            Color onSunkT = Color.FromArgb(238, 232, 146, 30), onSunkB = Color.FromArgb(238, 176, 92, 8);
            Color offRaisedT = Color.FromArgb(226, 228, 160, 60), offRaisedB = Color.FromArgb(226, 184, 110, 28);
            Color offSunkT = Color.FromArgb(226, 160, 92, 24), offSunkB = Color.FromArgb(226, 112, 58, 10);

            // side walls (the thickness of the raised end)
            for (int w = 0; w < 2; w++)
            {
                float wl = w == 0 ? stripL : stripR;
                if (wl < 0.15f) continue;
                RectangleF wr = w == 0 ? new RectangleF(x0, y0, wl, btn.Height) : new RectangleF(x1 - wl, y0, wl, btn.Height);
                Color outer = Lerp(Color.FromArgb(226, 176, 100, 20), Color.FromArgb(238, 214, 120, 24), s);
                Color inner = Lerp(Color.FromArgb(226, 240, 176, 84), Color.FromArgb(238, 255, 206, 110), s);
                using (LinearGradientBrush wb = new LinearGradientBrush(new RectangleF(wr.X - 0.01f, wr.Y, wr.Width + 0.02f, wr.Height),
                    w == 0 ? outer : inner, w == 0 ? inner : outer, 0f))
                    g.FillRectangle(wb, wr);
                float lx = w == 0 ? wr.Right : wr.X;
                using (Pen lp = new Pen(Color.FromArgb((int)(120 + 70 * s), 255, 228, 170), 0.5f))
                    g.DrawLine(lp, lx, y0, lx, y1);
            }

            // the two lens planes (raised plane is lighter, pressed plane deeper)
            RectangleF[] planes = new RectangleF[] { planeA, planeB };
            float[] raised = new float[] { rl, rr };
            for (int i = 0; i < 2; i++)
            {
                float ra = raised[i];
                Color cT = Lerp(Lerp(offSunkT, offRaisedT, ra), Lerp(onSunkT, onRaisedT, ra), s);
                Color cB = Lerp(Lerp(offSunkB, offRaisedB, ra), Lerp(onSunkB, onRaisedB, ra), s);
                if (pressed) { cT = Lerp(cT, Color.FromArgb(cT.A, 60, 30, 0), 0.3f); cB = Lerp(cB, Color.FromArgb(cB.A, 40, 18, 0), 0.3f); }
                RectangleF pr = planes[i];
                using (LinearGradientBrush lb = new LinearGradientBrush(new RectangleF(pr.X, pr.Y - 0.01f, pr.Width, pr.Height + 0.02f), cT, cB, 90f))
                    g.FillRectangle(lb, pr);
                // soft horizontal shading across each plane, like the reference
                Color sh0 = i == 0 ? Color.FromArgb(0, 60, 20, 0) : Color.FromArgb(46, 60, 20, 0);
                Color sh1 = i == 0 ? Color.FromArgb(46, 60, 20, 0) : Color.FromArgb(0, 60, 20, 0);
                using (LinearGradientBrush hs = new LinearGradientBrush(new RectangleF(pr.X - 0.01f, pr.Y, pr.Width + 0.02f, pr.Height), sh0, sh1, 0f))
                    g.FillRectangle(hs, pr);
            }

            // lamp behind the lens
            if (s > 0.02f)
            {
                RectangleF bl = new RectangleF(planeA.X + 0.6f, y0 + 0.8f, Math.Max(2f, planeA.Width - 1.2f), btn.Height - 1.6f);
                using (GraphicsPath bp = new GraphicsPath())
                {
                    bp.AddEllipse(bl);
                    using (PathGradientBrush pg = new PathGradientBrush(bp))
                    {
                        pg.CenterColor = Color.FromArgb((int)(120 * s * rl), 255, 248, 200);
                        pg.SurroundColors = new Color[] { Color.FromArgb(0, 255, 200, 90) };
                        g.FillPath(pg, bp);
                    }
                }
                RectangleF bl2 = new RectangleF(planeB.X + 1.0f, y0 + 1.6f, Math.Max(2f, planeB.Width - 2.0f), btn.Height - 3.2f);
                using (GraphicsPath bp2 = new GraphicsPath())
                {
                    bp2.AddEllipse(bl2);
                    using (PathGradientBrush pg2 = new PathGradientBrush(bp2))
                    {
                        pg2.CenterColor = Color.FromArgb((int)(70 * s + 50 * rr * (1f - s)), 255, 190, 70);
                        pg2.SurroundColors = new Color[] { Color.FromArgb(0, 255, 160, 40) };
                        g.FillPath(pg2, bp2);
                    }
                }
            }

            // frame shadow along the top and sides
            RectangleF shT = new RectangleF(x0, y0 - 1f, btn.Width, 2.8f);
            using (LinearGradientBrush stb = new LinearGradientBrush(shT, Color.FromArgb(110, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 90f))
                g.FillRectangle(stb, shT);

            // fine moulded texture, very subtle
            using (Brush tl = new SolidBrush(Color.FromArgb((int)(22 + 10 * s), 255, 244, 200)))
                for (float ty = y0 + 0.7f; ty < y1; ty += 1.45f)
                    for (float tx2 = x0 + 0.7f; tx2 < x1; tx2 += 1.45f)
                        g.FillEllipse(tl, tx2, ty, 0.8f, 0.8f);

            // gloss on the raised plane
            RectangleF rp = rl > 0.5f ? planeA : planeB;
            PointF[] streak = new PointF[] {
                new PointF(rp.X + 0.8f, y0 + 0.3f), new PointF(rp.X + rp.Width * 0.7f, y0 + 0.3f),
                new PointF(rp.X + rp.Width * 0.35f, y0 + 3.8f), new PointF(rp.X + 0.8f, y0 + 3.8f) };
            using (LinearGradientBrush gl = new LinearGradientBrush(new RectangleF(rp.X, y0, rp.Width, 4.0f),
                Color.FromArgb((int)(50 + 40 * s), 255, 255, 235), Color.FromArgb(0, 255, 255, 235), 90f))
                g.FillPolygon(gl, streak);

            // O and dash markings, printed on the lens and lit from behind
            float symA = planeA.X + planeA.Width / 2f, symB = planeB.X + planeB.Width / 2f;
            float sxA = Math.Max(0.6f, Math.Min(1.1f, planeA.Width / 9.6f));
            float sxB = Math.Max(0.6f, Math.Min(1.1f, planeB.Width / 9.6f));
            int symA_a = (int)(200 + 40 * s), symB_a = (int)(200 + 40 * s);
            float ringR = 2.2f;
            using (Pen glowP = new Pen(Color.FromArgb((int)(34 * s), 255, 240, 190), 1.9f))
            {
                g.DrawEllipse(glowP, symA - ringR * sxA, midY - ringR, 2 * ringR * sxA, 2 * ringR);
                g.DrawLine(glowP, symB - 2.3f * sxB, midY, symB + 2.3f * sxB, midY);
            }
            using (Pen symP = new Pen(Color.FromArgb(symA_a, 255, 252, 238), 0.75f))
                g.DrawEllipse(symP, symA - ringR * sxA, midY - ringR, 2 * ringR * sxA, 2 * ringR);
            using (Pen symD = new Pen(Color.FromArgb(symB_a, 255, 252, 238), 0.75f))
                g.DrawLine(symD, symB - 2.3f * sxB, midY, symB + 2.3f * sxB, midY);

            // ridge and outlines
            using (Pen rd = new Pen(Color.FromArgb(150, 110, 56, 4), 0.5f))
                g.DrawLine(rd, ridge, y0, ridge, y1);
            using (Pen rl2 = new Pen(Color.FromArgb((int)(60 + 60 * s), 255, 225, 150), 0.4f))
                g.DrawLine(rl2, ridge + (rl > 0.5f ? -0.45f : 0.45f), y0, ridge + (rl > 0.5f ? -0.45f : 0.45f), y1);
            using (Pen hl = new Pen(Color.FromArgb((int)(100 + 70 * s), 255, 246, 210), 0.5f))
                g.DrawLine(hl, x0 + 0.4f, y0 + 0.25f, x1 - 0.4f, y0 + 0.25f);
            using (Pen edge = new Pen(Color.FromArgb(150, 80, 40, 2), 0.5f))
                g.DrawRectangle(edge, x0, y0, btn.Width, btn.Height);
        }

        string Trunc(string s, int max)
        {
            if (s.Length <= max) return s;
            int tail = 6;
            return s.Substring(0, max - tail - 2) + ".." + s.Substring(s.Length - tail);
        }

        void DrawContent(Graphics g, RectangleF sr)
        {
            bool blink = ((tick / 3) % 2) == 0;
            float cx = sr.X + sr.Width / 2;

            if (pw == Power.Off) return;
            if (pw == Power.Booting)
            {
                double bt = (clock.ElapsedMilliseconds - pwMs) / 1000.0;
                if (bt < 1.35) return;

                string title = "RETRO RESOLUTION & FPS";
                string ver = "V1.02  RETROVIEW 9000";
                int n1 = Math.Max(0, Math.Min(title.Length, (int)((bt - 1.35) / 0.9 * title.Length)));
                int n2 = Math.Max(0, Math.Min(ver.Length, (int)((bt - 2.25) / 0.45 * ver.Length)));
                double u = Math.Max(0.0, Math.Min(1.0, (bt - 2.8) / 1.7));
                double prog = Math.Max(0.0, Math.Min(1.0, u + 0.025 * Math.Sin(6 * Math.PI * u)));
                if (u >= 1.0) prog = 1.0;

                float lh = fontSmall.Height + 2;
                float top0 = sr.Y + (sr.Height - (lh * 3 + 20)) / 2f;
                float tw = g.MeasureString(title, fontSmall, PointF.Empty, sf).Width;
                float vw = g.MeasureString(ver, fontSmall, PointF.Empty, sf).Width;
                if (n1 > 0) Glow(g, title.Substring(0, n1), fontSmall, cx - tw / 2f, top0, Green);
                if (n2 > 0) Glow(g, ver.Substring(0, n2), fontSmall, cx - vw / 2f, top0 + lh, GreenDim);

                if (bt >= 2.8)
                {
                    // vintage block progress bar
                    int barW = (int)Math.Min(sr.Width - 24f, 132f);
                    int bx0 = (int)Math.Round(cx - barW / 2f);
                    int by0 = (int)Math.Round(top0 + lh * 2 + 7);
                    using (Pen bp = new Pen(Green, 1f))
                        g.DrawRectangle(bp, bx0, by0, barW, 9);
                    int cells = (barW - 4) / 5;
                    int filled = (int)Math.Floor(prog * cells + 0.0001);
                    using (Brush cb = new SolidBrush(Green))
                        for (int k = 0; k < filled; k++)
                            g.FillRectangle(cb, bx0 + 3 + k * 5, by0 + 3, 4, 4);

                    int pct = (int)Math.Round(prog * 100);
                    string stage = prog >= 1.0 ? "READY" : prog < 0.34 ? "MEMORY TEST" : prog < 0.7 ? "VIDEO INIT" : "LOADING PROBE";
                    string line = prog >= 1.0 ? stage : stage + " " + pct + "%";
                    float lw2 = g.MeasureString(line, fontSmall, PointF.Empty, sf).Width;
                    bool on2 = prog < 1.0 || blink;
                    if (on2) Glow(g, line, fontSmall, cx - lw2 / 2f, by0 + 14, prog >= 1.0 ? Green : GreenDim);
                }
                return;
            }

            if (state == State.Idle || state == State.Hover)
            {
                string txt = state == State.Hover ? "drop to scan" : "add source";
                float w = g.MeasureString(txt, fontMain, PointF.Empty, sf).Width;
                float cw = g.MeasureString("_", fontMain, PointF.Empty, sf).Width;
                float x = cx - (w + cw) / 2;
                float y = sr.Y + sr.Height / 2 - fontMain.Height / 2f;
                Glow(g, txt, fontMain, x, y, Green);
                if (blink) Glow(g, "_", fontMain, x + w, y, Green);
                return;
            }

            List<Line> lines = new List<Line>();
            if (state == State.Scanning)
            {
                double frac = (DateTime.Now - scanStart).TotalSeconds / ScanSeconds;
                if (frac > 0.97) frac = 0.97;
                if (frac < 0) frac = 0;
                int filled = (int)Math.Round(frac * 14);
                string bar = "[" + new string('#', filled) + new string('.', 14 - filled) + "]";
                lines.Add(new Line("scanning" + new string('.', tick % 4), Green, fontMain));
                lines.Add(new Line(bar, Green, fontMain));
                lines.Add(new Line(Trunc(fileName, 16), GreenDim, fontSmall));
            }
            else if (state == State.Result)
            {
                string fps = info.Fps > 0
                    ? info.Fps.ToString("0.###", CultureInfo.InvariantCulture)
                    : "unknown";
                lines.Add(new Line(Trunc(fileName, 16), GreenDim, fontSmall));
                lines.Add(new Line("FPS  " + fps, Green, fontMain));
                lines.Add(new Line("RES  " + info.Width + "x" + info.Height, Green, fontMain));
                lines.Add(new Line(blink ? "drop another_" : "drop another ", GreenDim, fontSmall));
            }
            else
            {
                lines.Add(new Line("ERR: " + errorText, Green, fontMain));
                lines.Add(new Line(Trunc(fileName, 16), GreenDim, fontSmall));
                lines.Add(new Line(blink ? "try again_" : "try again ", GreenDim, fontSmall));
            }

            float total = 0, maxw = 0;
            foreach (Line l in lines)
            {
                total += l.Font.Height + 3;
                float lw = g.MeasureString(l.Text, l.Font, PointF.Empty, sf).Width;
                if (lw > maxw) maxw = lw;
            }
            float yy = sr.Y + (sr.Height - total) / 2f;
            float left = Math.Max(4f, sr.X + (sr.Width - maxw) / 2f);
            foreach (Line l in lines)
            {
                Glow(g, l.Text, l.Font, left, yy, l.Color);
                yy += l.Font.Height + 3;
            }
        }

        void Glow(Graphics g, string s, Font f, float x, float y, Color c)
        {
            using (Brush b = new SolidBrush(c))
                g.DrawString(s, f, b, (float)Math.Round(x), (float)Math.Round(y), sf);
        }

        // push the ARGB bitmap to the layered window
        void SetBitmap(Bitmap bmp)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero;
            IntPtr old = IntPtr.Zero;
            try
            {
                hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(memDc, hBmp);
                Size size = new Size(bmp.Width, bmp.Height);
                Point src = new Point(0, 0);
                Point dst = new Point(Left, Top);
                BLENDFUNCTION bf = new BLENDFUNCTION();
                bf.BlendOp = 0;
                bf.BlendFlags = 0;
                bf.SourceConstantAlpha = 255;
                bf.AlphaFormat = 1;
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref bf, 2);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
                if (hBmp != IntPtr.Zero)
                {
                    SelectObject(memDc, old);
                    DeleteObject(hBmp);
                }
                DeleteDC(memDc);
            }
        }
    }
}

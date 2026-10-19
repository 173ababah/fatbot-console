using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Threading;
using System.Windows.Forms;

namespace FishBotConsole
{
    // ============================================================ 圆角控件
    public class RoundButton : Button
    {
        public int Radius = 10;
        public Color FillColor = Color.FromArgb(0, 153, 255);
        public Color HoverColor = Color.FromArgb(30, 170, 255);
        public Color LabelColor = Color.White;
        bool _hover;

        public RoundButton()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            UseVisualStyleBackColor = false;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Color.FromArgb(43, 45, 49));
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = RoundRect(r, Radius))
            using (var br = new SolidBrush(_hover ? HoverColor : FillColor))
                g.FillPath(br, path);
            TextRenderer.DrawText(g, Text, Font, r, LabelColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        public static System.Drawing.Drawing2D.GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            int d = radius * 2;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    public class RoundPanel : Panel
    {
        public int Radius = 12;
        public Color FillColor = Color.FromArgb(49, 51, 56);
        public Color BorderColor = Color.FromArgb(49, 51, 56);

        public RoundPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Color.FromArgb(43, 45, 49));
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = RoundButton.RoundRect(r, Radius))
            {
                using (var br = new SolidBrush(FillColor)) g.FillPath(br, path);
                if (BorderColor.ToArgb() != FillColor.ToArgb())
                    using (var pen = new Pen(BorderColor)) g.DrawPath(pen, path);
            }
        }
    }

    // ============================================================ 聊天气泡面板（QQNT 风格）
    public class ChatBubblePanel : Panel
    {
        public Color BgColor = Color.FromArgb(30, 31, 34);
        public Color CardColor = Color.FromArgb(49, 51, 56);
        public Color LineColor = Color.FromArgb(60, 63, 69);
        public Color TextColor = Color.FromArgb(235, 237, 240);
        public Color SubColor = Color.FromArgb(150, 156, 166);
        public Color AccentColor = Color.FromArgb(0, 153, 255);
        readonly List<string[]> _rows = new List<string[]>();
        int _scroll = 0;

        public ChatBubblePanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void SetMessages(List<string[]> rows)
        {
            _rows.Clear();
            if (rows != null) _rows.AddRange(rows);
            _scroll = int.MaxValue;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _scroll -= e.Delta / 2;
            Invalidate();
        }

        static Color AvatarOf(string name)
        {
            Color[] palette = {
                Color.FromArgb(0,153,255), Color.FromArgb(76,175,80), Color.FromArgb(255,152,0),
                Color.FromArgb(156,39,176), Color.FromArgb(233,30,99), Color.FromArgb(0,150,136) };
            int h = 0;
            foreach (char ch in (name ?? "")) h = (h * 31 + ch) & 0x7fffffff;
            return palette[h % palette.Length];
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(BgColor);

            int padL = 16, av = 32, gap = 10;
            int maxW = (int)(Width * 0.62);

            using (var fBody = new Font("Microsoft YaHei UI", 10F))
            using (var fName = new Font("Microsoft YaHei UI", 8F))
            using (var fTime = new Font("Microsoft YaHei UI", 7.5F))
            using (var fmt = new StringFormat())
            {
                fmt.Trimming = StringTrimming.Word;

                // 先量一遍，算出总高
                var sizes = new List<SizeF[]>();
                int total = 8;
                for (int mi = 0; mi < _rows.Count; mi++)
                {
                    var r = _rows[mi];
                    bool isOut = r[0] == "out";
                    var szBody = g.MeasureString(r[3], fBody, maxW - 28);
                    var szName = g.MeasureString(r[1], fName);
                    float bw = Math.Min(szBody.Width + 26, maxW);
                    float bh = szBody.Height + 18;
                    bool sameM = mi > 0 && _rows[mi - 1][0] == r[0] && _rows[mi - 1][1] == r[1];
                    float nameH = (isOut || sameM) ? 0 : szName.Height + 2;
                    sizes.Add(new SizeF[] { new SizeF(bw, bh), new SizeF(0, nameH) });
                    total += (int)nameH + (int)bh + 22;
                }

                int viewH = Height;
                int maxScroll = Math.Max(0, total - viewH + 8);
                if (_scroll == int.MaxValue) _scroll = maxScroll;
                if (_scroll < 0) _scroll = 0;
                if (_scroll > maxScroll) _scroll = maxScroll;

                int y = 10 - _scroll;
                var avRect = Rectangle.Empty;

                for (int i = 0; i < _rows.Count; i++)
                {
                    var r = _rows[i];
                    bool isOut = r[0] == "out";
                    float bw = sizes[i][0].Width, bh = sizes[i][0].Height;
                    float nameH = sizes[i][1].Height;
                    if (y + nameH + bh < -40) { y += (int)nameH + (int)bh + 22; continue; }
                    if (y > Height + 40) break;

                    float textX;
                    bool sameAsPrev = i > 0 && _rows[i - 1][0] == r[0] && _rows[i - 1][1] == r[1];
                    if (isOut)
                    {
                        float bx = Width - padL - bw;
                        using (var br = new SolidBrush(SubColor))
                        {
                            var tw = g.MeasureString(r[2], fTime);
                            g.DrawString(r[2], fTime, br, bx - tw.Width - 8, y + bh - tw.Height - 2);
                        }
                        var rr = new RectangleF(bx, y, bw, bh);
                        using (var path = RoundRect(rr, 10))
                        using (var fill = new SolidBrush(AccentColor))
                            g.FillPath(fill, path);
                        using (var tb = new SolidBrush(Color.White))
                            g.DrawString(r[3], fBody, tb, new RectangleF(rr.X + 13, rr.Y + 9, rr.Width - 26, rr.Height - 18), fmt);
                        textX = bx;
                    }
                    else
                    {
                        // 头像
                        avRect = new Rectangle(padL, y, av, av);
                        using (var ab = new SolidBrush(AvatarOf(r[1])))
                            g.FillEllipse(ab, avRect);
                        string initial = (r[1] != null && r[1].Length > 0) ? r[1].Substring(r[1].Length - 1) : "?";
                        using (var wb = new SolidBrush(Color.White))
                        using (var fA = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold))
                        {
                            var sf = new StringFormat();
                            sf.Alignment = StringAlignment.Center;
                            sf.LineAlignment = StringAlignment.Center;
                            g.DrawString(initial, fA, wb, avRect, sf);
                        }
                        float bx = padL + av + gap;
                        using (var nb = new SolidBrush(SubColor))
                        {
                            if (!sameAsPrev) g.DrawString(r[1] + "  " + r[2], fName, nb, bx, y);
                        }
                        var rr = new RectangleF(bx, y + nameH, bw, bh);
                        using (var path = RoundRect(rr, 10))
                        using (var fill = new SolidBrush(CardColor))
                        {
                            g.FillPath(fill, path);
                            using (var pen = new Pen(LineColor))
                                g.DrawPath(pen, path);
                        }
                        using (var tb = new SolidBrush(TextColor))
                            g.DrawString(r[3], fBody, tb, new RectangleF(rr.X + 13, rr.Y + 9, rr.Width - 26, rr.Height - 18), fmt);
                        textX = bx;
                    }
                    y += (int)nameH + (int)bh + 22;
                }

                // 滚动条
                if (total > viewH)
                {
                    int trackH = Height - 8;
                    int thumbH = Math.Max(28, (int)((float)trackH * viewH / total));
                    int thumbY = 4 + (trackH - thumbH) * _scroll / Math.Max(1, total - viewH);
                    using (var sb = new SolidBrush(Color.FromArgb(80, 85, 95)))
                        g.FillRectangle(sb, Width - 6, thumbY, 4, thumbH);
                }
            }
        }

        static System.Drawing.Drawing2D.GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ============================================================ 双缓冲面板（消除切页闪烁）
    public class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint, true);
            DoubleBuffered = true;
            UpdateStyles();
        }
    }

    // ============================================================ 深色滚动条
    public static class DarkUi
    {
        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr h, string app, string idlist);
        [System.Runtime.InteropServices.DllImport("uxtheme.dll", EntryPoint = "#135")]
        static extern int SetPreferredAppMode(int mode);
        [System.Runtime.InteropServices.DllImport("uxtheme.dll", EntryPoint = "#133")]
        static extern bool AllowDarkModeForWindow(IntPtr h, bool allow);

        public static void Flatten(Control c)
        {
            try { if (c != null && c.IsHandleCreated) SetWindowTheme(c.Handle, "", ""); } catch { }
        }

        public static void Preferred(bool dark)
        {
            try { SetPreferredAppMode(dark ? 1 : 3); } catch { }
        }

        public static void Apply(Control root, bool dark)
        {
            if (root == null) return;
            try
            {
                if (root.IsHandleCreated)
                {
                    AllowDarkModeForWindow(root.Handle, dark);
                    SetWindowTheme(root.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
                }
            }
            catch { }
            foreach (Control c in root.Controls) Apply(c, dark);
        }
    }

    // ============================================================ 主题
    public class Theme
    {
        public Color Bg, Side, Card, Text, Sub, Border, Accent, AccentSoft, Danger, Ok, Hover;
        public bool Dark;

        public static Theme Get(bool dark)
        {
            Theme t = new Theme();
            t.Dark = dark;
            if (dark)
            {
                t.Bg = Color.FromArgb(24, 25, 27);
                t.Side = Color.FromArgb(30, 31, 34);
                t.Card = Color.FromArgb(38, 40, 44);
                t.Text = Color.FromArgb(232, 234, 238);
                t.Sub = Color.FromArgb(154, 160, 170);
                t.Border = Color.FromArgb(55, 58, 64);
                t.Accent = Color.FromArgb(77, 107, 254);
                t.AccentSoft = Color.FromArgb(46, 54, 84);
                t.Danger = Color.FromArgb(229, 72, 77);
                t.Ok = Color.FromArgb(60, 190, 130);
                t.Hover = Color.FromArgb(46, 48, 53);
            }
            else
            {
                t.Bg = Color.FromArgb(247, 248, 250);
                t.Side = Color.FromArgb(240, 242, 246);
                t.Card = Color.FromArgb(255, 255, 255);
                t.Text = Color.FromArgb(28, 32, 38);
                t.Sub = Color.FromArgb(110, 116, 128);
                t.Border = Color.FromArgb(226, 229, 235);
                t.Accent = Color.FromArgb(77, 107, 254);
                t.AccentSoft = Color.FromArgb(234, 239, 255);
                t.Danger = Color.FromArgb(220, 60, 66);
                t.Ok = Color.FromArgb(20, 160, 100);
                t.Hover = Color.FromArgb(232, 236, 243);
            }
            return t;
        }
    }

    // ============================================================ 圆角按钮
    public class RBtn : Control
    {
        public Color Fill = Color.White;
        public Color HoverFill = Color.Gainsboro;
        public Color TextColor = Color.Black;
        public Color BorderColor = Color.Transparent;
        public int Radius = 9;
        public bool Bold = false;
        public bool LeftAlign = false;
        bool _hover;

        public RBtn()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
        }

        static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            if (d <= 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = _hover ? HoverFill : Fill;
            using (var path = Round(r, Radius))
            {
                if (fill != Color.Transparent)
                    using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (BorderColor != Color.Transparent)
                    using (var pen = new Pen(BorderColor, 1)) g.DrawPath(pen, path);
            }
            var f = new Font(Font.FontFamily, Font.SizeInPoints, Bold ? FontStyle.Bold : FontStyle.Regular);
            var area = new Rectangle(LeftAlign ? 12 : 0, 0, Width - (LeftAlign ? 16 : 0), Height);
            TextRenderer.DrawText(g, Text, f, area, TextColor,
                (LeftAlign ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter) |
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            f.Dispose();
        }
    }

    // ============================================================ 圆角卡片
    public class RCard : Panel
    {
        public Color CardColor = Color.White;
        public Color BorderColor = Color.Gainsboro;
        public Color BgColor = Color.WhiteSmoke;
        public int Radius = 12;
        public int Inset = 6;      // 卡片四周留白（视觉上的间距）
        public int OffX = 12;      // 内容整体往里缩
        public int OffY = 8;

        public RCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            var c = e.Control;
            if (c.Dock == DockStyle.None)
                c.Location = new Point(c.Location.X + OffX, c.Location.Y + OffY);
            // Label / FlowLayoutPanel 背景透明，圆角卡片才不会被"色带"破坏
            try
            {
                if (c is Label || c is FlowLayoutPanel) c.BackColor = Color.Transparent;
            }
            catch { }
        }

        static GraphicsPath Round(Rectangle r, int rad)
        {
            var path = new GraphicsPath();
            int d = rad * 2;
            if (d <= 1) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(BgColor)) g.FillRectangle(bg, ClientRectangle);
            var r = new Rectangle(Inset, Inset, Width - Inset * 2 - 1, Height - Inset * 2 - 1);
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var path = Round(r, Radius))
            {
                using (var b = new SolidBrush(CardColor)) g.FillPath(b, path);
                if (BorderColor != Color.Transparent)
                    using (var pen = new Pen(BorderColor, 1)) g.DrawPath(pen, path);
            }
        }
    }

    // ============================================================ 配置
    public class Cfg
    {
        public string Host = "your-server.example.com";
        public string User = "ubuntu";
        public string Pass = "CHANGE_ME_SSH_PASSWORD";
        public string HostKey = "SHA256:CHANGE_ME_HOST_KEY";
        public string VBox = @"C:\Program Files\Oracle\VirtualBox\VBoxManage.exe";
        public string VmName = "QQbot-Win11";
        public string VmUser = "qqbot";
        public string VmPass = "CHANGE_ME_VM_PASSWORD";
        public bool Dark = true;
        public float Scale = 1.0f;
        public string WebuiToken = "CHANGE_ME_WEBUI_TOKEN";   // SnowLuma WebUI 端口（5099）不需要 token
        // 托盘
        public bool Tray = true;        // 最小化时收进托盘
        public bool TrayClose = true;   // 点关闭收进托盘（不退出）
        public bool TrayStart = false;  // 启动就藏进托盘

        static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FishBotConsole"); }
        }
        static string FilePath { get { return Path.Combine(Dir, "config.ini"); } }
        const string XorKey = "fish-bot-2026";

        public static string Obf(string s)
        {
            if (s == null) s = "";
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++) sb.Append((char)(s[i] ^ XorKey[i % XorKey.Length]));
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
        }
        public static string DeObf(string s)
        {
            try
            {
                var raw = Encoding.UTF8.GetString(Convert.FromBase64String(s));
                var sb = new StringBuilder();
                for (int i = 0; i < raw.Length; i++) sb.Append((char)(raw[i] ^ XorKey[i % XorKey.Length]));
                return sb.ToString();
            }
            catch { return ""; }
        }

        public static Cfg Load()
        {
            var c = new Cfg();
            try
            {
                if (!File.Exists(FilePath)) return c;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var p = line.Split(new char[] { '=' }, 2);
                    if (p.Length != 2) continue;
                    string k = p[0].Trim(), v = p[1].Trim();
                    if (k == "host") c.Host = v;
                    else if (k == "user") c.User = v;
                    else if (k == "pass") c.Pass = DeObf(v);
                    else if (k == "hostkey") c.HostKey = v;
                    else if (k == "vbox") c.VBox = v;
                    else if (k == "vm") c.VmName = v;
                    else if (k == "vmuser") c.VmUser = v;
                    else if (k == "vmpass") c.VmPass = DeObf(v);
                    else if (k == "dark") c.Dark = v == "1";
                    else if (k == "scale") { float f; if (float.TryParse(v, out f)) c.Scale = Math.Max(0.8f, Math.Min(1.5f, f)); }
                    else if (k == "webuitoken") c.WebuiToken = v;
                    else if (k == "tray") c.Tray = v != "0";
                    else if (k == "trayclose") c.TrayClose = v != "0";
                    else if (k == "traystart") c.TrayStart = v != "0";
                }
            }
            catch { }
            return c;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                sb.AppendLine("host=" + Host);
                sb.AppendLine("user=" + User);
                sb.AppendLine("pass=" + Obf(Pass));
                sb.AppendLine("hostkey=" + HostKey);
                sb.AppendLine("vbox=" + VBox);
                sb.AppendLine("vm=" + VmName);
                sb.AppendLine("vmuser=" + VmUser);
                sb.AppendLine("vmpass=" + Obf(VmPass));
                sb.AppendLine("dark=" + (Dark ? "1" : "0"));
                sb.AppendLine("scale=" + Scale.ToString("0.00"));
                sb.AppendLine("webuitoken=" + WebuiToken);
                sb.AppendLine("tray=" + (Tray ? "1" : "0"));
                sb.AppendLine("trayclose=" + (TrayClose ? "1" : "0"));
                sb.AppendLine("traystart=" + (TrayStart ? "1" : "0"));
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    // ============================================================ 主窗口
    public class MainForm : Form
    {
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        // WS_EX_COMPOSITED：整个窗口（含所有子控件）由系统一次性合成，切页不再闪
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x02000000;
                return cp;
            }
        }

        const string GuestCmd = @"C:\Users\qqbot\Downloads\ctl_cmd.json";
        const string GuestRes = @"C:\Users\qqbot\Downloads\ctl_result.json";

        Cfg _cfg;
        Theme _th;
        float S = 1f;
        Panel _side, _content;
        string _page = "cloud";
        Label _big, _sub, _vmLine;
        RichTextBox _logBox;
        Panel _curPage;
        int _cardW = 800;   // 卡片内可用宽度（按窗口算，用来折行）
        // ---- 切页不丢状态：把上次查到的结果和日志留在内存里 ----
        static readonly System.Collections.Generic.List<string> _logLines = new System.Collections.Generic.List<string>();
        string _bigText = null;
        Color _bigColor = Color.Empty;
        string[] _subParts = null;
        DateTime _statusAt = DateTime.MinValue;
        ToolTip _tip;
        readonly System.Collections.Generic.Dictionary<string, Panel> _pages =
            new System.Collections.Generic.Dictionary<string, Panel>();

        Control _activeNav;
        bool _busy;
        NotifyIcon _tray;
        bool _exiting;          // true = 真的要退出（托盘「退出」或关机）
        bool _trayHinted;       // 第一次收进托盘时给个气泡提示

        public MainForm()
        {
            _cfg = Cfg.Load();
            _th = Theme.Get(_cfg.Dark);
            S = _cfg.Scale;

            Text = "肥鱼 · 控制台";
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            ResizeForScale();
            MinimumSize = new Size(Sc(900), Sc(620));
            BackColor = _th.Bg;
            ForeColor = _th.Text;
            Font = new Font("Microsoft YaHei UI", 9F * S);
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _tip = new ToolTip();
            _tip.AutoPopDelay = 20000;
            _tip.InitialDelay = 350;
            _tip.ReshowDelay = 100;
            _tip.ShowAlways = true;

            BuildAll();
            var sendTimer = new System.Windows.Forms.Timer();
            sendTimer.Interval = 8000;
            sendTimer.Tick += (s, e) => { if (_page == "cloud") FetchMsgLog(); };
            sendTimer.Start();

            var resizeTimer = new System.Windows.Forms.Timer();
            resizeTimer.Interval = 260;
            resizeTimer.Tick += (s2, e2) => { resizeTimer.Stop(); FastRebuild(); };
            Resize += (s2, e2) =>
            {
                if (_cfg.Tray && WindowState == FormWindowState.Minimized) { HideToTray(false); return; }
                resizeTimer.Stop(); resizeTimer.Start();
            };

            // 关闭窗口 = 收进托盘（托盘菜单的「退出」才是真退出）
            FormClosing += (s2, e2) =>
            {
                if (!_exiting && _cfg.Tray && _cfg.TrayClose)
                {
                    e2.Cancel = true;
                    HideToTray(false);
                }
            };

            SetupTray();

            Load += (s, e) =>
            {
                ShowPage(_page);          // 窗口已显示、尺寸已确定，这时候建页面才正确
                BeginInvoke(new Action(() =>
                {
                    // 把其他页面提前建好，以后点开只是挂载 + 显示，不会再闪
                    foreach (var pid in new string[] { "local", "settings", "about", "dev", "paths", "docs" })
                    {
                        try { if (!_pages.ContainsKey(pid)) { var pg = BuildPage(pid); if (pg != null) _pages[pid] = pg; } }
                        catch { }
                    }
                }));
                DarkUi.Preferred(_cfg.Dark);
                DarkUi.Apply(this, _cfg.Dark);
                if (_cfg.Tray && _cfg.TrayStart) BeginInvoke(new Action(() => HideToTray(true)));
                Log("控制台已启动 · 服务器 " + _cfg.Host + " · " + (_cfg.Dark ? "夜间模式" : "白天模式") + " · 缩放 " + S.ToString("0.00") + "x");
                RefreshStatus();
            };
            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 30 * 60 * 1000;   // 半小时自动查一次（其余时间用手动刷新）
            timer.Tick += (s, e) => { if (!_busy && _page == "cloud") RefreshStatus(); };
            timer.Start();
        }

        int Sc(int v) { return (int)Math.Round(v * S); }
        Font F(float pt) { return new Font("Microsoft YaHei UI", pt * S); }
        Font FB(float pt) { return new Font("Microsoft YaHei UI", pt * S, FontStyle.Bold); }

        // ------------------------------------------------ 整体布局
        void BuildAll()
        {
            SuspendLayout();
            Controls.Clear();
            _pages.Clear();      // 容器重建了，缓存里的页面已经死了，必须清掉
            _logBox = null;
            _big = null;
            _sub = null;
            _vmLine = null;

            _side = new BufferedPanel();
            _side.Dock = DockStyle.Left;
            _side.Width = Sc(208);
            _side.BackColor = _th.Side;
            Controls.Add(_side);

            var logo = new Label();
            logo.Text = "肥鱼控制台";
            logo.Font = FB(13f);
            logo.ForeColor = _th.Text;
            logo.Location = new Point(Sc(16), Sc(18));
            logo.Size = new Size(Sc(180), Sc(28));
            _side.Controls.Add(logo);

            var ver = new Label();
            ver.Text = "v2.0 · 云端 + 本地";
            ver.Font = F(8f);
            ver.ForeColor = _th.Sub;
            ver.Location = new Point(Sc(18), Sc(48));
            ver.Size = new Size(Sc(180), Sc(18));
            _side.Controls.Add(ver);

            string[][] nav = new string[][]
            {
                new string[] { "cloud", "控制云端" },
                new string[] { "botctl", "机器人控件" },
                new string[] { "local", "控制本地" },
                new string[] { "settings", "设置" },
                new string[] { "about", "个人信息" },
                new string[] { "dev", "开发信息" },
                new string[] { "paths", "文件地址" },
                new string[] { "docs", "使用文档" }
            };
            int y = Sc(82);
            foreach (var n in nav)
            {
                var b = new RBtn();
                b.Text = n[1];
                b.LeftAlign = true;
                b.Radius = Sc(8);
                b.Location = new Point(Sc(12), y);
                b.Size = new Size(Sc(184), Sc(36));
                b.Fill = Color.Transparent;
                b.HoverFill = _th.Hover;
                b.TextColor = _th.Sub;
                b.BorderColor = Color.Transparent;
                b.Font = F(9.5f);
                string id = n[0];
                b.Tag = id;
                b.Click += (s, e) => ShowPage(id);
                _side.Controls.Add(b);
                y += Sc(42);
            }

            var ts = new RBtn();
            ts.Text = _cfg.Dark ? "切到白天" : "切到夜间";
            ts.Radius = Sc(8);
            ts.LeftAlign = true;
            ts.Location = new Point(Sc(12), _side.Height - Sc(96));
            ts.Size = new Size(Sc(184), Sc(34));
            ts.Fill = _th.Card;
            ts.HoverFill = _th.Hover;
            ts.TextColor = _th.Text;
            ts.BorderColor = _th.Border;
            ts.Font = F(9f);
            ts.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            ts.Click += (s, e) => { _cfg.Dark = !_cfg.Dark; _cfg.Save(); ApplyLook(); };
            _side.Controls.Add(ts);

            var fs = new RBtn();
            fs.Text = "字号 " + S.ToString("0.00") + "x";
            fs.Radius = Sc(8);
            fs.LeftAlign = true;
            fs.Location = new Point(Sc(12), _side.Height - Sc(56));
            fs.Size = new Size(Sc(184), Sc(34));
            fs.Fill = Color.Transparent;
            fs.HoverFill = _th.Hover;
            fs.TextColor = _th.Sub;
            fs.BorderColor = Color.Transparent;
            fs.Font = F(9f);
            fs.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            fs.Click += (s, e) => ShowPage("settings");
            _side.Controls.Add(fs);

            _content = new BufferedPanel();
            _content.Dock = DockStyle.Fill;
            _content.BackColor = _th.Bg;
            _content.Padding = new Padding(Sc(22), Sc(18), Sc(22), Sc(16));
            _content.AutoScroll = true;
            Controls.Add(_content);


            _content = new BufferedPanel();
            _content.Dock = DockStyle.Fill;
            _content.BackColor = _th.Bg;
            _content.Padding = new Padding(Sc(22), Sc(18), Sc(22), Sc(16));
            _content.AutoScroll = true;
            Controls.Add(_content);



            ResumeLayout(true);
            _content.BringToFront();    // Fill 要在最前层，停靠顺序才会排在 Left 之后 → 占剩余区域
            PerformLayout();
            _content.PerformLayout();
        }

        void ResizeForScale()
        {
            // 窗口太窄时按钮行会折行、把卡片撑破，所以给一个合理的最小尺寸
            MinimumSize = new Size(Sc(1040), Sc(660));
            // 注意：这里故意不用 Sc()——窗口大小固定，字号变大时靠"文字折行 + 内容滚动"来适配，
            // 否则 1.5x 时窗口会超出屏幕，右侧仍然被切掉。
            var wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(1150, wa.Width - 60), Math.Min(770, wa.Height - 70));
        }

        void ScheduleDark(Control scope)
        {
            try
            {
                if (IsHandleCreated) BeginInvoke(new Action(() =>
                {
                    // 应用深色滚动条会给每个控件重设主题，期间也要禁重绘，否则又是一闪
                    IntPtr h = _content != null && _content.IsHandleCreated ? _content.Handle : IntPtr.Zero;
                    if (h != IntPtr.Zero) SendMessage(h, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
                    DarkUi.Apply(scope ?? this, _cfg.Dark);
                    if (h != IntPtr.Zero)
                    {
                        SendMessage(h, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                        _content.Invalidate(true);
                        _content.Update();
                    }
                }));
                else
                {
                    EventHandler once = null;
                    once = (a, b) => { try { Shown -= once; } catch { } DarkUi.Apply(this, _cfg.Dark); };
                    Shown += once;
                }
            }
            catch { }
        }

        void ApplyLook()
        {
            ClearPages();
            _th = Theme.Get(_cfg.Dark);
            BackColor = _th.Bg;
            ForeColor = _th.Text;
            Font = new Font("Microsoft YaHei UI", 9F * S);
            ResizeForScale();          // 先按新字号把窗口撑开
            BuildAll();
            ShowPage(_page);
            DarkUi.Preferred(_cfg.Dark);
            ScheduleDark(this);
            Log("外观已切换：" + (_cfg.Dark ? "夜间" : "白天") + " · 字号 " + S.ToString("0.00") + "x");
            RefreshStatus();
        }

        public void GotoPage(string id) { ShowPage(id); }

        // 供自检截图用：切页后把内容滚到指定位置
        public void ScrollPageTo(int y)
        {
            if (_content == null) return;
            foreach (Control c in _content.Controls)
            {
                var sc = c as ScrollableControl;
                if (sc != null && sc.AutoScroll)
                {
                    sc.AutoScrollPosition = new Point(0, Math.Max(0, y));
                    sc.PerformLayout();
                    return;
                }
            }
        }

        Panel BuildPage(string id)
        {
            if (id == "cloud") return PageCloud();
            if (id == "botctl") return PageBotCtl();
            if (id == "local") return PageLocal();
            if (id == "settings") return PageSettings();
            if (id == "about") return PageAbout();
            if (id == "dev") return PageDev();
            if (id == "paths") return PagePaths();
            if (id == "docs") return PageDocs();
            return null;
        }

        // ============================================================ 托盘
        void SetupTray()
        {
            try
            {
                if (_tray != null) return;
                _tray = new NotifyIcon();
                try { _tray.Icon = Icon ?? Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                catch { _tray.Icon = SystemIcons.Application; }
                _tray.Text = "肥鱼控制台 · " + _cfg.Host;
                _tray.Visible = true;

                var menu = new ContextMenuStrip();
                menu.Font = new Font("Microsoft YaHei UI", 9F);
                var miShow = new ToolStripMenuItem("显示控制台");
                miShow.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
                miShow.Click += (s2, e2) => ShowFromTray();
                var miStatus = new ToolStripMenuItem("刷新状态");
                miStatus.Click += (s2, e2) => { ShowFromTray(); RefreshStatus(); };
                var miChat = new ToolStripMenuItem("打开聊天记录");
                miChat.Click += (s2, e2) => { ShowFromTray(); BeginInvoke(new Action(() => ShowChatWindow())); };
                var miExit = new ToolStripMenuItem("退出控制台");
                miExit.Click += (s2, e2) =>
                {
                    _exiting = true;
                    if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
                    Close();
                };
                menu.Items.Add(miShow);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(miStatus);
                menu.Items.Add(miChat);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(miExit);
                _tray.ContextMenuStrip = menu;
                _tray.DoubleClick += (s2, e2) => ShowFromTray();
                _tray.MouseClick += (s2, e2) =>
                {
                    if (e2.Button == MouseButtons.Left) ShowFromTray();
                };
            }
            catch (Exception ex)
            {
                Log("托盘图标创建失败：" + ex.Message);
            }
        }

        void HideToTray(bool silent)
        {
            try
            {
                Hide();
                ShowInTaskbar = false;
                if (_tray != null)
                {
                    _tray.Visible = true;
                    _tray.Text = "肥鱼控制台 · " + _cfg.Host;
                    if (!silent && !_trayHinted)
                    {
                        _trayHinted = true;
                        try
                        {
                            _tray.ShowBalloonTip(2500, "肥鱼控制台还在后台",
                                "点托盘图标可以重新打开；要真退出请右键托盘图标 →「退出控制台」。",
                                ToolTipIcon.Info);
                        }
                        catch { }
                    }
                }
                Log("已收进托盘（" + DateTime.Now.ToString("HH:mm:ss") + "）");
            }
            catch (Exception ex)
            {
                Log("收进托盘失败：" + ex.Message);
            }
        }

        public void ShowFromTray()
        {
            try
            {
                ShowInTaskbar = true;
                if (!Visible) Show();
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
                BringToFront();
                TopMost = true;
                TopMost = false;
            }
            catch { }
        }

        // 重建所有缓存的页面（开关状态变了就靠它刷新按钮文字）
        void RefreshPages()
        {
            try
            {
                _pages.Clear();
                ShowPage(_page);
            }
            catch { }
        }

        // 丢掉缓存（换主题 / 换字号时才需要重建）
        void ClearPages()
        {
            _content.SuspendLayout();

            _pages.Clear();

            _big = null; _sub = null; _vmLine = null; _logBox = null;
            _content.ResumeLayout();
        }

        void DumpTree(Control c, int depth)
        {
            try
            {
                if (c == null || depth > 3) return;
                File.AppendAllText(_logFile, new string(' ', depth * 2)
                    + (c.GetType().Name) + " vis=" + c.Visible + " bounds=" + c.Bounds
                    + " kids=" + c.Controls.Count + "\r\n");
                foreach (Control k in c.Controls) DumpTree(k, depth + 1);
            }
            catch { }
        }

        void ShowPage(string id)
        {
            _page = id;
            foreach (Control c in _side.Controls)
            {
                var b = c as RBtn;
                if (b == null || b.Tag == null) continue;
                bool on = (string)b.Tag == id;
                b.Fill = on ? _th.AccentSoft : Color.Transparent;
                b.TextColor = on ? _th.Accent : _th.Sub;
                b.Bold = true;          // 固定粗体：切换粗细会让文字宽度变化，看起来像"闪"
                b.Invalidate();
            }
            _cardW = Math.Max(Sc(320), ClientSize.Width - Sc(208) - Sc(44) - Sc(30));

            Panel p;
            if (!_pages.TryGetValue(id, out p))
            {
                p = BuildPage(id);
                if (p == null) return;
                _pages[id] = p;
            }
            if (p.Parent != _content)
            {
                p.Dock = DockStyle.Fill;
                p.AutoScroll = true;
                p.BackColor = _th.Bg;
                _content.SuspendLayout();
                _content.Controls.Add(p);
                AutoHeight(p);
                ClampWidth(p);
                RelayoutCards(p);   // 内部会调用 FitCardHeights
                _content.ResumeLayout();
                p.CreateControl();
                DarkUi.Flatten(p);        // 只给这一层（滚动条）上深色主题，不遍历整页
                ScheduleDark(p);
            }
            _curPage = p;                      // 记住当前页，窗口缩放时用它重排
            foreach (var kv in _pages)
                if (kv.Value.Visible != (kv.Key == id)) kv.Value.Visible = (kv.Key == id);
            p.BringToFront();
            try { p.AutoScrollPosition = new Point(0, 0); } catch { }   // 切回来时从头看，不要停在上次滚的位置

            // 第一次进「控制云端」且还没有任何状态时才查一次；之后切页只显示缓存
            if (id == "cloud" && _statusAt == DateTime.MinValue)
            {
                if (IsHandleCreated) BeginInvoke(new Action(() => RefreshStatus()));
                else
                {
                    EventHandler once = null;
                    once = (a, b) => { try { Shown -= once; } catch { } RefreshStatus(); };
                    Shown += once;
                }
            }
        }

        // 按宽度模拟 FlowLayoutPanel(LeftToRight, WrapContents) 的折行，返回总高度
        static int MeasureWrappedHeight(FlowLayoutPanel line, int width)
        {
            int x = 0, y = 0, rowH = 0, count = 0;
            foreach (Control c in line.Controls)
            {
                if (!c.Visible) continue;
                count++;
                int w = c.Width + c.Margin.Horizontal;
                int h = c.Height + c.Margin.Vertical;
                if (x > 0 && x + w > width)
                {
                    y += rowH;
                    x = 0;
                    rowH = 0;
                }
                x += w;
                if (h > rowH) rowH = h;
            }
            if (count == 0) return 0;
            y += rowH;
            return y + 4;
        }

        // 按当前宽度把这一页重新建一遍（宽度变了，按钮行该在哪折就在哪折）
        void FastRebuild()
        {
            if (string.IsNullOrEmpty(_page)) return;
            int scroll = 0;
            try { if (_curPage != null) scroll = -_curPage.AutoScrollPosition.Y; } catch { }
            try
            {
                _pages.Clear();
                _content.SuspendLayout();
                _content.Controls.Clear();
                _content.ResumeLayout();
                ShowPage(_page);
                if (_curPage != null && scroll > 0) _curPage.AutoScrollPosition = new Point(0, scroll);
            }
            catch { }
        }

        // 窗口变窄 → 按钮行折行后，卡片必须跟着长高，否则按钮会被卡片下边缘切掉
        void RelayoutCards(Control page)
        {
            if (page == null) return;
            FitCardHeights(page);
            if (true) return;   // 下面的旧逻辑保留但不再执行
            foreach (Control c in page.Controls)
            {
                var card = c as RCard;
                if (card == null) continue;
                var body = card.Tag as FlowLayoutPanel;
                if (body == null) continue;
                int inner = Math.Max(Sc(160), card.Width - card.Padding.Horizontal - Sc(6));
                foreach (Control rowCtl in body.Controls)
                {
                    var line = rowCtl as FlowLayoutPanel;
                    if (line == null) continue;
                    // FlowLayoutPanel 自动换行时 AutoSize 高度经常算不准（只报一行），
                    // 这里自己按宽度模拟折行、算出真实高度，再关掉 AutoSize 定死。
                    line.AutoSize = false;
                    int lineH = MeasureWrappedHeight(line, inner);
                    line.MaximumSize = new Size(inner, 0);
                    line.Size = new Size(inner, lineH);
                    line.PerformLayout();
                }
                int bottom = 0;
                foreach (Control k in body.Controls)
                    bottom = Math.Max(bottom, k.Bottom);
                int need = bottom + card.Padding.Top + card.Padding.Bottom + Sc(16);
                card.AutoSize = false;                      // AutoSize 会把我们算好的高度改回去，先关掉
                card.Dock = DockStyle.Top;                  // 别让表格行把高度压回去
                if (need > card.Height) card.Height = need;
                card.PerformLayout();
                // 把所在表格行改成固定高度，行高不再反过来压卡片
                var tlp = card.Parent as TableLayoutPanel;
                if (tlp != null)
                {
                    int idx = tlp.GetRow(card);
                    if (idx >= 0 && idx < tlp.RowStyles.Count)
                        tlp.RowStyles[idx] = new RowStyle(SizeType.Absolute, card.Height + card.Margin.Vertical);
                    tlp.PerformLayout();
                }
            }
        }

        // 任何子控件超出卡片内宽就收回来，保证不会跑出圆角边框
        void ClampWidth(Control page)
        {
            if (page == null) return;
            foreach (Control c in page.Controls)
            {
                var card = c as RCard;
                if (card == null) continue;
                int inner = _cardW;
                if (inner <= 0) continue;
                foreach (Control k in card.Controls)
                {
                    if (k.Dock != DockStyle.None) continue;
                    if (!(k is TextBox) && !(k is RBtn)) continue;   // 只收输入框/按钮
                    int maxW = inner - k.Left;
                    if (maxW < Sc(80)) continue;
                    if (k.Width > maxW) k.Width = maxW;
                }
            }
        }

        // 卡片高度：自己算（FlowLayoutPanel 的 AutoSize 在折行时会算错，不能信）
        void AutoHeight(Control page) { FitCardHeights(page); }

        void FitCardHeights(Control page)
        {
            if (page == null) return;
            foreach (Control c in page.Controls)
            {
                var card = c as RCard;
                if (card == null) continue;
                var body = card.Tag as FlowLayoutPanel;
                if (body == null) continue;

                int inner = Math.Max(Sc(160), card.Width - card.Padding.Horizontal - Sc(8));
                int sum = 0;
                foreach (Control k in body.Controls)
                {
                    var row = k as FlowLayoutPanel;
                    if (row != null)
                    {
                        row.AutoSize = false;                       // 关掉不可靠的自动高度
                        row.MaximumSize = new Size(inner, 0);
                        int h = Math.Max(Sc(28), MeasureWrappedHeight(row, inner));
                        if (row.Width != inner || row.Height != h) row.Size = new Size(inner, h);
                        row.PerformLayout();
                    }
                    sum += k.Height + k.Margin.Vertical;
                }
                body.AutoSize = false;
                body.Dock = DockStyle.Top;
                // 保险：再和 FlowLayoutPanel 自己算的首选高度取较大值（有时它会比手算多一行）
                try { int pref = body.PreferredSize.Height; if (pref > sum) sum = pref; } catch { }
                if (body.Height != sum) body.Height = sum;
                card.AutoSize = false;
                int want = sum + card.Padding.Top + card.Padding.Bottom + Sc(10);
                if (card.Height != want) card.Height = want;
                card.Dock = DockStyle.Top;
                card.PerformLayout();

                var tlp = card.Parent as TableLayoutPanel;
                if (tlp != null)
                {
                    int idx = tlp.GetRow(card);
                    if (idx >= 0 && idx < tlp.RowStyles.Count)
                        tlp.RowStyles[idx] = new RowStyle(SizeType.Absolute, card.Height + card.Margin.Vertical);
                    tlp.PerformLayout();
                }
            }
        }


        // ------------------------------------------------ 小部件
        Panel Card(string title, int h)
        {
            var c = new RCard();
            c.BackColor = _th.Bg;
            c.CardColor = _th.Card;
            c.BorderColor = _th.Border;
            c.BgColor = _th.Bg;
            c.Radius = Sc(12);
            c.Inset = Sc(6);
            c.OffX = Sc(14);
            c.OffY = Sc(8);
            c.Height = Sc(h) + Sc(26);   // 12 圆角间隙 + 14 底部留白
            c.Dock = DockStyle.Top;
            if (title.Length > 0)
            {
                var t = new Label();
                t.Text = title;
                t.Font = FB(10f);
                t.ForeColor = _th.Text;
                t.Location = new Point(Sc(10), Sc(6));
                t.Size = new Size(Sc(700), Math.Max(Sc(22), (int)Math.Ceiling(10f * 2.6 * S)));
                t.UseCompatibleTextRendering = true;
                c.Controls.Add(t);
            }
            return c;
        }

        FlowLayoutPanel Flow(int y)
        {
            var f = new FlowLayoutPanel();
            f.Location = new Point(Sc(14), Sc(y));
            f.Size = new Size(Sc(860), Sc(48));
            f.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            f.BackColor = _th.Card;
            f.WrapContents = true;
            f.AutoScroll = false;
            return f;
        }

        RBtn Btn(string text, int x, int y, int w, int h, bool primary, bool danger)
        {
            var b = new RBtn();
            b.Text = text;
            b.Radius = Sc(9);
            b.Location = new Point(Sc(x), Sc(y));
            b.Size = new Size(Sc(w), Sc(h));
            b.Fill = danger ? _th.Danger : (primary ? _th.Accent : _th.Card);
            b.HoverFill = danger ? Color.FromArgb(240, 92, 97)
                        : primary ? Color.FromArgb(96, 122, 255) : _th.Hover;
            b.TextColor = (primary || danger) ? Color.White : _th.Text;
            b.BorderColor = (primary || danger) ? Color.Transparent : _th.Border;
            b.Bold = true;
            b.Font = F(9.5f);
            b.Margin = new Padding(0, 0, Sc(10), Sc(8));
            return b;
        }

        Label Lbl(string text, int x, int y, int w, float pt, bool bold, Color c)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = false;
            l.Font = bold ? FB(pt) : F(pt);
            l.ForeColor = c;
            l.BackColor = Color.Transparent;
            // 宽度：不超过卡片可用宽度，这样文字会自动折行而不是被裁掉
            int width = Math.Min(Sc(w), Math.Max(Sc(120), _cardW - Sc(x)));
            l.Location = new Point(Sc(x), Sc(y));
            var sz = TextRenderer.MeasureText(text, l.Font, new Size(width, int.MaxValue),
                                              TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            l.Size = new Size(width, Math.Max(Sc(18), sz.Height + Sc(3)));
            return l;
        }

        // 控件右边缘贴齐卡片右侧时的 x（设计单位）
        int RightX(int w) { return (int)Math.Round((_cardW - Sc(6)) / (double)S) - w; }

        // 单行 + 省略号 + 悬浮显示全文
        Label Ellip(string text, int x, int y, int w, float pt, bool bold, Color c)
        {
            var l = Lbl(text, x, y, w, pt, bold, c);
            l.AutoEllipsis = true;
            l.Height = Math.Max(Sc(18), (int)Math.Ceiling(pt * 2.6 * S));
            if (_tip != null) _tip.SetToolTip(l, text);
            return l;
        }

        void Tip(Control c, string text)
        {
            if (_tip != null && c != null && !string.IsNullOrEmpty(text)) _tip.SetToolTip(c, text);
        }

        // 次级按钮（浅蓝底 + 蓝字），比白底更清楚
        RBtn Soft(string text, int x, int y, int w, int h)
        {
            var b = Btn(text, x, y, w, h, false, false);
            b.Fill = _th.AccentSoft;
            b.HoverFill = _th.Accent;
            b.TextColor = _th.Accent;
            b.BorderColor = Color.Transparent;
            return b;
        }

        // 依次往下排一段文字，返回下一个可用 y（设计单位）
        int WrapNext(Control parent, string text, int x, int y, int w, float pt, bool bold, Color c, int gap)
        {
            var l = Lbl(text, x, y, w, pt, bold, c);
            parent.Controls.Add(l);
            return y + (int)Math.Ceiling((l.Height + Sc(gap)) / (double)S);
        }

        static readonly string _logFile = Path.Combine(Path.GetTempPath(), "fishconsole.log");

        void Log(string msg)
        {
            try
            {
                File.AppendAllText(_logFile, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\r\n",
                                   new UTF8Encoding(false));
            }
            catch { }
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg;
            try
            {
                _logLines.Add(line);
                if (_logLines.Count > 400) _logLines.RemoveRange(0, _logLines.Count - 400);
            }
            catch { }
            if (_logBox == null) return;
            if (_logBox.InvokeRequired) { _logBox.BeginInvoke(new Action<string>(Log), msg); return; }
            _logBox.AppendText(line + "\r\n");
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }

        // ============================================================ 弹性布局工具箱
        // 页面 = 可滚动容器 + TableLayoutPanel（行自适应）；
        // 卡片 = RCard + FlowLayoutPanel(TopDown)，字体放大时自动长高、自动换行。

        Panel Grid(out TableLayoutPanel table)
        {
            var scroll = new BufferedPanel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            scroll.BackColor = _th.Bg;

            var t = new TableLayoutPanel();
            t.Dock = DockStyle.Top;
            t.AutoSize = true;
            t.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            t.ColumnCount = 1;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            t.BackColor = _th.Bg;
            t.Padding = new Padding(0, 0, Sc(4), 0);
            scroll.Controls.Add(t);
            table = t;
            return scroll;
        }

        void AddRow(Panel page, Control c, bool fill)
        {
            var t = page as TableLayoutPanel;
            if (t == null) t = (TableLayoutPanel)page.Controls[0];
            if (fill)
            {
                page.AutoScroll = false;
                t.Dock = DockStyle.Fill;
                t.AutoSize = false;
                int k = t.RowCount;
                t.RowCount = k + 1;
                t.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
                c.Dock = DockStyle.Fill;
                c.Margin = new Padding(0);
                t.Controls.Add(c, 0, k);
                return;
            }
            int i = t.RowCount;
            t.RowCount = i + 1;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            c.Dock = DockStyle.Fill;
            c.Margin = new Padding(0, 0, 0, Sc(10));
            t.Controls.Add(c, 0, i);
        }

        RCard FlexCard(string title)
        {
            var card = new RCard();
            card.BackColor = _th.Bg;
            card.CardColor = _th.Card;
            card.BorderColor = _th.Border;
            card.BgColor = _th.Bg;
            card.Radius = Sc(12);
            card.Inset = Sc(6);
            card.OffX = 0;
            card.OffY = 0;
            card.AutoSize = true;
            card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            card.Padding = new Padding(Sc(16), Sc(12), Sc(16), Sc(16));
            card.Margin = new Padding(0, 0, 0, Sc(8));

            var body = new FlowLayoutPanel();
            body.FlowDirection = FlowDirection.TopDown;
            body.WrapContents = false;
            body.AutoSize = true;
            body.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            body.Dock = DockStyle.Top;
            body.BackColor = Color.Transparent;
            body.Margin = new Padding(0);
            card.Controls.Add(body);
            card.Tag = body;

            if (title.Length > 0)
            {
                var t = new Label();
                t.Text = title;
                t.Font = FB(10f);
                t.ForeColor = _th.Text;
                t.BackColor = Color.Transparent;
                t.AutoSize = true;
                t.Margin = new Padding(0, 0, 0, Sc(10));
                body.Controls.Add(t);
            }
            return card;
        }

        FlowLayoutPanel Body(RCard card) { return (FlowLayoutPanel)card.Tag; }

        FlowLayoutPanel Line(FlowLayoutPanel body)
        {
            var l = new FlowLayoutPanel();
            l.FlowDirection = FlowDirection.LeftToRight;
            l.WrapContents = true;
            l.AutoSize = true;
            l.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            l.BackColor = Color.Transparent;
            l.Margin = new Padding(0, 0, 0, Sc(4));
            l.MaximumSize = new Size(Math.Max(Sc(200), _cardW), 0);
            body.Controls.Add(l);
            return l;
        }

        Label Txt(string text, float pt, bool bold, Color c)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Font = bold ? FB(pt) : F(pt);
            l.ForeColor = c;
            l.BackColor = Color.Transparent;
            l.MaximumSize = new Size(Math.Max(Sc(180), _cardW - Sc(24)), 0);
            l.Margin = new Padding(0, 0, 0, Sc(6));
            return l;
        }

        RBtn FBtn(string text, bool primary, bool danger)
        {
            var b = new RBtn();
            b.Text = text;
            b.Radius = Sc(9);
            b.Font = F(9.5f);
            b.Bold = true;
            var sz = TextRenderer.MeasureText(text, b.Font);
            b.Size = new Size(sz.Width + Sc(30), Sc(38));
            b.Fill = danger ? _th.Danger : (primary ? _th.Accent : _th.Card);
            b.HoverFill = danger ? Color.FromArgb(240, 92, 97)
                        : primary ? Color.FromArgb(96, 122, 255) : _th.Hover;
            b.TextColor = (primary || danger) ? Color.White : _th.Text;
            b.BorderColor = (primary || danger) ? Color.Transparent : _th.Border;
            b.Margin = new Padding(0, 0, Sc(8), Sc(8));
            return b;
        }

        RBtn SoftBtn(string text)
        {
            var b = FBtn(text, false, false);
            b.Fill = _th.AccentSoft;
            b.HoverFill = _th.Accent;
            b.TextColor = _th.Accent;
            b.BorderColor = Color.Transparent;
            return b;
        }

        TextBox InputFlex(string label, string val, FlowLayoutPanel body, bool secret)
        {
            var row = Line(body);
            row.WrapContents = false;
            var lb = Txt(label, 9f, false, _th.Sub);
            lb.MinimumSize = new Size(Sc(70), 0);
            row.Controls.Add(lb);
            var t = new TextBox();
            t.Text = val;
            t.Font = F(9.5f);
            t.BorderStyle = BorderStyle.FixedSingle;
            t.BackColor = _th.Dark ? Color.FromArgb(30, 31, 34) : Color.White;
            t.ForeColor = _th.Text;
            if (secret) t.UseSystemPasswordChar = true;
            t.Size = new Size(Math.Max(Sc(180), _cardW - Sc(120)), Sc(28));
            t.Margin = new Padding(0, 0, 0, Sc(6));
            row.Controls.Add(t);
            return t;
        }

        // ============================================================ 页面：控制云端
        Panel PageCloud()
        {
            TableLayoutPanel tbl;
            var page = Grid(out tbl);

            var head = FlexCard("云端服务器 · " + _cfg.Host);
            // 有上次结果就直接显示，不必重新查（切页不丢状态）
            _big = Txt(_bigText != null ? _bigText : "● 正在查询…", 14f, true,
                       _bigColor != Color.Empty ? _bigColor : _th.Sub);
            Body(head).Controls.Add(_big);
            _sub = Txt("", 9f, false, _th.Sub);
            Body(head).Controls.Add(_sub);
            if (_subParts != null && _subParts.Length > 0)
            {
                _sub.Text = FlowJoin(_subParts, _sub.Font, AvailW(_sub), "  ·  ");
            }
            AddRow(page, head, false);

            // 自检卡片：在线吗 / 为什么不在线 / 怎么修
            var diag = FlexCard("自检 · 机器人在线吗");
            _diagBig = Txt("● 点「重新自检」开始检查", 11f, true, _th.Sub);
            Body(diag).Controls.Add(_diagBig);
            _diagDetail = Txt("", 9f, false, _th.Sub);
            Body(diag).Controls.Add(_diagDetail);
            // 四个按钮分两行放：窗口窄的时候也不会被挤到卡片外面
            var drow = Line(Body(diag));
            var bDiag = FBtn("重新自检", false, false);
            bDiag.Click += (s2, e2) => RunDiagnosis();
            drow.Controls.Add(bDiag);
            var bFix = FBtn("一键修复（按结论）", true, false);
            bFix.Click += (s2, e2) => FixByDiag();
            drow.Controls.Add(bFix);
            var drow2 = Line(Body(diag));
            var bQr2 = FBtn("登录二维码", false, false);
            bQr2.Click += (s2, e2) => ShowQrWindow();
            drow2.Controls.Add(bQr2);
            var bWeb2 = FBtn("SnowLuma WebUI", false, false);
            bWeb2.Click += (s2, e2) => OpenWebUi();
            drow2.Controls.Add(bWeb2);
            AddRow(page, diag, false);

            var acts = FlexCard("操作");
            var r1 = Line(Body(acts));
            var b1 = FBtn("启动", true, false); b1.Click += (s, e) => SshAction("start"); r1.Controls.Add(b1);
            var b2 = FBtn("重启", false, false); b2.Click += (s, e) => SshAction("restart"); r1.Controls.Add(b2);
            var b3 = FBtn("关闭（先说再见）", false, true); b3.Click += (s, e) => SshAction("stop"); r1.Controls.Add(b3);
            var r2 = Line(Body(acts));
            var b4 = FBtn("刷新状态", false, false); b4.Click += (s, e) => RefreshStatus(); r2.Controls.Add(b4);
            var b5 = FBtn("看日志", false, false); b5.Click += (s, e) => FetchLogs(); r2.Controls.Add(b5);
            var b6 = FBtn("登录二维码", false, false); b6.Click += (s, e) => ShowQrWindow(); r2.Controls.Add(b6);
            var r3 = Line(Body(acts));
            var bChat = FBtn("聊天记录", false, false); bChat.Click += (s, e) => ShowChatWindow(); r3.Controls.Add(bChat);
            var b7 = FBtn("重启协议端", false, false); b7.Click += (s, e) => RestartNapCat(); r3.Controls.Add(b7);
            var b8 = FBtn("SnowLuma WebUI", false, false); b8.Click += (s, e) => OpenWebUi(); r3.Controls.Add(b8);
            AddRow(page, acts, false);

            // 底部：标签页式的日志区（点标签切换），像浏览器那样
            var bottom = BuildTabbedLogs();
            bottom.MinimumSize = new Size(0, Sc(420));   // 给日志一个够大的固定高度
            AddRow(page, bottom, false);                  // 用自适应行 → 整页可滚动（同其它页面一致）
            return page;
        }

        RichTextBox _msgBox;
        Panel _logHost;
        Panel _msgHost;
        RBtn _tabLog;
        RBtn _tabMsg;
        bool _fetchingMsg;
        readonly System.Collections.Generic.HashSet<string> _seenMsg = new System.Collections.Generic.HashSet<string>();

        RichTextBox MakeLogBox()
        {
            var box = new RichTextBox();
            box.Multiline = true;
            box.ReadOnly = true;
            box.BorderStyle = BorderStyle.None;
            box.Dock = DockStyle.Fill;
            box.BackColor = _th.Dark ? Color.FromArgb(22, 23, 25) : Color.FromArgb(250, 251, 253);
            box.ForeColor = _th.Dark ? Color.FromArgb(192, 198, 208) : Color.FromArgb(58, 64, 74);
            box.Font = new Font("Consolas", 8.8f * S);
            box.WordWrap = false;
            box.ScrollBars = RichTextBoxScrollBars.Both;
            box.DetectUrls = false;
            return box;
        }

        Panel MakeLogHost(RichTextBox box)
        {
            var host = new BufferedPanel();
            host.Dock = DockStyle.Fill;
            host.BackColor = box.BackColor;
            host.Padding = new Padding(Sc(10));
            host.Margin = new Padding(0);
            host.Controls.Add(box);
            return host;
        }

        RBtn TabBtn(string text, bool active, EventHandler onClick)
        {
            var b = new RBtn();
            b.Text = text;
            b.Radius = Sc(8);
            b.Font = F(9.5f);
            b.Bold = active;
            var sz = TextRenderer.MeasureText(text, b.Font);
            b.Size = new Size(sz.Width + Sc(28), Sc(32));
            b.Fill = active ? _th.AccentSoft : Color.Transparent;
            b.HoverFill = _th.Hover;
            b.TextColor = active ? _th.Accent : _th.Sub;
            b.BorderColor = Color.Transparent;
            b.Margin = new Padding(0, 0, Sc(8), 0);
            b.Click += onClick;
            return b;
        }

        void SelectLogTab(bool showLog)
        {
            if (_logHost != null) _logHost.Visible = showLog;
            if (_msgHost != null) _msgHost.Visible = !showLog;
            if (_tabLog != null)
            {
                _tabLog.Fill = showLog ? _th.AccentSoft : Color.Transparent;
                _tabLog.TextColor = showLog ? _th.Accent : _th.Sub;
                _tabLog.Bold = showLog;
                _tabLog.Invalidate();
            }
            if (_tabMsg != null)
            {
                _tabMsg.Fill = showLog ? Color.Transparent : _th.AccentSoft;
                _tabMsg.TextColor = showLog ? _th.Sub : _th.Accent;
                _tabMsg.Bold = !showLog;
                _tabMsg.Invalidate();
            }
            if (!showLog) FetchMsgLog();
        }

        // 底部日志区：标签 + 内容（两个内容层只切 Visible，不重建）
        RCard BuildTabbedLogs()
        {
            var card = new RCard();
            card.BackColor = _th.Bg;
            card.CardColor = _th.Card;
            card.BorderColor = _th.Border;
            card.BgColor = _th.Bg;
            card.Radius = Sc(12);
            card.Inset = Sc(6);
            card.OffX = 0;
            card.OffY = 0;
            card.Dock = DockStyle.Fill;

            var inner = new TableLayoutPanel();
            inner.Dock = DockStyle.Fill;
            inner.ColumnCount = 1;
            inner.RowCount = 2;
            inner.BackColor = _th.Card;
            inner.Padding = new Padding(Sc(16), Sc(12), Sc(16), Sc(14));
            inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var tabs = new FlowLayoutPanel();
            tabs.FlowDirection = FlowDirection.LeftToRight;
            tabs.WrapContents = false;
            tabs.AutoSize = true;
            tabs.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            tabs.BackColor = Color.Transparent;
            tabs.Margin = new Padding(0, 0, 0, Sc(8));
            _tabLog = TabBtn("运行日志", true, (s2, e2) => SelectLogTab(true));
            _tabMsg = TabBtn("消息日志", false, (s2, e2) => SelectLogTab(false));
            tabs.Controls.Add(_tabLog);
            tabs.Controls.Add(_tabMsg);
            inner.Controls.Add(tabs, 0, 0);

            var stage = new Panel();
            stage.Dock = DockStyle.Fill;
            stage.BackColor = Color.Transparent;
            stage.Margin = new Padding(0);

            _logBox = MakeLogBox();
            _logHost = MakeLogHost(_logBox);
            _msgBox = MakeLogBox();
            _msgHost = MakeLogHost(_msgBox);
            _msgHost.Visible = false;
            stage.Controls.Add(_msgHost);
            stage.Controls.Add(_logHost);
            inner.Controls.Add(stage, 0, 1);

            if (_logLines.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var ln in _logLines) sb.AppendLine(ln);
                _logBox.Text = sb.ToString();
                _logBox.SelectionStart = _logBox.TextLength;
                _logBox.ScrollToCaret();
            }
            card.Controls.Add(inner);
            return card;
        }

        void AppendColored(RichTextBox box, string line, Color c)
        {
            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionColor = c;
            box.AppendText(line + "\n");
            box.SelectionColor = box.ForeColor;
            box.SelectionStart = box.TextLength;
            box.ScrollToCaret();
        }

        // 去掉 journalctl 前缀，只留"10-18 16:20:40  内容"
        static string CleanJournalLine(string ln)
        {
            if (ln == null) return "";
            string tail = ln;
            int i = tail.IndexOf("肥鱼人设 | ");
            if (i >= 0) tail = tail.Substring(i + 7);
            var m = Regex.Match(ln, "\\d{2}-\\d{2} \\d{2}:\\d{2}:\\d{2}");
            return (m.Success ? m.Value + "  " : "") + tail.Trim();
        }

        // 拉取服务器的收发消息流水（[接收]/[发送]），按方向着色
        void FetchMsgLog()
        {
            if (_msgBox == null || _busy) return;
            if (_fetchingMsg) return;
            _fetchingMsg = true;
            new Thread(() =>
            {
                try
                {
                    string o = Remote("journalctl -u fishbot --since '30 min ago' --no-pager 2>/dev/null "
                        + "| grep -F -e '[接收]' -e '[发送]' | tail -120", 25);
                    var lines = (o ?? "").Replace("\r", "").Split(new char[] { '\n' });
                    var fresh = new List<string>();
                    foreach (var raw in lines)
                    {
                        string ln = raw.TrimEnd();
                        if (ln.Trim().Length == 0) continue;
                        if (_seenMsg.Contains(ln)) continue;
                        fresh.Add(ln);
                    }
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            if (_msgBox == null) return;
                            if (fresh.Count == 0 && _msgBox.TextLength > 0) return;
                            if (fresh.Count == 0)
                            {
                                AppendColored(_msgBox, "（最近 30 分钟没有收发记录）", _th.Sub);
                                return;
                            }
                            foreach (var ln in fresh)
                            {
                                _seenMsg.Add(ln);
                                bool incoming = ln.IndexOf("[接收]") >= 0;
                                Color c = incoming
                                    ? (_th.Dark ? Color.FromArgb(150, 156, 166) : Color.FromArgb(96, 102, 112))
                                    : Color.FromArgb(110, 152, 255);
                                AppendColored(_msgBox, (incoming ? "← " : "→ ") + CleanJournalLine(ln), c);
                            }
                            if (_seenMsg.Count > 600) _seenMsg.Clear();
                        }));
                    }
                    catch { }
                }
                catch { }
                finally { _fetchingMsg = false; }
            }) { IsBackground = true }.Start();
        }

        // ============================================================ 页面：控制本地
        Panel PageLocal()
        {
            TableLayoutPanel tbl;
            var page = Grid(out tbl);

            var head = FlexCard("家里虚拟机 · " + _cfg.VmName + "（备用）");
            _vmLine = Txt("虚拟机：查询中…", 9.5f, false, _th.Sub);
            Body(head).Controls.Add(_vmLine);
            AddRow(page, head, false);

            var acts = FlexCard("操作");
            var r1 = Line(Body(acts));
            var b1 = FBtn("启动虚拟机", true, false); b1.Click += (s, e) => VmStart(); r1.Controls.Add(b1);
            var b2 = FBtn("关机", false, true); b2.Click += (s, e) => VmPowerOff(); r1.Controls.Add(b2);
            var b3 = FBtn("启动机器人", false, false); b3.Click += (s, e) => VmBotCmd("start"); r1.Controls.Add(b3);
            var b4 = FBtn("关闭机器人", false, false); b4.Click += (s, e) => VmBotCmd("stop"); r1.Controls.Add(b4);
            Body(acts).Controls.Add(Txt("同一个 QQ 号只能在一处在线：云端在线时这台会被踢下线。要用它，先在云端点「关闭（告别）」。", 9f, false, _th.Sub));
            Body(acts).Controls.Add(Txt("这台是家里 VirtualBox 的 Windows 虚拟机，云端到期后可以切回来（备份包里有还原说明）。", 9f, false, _th.Sub));
            Body(acts).Controls.Add(Txt("注意：它的开关走 guestcontrol 文件信箱，需要虚拟机里的 ctl_server 在跑。", 8.5f, false, _th.Sub));
            AddRow(page, acts, false);

            var tail = FlexCard("切回来怎么做");
            Body(tail).Controls.Add(Txt("云端点「关闭（先说再见）」→ 启动这台虚拟机 → 点「启动机器人」，机器人就换回家里跑了。", 9f, false, _th.Sub));
            AddRow(page, tail, false);
            return page;
        }

        // ============================================================ 页面：设置
        Panel PageSettings()
        {
            TableLayoutPanel tbl;
            var page = Grid(out tbl);

            var conn = FlexCard("服务器连接");
            var tHost = InputFlex("IP", _cfg.Host, Body(conn), false);
            var tUser = InputFlex("用户名", _cfg.User, Body(conn), false);
            var tPass = InputFlex("密码", _cfg.Pass, Body(conn), true);
            var tFp = InputFlex("指纹", _cfg.HostKey, Body(conn), false);
            var tWebui = InputFlex("WebUI Token", _cfg.WebuiToken, Body(conn), false);
            var lSave = Line(Body(conn));
            var save = FBtn("保存连接", true, false);
            save.Click += (s, e) =>
            {
                _cfg.Host = tHost.Text.Trim(); _cfg.User = tUser.Text.Trim();
                _cfg.Pass = tPass.Text; _cfg.HostKey = tFp.Text.Trim();
                _cfg.WebuiToken = tWebui.Text.Trim();
                _cfg.Save(); Log("连接设置已保存"); RefreshStatus();
            };
            lSave.Controls.Add(save);
            AddRow(page, conn, false);

            var trayCard = FlexCard("托盘 / 后台");
            var rowTray = Line(Body(trayCard));
            rowTray.WrapContents = true;
            Func<string, bool, RBtn> trayBtn = (label, on) =>
            {
                var b = FBtn(label + (on ? "：开" : "：关"), on, false);
                b.Margin = new Padding(0, 0, Sc(8), 0);
                return b;
            };
            var bTray = trayBtn("最小化到托盘", _cfg.Tray);
            bTray.Click += (s2, e2) => { _cfg.Tray = !_cfg.Tray; _cfg.Save(); RefreshPages(); };
            var bTrayClose = trayBtn("点关闭收进托盘", _cfg.TrayClose);
            bTrayClose.Click += (s2, e2) => { _cfg.TrayClose = !_cfg.TrayClose; _cfg.Save(); RefreshPages(); };
            var bTrayStart = trayBtn("启动时隐藏", _cfg.TrayStart);
            bTrayStart.Click += (s2, e2) => { _cfg.TrayStart = !_cfg.TrayStart; _cfg.Save(); RefreshPages(); };
            rowTray.Controls.Add(bTray);
            rowTray.Controls.Add(bTrayClose);
            rowTray.Controls.Add(bTrayStart);
            var rowTray2 = Line(Body(trayCard));
            var bHideNow = FBtn("现在就收进托盘", false, false);
            bHideNow.Click += (s2, e2) => HideToTray(false);
            rowTray2.Controls.Add(bHideNow);
            var bExit = FBtn("彻底退出", false, true);
            bExit.Click += (s2, e2) =>
            {
                if (MessageBox.Show(this, "要从托盘退出控制台吗？（不影响服务器上的机器人）", "确认",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                _exiting = true;
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
                Close();
            };
            rowTray2.Controls.Add(bExit);
            Body(trayCard).Controls.Add(Txt("收进托盘后控制台还在后台跑，状态检查、聊天记录都不会停。", 9f, false, _th.Sub));
            AddRow(page, trayCard, false);

            var look = FlexCard("外观");
            var lTheme = Line(Body(look));
            lTheme.Controls.Add(Txt("主题", 9f, false, _th.Sub));
            var bl = FBtn("白天", !_cfg.Dark, false);
            bl.Click += (s, e) => { _cfg.Dark = false; _cfg.Save(); ApplyLook(); };
            lTheme.Controls.Add(bl);
            var bd = FBtn("夜间", _cfg.Dark, false);
            bd.Click += (s, e) => { _cfg.Dark = true; _cfg.Save(); ApplyLook(); };
            lTheme.Controls.Add(bd);

            var lSize = Line(Body(look));
            lSize.Controls.Add(Txt("字号", 9f, false, _th.Sub));
            var tb = new TrackBar();
            tb.Minimum = 80; tb.Maximum = 150; tb.TickFrequency = 10;
            tb.Value = (int)Math.Round(S * 100);
            tb.Width = Math.Max(Sc(140), _cardW / 2);
            tb.Height = Sc(38);
            tb.BackColor = _th.Card;
            tb.Margin = new Padding(0, 0, Sc(8), Sc(4));
            lSize.Controls.Add(tb);
            lSize.Controls.Add(Txt(S.ToString("0.00") + "x", 9.5f, true, _th.Text));
            var apply = FBtn("应用", true, false);
            apply.Click += (s, e) => { S = tb.Value / 100f; _cfg.Scale = S; _cfg.Save(); ApplyLook(); };
            lSize.Controls.Add(apply);

            Body(look).Controls.Add(Txt("改完立刻按新字号重排整个界面；内容比窗口高时可以滚动。", 8.5f, false, _th.Sub));
            AddRow(page, look, false);

            var note = FlexCard("说明");
            Body(note).Controls.Add(Txt("SSH 通道用 plink（PuTTY），远程命令 base64 传输，-hostkey 固定指纹防中间人。", 9f, false, _th.Sub));
            Body(note).Controls.Add(Txt("配置保存在 %APPDATA%\\FishBotConsole\\config.ini（密码异或 + base64，不是明文）", 8.5f, false, _th.Sub));
            AddRow(page, note, false);
            return page;
        }

        // ============================================================ 页面：个人信息
        Panel PageAbout()
        {
            TableLayoutPanel tbl;
            var page = Grid(out tbl);

            var c = FlexCard("机器人");
            Body(c).Controls.Add(Txt("昵称：示例机器人（她自己能改）", 10f, false, _th.Text));
            Body(c).Controls.Add(Txt("QQ：123456789　·　超管（主人）：100000001", 10f, false, _th.Text));
            Body(c).Controls.Add(Txt("人设：DeepSeek 的鲸鱼女孩「肥鱼」，自称以「我」为主，偶尔「人家」", 9f, false, _th.Sub));
            Body(c).Controls.Add(Txt("能力：聊天 / 看图 / 表情包 / 记忆 / 联网搜索 / 以图搜图 / 自己改资料 / 自动同意进群", 9f, false, _th.Sub));
            Body(c).Controls.Add(Txt("v27：拟人化打字 + 分条 + 刷屏惊讶 + 静群接话 + 群里花名册", 9f, false, _th.Sub));
            Body(c).Controls.Add(Txt("群：200000001（请在这里发篇）、200000002（Minecraft 一千零一夜）", 9f, false, _th.Sub));
            AddRow(page, c, false);

            var r = FlexCard("花名册（她记住了谁跟她说过话）");
            Body(r).Controls.Add(Txt("数据在服务器 /opt/qqbot/data/roster.json，退群会自动清掉这个群", 9f, false, _th.Sub));
            var lr = Line(Body(r));
            var b = FBtn("读取花名册到日志", true, false);
            b.Click += (s, e) =>
            {
                Log("读取花名册…");
                new Thread(() =>
                {
                    string script = "python3 - <<'EOF'\n"
                        + "import json, os\n"
                        + "p = '/opt/qqbot/data/roster.json'\n"
                        + "if not os.path.exists(p):\n"
                        + "    print('（还没有花名册）')\n"
                        + "else:\n"
                        + "    d = json.load(open(p))\n"
                        + "    for g, v in d.items():\n"
                        + "        names = '、'.join([x.get('name','?') for x in list(v.values())[:10]])\n"
                        + "        print('群 %s：%d 人  %s' % (g, len(v), names))\n"
                        + "EOF\n";
                    string o = Remote(script, 30);
                    foreach (var ln in o.Replace("\r", "").Split('\n'))
                        if (ln.Trim().Length > 0) Log("  " + ln);
                }) { IsBackground = true }.Start();
            };
            lr.Controls.Add(b);
            AddRow(page, r, false);
            return page;
        }

        // ============================================================ 页面：开发信息
        Panel PageDev()
        {
            TableLayoutPanel tbl;
            var page = Grid(out tbl);
            var c = FlexCard("版本与运行环境");
            string[] lines = new string[]
            {
                "控制台：FishBotConsole v2.1（C# WinForms / .NET Framework 4.x / csc 编译，弹性布局）",
                "机器人插件：肥鱼人设.py · v27",
                "云端：Ubuntu 22.04 · Python 3.10.12 · nonebot2 2.5.0 · nonebot-adapter-onebot 2.4.6",
                "协议端：SnowLuma（Docker 容器 snowluma）· OneBot v11 · WS 127.0.0.1:3001",
                "进程管理：systemd 服务 fishbot（Restart=on-failure，关闭走 stop.flag 告别）",
                "SSH：plink.exe（PuTTY 0.85）· base64 传输 · -hostkey 固定指纹",
                "模型：deepseek-v4-flash-0731（文字）／deepseek-v4-flash-vision（看图）",
                "存储：无数据库，全部 data/*.json（ai_state / memory / roster / sticker_index）"
            };
            foreach (var line in lines) Body(c).Controls.Add(Txt(line, 9f, false, _th.Sub));
            AddRow(page, c, false);
            return page;
        }

        // ============================================================ 页面：文件地址
        Panel PagePaths()
        {
            TableLayoutPanel tbl;
            var page = Grid(out tbl);

            var c = FlexCard("本地路径");
            string[][] rows = new string[][]
            {
                new string[] { "控制台程序", Application.StartupPath, "" },
                new string[] { "工作区", @"F:\肥鱼机器人", @"F:\肥鱼机器人" },
                new string[] { "插件源码", @"F:\肥鱼机器人\02-程序\插件源码", @"F:\肥鱼机器人\02-程序\插件源码" },
                new string[] { "备份包", @"F:\肥鱼机器人\06-备份", @"F:\肥鱼机器人\06-备份" },
                new string[] { "配置目录", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FishBotConsole"), "" }
            };
            foreach (var r in rows)
            {
                var row = Line(Body(c));
                row.WrapContents = false;
                var name = Txt(r[0], 9f, false, _th.Sub);
                name.MinimumSize = new Size(Sc(100), 0);
                row.Controls.Add(name);
                var pathLbl = new Label();
                pathLbl.Text = r[1];
                pathLbl.AutoSize = false;
                pathLbl.AutoEllipsis = true;
                pathLbl.Font = F(9f);
                pathLbl.ForeColor = _th.Text;
                pathLbl.BackColor = Color.Transparent;
                pathLbl.Height = Sc(26);
                pathLbl.Width = Math.Max(Sc(120), _cardW - Sc(230));
                pathLbl.TextAlign = ContentAlignment.MiddleLeft;
                pathLbl.Margin = new Padding(0, 0, Sc(8), Sc(6));
                Tip(pathLbl, r[1]);
                row.Controls.Add(pathLbl);
                if (r[2].Length > 0 && Directory.Exists(r[2]))
                {
                    string dir = r[2];
                    var b = SoftBtn("打开");
                    b.Click += (s, e) => { try { Process.Start("explorer.exe", dir); } catch { } };
                    Tip(b, dir);
                    row.Controls.Add(b);
                }
            }
            AddRow(page, c, false);

            var sCloud = FlexCard("云端路径（SSH 上看）");
            Body(sCloud).Controls.Add(Txt("/opt/qqbot　机器人目录（bot.py / src / .env / persona_prompt.txt）", 9f, false, _th.Text));
            Body(sCloud).Controls.Add(Txt("/opt/qqbot/data　记忆 / 花名册 / 表情包索引 / AI 开关", 9f, false, _th.Sub));
            Body(sCloud).Controls.Add(Txt("/opt/qqbot/stickers　287 张表情包（43MB）", 9f, false, _th.Sub));
            AddRow(page, sCloud, false);
            return page;
        }

        // ============================================================ 页面：机器人控件
        Panel PageBotCtl()
        {
            TableLayoutPanel tbl;
            var page = Grid(out tbl);

            string curChat = "";
            bool onlyAt = false;
            int maxLen = 26;
            double speed = 1.0;
            double chatRate = -1.0;      // -1 = 跟随全局

            // ---------- 选群 ----------
            var c1 = FlexCard("选一个群");
            var groupBox = new ComboBox();
            groupBox.DropDownStyle = ComboBoxStyle.DropDownList;
            groupBox.Font = F(9.5f);
            groupBox.BackColor = _th.Dark ? Color.FromArgb(30, 31, 34) : Color.White;
            groupBox.ForeColor = _th.Text;
            groupBox.Width = Math.Max(Sc(220), _cardW - Sc(160));
            groupBox.Height = Sc(30);
            var groupRow = Line(Body(c1));
            groupRow.WrapContents = true;
            groupBox.Width = Math.Max(Sc(240), _cardW - Sc(320));
            groupRow.Controls.Add(groupBox);
            var gidInput = new TextBox();
            gidInput.Font = F(9.5f);
            gidInput.BorderStyle = BorderStyle.FixedSingle;
            gidInput.BackColor = _th.Dark ? Color.FromArgb(30, 31, 34) : Color.White;
            gidInput.ForeColor = _th.Text;
            gidInput.Width = Sc(120);
            gidInput.Margin = new Padding(Sc(8), 0, 0, 0);
            Tip(gidInput, "也可以直接填群号，按「载入」");
            groupRow.Controls.Add(gidInput);
            var btnLoad = SoftBtn("载入");
            btnLoad.Margin = new Padding(Sc(8), 0, 0, 0);
            groupRow.Controls.Add(btnLoad);
            var btnRefresh = SoftBtn("刷新群列表");
            btnRefresh.Margin = new Padding(Sc(8), 0, 0, 0);
            groupRow.Controls.Add(btnRefresh);
            Body(c1).Controls.Add(Txt("勾选「只回 @」后，她在这个群只有被 @ 才说话。", 9f, false, _th.Sub));
            AddRow(page, c1, false);

            // ---------- 行为 ----------
            var c2 = FlexCard("她在这个群怎么说话");

            // 仅@后回复
            var rowAt = Line(Body(c2));
            rowAt.WrapContents = false;
            var lbAt = Txt("仅接受 @ 后回复", 9.5f, false, _th.Text);
            lbAt.AutoSize = false;
            lbAt.Width = Sc(150);
            lbAt.Height = Sc(30);
            lbAt.TextAlign = ContentAlignment.MiddleLeft;
            rowAt.Controls.Add(lbAt);
            var btnAt = FBtn("关闭", false, false);
            btnAt.Width = Sc(90);
            rowAt.Controls.Add(btnAt);

            // 回复速度
            var rowSp = Line(Body(c2));
            rowSp.WrapContents = false;
            var lbSp = Txt("回复速度", 9.5f, false, _th.Text);
            lbSp.AutoSize = false;
            lbSp.Width = Sc(150);
            lbSp.Height = Sc(30);
            lbSp.TextAlign = ContentAlignment.MiddleLeft;
            rowSp.Controls.Add(lbSp);
            var bar = new TrackBar();
            bar.Minimum = 30;       // 0.30x
            bar.Maximum = 300;      // 3.00x
            bar.TickFrequency = 30;
            bar.Value = 100;
            bar.Width = Math.Max(Sc(240), _cardW - Sc(320));
            bar.Height = Sc(34);
            bar.BackColor = _th.Card;
            rowSp.Controls.Add(bar);
            var lbSpVal = Txt("1.00x", 9.5f, true, _th.Accent);
            lbSpVal.AutoSize = false;
            lbSpVal.Width = Sc(70);
            lbSpVal.Height = Sc(30);
            lbSpVal.TextAlign = ContentAlignment.MiddleLeft;
            lbSpVal.Margin = new Padding(Sc(8), 0, 0, 0);
            rowSp.Controls.Add(lbSpVal);
            Body(c2).Controls.Add(Txt("1.00x 是默认；小于 1 更快（0.3 最快），大于 1 更慢（3.0 最慢）。", 9f, false, _th.Sub));

            // 聊天频率
            var rowRate = Line(Body(c2));
            rowRate.WrapContents = true;
            var lbRate = Txt("聊天频率", 9.5f, false, _th.Text);
            lbRate.AutoSize = false;
            lbRate.Width = Sc(120);
            lbRate.Height = Sc(30);
            lbRate.TextAlign = ContentAlignment.MiddleLeft;
            rowRate.Controls.Add(lbRate);
            var barRate = new TrackBar();
            barRate.Minimum = 0;
            barRate.Maximum = 100;
            barRate.TickFrequency = 10;
            barRate.Value = 100;
            barRate.Width = Math.Max(Sc(220), _cardW - Sc(430));
            barRate.Height = Sc(34);
            barRate.BackColor = _th.Card;
            rowRate.Controls.Add(barRate);
            var lbRateVal = Txt("跟随全局", 9.5f, true, _th.Accent);
            lbRateVal.AutoSize = false;
            lbRateVal.Width = Sc(90);
            lbRateVal.Height = Sc(30);
            lbRateVal.TextAlign = ContentAlignment.MiddleLeft;
            lbRateVal.Margin = new Padding(Sc(8), 0, 0, 0);
            rowRate.Controls.Add(lbRateVal);
            var btnRateClear = SoftBtn("跟随全局");
            btnRateClear.Width = Sc(88);
            btnRateClear.Margin = new Padding(Sc(6), 0, 0, 0);
            rowRate.Controls.Add(btnRateClear);
            Body(c2).Controls.Add(Txt("她在这个群「没人叫她」时主动接话的概率：0% 几乎不插话，100% 每条都想插一句。"
                + "拖到最左再点「跟随全局」= 用全局设置。", 9f, false, _th.Sub));

            // 句式长度
            var rowLen = Line(Body(c2));
            rowLen.WrapContents = false;
            var lbLen = Txt("句式长度", 9.5f, false, _th.Text);
            lbLen.AutoSize = false;
            lbLen.Width = Sc(150);
            lbLen.Height = Sc(30);
            lbLen.TextAlign = ContentAlignment.MiddleLeft;
            rowLen.Controls.Add(lbLen);
            var btn14 = FBtn("14", false, false);
            var btn26 = FBtn("26", false, false);
            var btn40 = FBtn("40", false, false);
            foreach (var b in new Control[] { btn14, btn26, btn40 })
            {
                b.Width = Sc(64);
                b.Margin = new Padding(0, 0, Sc(8), 0);
                rowLen.Controls.Add(b);
            }
            var lbLenVal = Txt("当前：26 字", 9.5f, true, _th.Accent);
            lbLenVal.AutoSize = false;
            lbLenVal.Width = Sc(120);
            lbLenVal.Height = Sc(30);
            lbLenVal.TextAlign = ContentAlignment.MiddleLeft;
            lbLenVal.Margin = new Padding(Sc(8), 0, 0, 0);
            rowLen.Controls.Add(lbLenVal);
            Body(c2).Controls.Add(Txt("超过这个字数的回复会被截断；14 很短、26 适中、40 能说完整句子。", 9f, false, _th.Sub));
            AddRow(page, c2, false);

            // ---------- 保存 ----------
            var c3 = FlexCard("保存");
            var rowSave = Line(Body(c3));
            rowSave.WrapContents = false;
            var btnSave = FBtn("保存到这个群", true, false);
            btnSave.Width = Sc(150);
            rowSave.Controls.Add(btnSave);
            var btnReload = SoftBtn("重新读取");
            btnReload.Margin = new Padding(Sc(10), 0, 0, 0);
            rowSave.Controls.Add(btnReload);
            var status = Txt("选个群，先「载入」看当前设置。", 9f, false, _th.Sub);
            Body(c3).Controls.Add(status);
            AddRow(page, c3, false);

            // ---------- 逻辑 ----------
            Action refreshAt = () =>
            {
                btnAt.Text = onlyAt ? "已开启" : "关闭";
                btnAt.Fill = onlyAt ? _th.Accent : (_th.Dark ? Color.FromArgb(44, 47, 53) : Color.Gainsboro);
                btnAt.TextColor = onlyAt ? Color.White : _th.Text;
                btnAt.Invalidate();
            };
            Action refreshLen = () =>
            {
                lbLenVal.Text = "当前：" + maxLen + " 字";
                Func<Control, bool, Control> mark = (b, on) =>
                {
                    b.ForeColor = on ? _th.Accent : _th.Text;
                    return b;
                };
                mark(btn14, maxLen == 14); mark(btn26, maxLen == 26); mark(btn40, maxLen == 40);
            };
            Action refreshRate = () =>
            {
                if (chatRate < 0)
                {
                    lbRateVal.Text = "（全局）";
                    barRate.Enabled = false;
                }
                else
                {
                    lbRateVal.Text = ((int)Math.Round(chatRate * 100)) + "%";
                    barRate.Enabled = true;
                    int v = (int)Math.Round(chatRate * 100);
                    if (v < barRate.Minimum) v = barRate.Minimum;
                    if (v > barRate.Maximum) v = barRate.Maximum;
                    if (barRate.Value != v) barRate.Value = v;
                }
            };

            Action refreshSpeed = () =>
            {
                lbSpVal.Text = speed.ToString("0.00") + "x";
                int v = (int)Math.Round(speed * 100);
                if (v < bar.Minimum) v = bar.Minimum;
                if (v > bar.Maximum) v = bar.Maximum;
                if (bar.Value != v) bar.Value = v;
            };

            Action<string> applyRules = (json) =>
            {
                try
                {
                    var ser = new JavaScriptSerializer();
                    var o = ser.DeserializeObject(json) as Dictionary<string, object>;
                    var r = (o.ContainsKey("rules") ? o["rules"] : null) as Dictionary<string, object>;
                    if (r != null)
                    {
                        onlyAt = Convert.ToBoolean(r.ContainsKey("only_at") ? r["only_at"] : false);
                        maxLen = Convert.ToInt32(r.ContainsKey("max_len") ? r["max_len"] : 0);
                        if (maxLen <= 0) maxLen = 26;
                        speed = Convert.ToDouble(r.ContainsKey("speed") ? r["speed"] : 1.0);
                        if (speed <= 0) speed = 1.0;
                        chatRate = Convert.ToDouble(r.ContainsKey("chat_rate") ? r["chat_rate"] : -1.0);
                    }
                    refreshAt(); refreshLen(); refreshSpeed(); refreshRate();
                }
                catch (Exception ex) { status.Text = "解析失败：" + ex.Message; }
            };

            Action<string, bool> loadRules = (chat, quiet) =>
            {
                if (string.IsNullOrEmpty(chat)) { status.Text = "先选一个群。"; return; }
                status.Text = "读取 " + chat + " …";
                new Thread(() =>
                {
                    string enc = Uri.EscapeDataString(chat);
                    string json = Remote("curl -s -m 20 '" + ChatApiBase + "/api/botctl?token=" + ChatApiToken
                                         + "&chat=" + enc + "'", 40);
                    BeginInvoke(new Action(() =>
                    {
                        applyRules(json);
                        status.Text = quiet ? "已载入 " + chat : "已重新读取 " + chat;
                    }));
                }) { IsBackground = true }.Start();
            };

            Action loadGroups = () =>
            {
                status.Text = "拉群列表…";
                new Thread(() =>
                {
                    string json = Remote("curl -s -m 25 '" + ChatApiBase + "/api/groups?token=" + ChatApiToken + "'", 45);
                    BeginInvoke(new Action(() =>
                    {
                        groupBox.Items.Clear();
                        try
                        {
                            var ser = new JavaScriptSerializer();
                            var o = ser.DeserializeObject(json) as Dictionary<string, object>;
                            var arr = o["items"] as object[];
                            foreach (var it in arr)
                            {
                                var d = it as Dictionary<string, object>;
                                string gid = Convert.ToString(d["gid"]);
                                string nm = Convert.ToString(d["name"]);
                                string text = "群 " + gid + (string.IsNullOrEmpty(nm) ? "" : "  · " + nm);
                                groupBox.Items.Add(new GroupItem(gid, text));
                            }
                        }
                        catch (Exception ex) { status.Text = "群列表解析失败：" + ex.Message; }
                        if (groupBox.Items.Count > 0)
                        {
                            groupBox.SelectedIndex = 0;
                            var gi = groupBox.SelectedItem as GroupItem;
                            if (gi != null) { curChat = "群 " + gi.Gid; loadRules(curChat, true); }
                        }
                        else status.Text = "没拿到群列表（协议端没连上？）";
                    }));
                }) { IsBackground = true }.Start();
            };

            btnAt.Click += (s2, e2) => { onlyAt = !onlyAt; refreshAt(); };
            btn14.Click += (s2, e2) => { maxLen = 14; refreshLen(); };
            btn26.Click += (s2, e2) => { maxLen = 26; refreshLen(); };
            btn40.Click += (s2, e2) => { maxLen = 40; refreshLen(); };
            bar.Scroll += (s2, e2) => { speed = bar.Value / 100.0; refreshSpeed(); };
            barRate.Scroll += (s2, e2) =>
            {
                chatRate = barRate.Value / 100.0;
                lbRateVal.Text = barRate.Value + "%";
            };
            btnRateClear.Click += (s2, e2) => { chatRate = -1.0; refreshRate(); };
            groupBox.SelectedIndexChanged += (s2, e2) =>
            {
                var gi = groupBox.SelectedItem as GroupItem;
                if (gi == null) return;
                curChat = "群 " + gi.Gid;
                gidInput.Text = gi.Gid;
                loadRules(curChat, true);
            };
            btnLoad.Click += (s2, e2) =>
            {
                string g = "";
                foreach (char ch in (gidInput.Text ?? ""))
                    if (char.IsDigit(ch)) g += ch;
                if (g.Length < 5) { status.Text = "群号看着不对。"; return; }
                curChat = "群 " + g;
                loadRules(curChat, true);
            };
            btnRefresh.Click += (s2, e2) => loadGroups();
            btnReload.Click += (s2, e2) => loadRules(curChat, false);

            btnSave.Click += (s2, e2) =>
            {
                if (string.IsNullOrEmpty(curChat)) { status.Text = "先选一个群。"; return; }
                btnSave.Enabled = false;
                status.Text = "保存中…";
                bool _at = onlyAt; int _len = maxLen; double _sp = speed;
                double _cr = chatRate; string _chat = curChat;
                new Thread(() =>
                {
                    var payload = new Dictionary<string, object>();
                    payload["chat"] = _chat;
                    payload["only_at"] = _at;
                    payload["max_len"] = _len;
                    payload["speed"] = Math.Round(_sp, 2);
                    payload["chat_rate"] = Math.Round(_cr, 2);
                    string body = new JavaScriptSerializer().Serialize(payload);
                    string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                    string cmd = "echo " + b64 + " | base64 -d > /tmp/botctl.json; "
                               + "curl -s -m 30 -X POST -H 'Content-Type: application/json' --data-binary @/tmp/botctl.json "
                               + "'" + ChatApiBase + "/api/botctl?token=" + ChatApiToken + "'";
                    string r = Remote(cmd, 60);
                    BeginInvoke(new Action(() =>
                    {
                        btnSave.Enabled = true;
                        try
                        {
                            var ser = new JavaScriptSerializer();
                            var o = ser.DeserializeObject(r) as Dictionary<string, object>;
                            bool ok = o.ContainsKey("ok") && Convert.ToBoolean(o["ok"]);
                            status.Text = ok
                                ? "已保存：" + _chat + " → 仅@" + (_at ? "开" : "关")
                                  + "，长度 " + _len + "，速度 " + _sp.ToString("0.00") + "x"
                                  + "，频率 " + (_cr < 0 ? "跟随全局" : ((int)Math.Round(_cr * 100)) + "%")
                                : ("保存失败：" + (o.ContainsKey("msg") ? Convert.ToString(o["msg"]) : r));
                            if (ok) applyRules(r);
                        }
                        catch (Exception ex) { status.Text = "保存返回异常：" + ex.Message; }
                    }));
                }) { IsBackground = true }.Start();
            };

            refreshAt(); refreshLen(); refreshSpeed(); refreshRate();
            page.HandleCreated += (s2, e2) => { if (groupBox.Items.Count == 0) loadGroups(); };
            return page;
        }

        class GroupItem
        {
            public string Gid = "";
            public string Text = "";
            public GroupItem(string g, string t) { Gid = g; Text = t; }
            public override string ToString() { return Text; }
        }

        // ============================================================ 页面：使用文档
        Panel PageDocs()
        {
            TableLayoutPanel tbl;
            var page = Grid(out tbl);

            // ---------- 1. 文档索引 ----------
            var c = FlexCard("文档 · 都在 01-文档 文件夹");
            string[][] docs = new string[][]
            {
                new string[] { "00-从这里开始.md              ← 总入口，先看这个", @"F:\肥鱼机器人\00-从这里开始.md" },
                new string[] { "系统总览.md                 架构 / 端口 / 数据流", @"F:\肥鱼机器人\01-文档\系统总览.md" },
                new string[] { "语音系统.md                 克隆音色 / SILK / 情绪 / 排错", @"F:\肥鱼机器人\01-文档\语音系统.md" },
                new string[] { "三端控制台.md               电脑 / 手机 / API 功能与构建", @"F:\肥鱼机器人\01-文档\三端控制台.md" },
                new string[] { "运维手册.md                 巡检 / 看门狗 / 故障处置", @"F:\肥鱼机器人\01-文档\运维手册.md" },
                new string[] { "更新日志.md                 每次改了什么、为什么", @"F:\肥鱼机器人\01-文档\更新日志.md" },
                new string[] { "部署与使用说明.md           v1~v29 历史（大而全）", @"F:\肥鱼机器人\01-文档\部署与使用说明.md" },
                new string[] { "历史-虚拟机时代交接.md      早期虚拟机版（已归档）", @"F:\肥鱼机器人\01-文档\历史-虚拟机时代交接.md" },
                new string[] { "06-备份/还原说明.md          备份与还原", @"F:\肥鱼机器人\06-备份\还原说明.md" }
            };
            foreach (var d in docs)
            {
                var row = Line(Body(c));
                row.WrapContents = false;
                var name = new Label();
                name.Text = d[0];
                name.AutoSize = false;
                name.AutoEllipsis = true;
                name.Font = F(9.5f);
                name.ForeColor = _th.Text;
                name.BackColor = Color.Transparent;
                name.Height = Sc(26);
                name.Width = Math.Max(Sc(120), _cardW - Sc(250));
                name.TextAlign = ContentAlignment.MiddleLeft;
                name.Margin = new Padding(0, 0, Sc(8), Sc(4));
                Tip(name, d[1]);
                row.Controls.Add(name);
                if (File.Exists(d[1]))
                {
                    string p2 = d[1];
                    var b = SoftBtn("打开");
                    b.Click += (s, e) => { try { Process.Start(p2); } catch { } };
                    Tip(b, p2);
                    row.Controls.Add(b);
                    var b2 = SoftBtn("所在目录");
                    b2.Click += (s, e) => { try { Process.Start("explorer.exe", "/select,\"" + p2 + "\""); } catch { } };
                    Tip(b2, p2);
                    row.Controls.Add(b2);
                }
                else row.Controls.Add(Txt("（没找到）", 9f, false, _th.Danger));
            }
            AddRow(page, c, false);

            // ---------- 2. 当前状态速览 ----------
            var c2 = FlexCard("当前状态（2026-10-19）");
            string[][] facts = new string[][]
            {
                new string[] { "机器人 QQ", "123456789（示例机器人） · 超管 100000001" },
                new string[] { "服务器", "腾讯云轻量 your-server.example.com · Ubuntu 22.04 · 2 核 2G" },
                new string[] { "协议端", "SnowLuma（Docker 容器 snowluma，容器内跑 Linux QQ）" },
                new string[] { "框架", "NoneBot2 2.5.0 + adapter-onebot 2.4.6 · Python 3.10" },
                new string[] { "语音", "CosyVoice2 克隆音色 diona / diona_anno / diona_pout → SILK" },
                new string[] { "接口", "OneBot 3000/3001 · WebUI 5099 · noVNC 6081 · 控制台 8090" },
                new string[] { "三个服务", "fishbot（主程序）· fishweb（控制台）· fishdog / fishlogin（定时器）" }
            };
            foreach (var f in facts)
            {
                var row = Line(Body(c2));
                row.WrapContents = false;
                var k = Txt(f[0], 9f, true, _th.Sub);
                k.AutoSize = false;
                k.Width = Sc(96);
                k.Height = Sc(24);
                k.TextAlign = ContentAlignment.MiddleLeft;
                row.Controls.Add(k);
                var v = Txt(f[1], 9.5f, false, _th.Text);
                v.AutoSize = false;
                v.Height = Sc(24);
                v.Width = Math.Max(Sc(200), _cardW - Sc(150));
                v.TextAlign = ContentAlignment.MiddleLeft;
                row.Controls.Add(v);
            }
            AddRow(page, c2, false);

            // ---------- 3. 三端各自能干什么 ----------
            var c3 = FlexCard("三端怎么分工");
            string[][] ends = new string[][]
            {
                new string[] { "电脑（本程序）", "状态 / 自检 / 扫码 / 聊天记录（QQNT 风格）/ 主动发文字或语音 / 快捷消息" },
                new string[] { "手机（肥鱼控制台.apk）", "同样的四页；会话分开、气泡、本地缓存秒开、20 秒自动刷新" },
                new string[] { "网页 API（:8090）", "status / diag / selfcheck / qr / conversations / messages / send" },
                new string[] { "数据来源", "journalctl 的 [接收] [发送] + /opt/qqbot/data/manual_sent.log" }
            };
            foreach (var e in ends)
            {
                var row = Line(Body(c3));
                row.WrapContents = false;
                var k = Txt(e[0], 9f, true, _th.Sub);
                k.AutoSize = false;
                k.Width = Sc(150);
                k.Height = Sc(24);
                k.TextAlign = ContentAlignment.MiddleLeft;
                row.Controls.Add(k);
                var v = Txt(e[1], 9.5f, false, _th.Text);
                v.AutoSize = false;
                v.Height = Sc(24);
                v.Width = Math.Max(Sc(200), _cardW - Sc(200));
                v.TextAlign = ContentAlignment.MiddleLeft;
                row.Controls.Add(v);
            }
            AddRow(page, c3, false);

            // ---------- 4. 语音速查 ----------
            var c4 = FlexCard("语音速查");
            Body(c4).Controls.Add(Txt("• 换音色：改 /opt/qqbot/.env 的 FISH_VOICE_SF_VOICE，然后 sudo systemctl restart fishbot", 9f, false, _th.Sub));
            Body(c4).Controls.Add(Txt("• 换语速：FISH_VOICE_SF_SPEED（别低于 1.05，1.02 会让模型把句子念断）", 9f, false, _th.Sub));
            Body(c4).Controls.Add(Txt("• 输出格式：必须是 SILK（FISH_VOICE_SILK=1），MP3 直发会被 QQ 二次转码，听着像劣质外放", 9f, false, _th.Sub));
            Body(c4).Controls.Add(Txt("• 中文音色只能用中文素材克隆；用日语素材克隆的音色念中文会变成乱码", 9f, false, _th.Sub));
            Body(c4).Controls.Add(Txt("• 情绪音色：diona（平常/开心）· diona_anno（不耐烦）· diona_pout（撒娇）", 9f, false, _th.Sub));
            Body(c4).Controls.Add(Txt("• 验证方法：把发出去的语音用 SenseVoice 转写，能念回来才算正常", 9f, false, _th.Sub));
            AddRow(page, c4, false);

            // ---------- 5. 排错速查 ----------
            var c5 = FlexCard("排错速查");
            string[][] errs = new string[][]
            {
                new string[] { "群里不说话", "journalctl -u fishbot --since '10 min ago' | grep '\\[接收\\]' → 没收到就查协议端" },
                new string[] { "收到但不回", "看日志里 ask_ai / AI 返回 HTTP：401 换 KEY，402/429 是额度或限流" },
                new string[] { "掉线 / 被踢", "先重启容器；还不行点「登录二维码」用手机扫；别用同一号在别处登录" },
                new string[] { "语音没声音", "journalctl 搜「语音 / silk / 语音日语版」；多半是音色与语言不匹配" },
                new string[] { "控制台 8090 打不开", "腾讯云安全组是否放行 TCP:8090；本机查 sudo ss -lntp | grep 8090" },
                new string[] { "聊天记录不更新", "手动发送写 manual_sent.log；确认 fishweb 已重启" }
            };
            foreach (var e in errs)
            {
                var row = Line(Body(c5));
                row.WrapContents = false;
                var k = Txt(e[0], 9f, true, _th.Danger);
                k.AutoSize = false;
                k.Width = Sc(120);
                k.Height = Sc(24);
                k.TextAlign = ContentAlignment.MiddleLeft;
                row.Controls.Add(k);
                var v = Txt(e[1], 9f, false, _th.Sub);
                v.AutoSize = false;
                v.Height = Sc(24);
                v.Width = Math.Max(Sc(200), _cardW - Sc(170));
                v.TextAlign = ContentAlignment.MiddleLeft;
                row.Controls.Add(v);
            }
            AddRow(page, c5, false);

            // ---------- 6. 常用命令 ----------
            var c6 = FlexCard("常用命令 · 点「执行」直接在服务器上跑");
            var output = new TextBox();
            output.Multiline = true;
            output.ReadOnly = true;
            output.ScrollBars = ScrollBars.Vertical;
            output.BorderStyle = BorderStyle.FixedSingle;
            output.BackColor = Color.FromArgb(26, 28, 33);
            output.ForeColor = _th.Sub;
            output.Font = new Font("Consolas", 9F);
            output.WordWrap = true;
            output.Height = Sc(150);
            output.Width = Math.Max(Sc(200), _cardW - Sc(40));
            output.Text = "（还没执行。点上面的「执行」看结果）";
            output.Margin = new Padding(0, Sc(6), 0, 0);

            string[][] cmds = new string[][]
            {
                new string[] { "服务状态", "systemctl is-active fishbot fishweb" },
                new string[] { "协议端容器", "sudo -n docker ps --format '{{.Names}} {{.Status}}' | grep snowluma || echo 容器没起" },
                new string[] { "登录情况", "curl -s -m 8 127.0.0.1:3000/get_login_info | head -c 220" },
                new string[] { "内存", "free -m | head -2" },
                new string[] { "最近日志", "journalctl -u fishbot -n 8 --no-pager" },
                new string[] { "最近语音", "sudo -n docker exec snowluma sh -lc 'ls -lt /app/data/voice | head -4'" },
                new string[] { "定时器", "systemctl list-timers 'fish*' --no-pager | head -5" },
                new string[] { "手动发送日志", "tail -3 /opt/qqbot/data/manual_sent.log 2>/dev/null || echo 还没有手动发送记录" }
            };

            Action<string, string> runCmd = (title, cmd) =>
            {
                output.Text = "执行中：" + title + " …";
                new Thread(() =>
                {
                    string r;
                    try { r = Remote(cmd, 60); }
                    catch (Exception ex) { r = "执行失败：" + ex.Message; }
                    if (r == null) r = "";
                    r = r.Trim();
                    if (r.Length > 4000) r = r.Substring(0, 4000) + "\n…（已截断）";
                    string text = "【" + title + "】\n$ " + cmd + "\n\n" + (r.Length == 0 ? "（没有输出）" : r);
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            output.Text = text;
                            output.SelectionStart = output.TextLength;
                            output.ScrollToCaret();
                        }));
                    }
                    catch { }
                })
                { IsBackground = true }.Start();
            };

            foreach (string[] cmd in cmds)
            {
                var row = Line(Body(c6));
                row.WrapContents = false;
                var b = SoftBtn("执行");
                b.Width = Sc(64);
                string t = cmd[0], c0 = cmd[1];
                b.Click += (s2, e2) => runCmd(t, c0);
                Tip(b, c0);
                row.Controls.Add(b);
                var lbl = Txt(cmd[0] + "   " + cmd[1], 9f, false, _th.Sub);
                lbl.AutoSize = false;
                lbl.AutoEllipsis = true;
                lbl.Height = Sc(26);
                lbl.Width = Math.Max(Sc(200), _cardW - Sc(120));
                lbl.TextAlign = ContentAlignment.MiddleLeft;
                lbl.Margin = new Padding(Sc(8), 0, 0, 0);
                Tip(lbl, c0);
                row.Controls.Add(lbl);
            }

            // 危险操作单独一行，带二次确认
            var rowR = Line(Body(c6));
            rowR.WrapContents = false;
            var bRestart = SoftBtn("重启服务");
            bRestart.Width = Sc(80);
            bRestart.Click += (s2, e2) =>
            {
                if (MessageBox.Show(this, "要重启 fishbot + fishweb 吗？", "确认",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                runCmd("重启服务", "sudo -n systemctl restart fishbot fishweb; sleep 3; systemctl is-active fishbot fishweb");
            };
            rowR.Controls.Add(bRestart);
            var lblR = Txt("重启 fishbot + fishweb（会二次确认）", 9f, false, _th.Danger);
            lblR.AutoSize = false;
            lblR.Height = Sc(26);
            lblR.Width = Math.Max(Sc(200), _cardW - Sc(120));
            lblR.TextAlign = ContentAlignment.MiddleLeft;
            lblR.Margin = new Padding(Sc(8), 0, 0, 0);
            rowR.Controls.Add(lblR);

            Body(c6).Controls.Add(output);
            AddRow(page, c6, false);

            // ---------- 7. 风险 ----------
            var c7 = FlexCard("风险提醒");
            Body(c7).Controls.Add(Txt("• 个人号 + 非官方协议端有风控可能，只在自己的号上用；反复掉线常见原因是同一号在别处登录", 9f, false, _th.Danger));
            Body(c7).Controls.Add(Txt("• .env 里是全部密钥（模型 KEY、SiliconFlow KEY、OneBot token、QQ 密码），不要外传或进公开仓库", 9f, false, _th.Danger));
            Body(c7).Controls.Add(Txt("• 语音素材 / 克隆音色 / 表情包仅自用，别商用或公开传播；8090 是公网端口，建议加 IP 白名单", 9f, false, _th.Danger));
            AddRow(page, c7, false);

            return page;
        }

        // ------------------------------------------------ SSH
        string PlinkPath()
        {
            string[] cands = new string[]
            {
                Path.Combine(Application.StartupPath, "bin\\plink.exe"),
                Path.Combine(Application.StartupPath, "plink.exe"),
                @"F:\肥鱼机器人\02-程序\开关软件\bin\plink.exe",
                @"F:\肥鱼机器人\02-程序\开关软件\plink.exe",
                @"C:\Program Files\PuTTY\plink.exe"
            };
            foreach (var c in cands) if (File.Exists(c)) return c;
            return "plink.exe";
        }

        string Remote(string bash, int timeoutSec)
        {
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(bash));
            string inner = "echo " + b64 + " | base64 -d | sudo -n bash -s";
            return RunProc(PlinkPath(),
                "-batch -no-antispoof -hostkey \"" + _cfg.HostKey + "\" -pw \"" + _cfg.Pass + "\" " +
                _cfg.User + "@" + _cfg.Host + " \"" + inner + "\"",
                timeoutSec * 1000);
        }

        static string RunProc(string exe, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    string e = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return o + "\r\n[超时]"; }
                    if (e.Trim().Length > 0 && o.Trim().Length == 0) return e;
                    return o + (e.Trim().Length > 0 ? "\r\n" + e : "");
                }
            }
            catch (Exception ex) { return "[执行失败] " + ex.Message; }
        }

        // ------------------------------------------------ 云端操作
        void RefreshStatus()
        {
            if (_busy) return;
            _busy = true;
            new Thread(() =>
            {
                try { DoRefresh(); }
                catch (Exception e) { Log("刷新出错: " + e.Message); }
                finally { _busy = false; }
            }) { IsBackground = true }.Start();
        }

        void DoRefresh()
        {
            string script =
                "echo SVC=$(systemctl is-active fishbot)\n" +
                "echo NC=$(docker ps --format '{{.Names}}' | tr '\\n' ',')\n" +
                "echo MEM=$(free -m | awk '/Mem:/{print $3\"/\"$2\" MB\"}')\n" +
                "echo UP=$(systemctl show -p ActiveEnterTimestamp --value fishbot)\n";
            string o = Remote(script, 25);
            Log("原始返回: " + o.Replace("\r", "").Replace("\n", " | ").Trim());
            if (o.Trim().Length == 0 || o.IndexOf("FATAL ERROR") >= 0 || o.IndexOf("[执行失败]") >= 0)
            {
                SetBig("● 服务器连不上", _th.Danger);
                SetSub("SSH 失败：" + FirstLine(o));
                Log("服务器查询失败：" + FirstLine(o));
                return;
            }
            string svc = Fld(o, "SVC="), nc = Fld(o, "NC="), mem = Fld(o, "MEM="), up = Fld(o, "UP=");
            bool active = svc.Trim() == "active";
            SetBig(active ? "● 运行中（云端）" : "● 已停止（云端）", active ? _th.Ok : _th.Danger);
            bool webui = WebUiReachable(3000);
            SetSubParts("服务 " + svc,
                        "容器 " + (nc.IndexOf("snowluma") >= 0 ? "snowluma 在跑" : "无"),
                        "WebUI " + (webui ? "通" : "不通"),
                        "内存 " + mem,
                        "启动于 " + (up.Length > 0 ? up : "-"));
            FillParts(_vmLine, "  ·  ", "虚拟机 " + VmState(), "同一 QQ 号只能一处在线");
            Log("状态 " + svc + " / 容器 " + (nc.IndexOf("snowluma") >= 0 ? "ok" : "缺") + " / 内存 " + mem);
        }

        void SetBig(string t, Color c)
        {
            _bigText = t; _bigColor = c;
            if (_big == null) return;
            if (_big.InvokeRequired) { _big.BeginInvoke(new Action<string, Color>(SetBig), t, c); return; }
            _big.Text = t; _big.ForeColor = c;
        }

        int AvailW(Label l)
        {
            if (l == null) return Math.Max(Sc(120), _cardW - Sc(24));
            if (l.MaximumSize.Width > 0) return l.MaximumSize.Width;
            if (l.Width > Sc(40)) return l.Width;
            return Math.Max(Sc(120), _cardW - Sc(24));
        }

        // 按"整段"拼行：只在分隔符处换行，不会把「内存」拆成「内 / 存」
        static string FlowJoin(string[] parts, Font f, int width, string sep)
        {
            if (parts == null || parts.Length == 0) return "";
            var sb = new StringBuilder();
            string line = "";
            foreach (var raw in parts)
            {
                string p = (raw ?? "").Trim();
                if (p.Length == 0) continue;
                if (line.Length == 0) { line = p; continue; }
                string cand = line + sep + p;
                if (TextRenderer.MeasureText(cand, f).Width > width)
                {
                    sb.AppendLine(line);
                    line = p;
                }
                else line = cand;
            }
            if (line.Length > 0) sb.Append(line);
            return sb.ToString();
        }

        // 设置文字后重新量高度，并按需要把卡片撑高
        void FitLabel(Label l)
        {
            if (l == null || l.AutoSize) return;
            try
            {
                var sz = TextRenderer.MeasureText(l.Text, l.Font, new Size(l.Width, int.MaxValue),
                                                  TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                l.Height = Math.Max(Sc(18), sz.Height + Sc(3));
                var card = l.Parent as RCard;
                if (card != null && card.Dock != DockStyle.Fill)
                {
                    int need = l.Bottom + Sc(24);
                    if (need > card.Height) card.Height = need;
                }
            }
            catch { }
        }

        void SetSub(string t)
        {
            if (_sub == null) return;
            if (_sub.InvokeRequired) { _sub.BeginInvoke(new Action<string>(SetSub), t); return; }
            _sub.Text = t;
            FitLabel(_sub);
        }

        // 状态行按「服务 / 容器 / 内存 / 启动于」逐段换行
        void SetSubParts(params string[] parts)
        {
            _subParts = parts;
            _statusAt = DateTime.Now;
            if (_sub == null) return;
            if (_sub.InvokeRequired) { _sub.BeginInvoke(new Action<string[]>(SetSubParts), new object[] { parts }); return; }
            _sub.Text = FlowJoin(parts, _sub.Font, AvailW(_sub), "  ·  ");
            FitLabel(_sub);
        }

        // 任意标签按段换行（同一个规则）
        void FillParts(Label l, string sep, params string[] parts)
        {
            if (l == null) return;
            if (l.InvokeRequired) { l.BeginInvoke(new Action<Label, string, string[]>(FillParts), l, sep, parts); return; }
            l.Text = FlowJoin(parts, l.Font, AvailW(l), sep);
            FitLabel(l);
        }

        static string Fld(string text, string key)
        {
            foreach (var ln in text.Replace("\r", "").Split('\n'))
                if (ln.StartsWith(key)) return ln.Substring(key.Length).Trim();
            return "";
        }
        static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(空)";
            var ln = s.Replace("\r", "").Split('\n')[0].Trim();
            return ln.Length == 0 ? "(空)" : ln;
        }
        static string LastLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(空)";
            var a = s.Replace("\r", "").Split('\n');
            for (int i = a.Length - 1; i >= 0; i--) if (a[i].Trim().Length > 0) return a[i].Trim();
            return "(空)";
        }

        void SshAction(string act)
        {
            if (_busy) { Log("上一个操作还没好"); return; }
            _busy = true;
            string label = act == "start" ? "启动" : act == "stop" ? "关闭（告别）" : "重启";
            Log("云端：" + label + "…");
            SetBig("● 正在" + label + "…", _th.Accent);
            new Thread(() =>
            {
                try
                {
                    string script = act == "stop"
                        ? "touch /opt/qqbot/data/stop.flag; sleep 11; systemctl is-active fishbot"
                        : "systemctl " + act + " fishbot; sleep 2; systemctl is-active fishbot";
                    string o = Remote(script, act == "stop" ? 70 : 60);
                    Log("云端：" + label + " → " + LastLine(o));
                    Thread.Sleep(1200);
                    DoRefresh();
                }
                catch (Exception e) { Log("操作失败: " + e.Message); }
                finally { _busy = false; }
            }) { IsBackground = true }.Start();
        }

        void FetchLogs()
        {
            if (_busy) return;
            _busy = true;
            Log("拉云端日志（60 行）…");
            new Thread(() =>
            {
                try
                {
                    string o = Remote("journalctl -u fishbot -n 60 --no-pager -o cat", 40);
                    foreach (var ln in o.Replace("\r", "").Split('\n'))
                        if (ln.Trim().Length > 0) Log("  " + ln);
                    Log("---- 结束 ----");
                }
                catch (Exception e) { Log("取日志失败: " + e.Message); }
                finally { _busy = false; }
            }) { IsBackground = true }.Start();
        }

        string QrShotScript()
        {
            return "sudo -n docker exec -u snowluma -e DISPLAY=:1 snowluma sh -lc "
                 + "\"ffmpeg -loglevel error -f x11grab -video_size 1920x1080 -i :1 "
                 + "-vf 'crop=220:220:852:434,scale=660:660:flags=neighbor' -frames:v 1 -y /tmp/q.png\" >/dev/null 2>&1; "
                 + "sudo -n docker exec snowluma sh -lc 'base64 -w0 /tmp/q.png 2>/dev/null'";
        }

        void FetchQrText()
        {
            if (_busy) return;
            _busy = true;
            Log("取二维码（从协议端窗口截屏）…");
            new Thread(() =>
            {
                try
                {
                    string b64 = Remote(QrShotScript(), 60).Trim();
                    if (b64.Length < 100) { Log("没取到二维码：可能已经登录了。"); return; }
                    Log("二维码已取到（" + (b64.Length / 1024) + " KB base64），看「登录二维码」窗口。");
                }
                catch (Exception e) { Log("取二维码失败: " + e.Message); }
                finally { _busy = false; }
            }) { IsBackground = true }.Start();
        }

        Label _diagBig;
        Label _diagDetail;
        string _diagVerdict = "";

        void RunDiagnosis()
        {
            if (_diagBig == null) return;
            _diagBig.Text = "● 正在自检…";
            _diagBig.ForeColor = _th.Sub;
            _diagDetail.Text = "";
            bool webui = false;
            new Thread(() =>
            {
                string raw = "";
                try { raw = Remote(DiagScript(), 40); } catch (Exception ex) { raw = "ERR=" + ex.Message; }
                try { webui = WebUiReachable(4000); } catch { }
                ApplyDiag(raw, webui);
            }) { IsBackground = true }.Start();
        }

        static string DiagScript()
        {
            return "echo SVC=$(systemctl is-active fishbot 2>/dev/null); "
                 + "echo NAP=$(sudo -n docker inspect -f '{{.State.Running}}' snowluma 2>/dev/null | grep -c true); "
                 + "echo RESTART=$(sudo -n docker inspect snowluma --format '{{.RestartCount}}' 2>/dev/null || echo 0); "
                 + "echo LOGINOK=$(curl -s -m 6 -H 'Authorization: Bearer mbVTYSJGtMmi9LkNF_t2QjJLlX9pQhqyoGOPtINfzAE' "
                 + "http://127.0.0.1:3000/get_login_info 2>/dev/null | grep -c '\"status\":\"ok\"'); "
                 + "echo RX3=$(journalctl -u fishbot --since '3 min ago' --no-pager 2>/dev/null | grep -cF '[接收]'); "
                 + "echo TX3=$(journalctl -u fishbot --since '3 min ago' --no-pager 2>/dev/null | grep -cF '[发送]'); "
                 + "echo RX60=$(journalctl -u fishbot --since '60 min ago' --no-pager 2>/dev/null | grep -cF '[接收]'); "
                 + "echo CONNECTEDNOW=$(journalctl -u fishbot --since '3 hours ago' --no-pager 2>/dev/null "
                 + "| grep -E 'connected|Trying to reconnect' | tail -1 | grep -c connected); "
                 + "echo LASTRX=$(journalctl -u fishbot --since '24 hours ago' --no-pager 2>/dev/null | grep -F '[接收]' | tail -1 | grep -oE '[0-9]{2}:[0-9]{2}:[0-9]{2}' | tail -1); "
                 + "echo LASTTX=$(journalctl -u fishbot --since '24 hours ago' --no-pager 2>/dev/null | grep -F '[发送]' | tail -1 | grep -oE '[0-9]{2}:[0-9]{2}:[0-9]{2}' | tail -1)";
        }

        void ApplyDiag(string raw, bool webui)
        {
            var kv = new Dictionary<string, string>();
            foreach (var line in (raw ?? "").Split(new char[] { '\n' }))
            {
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                kv[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
            Func<string, string, string> g = (k, d) => kv.ContainsKey(k) ? kv[k] : d;

            string svc = g("SVC", "?");
            int nap = 0, connNow = 0, loginOk = 0, restart = 0;
            int.TryParse(g("NAP", "0"), out nap);
            int.TryParse(g("CONNECTEDNOW", "0"), out connNow);
            int.TryParse(g("LOGINOK", "0"), out loginOk);
            int.TryParse(g("RESTART", "0"), out restart);
            string lastRx = g("LASTRX", ""), lastTx = g("LASTTX", "");
            int rx3 = 0, tx3 = 0, rx60 = 0;
            int.TryParse(g("RX3", "0"), out rx3);
            int.TryParse(g("TX3", "0"), out tx3);
            int.TryParse(g("RX60", "0"), out rx60);
            bool busy = (rx3 + tx3) > 0;
            bool loggedIn = loginOk > 0;

            var lines = new List<string>();
            lines.Add((svc == "active" ? "✓" : "✗") + " 机器人服务 fishbot：" + svc);
            lines.Add((nap > 0 ? "✓" : "✗") + " 协议端容器 snowluma：" + (nap > 0 ? "在跑（重启过 " + restart + " 次）" : "没起来"));
            lines.Add((loggedIn ? "✓" : "!") + " QQ 登录："
                      + (loggedIn ? "已登录（OneBot 能取到账号信息）" : "没登录 / 还在等扫码"));
            lines.Add((connNow == 1 ? "✓" : (loggedIn ? "!" : "✗")) + " OneBot 连接："
                      + (connNow == 1 ? "最近一次是「已连接」" : (loggedIn ? "最近一次是「重连中」" : "还没连上（未登录）")));
            lines.Add(busy ? "✓ 最近 3 分钟：收到 " + rx3 + " 条 / 发出 " + tx3 + " 条（这就是在线的铁证）"
                           : "· 最近 3 分钟：没有收发（最近 1 小时收 " + rx60 + " 条）");
            lines.Add("· 最近收到底：" + (string.IsNullOrEmpty(lastRx) ? "24 小时内没有" : lastRx + "（服务器时间）"));
            lines.Add("· 最近发出：" + (string.IsNullOrEmpty(lastTx) ? "24 小时内没有" : lastTx + "（服务器时间）"));
            lines.Add((webui ? "✓" : "·") + " SnowLuma WebUI 5099：" + (webui ? "从本机可访问" : "连不上（不影响机器人）"));

            string verdict, hint;
            Color color;
            if (svc != "active")
            {
                verdict = "不在线：机器人服务停了";
                hint = "点「一键修复」会 systemctl start；也可以点「看日志」看它为什么退。";
                color = _th.Danger;
            }
            else if (nap == 0)
            {
                verdict = "不在线：协议端容器没起来";
                hint = "点「一键修复」会 docker start snowluma（约 30 秒）。";
                color = _th.Danger;
            }
            else if (busy)
            {
                verdict = "在线，正在正常收发";
                hint = "她 3 分钟内还说过话，不用管。要复查就点「重新自检」。";
                color = Color.FromArgb(63, 200, 120);
            }
            else if (!loggedIn)
            {
                verdict = "不在线：卡在扫码登录";
                hint = "点「登录二维码」，手机 QQ 扫码确认（码 2 分钟过期，窗口每 25 秒自动换新的）。";
                color = _th.Danger;
            }
            else if (connNow == 1)
            {
                verdict = "在线，只是最近没人理她";
                hint = "WS 是通的，3 分钟没有消息说明群里安静（或没叫她）。";
                color = Color.FromArgb(63, 200, 120);
            }
            else
            {
                verdict = "不在线：OneBot 没连上";
                hint = "协议端在跑但 OneBot 没连上。点「重启协议端」试试；还不行就点「SnowLuma WebUI」看看。";
                color = _th.Danger;
            }

            _diagVerdict = verdict;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (_diagBig == null) return;
                    _diagBig.Text = "● " + verdict;
                    _diagBig.ForeColor = color;
                    _diagDetail.Text = hint + "\n" + string.Join("\n", lines.ToArray());
                    try { RelayoutCards(_curPage); } catch { }
                }));
            }
            catch { }
            Log("自检结论：" + verdict);
            foreach (var l in lines) Log("  " + l);
        }

        // 按自检结论自动做最合适的事
        void FixByDiag()
        {
            if (string.IsNullOrEmpty(_diagVerdict)) { RunDiagnosis(); return; }
            if (_diagVerdict.Contains("服务停了")) { SshAction("start"); Log("已请求启动 fishbot 服务"); return; }
            if (_diagVerdict.Contains("容器没起来"))
            {
                new Thread(() =>
                {
                    try { Remote("sudo -n docker start snowluma 2>&1; echo done", 60); Log("已尝试启动 snowluma 容器"); }
                    catch (Exception ex) { Log("启动失败: " + ex.Message); }
                    Thread.Sleep(1500);
                    RunDiagnosis();
                }) { IsBackground = true }.Start();
                return;
            }
            if (_diagVerdict.Contains("扫码"))
            {
                ShowQrWindow();
                return;
            }
            if (_diagVerdict.Contains("OneBot"))
            {
                if (WebUiReachable(3000)) OpenWebUi();
                else { Log("5099 不通，提示防火墙"); OpenWebUi(); }
                return;
            }
            Log("在线正常，无需修复。");
        }

        string WebUiUrl() { return "http://" + _cfg.Host + ":5099/"; }

        bool WebUiReachable(int ms)
        {
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(WebUiUrl());
                req.Method = "GET";
                req.Timeout = ms;
                req.ReadWriteTimeout = ms;
                req.AllowAutoRedirect = true;
                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                    return (int)resp.StatusCode > 0;
            }
            catch { return false; }
        }

        void OpenWebUi()
        {
            string url = WebUiUrl();
            try { Clipboard.SetText(_cfg.WebuiToken); } catch { }
            Log("打开 SnowLuma WebUI：" + url);
            Log("Token 已复制到剪贴板：" + _cfg.WebuiToken);
            try
            {
                Process.Start(url);
                Log("已在浏览器打开。进去后把 Token 粘进登录框即可。");
            }
            catch
            {
                try { Process.Start("explorer.exe", url); } catch { }
            }
            if (!WebUiReachable(4000))
            {
                Log("⚠ 5099 现在连不上：检查腾讯云防火墙是否放行 TCP 5099。");
                MessageBox.Show("浏览器已尝试打开：" + url
                    + "\n\nToken：" + _cfg.WebuiToken
                    + "\n\n（5099 暂时连不上，请确认腾讯云防火墙放行了 TCP 5099）",
                    "SnowLuma WebUI", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // 实时二维码窗口：从 SnowLuma 容器里的 QQ 窗口实时截屏，每 25 秒换新
        void ShowQrWindow()
        {
            var f = new Form();
            f.Text = "QQ 登录二维码";
            f.Size = new Size(Sc(470), Sc(660));
            f.StartPosition = FormStartPosition.CenterParent;
            f.BackColor = _th.Bg;
            f.ForeColor = _th.Text;
            f.Font = F(9.5f);
            f.MinimizeBox = false;

            var pic = new PictureBox();
            pic.SizeMode = PictureBoxSizeMode.Zoom;
            pic.Location = new Point(Sc(24), Sc(18));
            pic.Size = new Size(Sc(420), Sc(420));
            pic.BackColor = Color.White;
            f.Controls.Add(pic);

            var tip = new Label();
            tip.AutoSize = false;
            tip.Location = new Point(Sc(24), Sc(446));
            tip.Size = new Size(Sc(420), Sc(66));
            tip.ForeColor = _th.Sub;
            tip.Text = "正在获取二维码…";
            f.Controls.Add(tip);

            var bRefresh = Btn("刷新二维码", 24, 520, 128, 38, true, false);
            bRefresh.Click += (s2, e2) => LoadQr(pic, tip);
            f.Controls.Add(bRefresh);

            var bNew = Btn("重启协议端换新码", 164, 520, 190, 38, false, false);
            bNew.Click += (s2, e2) =>
            {
                tip.Text = "正在重启协议端（SnowLuma），约 30 秒后自动取新码…";
                new Thread(() =>
                {
                    try
                    {
                        Remote("sudo -n docker restart snowluma >/dev/null 2>&1; sleep 22", 60);
                        Thread.Sleep(1200);
                        LoadQr(pic, tip);
                    }
                    catch (Exception ex) { Log("重启协议端失败: " + ex.Message); }
                }) { IsBackground = true }.Start();
            };
            f.Controls.Add(bNew);

            var bClose = Btn("关闭", 366, 520, 78, 38, false, false);
            bClose.Click += (s2, e2) => f.Close();
            f.Controls.Add(bClose);

            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 25000;
            timer.Tick += (s2, e2) => LoadQr(pic, tip);
            timer.Start();

            f.Shown += (s2, e2) => { LoadQr(pic, tip); timer.Start(); };
            f.FormClosed += (s2, e2) => { timer.Stop(); timer.Dispose(); };
            f.ShowDialog(this);
        }

        // 取二维码：容器里点「刷新」→ 挪开鼠标 → 截屏裁图 → 容器里 base64 → 传回来
        string QrScript()
        {
            return "sudo -n docker exec -u snowluma -e DISPLAY=:1 snowluma sh -lc 'xdotool mousemove 961 581 click 1' >/dev/null 2>&1; "
                 + "sleep 3; "
                 + "sudo -n docker exec -u snowluma -e DISPLAY=:1 snowluma sh -lc \"xdotool mousemove 1500 950; "
                 + "ffmpeg -loglevel error -f x11grab -video_size 1920x1080 -i :1 "
                 + "-vf 'crop=220:220:852:434,scale=660:660:flags=neighbor' -frames:v 1 -y /tmp/qr_ui.png\" >/dev/null 2>&1; "
                 + "sudo -n docker exec snowluma sh -lc 'base64 -w0 /tmp/qr_ui.png'";
        }

        public byte[] FetchQrPng()
        {
            string b64 = Remote(QrScript(), 90);
            var m = Regex.Match(b64 ?? "", "[A-Za-z0-9+/=]{1500,}");
            if (!m.Success) return null;
            try { return Convert.FromBase64String(m.Value); } catch { return null; }
        }

        void LoadQr(PictureBox pic, Label tip)
        {
            tip.Text = "正在获取二维码（会先点一下刷新）…";
            new Thread(() =>
            {
                byte[] data = null;
                try { data = FetchQrPng(); }
                catch (Exception ex) { Log("取二维码失败: " + ex.Message); }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (data != null && data.Length > 2000)
                        {
                            using (var ms = new MemoryStream(data))
                            {
                                var img = Image.FromStream(ms);
                                if (pic.Image != null) pic.Image.Dispose();
                                pic.Image = img;
                            }
                            tip.Text = "用手机 QQ 扫码登录（每 25 秒自动换新）。";
                        }
                        else
                        {
                            tip.Text = "没取到二维码：可能已经登录，或容器还在启动。点「刷新二维码」重试。";
                        }
                    }));
                }
                catch { }
            }) { IsBackground = true }.Start();
        }

        void RestartNapCat()   // 现在重启的是 SnowLuma 容器
        {
            if (_busy) return;
            if (MessageBox.Show("重启协议端（SnowLuma）？约 30 秒恢复。", "确认",
                                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            _busy = true;
            Log("重启协议端（snowluma）…");
            new Thread(() =>
            {
                try
                {
                    string o = Remote("sudo -n docker restart snowluma >/dev/null 2>&1; sleep 25; sudo -n docker ps --format '{{.Names}} {{.Status}}'", 120);
                    Log("容器：" + FirstLine(o));
                }
                catch (Exception e) { Log("重启容器失败: " + e.Message); }
                finally { _busy = false; }
            }) { IsBackground = true }.Start();
        }

        // 控制台手机服务的 token（8090 端口），和 SnowLuma WebUI 的 token 无关
        static readonly string ChatApiToken =
            Environment.GetEnvironmentVariable("FATBOT_CONSOLE_TOKEN") ?? "CHANGE_ME_CONSOLE_TOKEN";
        const string ChatApiBase = "http://127.0.0.1:8090";

        // ------------------------------------------------ 聊天记录（QQ 风格）
        public Form ChatWindowForShot;
        public void OpenChatForShot() { ShowChatWindow(); }

        // QQNT 风格聊天窗口
        void ShowChatWindow()
        {
            Color bgA = Color.FromArgb(30, 31, 34);
            Color bgB = Color.FromArgb(43, 45, 49);
            Color bgC = Color.FromArgb(49, 51, 56);
            Color txt = Color.FromArgb(235, 237, 240);
            Color sub = Color.FromArgb(150, 156, 166);
            Color accent = Color.FromArgb(0, 153, 255);

            var win = new Form();
            win.Text = "聊天记录 · " + _cfg.Host;
            win.StartPosition = FormStartPosition.CenterParent;
            win.ClientSize = new Size(Sc(880), Sc(640));
            win.BackColor = bgA;
            win.ForeColor = txt;
            win.Font = new Font("Microsoft YaHei UI", 9F);
            win.MinimizeBox = false;

            var rightHead = new Panel();
            rightHead.Dock = DockStyle.Top;
            rightHead.Height = Sc(54);
            rightHead.BackColor = bgA;
            win.Controls.Add(rightHead);

            var headName = new Label();
            headName.Text = "";
            headName.ForeColor = txt;
            headName.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            headName.AutoSize = false;
            headName.Dock = DockStyle.Left;
            headName.Width = Sc(380);
            headName.TextAlign = ContentAlignment.MiddleLeft;
            headName.Padding = new Padding(Sc(18), 0, 0, Sc(2));
            rightHead.Controls.Add(headName);

            var headSub = new Label();
            headSub.Text = "";
            headSub.ForeColor = sub;
            headSub.Font = new Font("Microsoft YaHei UI", 8.5F);
            headSub.AutoSize = false;
            headSub.Dock = DockStyle.Left;
            headSub.Width = Sc(200);
            headSub.TextAlign = ContentAlignment.MiddleLeft;
            rightHead.Controls.Add(headSub);
            headSub.BringToFront();

            var btnRefresh = new RoundButton();
            btnRefresh.Text = "刷新";
            btnRefresh.Dock = DockStyle.Right;
            btnRefresh.Width = Sc(64);
            btnRefresh.Radius = 9;
            btnRefresh.FillColor = bgC;
            btnRefresh.HoverColor = Color.FromArgb(62, 65, 72);
            btnRefresh.LabelColor = sub;
            btnRefresh.Font = new Font("Microsoft YaHei UI", 8.5F);
            btnRefresh.Margin = new Padding(0, Sc(12), Sc(12), Sc(12));
            rightHead.Controls.Add(btnRefresh);

            var bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = Sc(88);
            bottom.BackColor = bgB;
            bottom.Padding = new Padding(Sc(14), Sc(8), Sc(14), Sc(10));
            win.Controls.Add(bottom);

            // 快捷消息
            var chips = new FlowLayoutPanel();
            chips.Dock = DockStyle.None;
            chips.Height = Sc(30);
            chips.FlowDirection = FlowDirection.LeftToRight;
            chips.WrapContents = false;
            chips.BackColor = bgB;
            chips.Padding = new Padding(0);
            bottom.Controls.Add(chips);

            // 输入行
            var sendRow = new Panel();
            sendRow.Dock = DockStyle.None;
            sendRow.Height = Sc(40);
            sendRow.BackColor = bgB;
            bottom.Controls.Add(sendRow);
            bottom.Resize += (s9, e9) =>
            {
                chips.SetBounds(0, 0, bottom.Width, Sc(30));
                sendRow.SetBounds(0, Sc(32), bottom.Width, Sc(40));
            };

            var btnSend = new RoundButton();
            btnSend.Text = "发送";
            btnSend.Dock = DockStyle.Right;
            btnSend.Width = Sc(76);
            btnSend.Radius = 10;
            btnSend.FillColor = accent;
            btnSend.HoverColor = Color.FromArgb(40, 175, 255);
            btnSend.LabelColor = Color.White;
            btnSend.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            btnSend.Margin = new Padding(Sc(8), 0, 0, 0);
            sendRow.Controls.Add(btnSend);

            bool voiceOn = false;
            var btnVoice = new RoundButton();
            btnVoice.Text = "语音";
            btnVoice.Dock = DockStyle.Right;
            btnVoice.Width = Sc(64);
            btnVoice.Radius = 10;
            btnVoice.FillColor = Color.FromArgb(58, 61, 67);
            btnVoice.HoverColor = Color.FromArgb(72, 76, 84);
            btnVoice.LabelColor = Color.FromArgb(210, 214, 220);
            btnVoice.Font = new Font("Microsoft YaHei UI", 9F);
            btnVoice.Margin = new Padding(Sc(8), 0, 0, 0);
            sendRow.Controls.Add(btnVoice);

            var inputWrap = new RoundPanel();
            inputWrap.Dock = DockStyle.Fill;
            inputWrap.Radius = 12;
            inputWrap.FillColor = bgC;
            inputWrap.BorderColor = bgC;
            inputWrap.Padding = new Padding(Sc(12), Sc(10), Sc(12), Sc(8));
            sendRow.Controls.Add(inputWrap);
            inputWrap.BringToFront();

            var input = new TextBox();
            input.BorderStyle = BorderStyle.None;
            input.BackColor = bgC;
            input.ForeColor = txt;
            input.Font = new Font("Microsoft YaHei UI", 10F);
            input.Dock = DockStyle.Fill;
            inputWrap.Controls.Add(input);

            var bubble = new ChatBubblePanel();
            bubble.Dock = DockStyle.Fill;
            bubble.BgColor = bgA;
            bubble.CardColor = bgC;
            bubble.LineColor = Color.FromArgb(60, 63, 69);
            bubble.TextColor = txt;
            bubble.SubColor = sub;
            bubble.AccentColor = accent;
            bubble.Font = new Font("Microsoft YaHei UI", 10F);
            win.Controls.Add(bubble);
            bubble.BringToFront();

            var side = new Panel();
            side.Dock = DockStyle.Left;
            side.Width = Sc(250);
            side.BackColor = bgB;
            win.Controls.Add(side);

            var search = new TextBox();
            search.Text = "搜索会话…";
            search.ForeColor = sub;
            search.Enter += (s9, e9) => { if (search.Text == "搜索会话…") { search.Text = ""; search.ForeColor = txt; } };
            search.Leave += (s9, e9) => { if (search.Text.Trim().Length == 0) { search.Text = "搜索会话…"; search.ForeColor = sub; } };
            search.BorderStyle = BorderStyle.None;
            search.BackColor = bgA;
            search.ForeColor = txt;
            search.Font = new Font("Microsoft YaHei UI", 9F);
            var searchWrap = new Panel();
            searchWrap.Dock = DockStyle.Top;
            searchWrap.Height = Sc(52);
            searchWrap.BackColor = bgB;
            searchWrap.Padding = new Padding(Sc(12), Sc(12), Sc(12), Sc(8));
            var searchBox = new Panel();
            searchBox.Dock = DockStyle.Fill;
            searchBox.BackColor = bgA;
            searchBox.Padding = new Padding(Sc(10), Sc(7), Sc(10), Sc(7));
            searchBox.Controls.Add(search);
            searchWrap.Controls.Add(searchBox);
            side.Controls.Add(searchWrap);

            var list = new ListBox();
            list.Dock = DockStyle.Fill;
            list.BackColor = bgB;
            list.ForeColor = txt;
            list.BorderStyle = BorderStyle.None;
            list.ItemHeight = Sc(62);
            list.DrawMode = DrawMode.OwnerDrawFixed;
            list.IntegralHeight = false;
            side.Controls.Add(list);
            list.BringToFront();

            string current = "";
            var all = new List<ConvItem>();

            list.DrawItem += (s3, e3) =>
            {
                if (e3.Index < 0 || e3.Index >= list.Items.Count) return;
                var it = list.Items[e3.Index] as ConvItem;
                if (it == null) return;
                var bounds = e3.Bounds;
                var g = e3.Graphics;
                bool sel = list.SelectedIndex == e3.Index;
                using (var bg = new SolidBrush(sel ? Color.FromArgb(58, 61, 67) : bgB))
                    g.FillRectangle(bg, bounds);
                if (sel)
                    using (var bar = new SolidBrush(accent))
                        g.FillRectangle(bar, bounds.X, bounds.Y + Sc(8), Sc(3), bounds.Height - Sc(16));
                int av = Sc(38);
                var avRect = new Rectangle(bounds.X + Sc(12), bounds.Y + Sc(12), av, av);
                using (var ab = new SolidBrush(it.AvatarColor))
                    g.FillEllipse(ab, avRect);
                using (var fA = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold))
                using (var wb = new SolidBrush(Color.White))
                {
                    var sf = new StringFormat();
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(it.Initial, fA, wb, avRect, sf);
                }
                using (var fN = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold))
                using (var fP = new Font("Microsoft YaHei UI", 8F))
                using (var fT = new Font("Microsoft YaHei UI", 7.5F))
                {
                    using (var br = new SolidBrush(txt))
                        g.DrawString(it.Name, fN, br, bounds.X + Sc(60), bounds.Y + Sc(11));
                    var tsz = g.MeasureString(it.Time, fT);
                    using (var br = new SolidBrush(sub))
                        g.DrawString(it.Time, fT, br, bounds.Right - tsz.Width - Sc(12), bounds.Y + Sc(12));
                    using (var br = new SolidBrush(sub))
                        g.DrawString(it.Preview, fP, br,
                            new RectangleF(bounds.X + Sc(60), bounds.Y + Sc(33), bounds.Width - Sc(72), Sc(20)));
                }
            };

            Action reloadConvs = null;
            Action reloadMsgs = null;
            Action ApplyFilter = null;

            reloadConvs = () =>
            {
                new Thread(() =>
                {
                    string json = Remote("curl -s -m 30 '" + ChatApiBase + "/api/conversations?token=" + ChatApiToken + "'", 60);
                    var items = new List<ConvItem>();
                    try
                    {
                        var ser = new JavaScriptSerializer();
                        var root = ser.DeserializeObject(json) as Dictionary<string, object>;
                        var arr = root["items"] as object[];
                        Color[] palette = {
                            Color.FromArgb(0,153,255), Color.FromArgb(76,175,80), Color.FromArgb(255,152,0),
                            Color.FromArgb(156,39,176), Color.FromArgb(233,30,99), Color.FromArgb(0,150,136) };
                        foreach (var o in arr)
                        {
                            var d = o as Dictionary<string, object>;
                            var it = new ConvItem();
                            it.Name = Convert.ToString(d["chat"]);
                            it.Time = Convert.ToString(d["ts"]);
                            it.Count = Convert.ToInt32(d["count"]);
                            string last = Convert.ToString(d["last"]);
                            if (last.StartsWith("【")) last = last.Contains("表情") ? "[表情]" : "[图片]";
                            it.Preview = last;
                            if (it.Name.StartsWith("群")) it.Initial = "群";
                            else if (it.Name.StartsWith("私聊")) it.Initial = "私";
                            else it.Initial = "?";
                            int h = 0;
                            foreach (char ch in it.Name) h = (h * 31 + ch) & 0x7fffffff;
                            it.AvatarColor = palette[h % palette.Length];
                            items.Add(it);
                        }
                    }
                    catch (Exception ex) { Log("会话列表失败: " + ex.Message); }
                    BeginInvoke(new Action(() =>
                    {
                        string keep = current;
                        all.Clear();
                        all.AddRange(items);
                        ApplyFilter();
                        // 刷新后回到原来那个会话；用户没得选才切到第一个
                        int idx = -1;
                        if (keep.Length > 0)
                            for (int k = 0; k < list.Items.Count; k++)
                            {
                                var ci = list.Items[k] as ConvItem;
                                if (ci != null && ci.Name == keep) { idx = k; break; }
                            }
                        if (idx >= 0)
                        {
                            if (list.SelectedIndex != idx) list.SelectedIndex = idx;
                            else list.Invalidate();
                        }
                        else if (keep.Length == 0 && list.Items.Count > 0)
                        {
                            list.SelectedIndex = 0;
                        }
                    }));
                }) { IsBackground = true }.Start();
            };

            ApplyFilter = () =>
            {
                string q = search.Text.Trim();
                if (q == "搜索会话…") q = "";
                list.Items.Clear();
                foreach (var it in all)
                    if (q.Length == 0 || it.Name.IndexOf(q) >= 0 || it.Preview.IndexOf(q) >= 0)
                        list.Items.Add(it);
            };

            reloadMsgs = () =>
            {
                if (string.IsNullOrEmpty(current)) return;
                new Thread(() =>
                {
                    string enc = Uri.EscapeDataString(current);
                    string json = Remote("curl -s -m 40 '" + ChatApiBase + "/api/messages?token=" + ChatApiToken + "&chat=" + enc + "'", 60);
                    var rows = new List<string[]>();
                    try
                    {
                        var ser = new JavaScriptSerializer();
                        var root = ser.DeserializeObject(json) as Dictionary<string, object>;
                        var arr = root["messages"] as object[];
                        foreach (var o in arr)
                        {
                            var d = o as Dictionary<string, object>;
                            string kind = Convert.ToString(d["kind"]);
                            string text = Convert.ToString(d["text"]);
                            if (kind == "media") text = text.Contains("表情") ? "[表情]" : "[图片]";
                            rows.Add(new string[] {
                                Convert.ToString(d["dir"]), Convert.ToString(d["who"]),
                                Convert.ToString(d["ts"]), text });
                        }
                    }
                    catch (Exception ex) { Log("消息加载失败: " + ex.Message); }
                    BeginInvoke(new Action(() => bubble.SetMessages(rows)));
                }) { IsBackground = true }.Start();
            };

            list.SelectedIndexChanged += (s2, e2) =>
            {
                list.Invalidate();          // 关键：整表重绘，否则旧行的高亮擦不掉
                var it = list.SelectedItem as ConvItem;
                if (it == null) return;
                bool changed = current != it.Name;
                current = it.Name;
                headName.Text = it.Name;
                headSub.Text = it.Count + " 条消息";
                if (changed) reloadMsgs();
            };

            search.TextChanged += (s2, e2) => ApplyFilter();

            Action doSend = () =>
            {
                string text = input.Text.Trim();
                if (text.Length == 0 || current.Length == 0) return;
                bool voice = voiceOn;
                input.Text = "";
                new Thread(() =>
                {
                    var payload = new Dictionary<string, object>();
                    payload["chat"] = current;
                    payload["text"] = text;
                    payload["voice"] = voice;
                    string body = new JavaScriptSerializer().Serialize(payload);
                    string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                    string cmd = "echo " + b64 + " | base64 -d > /tmp/chat_send.json; "
                               + "curl -s -m 180 -X POST -H 'Content-Type: application/json' --data-binary @/tmp/chat_send.json "
                               + "'" + ChatApiBase + "/api/send?token=" + ChatApiToken + "'";
                    string r = Remote(cmd, 200);
                    Log("发送结果: " + r);
                    BeginInvoke(new Action(reloadMsgs));
                }) { IsBackground = true }.Start();
            };

            btnVoice.Click += (s2, e2) =>
            {
                voiceOn = !voiceOn;
                btnVoice.Text = voiceOn ? "语音 ✓" : "语音";
                btnVoice.FillColor = voiceOn ? accent : Color.FromArgb(58, 61, 67);
                btnVoice.HoverColor = voiceOn ? Color.FromArgb(40, 175, 255) : Color.FromArgb(72, 76, 84);
                btnVoice.LabelColor = voiceOn ? Color.White : Color.FromArgb(210, 214, 220);
                btnVoice.Invalidate();
            };

            // 快捷消息按钮
            string[] quick = { "我回来啦", "我去吃饭了", "晚安", "在忙吗" };
            foreach (string q in quick)
            {
                var chip = new RoundButton();
                chip.Text = q;
                chip.Radius = 12;
                chip.FillColor = Color.FromArgb(52, 55, 61);
                chip.HoverColor = Color.FromArgb(68, 72, 79);
                chip.LabelColor = Color.FromArgb(200, 205, 212);
                chip.Font = new Font("Microsoft YaHei UI", 8.5F);
                chip.Size = new Size(Sc(86), Sc(26));
                chip.Margin = new Padding(0, 0, Sc(8), 0);
                string qq = q;
                chip.Click += (s2, e2) => { input.Text = qq; doSend(); };
                chips.Controls.Add(chip);
            }

            btnSend.Click += (s2, e2) => doSend();
            input.KeyDown += (s2, e2) =>
            {
                if (e2.KeyCode == Keys.Enter) { e2.SuppressKeyPress = true; doSend(); }
            };
            btnRefresh.Click += (s2, e2) => { reloadConvs(); reloadMsgs(); };

            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 15000;
            timer.Tick += (s2, e2) =>
            {
                reloadConvs();
                // 用户正在打字就别刷消息区，免得内容/滚动跳走
                if (input.Focused && input.Text.Length > 0) return;
                reloadMsgs();
            };
            timer.Start();
            win.FormClosed += (s2, e2) => timer.Stop();

            DarkUi.Flatten(win);
            reloadConvs();
            ChatWindowForShot = win;
            win.Show(this);
            win.BringToFront();
        }

        class ConvItem
        {
            public string Name = "";
            public string Time = "";
            public string Preview = "";
            public string Initial = "?";
            public int Count = 0;
            public Color AvatarColor = Color.FromArgb(0, 153, 255);
            public override string ToString() { return Name; }
        }

        // ------------------------------------------------ 本地虚拟机
        string VmState()
        {
            try
            {
                string o = RunProc(_cfg.VBox, "showvminfo \"" + _cfg.VmName + "\" --machinereadable", 8000);
                var m = Regex.Match(o, "VMState=\"([^\"]+)\"");
                return m.Success ? m.Groups[1].Value : "未注册";
            }
            catch { return "查询失败"; }
        }

        void VmStart()
        {
            if (_busy) return;
            _busy = true;
            Log("启动家里虚拟机…");
            new Thread(() =>
            {
                try
                {
                    string o = RunProc(_cfg.VBox, "startvm \"" + _cfg.VmName + "\" --type headless", 120000);
                    Log("VBox：" + FirstLine(o));
                    if (_vmLine != null) _vmLine.Text = "虚拟机：" + VmState();
                }
                catch (Exception e) { Log("启动失败: " + e.Message); }
                finally { _busy = false; }
            }) { IsBackground = true }.Start();
        }

        void VmPowerOff()
        {
            if (_busy) return;
            if (MessageBox.Show("给虚拟机发 ACPI 关机信号？", "确认", MessageBoxButtons.OKCancel,
                                MessageBoxIcon.Question) != DialogResult.OK) return;
            _busy = true;
            new Thread(() =>
            {
                try
                {
                    RunProc(_cfg.VBox, "controlvm \"" + _cfg.VmName + "\" acpipowerbutton", 30000);
                    Thread.Sleep(3000);
                    if (_vmLine != null) _vmLine.Text = "虚拟机：" + VmState();
                    Log("已发关机信号");
                }
                catch (Exception e) { Log("关机失败: " + e.Message); }
                finally { _busy = false; }
            }) { IsBackground = true }.Start();
        }

        void VmBotCmd(string cmd)
        {
            if (_busy) return;
            _busy = true;
            Log("虚拟机：机器人 " + (cmd == "stop" ? "关闭" : "启动") + "…");
            new Thread(() =>
            {
                try
                {
                    string json = VmSendCommand(cmd, cmd == "stop" ? 70 : 50);
                    if (json == null) { Log("虚拟机没回结果（没开机？）"); return; }
                    Log("结果：" + json.Replace("\r", "").Replace("\n", " "));
                }
                catch (Exception e) { Log("失败: " + e.Message); }
                finally { _busy = false; }
            }) { IsBackground = true }.Start();
        }

        string VmSendCommand(string cmd, int timeoutSec)
        {
            string nonce = Guid.NewGuid().ToString("N").Substring(0, 8);
            string tmpCmd = Path.Combine(Path.GetTempPath(), "fish_cmd.json");
            string tmpRes = Path.Combine(Path.GetTempPath(), "fish_res.json");
            try { if (File.Exists(tmpRes)) File.Delete(tmpRes); } catch { }
            File.WriteAllText(tmpCmd, "{\"cmd\":\"" + cmd + "\",\"nonce\":\"" + nonce + "\"}", new UTF8Encoding(false));
            RunProc(_cfg.VBox, "guestcontrol \"" + _cfg.VmName + "\" copyto --username " + _cfg.VmUser +
                    " --password \"" + _cfg.VmPass + "\" \"" + tmpCmd + "\" \"" + GuestCmd + "\"", 60000);
            for (int i = 0; i < timeoutSec; i++)
            {
                Thread.Sleep(1000);
                RunProc(_cfg.VBox, "guestcontrol \"" + _cfg.VmName + "\" copyfrom --username " + _cfg.VmUser +
                        " --password \"" + _cfg.VmPass + "\" \"" + GuestRes + "\" \"" + tmpRes + "\"", 30000);
                if (!File.Exists(tmpRes)) continue;
                string json = "";
                try { json = File.ReadAllText(tmpRes, Encoding.UTF8); } catch { continue; }
                if (json.IndexOf(nonce) < 0) continue;
                return json;
            }
            return null;
        }

        const int WM_SETREDRAW = 0x000B;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool RedrawWindow(IntPtr h, IntPtr r, IntPtr rg, uint flags);

        void ForceRedraw()
        {
            try
            {
                if (!IsHandleCreated) return;
                // RDW_INVALIDATE|RDW_ERASE|RDW_ALLCHILDREN|RDW_UPDATENOW|RDW_FRAME
                RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0100 | 0x0400);
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        public static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            DarkUi.Preferred(true);
            if (args.Length > 0 && args[0].TrimStart('-').ToLower() == "status")
            {
                var f = new MainForm();
                string o = f.Remote("systemctl is-active fishbot", 20);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "fish_cli_result.json"),
                    "{\"ok\":true,\"raw\":\"" + o.Replace("\r", "").Replace("\n", " ").Replace("\"", "'") + "\"}",
                    new UTF8Encoding(false));
                return;
            }
            if (args.Length >= 2 && args[0].TrimStart('-').ToLower() == "qr")
            {
                var f = new MainForm();
                var png = f.FetchQrPng();
                if (png != null && png.Length > 2000)
                {
                    File.WriteAllBytes(args[1], png);
                    Console.WriteLine("OK " + png.Length + " bytes -> " + args[1]);
                }
                else Console.WriteLine("FAIL 没有取到二维码");
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length >= 2 && args[0].TrimStart('-').ToLower() == "shotchat")
            {
                var mf = new MainForm();
                mf.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(800);
                mf.OpenChatForShot();
                Application.DoEvents();
                for (int i = 0; i < 14; i++) { System.Threading.Thread.Sleep(1000); Application.DoEvents(); }
                Application.DoEvents();
                try
                {
                    var f = mf.ChatWindowForShot;
                    if (f == null) { Console.WriteLine("FAIL 没有聊天窗口"); }
                    else
                    {
                        var bmp = new Bitmap(f.Width, f.Height);
                        using (var g = Graphics.FromImage(bmp))
                        {
                            IntPtr hdc = g.GetHdc();
                            PrintWindow(f.Handle, hdc, 2);
                            g.ReleaseHdc(hdc);
                        }
                        bmp.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                        bmp.Dispose();
                        Console.WriteLine("OK -> " + args[1]);
                    }
                }
                catch (Exception ex) { Console.WriteLine("ERR " + ex.Message); }
                return;
            }

            if (args.Length >= 3 && args[0].TrimStart('-').ToLower() == "shotchat")
            {
                var mf = new MainForm();
                mf.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(800);
                mf.OpenChatForShot();
                Application.DoEvents();
                for (int i = 0; i < 14; i++) { System.Threading.Thread.Sleep(1000); Application.DoEvents(); }
                Application.DoEvents();
                try
                {
                    var f = mf.ChatWindowForShot;
                    if (f == null) { Console.WriteLine("FAIL 没有聊天窗口"); }
                    else
                    {
                        var bmp = new Bitmap(f.Width, f.Height);
                        using (var g = Graphics.FromImage(bmp))
                        {
                            IntPtr hdc = g.GetHdc();
                            PrintWindow(f.Handle, hdc, 2);
                            g.ReleaseHdc(hdc);
                        }
                        bmp.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                        bmp.Dispose();
                        Console.WriteLine("OK -> " + args[1]);
                    }
                }
                catch (Exception ex) { Console.WriteLine("ERR " + ex.Message); }
                return;
            }

            if (args.Length >= 3 && args[0].TrimStart('-').ToLower() == "shot")
            {
                var f = new MainForm();
                if (args.Length >= 4)
                {
                    int hh;
                    if (int.TryParse(args[3], out hh) && hh > 400)
                        f.Height = hh;
                }
                f.Show();
                f.GotoPage(args[1]);
                if (args.Length >= 5)
                {
                    int sy;
                    if (int.TryParse(args[4], out sy) && sy > 0)
                    {
                        Application.DoEvents();
                        f.ScrollPageTo(sy);
                    }
                }
                Application.DoEvents();
                for (int i = 0; i < 6; i++) { System.Threading.Thread.Sleep(700); Application.DoEvents(); }
                try
                {
                    using (var bmp = new Bitmap(f.Width, f.Height))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            IntPtr hdc = g.GetHdc();
                            PrintWindow(f.Handle, hdc, 2);
                            g.ReleaseHdc(hdc);
                        }
                        bmp.Save(args[2], System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                catch (Exception ex)
                {
                    File.AppendAllText(_logFile, "shot failed: " + ex.Message + "\r\n");
                }
                return;
            }

            Application.Run(new MainForm());
        }
    }
}

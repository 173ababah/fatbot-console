// 肥鱼控制台 · WPF 版
// 为什么换：WinForms 的滚动是把子控件整块重画，卡片一多就闪/撕裂；
// WPF 是保留模式 + 合成渲染，滚动天然不闪，深色主题和圆角也简单得多。
//
// 只用 .NET Framework 4.8 自带的东西（WPF + WinForms 托盘图标），不需要装任何运行时。
// 配置沿用 %APPDATA%\FishBotConsole\config.ini，跟老版共用，切过去不用重填。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace FishConsoleWpf
{
    // ============================================================ 配置（和老版同格式）
    class Cfg
    {
        public string Host = "";      // 首次运行填 / 读 console-config.json
        public string User = "";
        public string Pass = "";
        public string HostKey = "";     // 服务器指纹，首次连上时自己缓存
        public bool Dark = true;
        public double Scale = 1.0;
        public bool Tray = true;
        public bool TrayClose = true;
        public bool TrayStart = false;
        public string WebuiToken = "";  // 控制台 token，读 console-config.json

        const string XorKey = "fish-bot-2026";

        public static string Dir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FishBotConsole");
            }
        }
        public static string FilePath { get { return Path.Combine(Dir, "config.ini"); } }

        public static string Obf(string s)
        {
            if (s == null) s = "";
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++) sb.Append((char)(s[i] ^ XorKey[i % XorKey.Length]));
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
        }
        public static string DeObf(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
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
                    var p = line.Split('=');
                    if (p.Length < 2) continue;
                    string k = p[0].Trim().ToLower();
                    string v = line.Substring(line.IndexOf('=') + 1);
                    if (k == "host") c.Host = v;
                    else if (k == "user") c.User = v;
                    else if (k == "pass") c.Pass = DeObf(v);
                    else if (k == "hostkey") c.HostKey = v;
                    else if (k == "dark") c.Dark = v == "1";
                    else if (k == "scale") { double d; if (double.TryParse(v, out d)) c.Scale = Math.Max(0.8, Math.Min(1.5, d)); }
                    else if (k == "webuitoken") c.WebuiToken = v;
                    else if (k == "tray") c.Tray = v != "0";
                    else if (k == "trayclose") c.TrayClose = v != "0";
                    else if (k == "traystart") c.TrayStart = v != "0";
                }
            }
            catch { }
            return c;
        }

        /// <summary>保存时保留老版的其他键（vbox / vm 这些），别把配置文件写残。</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var map = new Dictionary<string, string>();
                var order = new List<string>();
                if (File.Exists(FilePath))
                {
                    foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        int i = line.IndexOf('=');
                        if (i <= 0) continue;
                        string k = line.Substring(0, i).Trim();
                        if (!map.ContainsKey(k)) order.Add(k);
                        map[k] = line.Substring(i + 1);
                    }
                }
                Action<string, string> put = delegate(string k, string v)
                {
                    if (!map.ContainsKey(k)) order.Add(k);
                    map[k] = v;
                };
                put("host", Host);
                put("user", User);
                put("pass", Obf(Pass));
                put("hostkey", HostKey);
                put("dark", Dark ? "1" : "0");
                put("scale", Scale.ToString("0.00"));
                put("webuitoken", WebuiToken);
                put("tray", Tray ? "1" : "0");
                put("trayclose", TrayClose ? "1" : "0");
                put("traystart", TrayStart ? "1" : "0");

                var sb = new StringBuilder();
                foreach (var k in order)
                {
                    if (k == "pass" || k == "vmpass") continue;      // 密码单独按顺序补
                    sb.AppendLine(k + "=" + map[k]);
                }
                var all = new List<string>();
                foreach (var k in order)
                {
                    if (k == "pass") continue;
                    all.Add(k + "=" + map[k]);
                }
                // 保持 host/user/pass 在开头（和老版一致，方便手改）
                var final = new StringBuilder();
                final.AppendLine("host=" + Host);
                final.AppendLine("user=" + User);
                final.AppendLine("pass=" + Obf(Pass));
                foreach (var k in order)
                {
                    if (k == "host" || k == "user" || k == "pass") continue;
                    final.AppendLine(k + "=" + map[k]);
                }
                File.WriteAllText(FilePath, final.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Ssh.Dbg = "保存配置失败：" + ex.Message; }
        }
    }

    // ============================================================ SSH
    static class Ssh
    {
        public static string Dbg = "";

        static string _plinkPath;

        /// <summary>
        /// 找 plink.exe。不能写死目录——之前写死 `02-程序\开关软件\plink.exe`，
        /// 文件夹一改名（开关软件 → 旧-控制台）就全断了：状态查不到、聊天记录空、保存失败。
        /// 现在按「同目录 → 往上几层和它们的 bin → 02-程序 下递归 → 仓库根 → 环境变量」依次找。
        /// </summary>
        public static string Plink()
        {
            if (!string.IsNullOrEmpty(_plinkPath) && File.Exists(_plinkPath)) return _plinkPath;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var dirs = new List<string>();
            dirs.Add(baseDir);
            dirs.Add(Path.Combine(baseDir, "bin"));
            try
            {
                var d = new DirectoryInfo(baseDir);
                for (int i = 0; i < 5 && d != null && d.Parent != null; i++)
                {
                    d = d.Parent;
                    dirs.Add(d.FullName);
                    dirs.Add(Path.Combine(d.FullName, "bin"));
                }
            }
            catch { }
            foreach (var d in dirs)
            {
                try
                {
                    string p = Path.Combine(d, "plink.exe");
                    if (File.Exists(p)) { _plinkPath = p; return p; }
                }
                catch { }
            }

            // 递归找（先 02-程序，再仓库根；只找一次，找到就缓存）
            string[] roots = new string[]
            {
                @"F:\肥鱼机器人\02-程序",
                @"F:\肥鱼机器人"
            };
            foreach (var root in roots)
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    var hits = Directory.GetFiles(root, "plink.exe", SearchOption.AllDirectories);
                    if (hits.Length > 0)
                    {
                        Array.Sort(hits, delegate(string a, string b) { return a.Length.CompareTo(b.Length); });
                        _plinkPath = hits[0];
                        return _plinkPath;
                    }
                }
                catch { }
            }

            string env = Environment.GetEnvironmentVariable("FISH_PLINK");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) { _plinkPath = env; return env; }
            return "plink.exe";     // 最后退到 PATH
        }

        public static string Run(Cfg cfg, string bash, int timeoutSec)
        {
            try
            {
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(bash));
                string inner = "echo " + b64 + " | base64 -d | sudo -n bash -s";
                string args = "-batch -no-antispoof -hostkey \"" + cfg.HostKey + "\" -pw \"" + cfg.Pass + "\" " +
                              cfg.User + "@" + cfg.Host + " \"" + inner + "\"";
                return Proc(Plink(), args, timeoutSec * 1000);
            }
            catch (Exception ex) { return "[执行失败] " + ex.Message; }
        }

        public static string Proc(string exe, string args, int timeoutMs)
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

        /// <summary>在服务器上跑 curl（127.0.0.1:8090 只有服务器自己能访问）。</summary>
        public static string Curl(Cfg cfg, string pathAndQuery, int timeoutSec)
        {
            string cmd = "curl -s -m " + Math.Max(5, timeoutSec - 5) + " 'http://127.0.0.1:8090" + pathAndQuery + "'";
            return Run(cfg, cmd, timeoutSec);
        }

        /// <summary>
        /// 把本地文件传到服务器（走 plink 的 stdin，不拼进命令行）。
        /// 为什么不 base64 塞进命令：Windows 命令行只有 32KB 上限，一张图就爆。
        /// </summary>
        public static string PutFile(Cfg cfg, string localPath, string remotePath, int timeoutSec)
        {
            try
            {
                if (!File.Exists(localPath)) return "[上传失败] 文件不存在";
                string inner = "cat > '" + remotePath + "'";
                string args = "-batch -no-antispoof -hostkey \"" + cfg.HostKey + "\" -pw \"" + cfg.Pass + "\" " +
                              cfg.User + "@" + cfg.Host + " \"" + inner + "\"";
                var psi = new ProcessStartInfo(Plink(), args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (var p = Process.Start(psi))
                {
                    using (var fs = File.OpenRead(localPath))
                        fs.CopyTo(p.StandardInput.BaseStream);
                    p.StandardInput.Close();
                    string o = p.StandardOutput.ReadToEnd();
                    string e = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutSec * 1000)) { try { p.Kill(); } catch { } return "[超时]"; }
                    if (o.Trim().Length > 0) return o;
                    return e.Trim().Length > 0 ? e : "ok";
                }
            }
            catch (Exception ex) { return "[上传失败] " + ex.Message; }
        }
    }

    // ============================================================ 主题
    class Theme
    {
        public bool Dark;
        public Color Bg, Side, Card, Btn, Text, Sub, Line, Accent, AccentSoft, Ok, Bad;
        public SolidColorBrush bBg, bSide, bCard, bBtn, bText, bSub, bLine, bAccent, bAccentSoft, bOk, bBad;

        public static Theme Get(bool dark)
        {
            var t = new Theme();
            t.Dark = dark;
            if (dark)
            {
                t.Bg = C("#17181b"); t.Side = C("#1d1f23"); t.Card = C("#232529"); t.Btn = C("#2b2e34");
                t.Text = C("#e9eaec"); t.Sub = C("#8d939c"); t.Line = C("#34383f");
                t.Accent = C("#4a7bff"); t.AccentSoft = C("#26334f"); t.Ok = C("#4fbf7f"); t.Bad = C("#e0605f");
            }
            else
            {
                t.Bg = C("#f6f6f4"); t.Side = C("#efefec"); t.Card = C("#ffffff"); t.Btn = C("#f1f1ee");
                t.Text = C("#25231f"); t.Sub = C("#7d7970"); t.Line = C("#e2e1dd");
                t.Accent = C("#2f6fed"); t.AccentSoft = C("#dde7ff"); t.Ok = C("#2f9e5b"); t.Bad = C("#d24b4b");
            }
            t.bBg = new SolidColorBrush(t.Bg); t.bSide = new SolidColorBrush(t.Side);
            t.bCard = new SolidColorBrush(t.Card); t.bBtn = new SolidColorBrush(t.Btn);
            t.bText = new SolidColorBrush(t.Text); t.bSub = new SolidColorBrush(t.Sub);
            t.bLine = new SolidColorBrush(t.Line); t.bAccent = new SolidColorBrush(t.Accent);
            t.bAccentSoft = new SolidColorBrush(t.AccentSoft); t.bOk = new SolidColorBrush(t.Ok);
            t.bBad = new SolidColorBrush(t.Bad);
            return t;
        }
        static Color C(string hex)
        {
            byte r = Convert.ToByte(hex.Substring(1, 2), 16);
            byte g = Convert.ToByte(hex.Substring(3, 2), 16);
            byte b = Convert.ToByte(hex.Substring(5, 2), 16);
            return Color.FromRgb(r, g, b);
        }
    }

    // ============================================================ 主窗口
    class MainWindow : Window
    {
        Cfg _cfg;
        Theme _th;
        double S = 1.0;

        Border _side;
        StackPanel _nav;
        ScrollViewer _scroll;
        StackPanel _page;
        readonly Dictionary<string, Button> _navBtns = new Dictionary<string, Button>();
        Button _curNav;
        string _curPageId = "cloud";

        TextBox _log;
        readonly StringBuilder _logBuf = new StringBuilder();   // 切页面时不丢历史
        TextBlock _bigStatus, _subStatus;
        // 状态缓存：切页面回来直接显示上次结果，不重新查（要手动刷就点「刷新状态」）
        string _stBig = "", _stSub = "";
        Brush _stColor;
        DateTime _stAt = DateTime.MinValue;
        bool _stFresh = false;
        string _chat = "";
        Cfg _cfgForSsh;

        // 看门狗（fishdog.timer）：手机端状态页有这一行，桌面端一直缺（2026-10-28 补上）
        TextBlock _watchdog;

        // ---- 扫码登录页（2026-10-28 从手机端同步：以前只能开 SnowLuma 网页看码 ----
        System.Windows.Controls.Image _qrImg;
        TextBlock _qrStatus;
        DispatcherTimer _qrTimer;
        string _qrB64 = "";

        // ---- 自检「一键修复」：手机端有这个按钮，桌面端只有展示（2026-10-28 同步）----
        readonly List<string> _healthBad = new List<string>();

        // 控件页状态
        WrapPanel _grpWrap;
        string _selGid = "";
        TextBox _gidInput;
        bool _onlyAt;
        int _aiMode = -1;
        int _burstMode = -1;      // 刷屏惊讶：-1 跟全局 / 1 本群开 / 0 本群关
        int _maxLen = 26;
        double _speed = 1.0, _rate = -1;
        double _pokeRate = 1.0;                  // 戳一戳回复概率 0.01~1.00
        string _voiceStyle = "diona";            // 音色风格：diona / diona_soft
        readonly Dictionary<Button, string> _btnVoice = new Dictionary<Button, string>();
        TextBlock _voiceVal;
        // 性能模式（全局热切换）：low 省钱 / deft 正常 / high 全开
        string _perfMode = "low";
        TextBlock _perfVal, _perfHint;
        Grid _perfGrid;                       // 控制条（底槽 + 斜纹 + 滑块）
        Border _perfFill, _perfThumb;
        Canvas _perfThumbLayer;
        LinearGradientBrush _perfGrad;
        GradientStop[] _perfStops;
        ScaleTransform _perfFillScale;        // 填充宽度 = ScaleX（变换，不动布局）
        Canvas _perfStarLayer;                // 星星 + 拖尾层
        RectangleGeometry _perfBarClip;       // 星星层的圆角裁剪
        int _perfLastPaint;                   // 拖动时的重绘节流
        double _perfDragFrac = -1;            // >=0：正在拖，用指针位置顶替档位
        bool _perfDragging;
        double _perfVisualFrac = 0.33;        // 滑块现在停在哪（拖动后不回到格点，就停在你放下的地方）
        double _perfDropFrac = -1;            // 松手时的位置，切档成功后沿用它
        string _perfColorMode = "";           // 已经上过色的档位，避免每帧重设渐变色
        bool _perfFreezeStarsOnDrag = false;  // 拖动时是否冻住星星（true=更跟手但看着像卡住）
        Button _btnAt, _btnAi, _btnBurst, _btn14, _btn26, _btn40;
        // 图库（机器人控件页）
        TextBox _plName, _plDesc, _plTags;
        readonly System.Collections.Generic.Dictionary<string, FrameworkElement> _plCardById
            = new System.Collections.Generic.Dictionary<string, FrameworkElement>();
        readonly System.Collections.Generic.Dictionary<string, Action<string, string, string>> _plTextById
            = new System.Collections.Generic.Dictionary<string, Action<string, string, string>>();
        string _plLastCount = "";
        TextBlock _plStatus;
        WrapPanel _plList;                  // 平铺卡片（2026-11-03 用户：恢复成之前的样子）
        WrapPanel _plTagBar;
        WrapPanel _plTagHint;                  // 相近标签合并提示
        readonly System.Collections.Generic.List<string> _plTagNames
            = new System.Collections.Generic.List<string>();
        // 标签库搜索（2026-11-02 用户：「可以拼音搜索也可以中文搜索」）
        readonly System.Collections.Generic.List<object[]> _plTagData
            = new System.Collections.Generic.List<object[]>();   // {标签, 次数, 全拼, 首字母}
        TextBox _plTagSearch;
        string _plTagQuery = "";
        // 通讯录式分组（2026-11-03 用户：「像手机通讯录一样，每个字母一个抽屉」）
        string _plTagGroupOpen = "";                       // 当前展开的首字母（空 = 全收起）
        readonly System.Collections.Generic.Dictionary<string, string[]> _plTagPy
            = new System.Collections.Generic.Dictionary<string, string[]>();   // 标签 → {全拼, 首字母}
        readonly System.Collections.Generic.List<object> _plItems
            = new System.Collections.Generic.List<object>();      // 解析后的原始条目
        // 图集（相册）缓存：每项 [id, 名字, 张数]
        readonly System.Collections.Generic.List<object[]> _plAlbums
            = new System.Collections.Generic.List<object[]>();
        string _plAlbumId = "";                 // 当前进入的图集（空 = 相册首页）
        bool _plSelMode = false;                // 多选模式（2026-11-03：多选移动）
        readonly System.Collections.Generic.List<string> _plSel
            = new System.Collections.Generic.List<string>();
        readonly System.Collections.Generic.Dictionary<string, BitmapImage> _plBmp
            = new System.Collections.Generic.Dictionary<string, BitmapImage>();  // 缩略图缓存
        string _plFilterTag = "";               // 只看这个标签（空 = 全部）
        bool _plTagFilterMode = false;           // 点标签 = 筛选 还是 加进输入框
        Button _plModeBtn;
        TextBlock _plFilterInfo;
        StackPanel _plTagDrawer;                // 标签库抽屉（折叠起来，别把整页吃掉）
        Button _plTagToggle;
        bool _plTagOpen = false;                // 抽屉开没开（默认收起）
        // AI 自动打标签（2026-11-02 用户要求：手动点「立即识别」才跑，别每张都识别）
        Button _plAutoBtn, _plAutoRun;
        TextBlock _plAutoInfo, _plAutoLog;
        bool _plAutoEnabled = true;
        bool _plAutoRunning = false;
        readonly Dictionary<Button, double> _btnPoke = new Dictionary<Button, double>();
        Slider _slSpeed, _slRate;
        Button _btnRateFollow;
        TextBlock _ctlStatus, _lenVal, _speedVal, _rateVal, _pokeVal;

        // 沉默名单（拉黑别的机器人，全局）
        class BlackEntry
        {
            public string Uid, Note, Ts;
            public bool Removed;        // 已点解除，但列表还开着 → 先留在原位，关列表才消失
        }
        readonly List<BlackEntry> _black = new List<BlackEntry>();
        TextBox _blInput, _blNote;
        TextBlock _blHint;
        Button _blToggle;
        Border _blPanel;
        StackPanel _blList;
        bool _blOpen;

        // 戳一戳概率挡位：最低 1%，最高 100%，中间每 10% 一档（共 11 档）
        static readonly double[] POKE_STEPS = {
            0.01, 0.10, 0.20, 0.30, 0.40, 0.50, 0.60, 0.70, 0.80, 0.90, 1.00
        };

        // 默默发表情挡位：0 = 这个群关掉；-1 = 用插件默认（3%）
        double _ambient = -1;
        static readonly double[] AMBIENT_STEPS = {
            0.0, 0.01, 0.02, 0.03, 0.05, 0.08, 0.10, 0.15, 0.20, 0.30, 0.50
        };
        readonly Dictionary<Button, double> _btnAmbient = new Dictionary<Button, double>();
        Button _btnAmbientFollow;
        TextBlock _ambientVal;
        WrapPanel _ambientRow;                   // -shot botctl ... ambient 时滚到这里

        WF.NotifyIcon _tray;
        bool _exiting;

        // ---- 她的 QQ 页 ----
        TextBlock _qqName, _qqSign, _qqNum, _qqStatus;
        TextBox _qqNickIn, _qqSignIn, _qqPostIn;
        StackPanel _qqList;
        System.Windows.Controls.Image _qqAvatar;
        string _qqAvatarB64 = "";
        readonly List<byte[]> _qqPostImgs = new List<byte[]>();   // 待发的图（已压好的 JPEG）
        WrapPanel _qqImgStrip;
        readonly System.Windows.Media.Imaging.BitmapImage _qqAvaImg = null;

        static string Token = "";   // 公开版不含凭据，见 LoadConsoleConfig()
        static string ApiBase = "http://127.0.0.1:8090";   // 可被 console-config.json 的 base 覆盖

        public MainWindow()
        {
            _cfg = Cfg.Load();
            _cfgForSsh = _cfg;
            S = _cfg.Scale;
            _th = Theme.Get(_cfg.Dark);

            Title = "肥鱼 · 控制台 WPF";
            // 尺寸用 DIP，不要再乘 S（WPF 自己按 DPI 缩放），并限制在屏幕工作区内
            // （-shot 自检时不受限制：要拍长页面，窗口得比屏幕高）
            var wa = SystemParameters.WorkArea;
            Width = Math.Min(ShotMode ? 1280 : 1120, wa.Width - 40);
            Height = ShotMode ? 900 : Math.Min(760, wa.Height - 40);
            MinWidth = 860;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = _th.bBg;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 12.5 * S;
            Foreground = _th.bText;
            try { var wi = LoadAppIcon(32); if (wi != null) Icon = wi; } catch { }
            SourceInitialized += delegate { DarkTitleBar(); };

            ApplyScrollBarStyle();
            BuildShell();
            ShowPage("cloud");

            // 自检：-shot 是临时进程，绝不能建托盘图标（否则每截一次图就多一个常驻图标）
            if (_cfg.Tray && !ShotMode) SetupTray();
            if (_cfg.TrayStart && !ShotMode) Loaded += delegate { Hide(); };

            Closing += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                if (!_exiting && _cfg.Tray && _cfg.TrayClose) { e.Cancel = true; HideToTray(); }
            };
        }

        // ------------------------------------------------ 壳
        void BuildShell()
        {
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(212 * S) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 左栏
            _side = new Border();
            _side.Background = _th.bSide;
            _side.BorderBrush = _th.bLine;
            _side.BorderThickness = new Thickness(0, 0, 1, 0);
            var sideStack = new DockPanel();

            var logo = new StackPanel();
            logo.Margin = new Thickness(18 * S, 18 * S, 18 * S, 10 * S);
            var t1 = new TextBlock();
            t1.Text = "肥鱼控制台"; t1.FontSize = 17 * S; t1.FontWeight = FontWeights.Bold;
            t1.Foreground = _th.bText;
            var t2 = new TextBlock();
            t2.Text = "v3.0 · WPF"; t2.FontSize = 11 * S; t2.Foreground = _th.bSub;
            t2.Margin = new Thickness(0, 2 * S, 0, 0);
            logo.Children.Add(t1); logo.Children.Add(t2);
            DockPanel.SetDock(logo, Dock.Top);
            sideStack.Children.Add(logo);

            var bottom = new StackPanel();
            bottom.Margin = new Thickness(12 * S, 6 * S, 12 * S, 12 * S);
            var tBtn = Btn(_cfg.Dark ? "切到白天" : "切到夜间", false, null);
            tBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            tBtn.HorizontalContentAlignment = HorizontalAlignment.Left;
            tBtn.Margin = new Thickness(0, 0, 0, 6);
            tBtn.Click += delegate
            {
                _cfg.Dark = !_cfg.Dark; _cfg.Save();
                RebuildAll();
            };
            bottom.Children.Add(tBtn);
            var ver = new TextBlock();
            ver.Text = "SSH · plink   ·   端口 8090";
            ver.FontSize = 10.5 * S; ver.Foreground = _th.bSub;
            ver.Margin = new Thickness(2 * S, 0, 0, 0);
            bottom.Children.Add(ver);
            DockPanel.SetDock(bottom, Dock.Bottom);
            sideStack.Children.Add(bottom);

            _nav = new StackPanel();
            _nav.Margin = new Thickness(8 * S, 4 * S, 8 * S, 4 * S);
            var sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.Content = _nav;
            sideStack.Children.Add(sv);

            _side.Child = sideStack;
            Grid.SetColumn(_side, 0);
            root.Children.Add(_side);

            // 右栏
            _scroll = new ScrollViewer();
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scroll.Background = _th.bBg;
            _scroll.Padding = new Thickness(20 * S, 16 * S, 20 * S, 20 * S);
            _scroll.CanContentScroll = false;          // 按像素滚，手感顺
            _page = new StackPanel();
            // 套一层 Grid，并把页面宽度钉成视口宽度：
            // 不然 StackPanel 会被按「无限宽」测量，卡片会横向溢出、右侧被切掉
            var pageHost = new Grid();
            pageHost.Children.Add(_page);
            // 用 ScrollViewer 自己的实际宽度反推页面宽度（最可靠：
            // ScrollViewer 给子项的可用宽度有时是无限大，WrapPanel 和卡片就会溢出）
            _scroll.SizeChanged += delegate
            {
                double avail = _scroll.ActualWidth - _scroll.Padding.Left - _scroll.Padding.Right - 22;
                if (avail > 120) _page.Width = avail;
            };
            Loaded += delegate
            {
                double avail = _scroll.ActualWidth - _scroll.Padding.Left - _scroll.Padding.Right - 22;
                if (avail > 120) _page.Width = avail;
            };
            _scroll.Content = pageHost;
            Grid.SetColumn(_scroll, 1);
            root.Children.Add(_scroll);

            Content = root;

            AddNav("cloud", "控制云端");
            AddNav("chat", "聊天记录");
            AddNav("qr", "扫码登录");
            AddNav("botctl", "机器人控件");
            AddNav("roster", "花名册");
            AddNav("qq", "她的 QQ");
            AddNav("settings", "设置");
            AddNav("dev", "开发信息");
            AddNav("baseprompt", "底层人设");
        }

        void AddNav(string id, string text)
        {
            var b = new Button();
            b.Content = text;
            b.Tag = id;
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            b.Padding = new Thickness(14 * S, 8 * S, 14 * S, 8 * S);
            b.Margin = new Thickness(0, 1 * S, 0, 1 * S);
            b.Background = Brushes.Transparent;
            b.BorderThickness = new Thickness(0);
            b.Foreground = _th.bSub;
            b.FontSize = 13 * S;
            b.Cursor = Cursors.Hand;
            b.Template = FlatTemplate();
            b.Click += delegate { ShowPage(id); };
            _nav.Children.Add(b);
            _navBtns[id] = b;
        }

        // ------------------------------------------------ 扫码登录（内置二维码）
        void BuildQr()
        {
            var lb0 = new StackPanel();
            var card = Card("扫码登录", out lb0);
            _page.Children.Add(card);

            lb0.Children.Add(Txt("协议端（SnowLuma）没登录时，这里会显示它的登录二维码。"
                                 + "手机上扫一下就登进去了；刷新后 3 秒内没变化就多点几次。",
                                 11, false, _th.bSub, 2));

            _qrImg = new System.Windows.Controls.Image();
            _qrImg.Width = 260 * S;
            _qrImg.Height = 260 * S;
            _qrImg.Stretch = Stretch.Uniform;
            _qrImg.Margin = new Thickness(0, 10 * S, 0, 6 * S);
            var wrap = new Border();
            wrap.CornerRadius = new CornerRadius(12 * S);
            wrap.Background = Brushes.White;
            wrap.Padding = new Thickness(10 * S);
            wrap.HorizontalAlignment = HorizontalAlignment.Left;
            wrap.Child = _qrImg;
            lb0.Children.Add(wrap);

            _qrStatus = Txt("", 11.5, false, _th.bSub, 2);
            lb0.Children.Add(_qrStatus);

            var row = new WrapPanel();
            row.Margin = new Thickness(0, 10 * S, 0, 0);
            row.Children.Add(Btn("刷新二维码", true, delegate { QrPull(true); }));
            row.Children.Add(Btn("重新拉取状态", false, delegate { QrPull(false); }));
            var open = Btn("打开手机版控制台", false, delegate
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        WebBase()) { UseShellExecute = true });
                }
                catch (Exception ex) { LogLine("打开网页失败：" + ex.Message); }
            });
            open.Margin = new Thickness(8 * S, 0, 0, 0);
            row.Children.Add(open);
            lb0.Children.Add(row);

            QrPull(true);

            _qrTimer = new DispatcherTimer();
            _qrTimer.Interval = TimeSpan.FromSeconds(4);
            _qrTimer.Tick += delegate { QrPull(false); };
            _qrTimer.Start();
        }

        void QrPull(bool fresh)
        {
            if (_qrTimer != null && !fresh) _qrTimer.Stop();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string b64 = Ssh.Run(_cfg,
                    "curl -s -m 20 'http://127.0.0.1:8090/api/qr" + (fresh ? "?fresh=1&" : "?")
                    + "token=" + Token + "' | base64 -w0", 40).Trim();
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (string.IsNullOrEmpty(b64) || b64.Length < 100)
                    {
                        if (_qrStatus != null)
                            _qrStatus.Text = "现在没有二维码（可能已经登录了）。点「重新拉取状态」看看。";
                        if (_qrTimer != null) _qrTimer.Stop();
                        return;
                    }
                    try
                    {
                        byte[] raw = Convert.FromBase64String(b64);
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = new MemoryStream(raw);
                        bmp.EndInit();
                        bmp.Freeze();
                        if (_qrImg != null) _qrImg.Source = bmp;
                        _qrB64 = b64;
                        if (_qrStatus != null)
                            _qrStatus.Text = "二维码已加载（" + DateTime.Now.ToString("HH:mm:ss")
                                             + "）· 用手机 QQ 扫一扫";
                    }
                    catch (Exception ex)
                    {
                        if (_qrStatus != null) _qrStatus.Text = "二维码解析失败：" + ex.Message;
                    }
                }));
            });
        }

        void RebuildAll()
        {
            var page = _curPageId;
            _th = Theme.Get(_cfg.Dark);
            S = _cfg.Scale;
            _navBtns.Clear();
            ApplyScrollBarStyle();
            BuildShell();
            ShowPage(page);
        }

        public void Goto(string id) { ShowPage(id); }

        void ShowPage(string id)
        {
            _curPageId = id;
            foreach (var kv in _navBtns)
            {
                bool on = kv.Key == id;
                kv.Value.Background = on ? (Brush)_th.bAccentSoft : Brushes.Transparent;
                kv.Value.Foreground = on ? (Brush)new SolidColorBrush(Color.FromRgb(0xdb, 0xe4, 0xff)) : _th.bSub;
            }
            _page.Children.Clear();
            _scroll.ScrollToTop();
            if (id == "cloud") BuildCloud();
            else if (id == "chat") BuildChat();
            else if (id == "qr") BuildQr();
            else if (id == "botctl") BuildBotCtl();
            else if (id == "roster") BuildRoster();
            else if (id == "qq") BuildQQProfile();
            else if (id == "settings") BuildSettings();
            else if (id == "baseprompt") BuildBasePrompt();
            else BuildDevInfo();
        }

        // ------------------------------------------------ 控件工厂
        static ControlTemplate _flatTpl, _btnTpl;

        static ControlTemplate FlatTemplate()
        {
            if (_flatTpl != null) return _flatTpl;
            var tpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "bd";
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Button.PaddingProperty));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            _flatTpl = tpl;
            return tpl;
        }

        static ControlTemplate BtnTemplate()
        {
            if (_btnTpl != null) return _btnTpl;
            var tpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "bd";
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Button.PaddingProperty));
            // 跟按钮自己的 HorizontalContentAlignment 走（默认 Center；下拉列表项要 Left，
            // 不然长群名一居中两边留白参差不齐，2026-11-02 用户说「风格不统一」）
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty,
                        new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.86, "bd"));
            tpl.Triggers.Add(hover);
            var down = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            down.Setters.Add(new Setter(UIElement.OpacityProperty, 0.72, "bd"));
            tpl.Triggers.Add(down);
            _btnTpl = tpl;
            return tpl;
        }

        Button Btn(string text, bool primary, RoutedEventHandler click)
        {
            var b = new Button();
            b.Content = text;
            b.Padding = new Thickness(16 * S, 7 * S, 16 * S, 7 * S);
            b.MinWidth = 62 * S;
            b.MinHeight = 30 * S;
            b.Margin = new Thickness(0, 0, 8 * S, 8 * S);
            b.FontSize = 12.5 * S;
            b.FontFamily = new FontFamily("Microsoft YaHei UI");
            b.Cursor = Cursors.Hand;
            b.Foreground = primary ? Brushes.White : _th.bText;
            b.Background = primary ? _th.bAccent : _th.bBtn;
            b.BorderBrush = _th.bLine;
            b.BorderThickness = new Thickness(primary ? 0 : 1);
            b.Template = BtnTemplate();
            b.VerticalAlignment = VerticalAlignment.Center;
            if (click != null) b.Click += click;
            return b;
        }

        /// <summary>
        /// 深色下拉样式（2026-11-02 用户：「下拉菜单白底、图标和风格不搭」）。
        /// 把 WPF 自带 ComboBox 的模板换成我们自己这套（同 Btn 的圆角/配色/内边距），
        /// 但**仍然是标准 ComboBox** —— UIAutomation 的展开/选择、冒烟脚本都照旧能用。
        /// </summary>
        void ComboStyle(ComboBox cb)
        {
            cb.Background = _th.bBtn;
            cb.Foreground = _th.bText;
            cb.BorderBrush = _th.bLine;
            cb.BorderThickness = new Thickness(1);
            cb.Padding = new Thickness(12 * S, 7 * S, 28 * S, 7 * S);   // 右边给箭头留位
            cb.FontSize = 12.5 * S;
            cb.MinHeight = 32 * S;
            cb.FontFamily = new FontFamily("Microsoft YaHei UI");
            cb.HorizontalContentAlignment = HorizontalAlignment.Left;

            // ---- 每一项 ----
            var it = new ControlTemplate(typeof(ComboBoxItem));
            var ib = new FrameworkElementFactory(typeof(Border));
            ib.Name = "bd";
            ib.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            ib.SetValue(Border.MarginProperty, new Thickness(3, 1, 3, 1));
            ib.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var icp = new FrameworkElementFactory(typeof(ContentPresenter));
            icp.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            icp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            ib.AppendChild(icp);
            it.VisualTree = ib;
            var hov = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
            hov.Setters.Add(new Setter(Border.BackgroundProperty, _th.bAccentSoft, "bd"));
            it.Triggers.Add(hov);
            var ons = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
            ons.Setters.Add(new Setter(Border.BackgroundProperty, _th.bAccentSoft, "bd"));
            it.Triggers.Add(ons);

            var st = new Style(typeof(ComboBoxItem));
            st.Setters.Add(new Setter(Control.ForegroundProperty, _th.bText));
            st.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10 * S, 6 * S, 10 * S, 6 * S)));
            st.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Microsoft YaHei UI")));
            st.Setters.Add(new Setter(Control.TemplateProperty, it));
            cb.ItemContainerStyle = st;

            // ---- 整体 ----
            var tpl = new ControlTemplate(typeof(ComboBox));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));

            var dock = new FrameworkElementFactory(typeof(DockPanel));
            var arrow = new FrameworkElementFactory(typeof(TextBlock));
            arrow.SetValue(TextBlock.TextProperty, "▾");
            arrow.SetValue(TextBlock.FontSizeProperty, 11.0 * S);
            arrow.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 10 * S, 0));
            arrow.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            arrow.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            arrow.SetValue(DockPanel.DockProperty, Dock.Right);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.ContentProperty,
                        new TemplateBindingExtension(ComboBox.SelectionBoxItemProperty));
            cp.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            cp.SetValue(UIElement.IsHitTestVisibleProperty, false);
            dock.AppendChild(arrow);
            dock.AppendChild(cp);
            bd.AppendChild(dock);

            var popup = new FrameworkElementFactory(typeof(Popup));
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(Popup.StaysOpenProperty, false);
            popup.SetValue(Popup.IsOpenProperty,
                           new Binding("IsDropDownOpen")
                           {
                               RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
                               Mode = BindingMode.TwoWay
                           });
            var pbd = new FrameworkElementFactory(typeof(Border));
            pbd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            pbd.SetValue(Border.MarginProperty, new Thickness(0, 4 * S, 0, 0));
            pbd.SetValue(Border.PaddingProperty, new Thickness(4 * S));
            pbd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            pbd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            pbd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            var sv = new FrameworkElementFactory(typeof(ScrollViewer));
            sv.SetValue(ScrollViewer.MaxHeightProperty, 380.0 * S);
            sv.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            sv.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            sv.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
            pbd.AppendChild(sv);
            popup.AppendChild(pbd);

            // 盖在整块上的透明 ToggleButton（点一下开/合下拉）。
            // ⚠️ 模板类型必须写成 ToggleButton：拿 Button 的模板套上来会
            // ArgumentException（ControlTemplate.ValidateTemplatedParent）—— 2026-11-02 真崩过一次。
            var ttpl = new ControlTemplate(typeof(ToggleButton));
            var tbd = new FrameworkElementFactory(typeof(Border));
            tbd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            tbd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            ttpl.VisualTree = tbd;
            var toggle = new FrameworkElementFactory(typeof(ToggleButton));
            toggle.SetValue(ToggleButton.TemplateProperty, ttpl);
            toggle.SetValue(Control.BackgroundProperty, Brushes.Transparent);
            toggle.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            toggle.SetValue(ToggleButton.FocusableProperty, false);
            toggle.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
            toggle.SetValue(ToggleButton.IsCheckedProperty,
                            new Binding("IsDropDownOpen")
                            {
                                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
                                Mode = BindingMode.TwoWay
                            });

            var root = new FrameworkElementFactory(typeof(Grid));
            root.AppendChild(bd);
            root.AppendChild(toggle);
            root.AppendChild(popup);
            tpl.VisualTree = root;
            cb.Template = tpl;
        }

        Border Card(string title, out StackPanel body)
        {
            var outer = new Border();
            outer.Background = _th.bCard;
            outer.CornerRadius = new CornerRadius(12);
            outer.Padding = new Thickness(16 * S);
            outer.Margin = new Thickness(0, 0, 0, 12 * S);
            outer.BorderBrush = _th.bLine;
            outer.BorderThickness = new Thickness(1);
            var sp = new StackPanel();
            if (!string.IsNullOrEmpty(title))
            {
                var tb = new TextBlock();
                tb.Text = title;
                tb.FontSize = 15 * S;
                tb.FontWeight = FontWeights.SemiBold;
                tb.Foreground = _th.bText;
                tb.Margin = new Thickness(0, 0, 0, 10 * S);
                sp.Children.Add(tb);
            }
            outer.Child = sp;
            body = sp;
            return outer;
        }

        /// <summary>
        /// 可折叠卡片：标题右边一个「展开 ▾ / 收起 ▴」。
        /// 机器人控件页卡片太多、太挤，能折的都折起来，只留常用的展开。
        /// </summary>
        Border FoldCard(string title, bool open, out StackPanel content)
        {
            StackPanel outer;
            var card = Card(title, out outer);
            var inner = new StackPanel();
            var btn = Btn(open ? "收起 ▴" : "展开 ▾", false, null);
            btn.Padding = new Thickness(12 * S, 4 * S, 12 * S, 4 * S);
            btn.FontSize = 11.5 * S;
            btn.HorizontalAlignment = HorizontalAlignment.Left;
            btn.Margin = new Thickness(0, 0, 0, 8 * S);
            btn.Click += delegate
            {
                bool show = inner.Visibility != Visibility.Visible;
                inner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                btn.Content = show ? "收起 ▴" : "展开 ▾";
            };
            inner.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            outer.Children.Add(btn);
            outer.Children.Add(inner);
            content = inner;
            return card;
        }

        TextBlock Txt(string text, double size, bool bold, Brush color, double topMargin)
        {
            var t = new TextBlock();
            t.Text = text;
            t.FontSize = size * S;
            t.FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal;
            t.Foreground = color;
            t.TextWrapping = TextWrapping.Wrap;
            t.Margin = new Thickness(0, topMargin * S, 0, 0);
            return t;
        }

        WrapPanel Row()
        {
            var w = new WrapPanel();
            w.Margin = new Thickness(0, 4 * S, 0, 0);
            return w;
        }

        // ------------------------------------------------ 控制云端
        void BuildCloud()
        {
            StackPanel b;
            var card = Card("云端服务器 · " + _cfg.Host, out b);
            _bigStatus = Txt("● 刷新中…", 20, true, _th.bSub, 0);
            b.Children.Add(_bigStatus);
            _subStatus = Txt("正在连接…", 11.5, false, _th.bSub, 6);
            b.Children.Add(_subStatus);
            _watchdog = Txt("看门狗：查询中…", 11, false, _th.bSub, 4);
            b.Children.Add(_watchdog);
            BuildQuickMenu(b);
            _page.Children.Add(card);

            // 每日 token 用量：总览常显，明细折叠（像 localusage 那种紧凑显示）
            BuildTokenCard();

            StackPanel lb;
            var logCard = Card("运行日志", out lb);
            _log = new TextBox();
            _log.IsReadOnly = true;
            _log.AcceptsReturn = true;
            _log.TextWrapping = TextWrapping.Wrap;
            _log.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _log.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _log.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI");
            _log.FontSize = 11.5 * S;
            _log.Background = _th.bBg;
            _log.Foreground = _th.bSub;
            _log.BorderBrush = _th.bLine;
            _log.Padding = new Thickness(8 * S);
            _log.Height = 240 * S;
            StyleInput(_log);
            if (_logBuf.Length > 0) { _log.Text = _logBuf.ToString(); _log.ScrollToEnd(); }
            lb.Children.Add(_log);
            _page.Children.Add(logCard);

            RefreshStatus(false);      // 有缓存就直接显示，切回来不重查
        }

        string WebBase()
        {
            return "http://" + _cfg.Host + ":8090";
        }

        void LogLine(string s)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s + "\r\n";
            _logBuf.Append(line);
            if (_logBuf.Length > 60000) _logBuf.Remove(0, _logBuf.Length - 40000);   // 别无限长
            var box = _log;
            if (box == null) return;
            box.AppendText(line);
            box.ScrollToEnd();
        }

        void SetStatus(string big, string sub, Brush color)
        {
            _stBig = big;
            _stColor = color;
            if (sub != null) _stSub = sub;
            if (_bigStatus == null) return;
            _bigStatus.Text = big;
            _bigStatus.Foreground = color;
            if (sub != null && _subStatus != null) _subStatus.Text = sub;
        }

        void RefreshStatus() { RefreshStatus(false); }

        void RefreshStatus(bool force)
        {
            if (!force && _stFresh && (DateTime.Now - _stAt).TotalSeconds < 30)
            {
                if (_bigStatus != null)
                {
                    _bigStatus.Text = _stBig;
                    _bigStatus.Foreground = _stColor ?? _th.bSub;
                }
                if (_subStatus != null) _subStatus.Text = _stSub + "　（" + _stAt.ToString("HH:mm:ss") + "，要看最新点「刷新状态」）";
                return;
            }
            SetStatus("● 刷新中…", "正在连接服务器…", _th.bSub);
            string host = _cfg.Host;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string script =
                    "echo SVC=$(systemctl is-active fishbot)\n" +
                    "echo NC=$(docker ps --format '{{.Names}}' | tr '\\n' ',')\n" +
                    "echo MEM=$(free -m | awk '/Mem:/{print $3\"/\"$2\" MB\"}')\n" +
                    "echo UP=$(systemctl show -p ActiveEnterTimestamp --value fishbot)\n";
                string o = Ssh.Run(_cfg, script, 25);
                string web = Ssh.Run(_cfg, "curl -s -o /dev/null -w '%{http_code}' -m 5 http://127.0.0.1:8090/api/ping 2>/dev/null || echo none", 12);
                // 看门狗（手机端状态页那一行）：/api/health 给 timer/count/last
                string hl = Ssh.Curl(_cfg, "/api/health?token=" + Token, 20);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    var L = new System.Collections.Generic.Dictionary<string, string>();
                    foreach (var ln in (o ?? "").Split('\n'))
                    {
                        int eq = ln.IndexOf('=');
                        if (eq > 0) L[ln.Substring(0, eq).Trim()] = ln.Substring(eq + 1).Trim();
                    }
                    string svc = L.ContainsKey("SVC") ? L["SVC"] : "?";
                    string nc = L.ContainsKey("NC") ? L["NC"].Trim(',') : "";
                    string mem = L.ContainsKey("MEM") ? L["MEM"] : "?";
                    string up = L.ContainsKey("UP") ? L["UP"] : "?";
                    bool ok = svc == "active";
                    string sub = "服务 " + svc + " · 容器 " + (nc.Length > 0 ? nc + " 在跑" : "没找到")
                                 + " · 内存 " + mem + " · 启动于 " + up;
                    // 看门狗：/api/health 给 timer / count / last
                    try
                    {
                        if (!string.IsNullOrEmpty(hl) && hl.TrimStart().StartsWith("{"))
                        {
                            var ser2 = new JavaScriptSerializer();
                            var h = ser2.DeserializeObject(hl) as Dictionary<string, object>;
                            if (h != null && h.ContainsKey("watchdog"))
                            {
                                var wd = h["watchdog"] as Dictionary<string, object>;
                                if (wd != null)
                                {
                                    string state = wd.ContainsKey("timer") ? Convert.ToString(wd["timer"]) : "?";
                                    string cnt = wd.ContainsKey("count") ? Convert.ToString(wd["count"]) : "0";
                                    sub += "｜看门狗 " + state + " · 已自愈 " + cnt + " 次";
                                }
                            }
                        }
                    }
                    catch { }
                    if (web != null && web.Trim() != "200" && web.Trim() != "none")
                        sub += "｜网页 " + web.Trim();
                    SetStatus(ok ? "● 运行中（云端）" : "● 没在运行", sub, ok ? _th.bOk : _th.bBad);
                    _stFresh = true;
                    _stAt = DateTime.Now;
                    _stSub = sub;
                    if (_subStatus != null)
                        _subStatus.Text = sub + "　（" + _stAt.ToString("HH:mm:ss") + "）";
                }));
            });
        }

        /// <summary>POST 一个 JSON 到 8090（走 SSH，本机不用能访问 8090）。</summary>
        string PostJson(string pathAndQuery, string json)
        {
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            string cmd = "echo " + b64 + " | base64 -d > /tmp/_wpf_post.json && " +
                         "curl -s -m 50 -X POST -H 'Content-Type: application/json' " +
                         "--data-binary @/tmp/_wpf_post.json 'http://127.0.0.1:8090" + pathAndQuery + "'";
            return Ssh.Run(_cfg, cmd, 60);
        }

        static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(空)";
            int i = s.IndexOfAny(new char[] { '\r', '\n' });
            string t = i > 0 ? s.Substring(0, i) : s;
            return t.Length > 160 ? t.Substring(0, 160) + "…" : t;
        }

        static string Fld(string text, string key)
        {
            foreach (var line in text.Replace("\r", "").Split('\n'))
            {
                if (line.StartsWith(key)) return line.Substring(key.Length).Trim();
            }
            return "";
        }

        void CloudCmd(string what)
        {
            LogLine("执行 " + what + " …");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string o = Ssh.Run(_cfg, "systemctl " + what + " fishbot; sleep 1; systemctl is-active fishbot", 60);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    LogLine("结果：" + FirstLine(o));
                    RefreshStatus();
                }));
            });
        }

        void TailLog()
        {
            LogLine("拉取最近日志…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string o = Ssh.Run(_cfg, "journalctl -u fishbot -n 60 --no-pager | tail -60", 40);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    foreach (var line in o.Replace("\r", "").Split('\n'))
                        if (line.Trim().Length > 0) LogLine(line);
                }));
            });
        }

        // ------------------------------------------------ 机器人控件
        void BuildBotCtl()
        {
            StackPanel b;
            var card = Card("选一个群", out b);
            // 群用一组按钮列出来（WPF 默认 ComboBox 在深色下是白底，而且点一下要多一步）
            _grpWrap = new WrapPanel();
            _grpWrap.Margin = new Thickness(0, 0, 0, 6 * S);
            b.Children.Add(_grpWrap);

            var row = new WrapPanel();

            _gidInput = new TextBox();
            _gidInput.Width = 130 * S;
            _gidInput.Height = 30 * S;
            _gidInput.FontSize = 12.5 * S;
            _gidInput.Margin = new Thickness(0, 0, 8 * S, 8 * S);
            _gidInput.Background = _th.bBtn;
            _gidInput.Foreground = _th.bText;
            _gidInput.BorderBrush = _th.bLine;
            _gidInput.Padding = new Thickness(8 * S, 0, 8 * S, 0);
            StyleInput(_gidInput);
            row.Children.Add(_gidInput);

            row.Children.Add(Btn("载入", true, delegate { LoadGroupById(); }));
            row.Children.Add(Btn("刷新群列表", false, delegate { LoadGroups(); }));
            b.Children.Add(row);

            _ctlStatus = Txt("还没选群", 11.5, false, _th.bSub, 4);
            b.Children.Add(_ctlStatus);
            _page.Children.Add(card);

            StackPanel b2;
            var card2 = FoldCard("她在这个群怎么说话", true, out b2);
            _page.Children.Add(card2);

            var rAt = new WrapPanel();
            rAt.Margin = new Thickness(0, 0, 0, 6 * S);
            rAt.Children.Add(Txt("仅接受 @ 后回复", 13, false, _th.bText, 0));
            _btnAt = Btn("关闭", false, delegate { _onlyAt = !_onlyAt; PaintCtl(); });
            _btnAt.Margin = new Thickness(16 * S, 0, 0, 0);
            rAt.Children.Add(_btnAt);
            b2.Children.Add(rAt);
            // 2026-10-28 用户的实际困惑就在这一行：关了「仅@」为什么还是不接话？
            // 因为「仅@」和「聊天频率」是两个开关，光关前者、后者还是「跟全局」时她照样闭嘴。
            // 现在插件那边改了语义：**关了仅@ = 这个群允许主动接话**，没设频率时按 35%。
            b2.Children.Add(Txt("关掉它 = 这个群她可以自己接话（下面的聊天频率还是「跟全局」时按 35% 接）；" +
                                "想让某个群彻底闭嘴，把聊天频率拉到 0。",
                                11, false, _th.bSub, 2));

            var rAi = new WrapPanel();
            rAi.Margin = new Thickness(0, 0, 0, 4 * S);
            rAi.Children.Add(Txt("AI 对话（本群）", 13, false, _th.bText, 0));
            _btnAi = Btn("跟随全局", false, delegate
            {
                _aiMode = (_aiMode == -1) ? 1 : (_aiMode == 1 ? 0 : -1);
                PaintCtl();
            });
            _btnAi.Margin = new Thickness(16 * S, 0, 0, 0);
            rAi.Children.Add(_btnAi);
            b2.Children.Add(rAi);
            b2.Children.Add(Txt("「关」= 这个群只按词库说话，别的群不受影响；「跟随全局」= 按 /ai on|off 的总开关。",
                                11, false, _th.bSub, 2));

            // 刷屏惊讶（2026-10-28 用户：「哇，一口气这么多条…」这句有人觉得吵）——
            // 三态和 AI 一样：跟随全局 / 本群开 / 本群关
            var rBurst = new WrapPanel();
            rBurst.Margin = new Thickness(0, 0, 0, 4 * S);
            rBurst.Children.Add(Txt("刷屏惊讶（本群）", 13, false, _th.bText, 0));
            _btnBurst = Btn("跟随全局", false, delegate
            {
                _burstMode = (_burstMode == -1) ? 0 : (_burstMode == 0 ? 1 : -1);
                PaintCtl();
            });
            _btnBurst.Margin = new Thickness(16 * S, 0, 0, 0);
            rBurst.Children.Add(_btnBurst);
            b2.Children.Add(rBurst);
            b2.Children.Add(Txt("「本群关」= 这个群不再说「哇，一口气这么多条，我眼睛都看花了」，别的群不受影响。",
                                11, false, _th.bSub, 2));

            b2.Children.Add(Txt("句式长度", 13, false, _th.bText, 12));
            var rLen = new WrapPanel();
            _btn14 = Btn("14", false, delegate { _maxLen = 14; PaintCtl(); });
            _btn26 = Btn("26", false, delegate { _maxLen = 26; PaintCtl(); });
            _btn40 = Btn("40", false, delegate { _maxLen = 40; PaintCtl(); });
            rLen.Children.Add(_btn14); rLen.Children.Add(_btn26); rLen.Children.Add(_btn40);
            _lenVal = Txt("当前：26 字", 11.5, true, _th.bAccent, 0);
            _lenVal.VerticalAlignment = VerticalAlignment.Center;
            _lenVal.Margin = new Thickness(6 * S, 0, 0, 8 * S);
            rLen.Children.Add(_lenVal);
            b2.Children.Add(rLen);
            b2.Children.Add(Txt("超过这个字数她会重新生成更短的说法（不再硬截断）。", 11, false, _th.bSub, 2));

            b2.Children.Add(Txt("回复速度", 13, false, _th.bText, 12));
            var rSp = new WrapPanel();
            _slSpeed = new Slider();
            _slSpeed.Minimum = 0.3; _slSpeed.Maximum = 3.0; _slSpeed.Value = 1.0;
            _slSpeed.Width = 300 * S;
            _slSpeed.TickFrequency = 0.3;
            _slSpeed.IsSnapToTickEnabled = false;
            _slSpeed.VerticalAlignment = VerticalAlignment.Center;
            _slSpeed.ValueChanged += delegate { _speed = Math.Round(_slSpeed.Value, 2); PaintCtl(); };
            NoWheel(_slSpeed);
            rSp.Children.Add(_slSpeed);
            _speedVal = Txt("1.00x", 11.5, true, _th.bAccent, 0);
            _speedVal.VerticalAlignment = VerticalAlignment.Center;
            _speedVal.Margin = new Thickness(10 * S, 0, 0, 0);
            rSp.Children.Add(_speedVal);
            b2.Children.Add(rSp);
            b2.Children.Add(Txt("1.00x 默认；小于 1 更快（0.3 最快），大于 1 更慢（3.0 最慢）。", 11, false, _th.bSub, 2));

            b2.Children.Add(Txt("聊天频率", 13, false, _th.bText, 12));
            var rRt = new WrapPanel();
            _slRate = new Slider();
            _slRate.Minimum = -1; _slRate.Maximum = 1; _slRate.Value = -1;
            _slRate.Width = 300 * S;
            _slRate.VerticalAlignment = VerticalAlignment.Center;
            _slRate.ValueChanged += delegate
            {
                _rate = _slRate.Value <= -0.995 ? -1 : Math.Round(_slRate.Value, 2);
                PaintCtl();
            };
            NoWheel(_slRate);
            rRt.Children.Add(_slRate);
            _rateVal = Txt("（跟全局）", 11.5, true, _th.bAccent, 0);
            _rateVal.VerticalAlignment = VerticalAlignment.Center;
            _rateVal.Margin = new Thickness(10 * S, 0, 0, 0);
            rRt.Children.Add(_rateVal);
            _btnRateFollow = Btn("跟全局 ✓", false, delegate { _rate = -1; if (_slRate != null) _slRate.Value = -1; PaintCtl(); });
            _btnRateFollow.Margin = new Thickness(10 * S, 0, 0, 8 * S);
            rRt.Children.Add(_btnRateFollow);
            b2.Children.Add(rRt);
            b2.Children.Add(Txt("她在这个群「没人叫她」时主动接话的概率；拖滑块就自己算，点「跟全局」交还给总开关。", 11, false, _th.bSub, 2));

            // ---- 戳一戳回复概率：挡位按钮，不用滑动条（滚轮误触太烦）----
            b2.Children.Add(Txt("戳一戳回复概率", 13, false, _th.bText, 12));
            var rPk = new WrapPanel();
            _btnPoke.Clear();
            foreach (double step in POKE_STEPS)
            {
                double v = step;                       // 闭包捕获
                string label = (int)Math.Round(v * 100) + "%";
                var btn = Btn(label, false, delegate { _pokeRate = v; PaintCtl(); });
                btn.Padding = new Thickness(10 * S, 6 * S, 10 * S, 6 * S);
                btn.MinWidth = 52 * S;
                _btnPoke[btn] = v;
                rPk.Children.Add(btn);
            }
            _pokeVal = Txt("100%", 11.5, true, _th.bAccent, 0);
            _pokeVal.VerticalAlignment = VerticalAlignment.Center;
            _pokeVal.Margin = new Thickness(6 * S, 0, 0, 8 * S);
            rPk.Children.Add(_pokeVal);
            b2.Children.Add(rPk);
            b2.Children.Add(Txt("她在这个群被戳时回应的概率：100% = 每次都回，10% = 十次回一次，1% = 很少理。", 11, false, _th.bSub, 2));

            // ---- 默默发表情：看群友聊天时，有概率只丢一张表情包、不说话 ----
            b2.Children.Add(Txt("默默发表情（只发图不说话）", 13, false, _th.bText, 12));
            var rAm = new WrapPanel();
            _btnAmbient.Clear();
            // 「用默认」放最左边：先选它是"不改这个群"，再往后才是具体挡位
            _btnAmbientFollow = Btn("用默认 3%", false, delegate { _ambient = -1; PaintCtl(); });
            _btnAmbientFollow.MinWidth = 88 * S;
            _btnAmbientFollow.Padding = new Thickness(10 * S, 6 * S, 10 * S, 6 * S);
            rAm.Children.Add(_btnAmbientFollow);
            foreach (double step in AMBIENT_STEPS)
            {
                double v = step;
                string label = (int)Math.Round(v * 100) + "%";
                var btn = Btn(label, false, delegate { _ambient = v; PaintCtl(); });
                btn.Padding = new Thickness(8 * S, 6 * S, 8 * S, 6 * S);
                btn.MinWidth = 46 * S;
                _btnAmbient[btn] = v;
                rAm.Children.Add(btn);
            }
            _ambientVal = Txt("3%", 11.5, true, _th.bAccent, 0);
            _ambientVal.VerticalAlignment = VerticalAlignment.Center;
            _ambientVal.Margin = new Thickness(6 * S, 0, 0, 8 * S);
            rAm.Children.Add(_ambientVal);
            b2.Children.Add(rAm);
            _ambientRow = rAm;
            b2.Children.Add(Txt("她在群里看别人聊天时，偶尔只丢一张表情包、一个字都不说。" +
                                "上面是「每来一条她能看到的群消息」的概率；0% = 这个群关掉。", 11, false, _th.bSub, 2));
            b2.Children.Add(Txt("另有硬限制：同一个群最多 10 分钟一次、一小时 4 次；她刚说完话的几秒内不会再甩图；" +
                                "静默时段（FISH_QUIET_HOURS）整段时间都不发。",
                                11, false, _th.bSub, 2));

            var rSave = Row();
            rSave.Margin = new Thickness(0, 14 * S, 0, 0);
            rSave.Children.Add(Btn("保存到这个群", true, delegate { SaveRules(); }));
            rSave.Children.Add(Btn("重新读取", false, delegate { LoadRules(); }));
            b2.Children.Add(rSave);

            // ---- 全局控件（抽屉）：这些对**所有群**生效，点一项进子页面，左上角返回 ----
            // 2026-10-30 用户：「把全局的机器人控件单独做成控件页面的子页面，就像抽屉」——
            // 以前控件页一长串折叠卡（性能/API/梗库/图库/人格/声音/沉默名单），翻起来很累。
            if (ShotDemo == "voice") { ShowCtlSub("voice"); return; }    // 自检：直接拍音色子页面
            if (ShotDemo == "draw") { ShowCtlSub("draw"); return; }      // 自检：画图（本地 ComfyUI）
            if (ShotDemo == "drawstyles") { ShowCtlSub("drawstyles"); return; }  // 自检：画图模板
            if (ShotDemo == "fishmem") { ShowCtlSub("fishmem"); return; }        // 自检：鱼的记忆
            if (ShotDemo == "bots") { ShowCtlSub("bots"); return; }      // 自检：机器人名单子页面
            if (ShotDemo == "photolib" || ShotDemo == "photolib2"
                || (ShotDemo != null && ShotDemo.StartsWith("tag:")))
            { ShowCtlSub("photolib"); return; }  // 自检：图库（含模糊搜索）
            StackPanel bgd;
            _page.Children.Add(Card("全局控件（不分群）", out bgd));
            bgd.Children.Add(Txt("下面这些设置对所有群生效。点一项进去改，左上角「← 返回控件」回来。",
                                 11.5, false, _th.bSub, 2));
            AddCtlDrawer(bgd, "perf", "她的性能（思考能力）", "省电 / 平衡 / 满血 · 决定她多聪明、多快");
            AddCtlDrawer(bgd, "api", "API 渠道", "多条渠道各自命名，点一下热切换");
            AddCtlDrawer(bgd, "meme", "梗库", "名字;名字 | 介绍（她接梗就靠这个）");
            AddCtlDrawer(bgd, "photolib", "记忆图库", "教她认图：她见过这张图就按你写的备注回");
            AddCtlDrawer(bgd, "draw", "画图（本地 ComfyUI）", "群里的 /画图 用这台电脑的显卡；隧道没起来就画不出来");
            AddCtlDrawer(bgd, "drawlib", "画图图库", "每次 /画图 的原图都在这儿：能看、能改标题标签、能删");
            AddCtlDrawer(bgd, "drawstyles", "画图模板", "二次元 / Q版 / 电影 / 水墨 / 脱衣：提示词能改、能加新的");
            AddCtlDrawer(bgd, "persona", "角色人格", "多套人设隔离 · 现在 3 个（肥鱼 / 塔菲 / 黑鱼）");
            AddCtlDrawer(bgd, "voice", "她的声音（每个人格一套音色）", "肥鱼 / 塔菲 / 黑鱼 分别配音色、模型");
            AddCtlDrawer(bgd, "bots", "机器人名单（谁是机器人）", "名单里的人走专属话术；自动识别已关，全部手动加");
            AddCtlDrawer(bgd, "black", "沉默名单", "不理会的机器人");
            AddCtlDrawer(bgd, "fishmem", "鱼的记忆（会淡忘的长期记忆）", "她记得的事 + 试召回：看得见每条为什么被想起/没被想起");

            PaintCtl();
            LoadGroups();
            if (ShotDemo == "blacklist") ApplyBlackDemo();
        }

        // ------------------------------------------------ 全局控件：抽屉 → 子页面
        /// <summary>
        /// 抽屉里的一行：标题 + 一句说明 + 右边「›」。
        /// 点进去 = 这个子页面（`ShowCtlSub`），控件页本身只留群级的东西。
        /// </summary>
        void AddCtlDrawer(StackPanel host, string id, string title, string hint)
        {
            var border = new Border();
            border.Background = Brushes.Transparent;
            border.CornerRadius = new CornerRadius(10);
            border.Padding = new Thickness(10 * S, 9 * S, 10 * S, 9 * S);
            border.Margin = new Thickness(0, 2 * S, 0, 2 * S);
            border.Cursor = Cursors.Hand;

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var sp = new StackPanel();
            var t = Txt("▸ " + title, 13, true, _th.bText, 0);
            sp.Children.Add(t);
            if (!string.IsNullOrEmpty(hint))
            {
                var h = Txt(hint, 10.5, false, _th.bSub, 2);
                h.Margin = new Thickness(14 * S, 2 * S, 0, 0);
                sp.Children.Add(h);
            }
            Grid.SetColumn(sp, 0);
            g.Children.Add(sp);

            var arrow = Txt("›", 16, true, _th.bSub, 0);
            arrow.VerticalAlignment = VerticalAlignment.Center;
            arrow.Margin = new Thickness(8 * S, 0, 2 * S, 0);
            Grid.SetColumn(arrow, 1);
            g.Children.Add(arrow);

            border.Child = g;
            border.MouseEnter += delegate { border.Background = _th.bBtn; t.Foreground = _th.bAccent; };
            border.MouseLeave += delegate { border.Background = Brushes.Transparent; t.Foreground = _th.bText; };
            border.MouseLeftButtonUp += delegate { ShowCtlSub(id); };
            host.Children.Add(border);
        }

        /// <summary>打开一个全局控件的子页面（控件页的「抽屉」里点进来）。</summary>
        void ShowCtlSub(string id)
        {
            _page.Children.Clear();
            _scroll.ScrollToTop();
            _ctlSubId = id;

            var backRow = new WrapPanel();
            backRow.Margin = new Thickness(0, 0, 0, 4 * S);
            var back = Btn("← 返回控件", false, delegate { ShowPage("botctl"); });
            back.Margin = new Thickness(0, 0, 0, 0);
            backRow.Children.Add(back);
            _page.Children.Add(backRow);

            string title = "全局控件", desc = "";
            if (id == "perf") { title = "她的性能（思考能力）"; desc = "三挡：省电 / 平衡 / 满血。改完立刻生效。"; }
            else if (id == "api") { title = "API 渠道"; desc = "多条渠道各自命名、各自 key，点「用这个」立刻切。"; }
            else if (id == "meme") { title = "梗库"; desc = "名字;名字 | 介绍。她听到这些词会接梗。"; }
            else if (id == "photolib") { title = "记忆图库"; desc = "教她认图：发图 + 名字 | 描述，之后见到就按你的说法回。"; }
            else if (id == "draw") { title = "画图（本地 ComfyUI）"; desc = "群里的 /画图 走这台电脑的显卡；这里管隧道和额度。"; }
            else if (id == "drawlib") { title = "画图图库"; desc = "每次 /画图 出的原图都在这里；点缩略图看大图，能改标题/标签/备注、收藏、删除。"; }
            else if (id == "drawstyles") { title = "画图模板"; desc = "群里的 /画图 认的那几个画风词就在这儿：名字就是关键词，提示词能改、能加新的、能停用或删除。改完不用重启机器人。"; }
            else if (id == "persona") { title = "角色人格"; desc = "一套人设 = 正文 + 它自己的文案；互不借用。"; }
            else if (id == "voice") { title = "她的声音（每个人格一套音色）"; desc = "每个角色一条嗓子，互不借用；改完下一条消息生效。"; }
            else if (id == "bots") { title = "机器人名单"; desc = "名单里的号被当成机器人：不主动接话；它 @ 她时只用一句专属回复。自动识别已关，全部手动加。"; }
            else if (id == "black") { title = "沉默名单"; desc = "这些机器人号发的消息，她一律当没看见。"; }
            else if (id == "fishmem") { title = "鱼的记忆"; desc = "会淡忘的长期记忆：长期事实每轮都在，事件按鲜活度/重要度/情绪挑；越久越淡，但不删。"; }
            _page.Children.Add(Txt(title, 18, true, _th.bText, 4));
            if (!string.IsNullOrEmpty(desc)) _page.Children.Add(Txt(desc, 11.5, false, _th.bSub, 2));

            StackPanel body;
            _page.Children.Add(Card("", out body));
            body.Margin = new Thickness(0, 4 * S, 0, 0);
            if (id == "perf") BuildPerfCard(body);
            else if (id == "api") BuildApiCard(body);
            else if (id == "meme") BuildMemeCard(body);
            else if (id == "photolib") BuildPhotoLibCard(body);
            else if (id == "draw") BuildDrawCard(body);
            else if (id == "drawlib") BuildDrawLibCard(body);
            else if (id == "drawstyles") BuildDrawStylesCard(body);
            else if (id == "persona") BuildPersonaCard(body);
            else if (id == "voice") BuildVoiceCard(body);
            else if (id == "bots") BuildBotListCard(body);
            else if (id == "black") BuildBlacklistCard(body);
            else if (id == "fishmem") BuildFishMemCard(body);
            PaintCtl();
            if (ShotDemo == "blacklist" && id == "black") ApplyBlackDemo();
        }

        // ------------------------------------------------ 鱼的记忆（2026-11-07）
        // 用户：「做到控制台里」→ 和图库/模板同款：列表 + 手动加 + 删 + 试召回。
        // 数据在服务器 data/fishmemory/<人格>/<会话>.json；后端加载的是记忆插件的同一套纯逻辑，
        // 所以这里「试召回」看到的就是她真正会想起的东西，不是前端另算一遍。
        readonly System.Collections.Generic.List<Dictionary<string, object>> _fmItems
            = new System.Collections.Generic.List<Dictionary<string, object>>();
        readonly System.Collections.Generic.List<string> _fmChatList
            = new System.Collections.Generic.List<string>();
        readonly System.Collections.Generic.List<string> _fmChatInfo
            = new System.Collections.Generic.List<string>();
        string _fmPersona = "feiyu";
        string _fmChat = "";
        WrapPanel _fmPersonaRow, _fmChatRow;
        StackPanel _fmList;
        TextBlock _fmStat, _fmRecall;
        TextBox _fmAddText, _fmAddKey, _fmQuery;
        string _fmFlash = "";

        static int FmNum(Dictionary<string, object> d, string k)
        {
            try { if (d != null && d.ContainsKey(k) && d[k] != null) return Convert.ToInt32(d[k]); }
            catch { }
            return 0;
        }

        static double FmDbl(Dictionary<string, object> d, string k)
        {
            try { if (d != null && d.ContainsKey(k) && d[k] != null) return Convert.ToDouble(d[k]); }
            catch { }
            return 0;
        }

        TextBox FmInput(string text)
        {
            var t = new TextBox();
            t.Text = text;
            t.FontSize = 12.5 * S;
            t.Height = 30 * S;
            t.Margin = new Thickness(0, 3 * S, 6 * S, 0);
            t.Background = _th.bBtn;
            t.Foreground = _th.bText;
            t.BorderBrush = _th.bLine;
            t.Padding = new Thickness(7 * S, 0, 7 * S, 0);
            return t;
        }

        void BuildFishMemCard(StackPanel b)
        {
            b.Children.Add(Txt("她记得的事都在服务器的 data/fishmemory/<人格>/<会话>.json。\n"
                + "长期事实（画像 / 喜好 / 约定）每轮都在；普通事件按「鲜活度 × 重要度 × 情绪」挑，"
                + "证据够特别才注入 —— 越久越淡，但不会自己删。\n"
                + "群里同名的管理指令是 /鱼记忆（只有超管能用），两边看的是同一份数据。",
                11, false, _th.bSub, 2));

            b.Children.Add(Txt("角色", 11.5, false, _th.bSub, 10));
            _fmPersonaRow = Row();
            b.Children.Add(_fmPersonaRow);

            b.Children.Add(Txt("会话（只列有记忆的）", 11.5, false, _th.bSub, 10));
            _fmChatRow = Row();
            b.Children.Add(_fmChatRow);

            var r0 = Row();
            r0.Margin = new Thickness(0, 8 * S, 0, 0);
            r0.Children.Add(Btn("刷新", true, delegate { FishMemLoad(); }));
            b.Children.Add(r0);

            _fmStat = Txt("读取中…", 12, true, _th.bText, 4);
            b.Children.Add(_fmStat);

            b.Children.Add(Txt("加一条（键可留空；填了键就是「同一件事只留最新」，例：她.生日）",
                11.5, false, _th.bSub, 12));
            var ra = Row();
            _fmAddText = FmInput("");
            _fmAddText.Width = 300 * S;
            ra.Children.Add(_fmAddText);
            _fmAddKey = FmInput("");
            _fmAddKey.Width = 140 * S;
            ra.Children.Add(_fmAddKey);
            ra.Children.Add(Btn("记下", false, delegate { FishMemAdd(); }));
            b.Children.Add(ra);

            b.Children.Add(Txt("试一句：看这次会想起哪几条、别的为什么没进（和群里注入的完全一样）",
                11.5, false, _th.bSub, 12));
            var rq = Row();
            _fmQuery = FmInput("");
            _fmQuery.Width = 360 * S;
            rq.Children.Add(_fmQuery);
            rq.Children.Add(Btn("试召回", false, delegate { FishMemRecall(); }));
            b.Children.Add(rq);

            _fmRecall = Txt("", 11.5, false, _th.bText, 6);
            _fmRecall.TextWrapping = TextWrapping.Wrap;
            b.Children.Add(_fmRecall);

            b.Children.Add(Txt("记住了什么（灰的是已被新值取代的；每张卡右下角能删）",
                11.5, false, _th.bSub, 12));
            _fmList = new StackPanel();
            _fmList.Margin = new Thickness(0, 4 * S, 0, 0);
            b.Children.Add(_fmList);

            var rz = Row();
            rz.Margin = new Thickness(0, 10 * S, 0, 0);
            rz.Children.Add(Btn("清空这个会话的记忆", false, delegate { FishMemClear(); }));
            b.Children.Add(rz);

            FishMemLoad();
            if (ShotDemo == "fishmem")
            {
                // 自检：自动填一句并试召回，截图里就能看到「会注入什么 / 别的为什么没进」
                var tmrFm = new DispatcherTimer();
                tmrFm.Interval = TimeSpan.FromSeconds(7);
                tmrFm.Tick += delegate
                {
                    tmrFm.Stop();
                    if (_fmQuery != null) _fmQuery.Text = "她生日是哪天呀";
                    FishMemRecall();
                };
                tmrFm.Start();
            }
        }

        void FishMemLoad()
        {
            if (_fmStat != null && _fmFlash.Length == 0) _fmStat.Text = "正在读…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/fishmem?token=" + Token
                    + "&persona=" + _fmPersona + "&chat=" + _fmChat, 45);
                Dispatcher.BeginInvoke(new Action(delegate { FishMemApply(json); }));
            });
        }

        void FishMemApply(string json)
        {
            _fmItems.Clear();
            _fmChatList.Clear();
            _fmChatInfo.Clear();
            string err = "";
            try
            {
                var o = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                if (o == null) err = "没有返回";
                else
                {
                    if (DlStr(o, "ok").ToLower() == "false") err = DlStr(o, "error");
                    var ca = (o.ContainsKey("chats") ? o["chats"] : null) as object[];
                    if (ca != null)
                        foreach (var cv in ca)
                        {
                            var c = cv as Dictionary<string, object>;
                            if (c == null) continue;
                            _fmChatList.Add(DlStr(c, "chat"));
                            _fmChatInfo.Add(FmNum(c, "live") + " 条 · 长期 " + FmNum(c, "facts"));
                        }
                    var ia = (o.ContainsKey("items") ? o["items"] : null) as object[];
                    if (ia != null)
                        foreach (var iv in ia)
                        {
                            var d = iv as Dictionary<string, object>;
                            if (d != null) _fmItems.Add(d);
                        }
                    if (_fmChat.Length == 0 && _fmChatList.Count > 0 && err.Length == 0)
                    {
                        _fmChat = _fmChatList[0];
                        FishMemLoad();
                        return;
                    }
                    var st = (o.ContainsKey("stats") ? o["stats"] : null) as Dictionary<string, object>;
                    if (st != null && _fmStat != null)
                    {
                        int total = FmNum(st, "total");
                        int live = FmNum(st, "live");
                        string msg = "共 " + total + " 条（长期 " + FmNum(st, "facts")
                            + " · 事件 " + FmNum(st, "events") + "）· 已取代 " + (total - live)
                            + " · 今天 " + FmNum(st, "today") + " · 累计被想起 " + FmNum(st, "hits") + " 次";
                        if (_fmFlash.Length > 0) { msg = _fmFlash + " · " + msg; _fmFlash = ""; }
                        _fmStat.Text = msg;
                    }
                }
            }
            catch { err = "解析失败"; }
            if (err.Length > 0 && _fmStat != null) _fmStat.Text = "读取失败：" + err;
            FishMemRenderSelectors();
            FishMemRenderList();
        }

        void FishMemRenderSelectors()
        {
            if (_fmPersonaRow != null)
            {
                _fmPersonaRow.Children.Clear();
                string[] ps = { "feiyu", "dsh", "taffy", "feiyu_adult" };
                foreach (var p in ps)
                {
                    string pid = p;
                    _fmPersonaRow.Children.Add(Btn(pid, pid == _fmPersona, delegate
                    {
                        _fmPersona = pid;
                        _fmChat = "";
                        FishMemLoad();
                    }));
                }
            }
            if (_fmChatRow == null) return;
            _fmChatRow.Children.Clear();
            if (_fmChatList.Count == 0)
            {
                _fmChatRow.Children.Add(Txt("（这个角色还没有任何记忆）", 11, false, _th.bSub, 0));
                return;
            }
            for (int i = 0; i < _fmChatList.Count; i++)
            {
                string ck = _fmChatList[i];
                string label = ck + "  （" + _fmChatInfo[i] + "）";
                _fmChatRow.Children.Add(Btn(label, ck == _fmChat, delegate
                {
                    _fmChat = ck;
                    FishMemLoad();
                }));
            }
        }

        void FishMemRenderList()
        {
            if (_fmList == null) return;
            _fmList.Children.Clear();
            if (_fmItems.Count == 0)
            {
                _fmList.Children.Add(Txt(_fmChat.Length == 0
                    ? "还没有记忆：先陪她聊几句，或者在上面「加一条」。"
                    : "这个会话还没记下什么。", 11.5, false, _th.bSub, 2));
                return;
            }
            foreach (var it in _fmItems)
            {
                bool dead = DlStr(it, "superseded").ToLower() == "true";
                bool fact = DlStr(it, "kind") == "fact";
                string key = DlStr(it, "key");

                var card = new Border();
                card.Background = _th.bBtn;
                card.BorderBrush = _th.bLine;
                card.BorderThickness = new Thickness(1);
                card.CornerRadius = new CornerRadius(10);
                card.Padding = new Thickness(11 * S, 8 * S, 11 * S, 8 * S);
                card.Margin = new Thickness(0, 0, 0, 8 * S);

                var sp = new StackPanel();
                string head = DlStr(it, "date") + (key.Length > 0 ? "   [" + key + "]" : "")
                              + (fact ? "   · 长期" : "") + (dead ? "   【已被取代】" : "");
                sp.Children.Add(Txt(head, 11, false, dead ? _th.bSub : _th.bAccent, 0));
                var tx = Txt(DlStr(it, "text"), 13, true, dead ? _th.bSub : _th.bText, 2);
                tx.TextWrapping = TextWrapping.Wrap;
                sp.Children.Add(tx);
                sp.Children.Add(Txt("鲜活 " + Math.Round(FmDbl(it, "vitality") * 100) + "% · 重要 "
                    + Math.Round(FmDbl(it, "importance") * 100) + "% · 想起 "
                    + FmNum(it, "hits") + " 次", 10.5, false, _th.bSub, 2));

                var r = Row();
                r.Margin = new Thickness(0, 6 * S, 0, 0);
                string sid = DlStr(it, "id");
                string stext = DlStr(it, "text");
                r.Children.Add(Btn("删", false, delegate { FishMemDel(sid, stext); }));
                sp.Children.Add(r);
                card.Child = sp;
                _fmList.Children.Add(card);
            }
        }

        void FishMemAdd()
        {
            string text = (_fmAddText == null ? "" : _fmAddText.Text).Trim();
            string key = (_fmAddKey == null ? "" : _fmAddKey.Text).Trim();
            if (text.Length == 0) { _fmFlash = "要记什么？内容不能空"; FishMemLoad(); return; }
            if (_fmChat.Length == 0) { _fmFlash = "先选一个会话"; FishMemLoad(); return; }
            var p = new Dictionary<string, object>();
            p["act"] = "add";
            p["persona"] = _fmPersona;
            p["chat"] = _fmChat;
            p["text"] = text;
            p["key"] = key;
            FishMemPost(p, "记下了", null, true);
            if (_fmAddText != null) _fmAddText.Text = "";
            if (_fmAddKey != null) _fmAddKey.Text = "";
        }

        void FishMemDel(string id, string text)
        {
            if (MessageBox.Show("删掉这条？\n\n" + text, "鱼的记忆",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            var p = new Dictionary<string, object>();
            p["act"] = "del";
            p["persona"] = _fmPersona;
            p["chat"] = _fmChat;
            p["id"] = id;
            FishMemPost(p, "删掉了", null, true);
        }

        void FishMemClear()
        {
            if (MessageBox.Show("把 " + _fmChat + " 的记忆全部删掉？\n（不可撤销）", "鱼的记忆",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            var p = new Dictionary<string, object>();
            p["act"] = "clear";
            p["persona"] = _fmPersona;
            p["chat"] = _fmChat;
            FishMemPost(p, "清空了", null, true);
        }

        void FishMemRecall()
        {
            string q = (_fmQuery == null ? "" : _fmQuery.Text).Trim();
            if (q.Length == 0) { _fmRecall.Text = "先写一句要试的话。"; return; }
            if (_fmChat.Length == 0) { _fmRecall.Text = "先选一个会话。"; return; }
            var p = new Dictionary<string, object>();
            p["act"] = "recall";
            p["persona"] = _fmPersona;
            p["chat"] = _fmChat;
            p["q"] = q;
            _fmRecall.Text = "正在算…";
            FishMemPost(p, null, delegate(string raw)
            {
                var sb = new StringBuilder();
                try
                {
                    var o = new JavaScriptSerializer().DeserializeObject(raw) as Dictionary<string, object>;
                    if (o == null) { sb.Append("没有返回。"); }
                    else if (DlStr(o, "ok").ToLower() == "false") { sb.Append("失败：").Append(DlStr(o, "error")); }
                    else
                    {
                        string blk = DlStr(o, "block");
                        sb.Append(blk.Length == 0 ? "（这条什么都不会注入）" : blk);
                        var sk = (o.ContainsKey("skipped") ? o["skipped"] : null) as object[];
                        if (sk != null && sk.Length > 0)
                        {
                            sb.Append("\n\n没进来的：");
                            for (int i = 0; i < sk.Length && i < 8; i++)
                            {
                                var d = sk[i] as Dictionary<string, object>;
                                if (d == null) continue;
                                sb.Append("\n· ").Append(DlStr(d, "text")).Append("：").Append(DlStr(d, "why"));
                            }
                        }
                    }
                }
                catch { sb.Append("解析失败：").Append(FirstLine(raw)); }
                _fmRecall.Text = sb.ToString();
            }, false);
        }

        void FishMemPost(Dictionary<string, object> payload, string okText, Action<string> after, bool reload)
        {
            if (_fmStat != null && reload) _fmStat.Text = "正在写入…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string body = new JavaScriptSerializer().Serialize(payload);
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                string cmd = "echo " + b64 + " | base64 -d > /tmp/fm.json; "
                    + "curl -s -m 25 -X POST -H 'Content-Type: application/json' "
                    + "--data-binary @/tmp/fm.json '" + ApiBase + "/api/fishmem?token=" + Token + "'";
                string r = Ssh.Run(_cfg, cmd, 45);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool ok = r.IndexOf("\"ok\": true") >= 0 || r.IndexOf("\"ok\":true") >= 0;
                    if (okText != null) _fmFlash = ok ? okText : ("失败：" + FirstLine(r));
                    if (after != null) after(r);
                    if (reload) FishMemLoad();
                }));
            });
        }

        // ------------------------------------------------ 画图图库（2026-11-06 用户要求）
        // 每次群里 `/画图` 的原图都由插件写进服务器 `data/drawlib/`，这里读 `/api/drawlib`。
        // 缩略图是**服务端懒生成**的（第一张点开才做，之后命中缓存），这边再存一份本地缓存，
        // 下次打开控制台秒出。能改标题/标签/备注、收藏、删除、看大图。
        readonly System.Collections.Generic.Dictionary<string, BitmapImage> _dlBmp
            = new System.Collections.Generic.Dictionary<string, BitmapImage>();
        readonly System.Collections.Generic.List<Dictionary<string, object>> _dlItems
            = new System.Collections.Generic.List<Dictionary<string, object>>();
        WrapPanel _dlGrid;
        TextBlock _dlStatus;
        TextBox _dlSearch;
        Button _dlFavBtn;
        bool _dlFavOnly;

        static string DrawLibCacheDir()
        {
            string d = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FishConsole", "drawlib");
            try { System.IO.Directory.CreateDirectory(d); } catch { }
            return d;
        }

        static string DrawLibThumbPath(string id)
        {
            string safe = (id ?? "").Trim();
            foreach (var ch in System.IO.Path.GetInvalidFileNameChars()) safe = safe.Replace(ch, '_');
            return System.IO.Path.Combine(DrawLibCacheDir(), safe + ".jpg");
        }

        void BuildDrawLibCard(StackPanel b)
        {
            b.Children.Add(Txt("每次 `/画图` 出的**原图**都会自动进这里（服务器 data/drawlib/）。"
                               + "点缩略图看大图；「改」能写标题 / 标签 / 备注，「删」会把原图和记录一起删掉。",
                               11, false, _th.bSub, 2));

            var row = new WrapPanel();
            row.Margin = new Thickness(0, 10 * S, 0, 0);

            _dlSearch = new TextBox();
            _dlSearch.Width = 260 * S;
            _dlSearch.Height = 30 * S;
            _dlSearch.FontSize = 12.5 * S;
            _dlSearch.Margin = new Thickness(0, 0, 8 * S, 8 * S);
            _dlSearch.Background = _th.bBtn;
            _dlSearch.Foreground = _th.bText;
            _dlSearch.BorderBrush = _th.bLine;
            _dlSearch.Padding = new Thickness(8 * S, 0, 8 * S, 0);
            StyleInput(_dlSearch);
            Watermark(_dlSearch, "搜提示词 / 标题 / 备注 / QQ号");
            _dlSearch.KeyDown += delegate(object s, System.Windows.Input.KeyEventArgs e)
            {
                if (e.Key == System.Windows.Input.Key.Enter) DrawLibLoad();
            };
            row.Children.Add(_dlSearch);

            row.Children.Add(Btn("搜索", true, delegate { DrawLibLoad(); }));

            _dlFavBtn = Btn("只看收藏", false, delegate
            {
                _dlFavOnly = !_dlFavOnly;
                if (_dlFavBtn != null) _dlFavBtn.Content = _dlFavOnly ? "只看收藏 ✦" : "只看收藏";
                DrawLibLoad();
            });
            _dlFavBtn.Margin = new Thickness(8 * S, 0, 0, 8 * S);
            row.Children.Add(_dlFavBtn);

            var rf = Btn("刷新", false, delegate { DrawLibLoad(); });
            rf.Margin = new Thickness(8 * S, 0, 0, 8 * S);
            row.Children.Add(rf);

            _dlStatus = Txt("正在读…", 10.5, false, _th.bSub, 0);
            _dlStatus.VerticalAlignment = VerticalAlignment.Center;
            _dlStatus.Margin = new Thickness(10 * S, 0, 0, 8 * S);
            row.Children.Add(_dlStatus);
            b.Children.Add(row);

            _dlGrid = new WrapPanel();
            _dlGrid.Margin = new Thickness(0, 10 * S, 0, 0);
            b.Children.Add(_dlGrid);

            DrawLibLoad();
        }

        void DrawLibLoad()
        {
            if (_dlStatus != null) _dlStatus.Text = "正在读…";
            string q = _dlSearch == null ? "" : (_dlSearch.Text ?? "").Trim();
            string path = "/api/drawlib?limit=200"
                          + (q.Length > 0 ? "&q=" + Uri.EscapeDataString(q) : "")
                          + (_dlFavOnly ? "&fav=1" : "")
                          + "&token=" + Token;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, path, 45);
                Dispatcher.BeginInvoke(new Action(delegate { DrawLibApply(json); }));
            });
        }

        void DrawLibApply(string json)
        {
            _dlItems.Clear();
            int total = 0, all = 0;
            try
            {
                var o = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                if (o != null)
                {
                    if (o.ContainsKey("total")) total = Convert.ToInt32(o["total"]);
                    if (o.ContainsKey("all")) all = Convert.ToInt32(o["all"]);
                    var arr = (o.ContainsKey("items") ? o["items"] : null) as object[];
                    if (arr != null)
                        foreach (var itv in arr)
                        {
                            var d = itv as Dictionary<string, object>;
                            if (d != null) _dlItems.Add(d);
                        }
                }
            }
            catch { }
            if (_dlStatus != null)
                _dlStatus.Text = all > 0
                    ? ("图库共 " + all + " 张，这里 " + _dlItems.Count + " 张")
                    : "图库还是空的（群里画一张就有了）";
            RenderDrawLib();
        }

        static string DlStr(Dictionary<string, object> d, string k)
        {
            if (d == null || !d.ContainsKey(k) || d[k] == null) return "";
            return Convert.ToString(d[k]);
        }

        void RenderDrawLib()
        {
            if (_dlGrid == null) return;
            _dlGrid.Children.Clear();
            foreach (var it in _dlItems)
            {
                string id = DlStr(it, "id");
                string title = DlStr(it, "title");
                if (title.Length == 0)
                {
                    title = DlStr(it, "user_want");
                    if (title.Length == 0) title = DlStr(it, "prompt");
                }
                if (title.Length > 18) title = title.Substring(0, 18) + "…";
                if (title.Length == 0) title = id;

                string at = DlStr(it, "at");
                if (at.Length >= 16) at = at.Substring(5, 11);      // 11-06 16:21
                string style = DlStr(it, "style");
                bool fav = DlStr(it, "fav").ToLower() == "true";
                bool nsfw = DlStr(it, "nsfw").ToLower() == "true";

                var card = new Border();
                card.Width = 176 * S;
                card.Margin = new Thickness(0, 0, 10 * S, 10 * S);
                card.Background = _th.bCard;
                card.BorderBrush = _th.bLine;
                card.BorderThickness = new Thickness(1);
                card.CornerRadius = new CornerRadius(10);
                card.Padding = new Thickness(8 * S);

                var sp = new StackPanel();

                var imgBox = new Border();
                imgBox.Width = 158 * S;
                imgBox.Height = 158 * S;
                imgBox.Background = _th.bBtn;
                imgBox.CornerRadius = new CornerRadius(7);
                imgBox.ClipToBounds = true;
                var img = new System.Windows.Controls.Image();
                img.Stretch = Stretch.UniformToFill;
                img.Cursor = Cursors.Hand;
                img.Tag = id;
                img.MouseLeftButtonUp += delegate { DrawLibBig(id); };
                imgBox.Child = img;
                sp.Children.Add(imgBox);
                DrawLibThumb(id, img);

                var t = Txt((fav ? "⭐ " : "") + title, 12, true, _th.bText, 0);
                t.Margin = new Thickness(0, 6 * S, 0, 0);
                t.TextTrimming = TextTrimming.CharacterEllipsis;
                sp.Children.Add(t);

                string sub = at + (style.Length > 0 ? " · " + style : "");
                if (nsfw) sub += " · NSFW";
                var st = Txt(sub, 10, false, _th.bSub, 0);
                st.Margin = new Thickness(0, 2 * S, 0, 0);
                sp.Children.Add(st);

                var ops = new WrapPanel();
                ops.Margin = new Thickness(0, 6 * S, 0, 0);
                var edit = Btn("改", false, delegate { DrawLibEdit(it); });
                edit.Padding = new Thickness(10 * S, 2 * S, 10 * S, 2 * S);
                edit.FontSize = 10.5 * S;
                edit.MinWidth = 0;
                edit.MinHeight = 0;
                ops.Children.Add(edit);
                var del = Btn("删", false, delegate { DrawLibDel(id, title); });
                del.Margin = new Thickness(6 * S, 0, 0, 0);
                del.Padding = new Thickness(10 * S, 2 * S, 10 * S, 2 * S);
                del.FontSize = 10.5 * S;
                del.MinWidth = 0;
                del.MinHeight = 0;
                ops.Children.Add(del);
                sp.Children.Add(ops);

                card.Child = sp;
                _dlGrid.Children.Add(card);
            }
            if (_dlItems.Count == 0)
                _dlGrid.Children.Add(Txt("没有匹配的图。", 11.5, false, _th.bSub, 6));
        }

        void DrawLibThumb(string id, System.Windows.Controls.Image img)
        {
            BitmapImage hit;
            if (_dlBmp.TryGetValue(id, out hit))
            {
                img.Source = hit;
                img.Tag = hit;
                return;
            }
            try
            {
                string fp = DrawLibThumbPath(id);
                if (System.IO.File.Exists(fp))
                {
                    var bm0 = new BitmapImage();
                    bm0.BeginInit();
                    bm0.CacheOption = BitmapCacheOption.OnLoad;
                    bm0.UriSource = new Uri(fp);
                    bm0.EndInit();
                    bm0.Freeze();
                    _dlBmp[id] = bm0;
                    img.Source = bm0;
                    img.Tag = bm0;
                    return;
                }
            }
            catch { }
            ThreadPool.QueueUserWorkItem(delegate
            {
                string b64 = Ssh.Run(_cfg,
                    "curl -s -m 30 'http://127.0.0.1:8090/api/drawlib/thumb?id=" + id
                    + "&token=" + Token + "' | base64 -w0", 45).Trim();
                if (string.IsNullOrEmpty(b64) || b64.Length < 100) return;
                try
                {
                    byte[] raw = Convert.FromBase64String(b64);
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        try
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.StreamSource = new MemoryStream(raw);
                            bmp.EndInit();
                            bmp.Freeze();
                            img.Source = bmp;
                            img.Tag = bmp;
                            _dlBmp[id] = bmp;
                            try { System.IO.File.WriteAllBytes(DrawLibThumbPath(id), raw); } catch { }
                        }
                        catch { }
                    }));
                }
                catch { }
            });
        }

        void DrawLibBig(string id)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string b64 = Ssh.Run(_cfg,
                    "curl -s -m 60 'http://127.0.0.1:8090/api/drawlib/img?id=" + id
                    + "&token=" + Token + "' | base64 -w0", 90).Trim();
                if (string.IsNullOrEmpty(b64) || b64.Length < 100) return;
                try
                {
                    byte[] raw = Convert.FromBase64String(b64);
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        try
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.StreamSource = new MemoryStream(raw);
                            bmp.EndInit();
                            bmp.Freeze();
                            ShowImageViewer(bmp, "画图图库 · " + id);
                        }
                        catch { }
                    }));
                }
                catch { }
            });
        }

        void DrawLibDel(string id, string title)
        {
            if (MessageBox.Show("真的删掉「" + title + "」？\n原图会从服务器上一并删掉，删了找不回来。",
                                "删画图记录", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                != MessageBoxResult.OK) return;
            DrawLibPost("/api/drawlib/del", new Dictionary<string, object> { { "id", id } },
                        "已删除「" + title + "」");
        }

        void DrawLibEdit(Dictionary<string, object> it)
        {
            string id = DlStr(it, "id");
            string eTitle = DlStr(it, "title");
            string eNote = DlStr(it, "note");
            string eTags = "";
            try
            {
                var tagsArr = (it.ContainsKey("tags") ? it["tags"] : null) as object[];
                if (tagsArr != null)
                {
                    var l2 = new System.Collections.Generic.List<string>();
                    foreach (var x in tagsArr) if (x != null) l2.Add(Convert.ToString(x));
                    eTags = string.Join(", ", l2.ToArray());
                }
            }
            catch { }

            var w = new Window();
            w.Title = "编辑画图记录";
            w.Width = 480 * S;
            w.SizeToContent = SizeToContent.Height;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            w.Owner = this;
            w.Background = _th.bCard;
            var sp = new StackPanel();
            sp.Margin = new Thickness(16 * S);
            sp.Children.Add(Txt("标题（列表里显示这个）", 11.5, false, _th.bSub, 0));
            var t1 = new TextBox();
            t1.Text = eTitle;
            t1.FontSize = 13 * S;
            t1.Height = 32 * S;
            t1.Margin = new Thickness(0, 4 * S, 0, 0);
            t1.Background = _th.bBtn;
            t1.Foreground = _th.bText;
            t1.BorderBrush = _th.bLine;
            t1.Padding = new Thickness(8 * S, 0, 8 * S, 0);
            sp.Children.Add(t1);
            sp.Children.Add(Txt("标签（逗号分隔：白毛, 泳装, 脱衣）", 11.5, false, _th.bSub, 10));
            var t2 = new TextBox();
            t2.Text = eTags;
            t2.FontSize = 13 * S;
            t2.Height = 32 * S;
            t2.Margin = new Thickness(0, 4 * S, 0, 0);
            t2.Background = _th.bBtn;
            t2.Foreground = _th.bText;
            t2.BorderBrush = _th.bLine;
            t2.Padding = new Thickness(8 * S, 0, 8 * S, 0);
            sp.Children.Add(t2);
            sp.Children.Add(Txt("备注（随便写，搜索也能搜到）", 11.5, false, _th.bSub, 10));
            var t3 = new TextBox();
            t3.Text = eNote;
            t3.FontSize = 13 * S;
            t3.Height = 70 * S;
            t3.Margin = new Thickness(0, 4 * S, 0, 0);
            t3.Background = _th.bBtn;
            t3.Foreground = _th.bText;
            t3.BorderBrush = _th.bLine;
            t3.Padding = new Thickness(8 * S);
            t3.TextWrapping = TextWrapping.Wrap;
            t3.AcceptsReturn = true;
            sp.Children.Add(t3);
            sp.Children.Add(Txt("提示词：" + DlStr(it, "prompt"), 10, false, _th.bSub, 10));

            var okRow = new WrapPanel();
            okRow.Margin = new Thickness(0, 8 * S, 0, 0);
            okRow.Children.Add(Btn("保存", true, delegate
            {
                var p = new Dictionary<string, object>();
                p["id"] = id;
                p["title"] = t1.Text;
                p["tags"] = t2.Text;
                p["note"] = t3.Text;
                w.Close();
                DrawLibPost("/api/drawlib/edit", p, "已保存");
            }));
            var favBtn = Btn(DlStr(it, "fav").ToLower() == "true" ? "取消收藏" : "⭐ 收藏", false, delegate
            {
                var p = new Dictionary<string, object>();
                p["id"] = id;
                p["fav"] = DlStr(it, "fav").ToLower() != "true";
                w.Close();
                DrawLibPost("/api/drawlib/edit", p, "收藏状态改好了");
            });
            favBtn.Margin = new Thickness(8 * S, 0, 0, 0);
            okRow.Children.Add(favBtn);
            var cancel = Btn("取消", false, delegate { w.Close(); });
            cancel.Margin = new Thickness(8 * S, 0, 0, 0);
            okRow.Children.Add(cancel);
            sp.Children.Add(okRow);

            w.Content = sp;
            w.ShowDialog();
        }

        /// <summary>画图图库的 POST：body 走 base64 传，免得到处转义中文（照 ApiPost 的做法）。</summary>
        void DrawLibPost(string path, Dictionary<string, object> payload, string okText)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string body = new JavaScriptSerializer().Serialize(payload);
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                string cmd = "echo " + b64 + " | base64 -d > /tmp/dl.json; " +
                             "curl -s -m 25 -X POST -H 'Content-Type: application/json' " +
                             "--data-binary @/tmp/dl.json '" + ApiBase + path + "?token=" + Token + "'";
                string r = Ssh.Run(_cfg, cmd, 45);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool ok = r.IndexOf("\"ok\": true") >= 0 || r.IndexOf("\"ok\":true") >= 0;
                    if (_dlStatus != null) _dlStatus.Text = ok ? okText : ("失败：" + FirstLine(r));
                    DrawLibLoad();
                }));
            });
        }

        // ------------------------------------------------ 画图模板（2026-11-06 用户要求）
        // 用户原话：「把画图功能预设模板作为一个页面添加到wpf控制台，就是二次元q版电影水墨脱衣，
        // 这五个模板先加进去，然后得有增删改查功能」。
        // 数据在服务器 `data/draw_styles.json`，插件按 mtime **热读** —— 这里改完，群里下一条
        // `/画图` 就生效，不用重启 fishbot（也不用动代码）。
        // 接口：GET /api/draw_styles · POST /api/draw_styles/save | /del | /reset
        readonly System.Collections.Generic.List<Dictionary<string, object>> _dsItems
            = new System.Collections.Generic.List<Dictionary<string, object>>();
        StackPanel _dsHost;
        TextBlock _dsStatus;
        string _dsFlash = "";

        static string DsKindName(string kind)
        {
            if (kind == "char") return "角色风";
            if (kind == "scene") return "场景风";
            if (kind == "act") return "动作";
            if (kind == "wear") return "换装";
            return kind ?? "";
        }

        void BuildDrawStylesCard(StackPanel b)
        {
            b.Children.Add(Txt("群里的 `/画图 二次元 …` / `q版` / `电影` / `水墨` / `脱衣` 用的就是这里的模板。\n"
                               + "**名字就是关键词**（别名也认）；角色风互斥、场景风可叠加、动作（脱衣）是整段替换提示词。\n"
                               + "改完**不用重启机器人**：群里下一条 `/画图` 就按新模板走。",
                               11, false, _th.bSub, 2));

            var row = new WrapPanel();
            row.Margin = new Thickness(0, 10 * S, 0, 0);
            row.Children.Add(Btn("＋ 新增模板", true, delegate { DrawStyleEdit(null); }));
            var rf = Btn("刷新", false, delegate { DrawStylesLoad(); });
            rf.Margin = new Thickness(8 * S, 0, 0, 0);
            row.Children.Add(rf);
            var rs = Btn("恢复 5 个默认", false, delegate
            {
                if (MessageBox.Show("把 5 个内置模板写回服务器？\n（你自己加的、改过的都会被覆盖）",
                                    "恢复默认模板", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                    != MessageBoxResult.OK) return;
                DrawStylePost("/api/draw_styles/reset", new Dictionary<string, object>(),
                              "已恢复 5 个默认模板");
            });
            rs.Margin = new Thickness(8 * S, 0, 0, 0);
            row.Children.Add(rs);

            _dsStatus = Txt("正在读…", 10.5, false, _th.bSub, 0);
            _dsStatus.VerticalAlignment = VerticalAlignment.Center;
            _dsStatus.Margin = new Thickness(10 * S, 0, 0, 0);
            row.Children.Add(_dsStatus);
            b.Children.Add(row);

            _dsHost = new StackPanel();
            _dsHost.Margin = new Thickness(0, 10 * S, 0, 0);
            b.Children.Add(_dsHost);
            DrawStylesLoad();
        }

        void DrawStylesLoad()
        {
            if (_dsStatus != null && _dsFlash.Length == 0) _dsStatus.Text = "正在读…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/draw_styles?token=" + Token, 45);
                Dispatcher.BeginInvoke(new Action(delegate { DrawStylesApply(json); }));
            });
        }

        void DrawStylesApply(string json)
        {
            _dsItems.Clear();
            int total = 0;
            try
            {
                var o = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                if (o != null)
                {
                    if (o.ContainsKey("total")) total = Convert.ToInt32(o["total"]);
                    var arr = (o.ContainsKey("items") ? o["items"] : null) as object[];
                    if (arr != null)
                        foreach (var itv in arr)
                        {
                            var d = itv as Dictionary<string, object>;
                            if (d != null) _dsItems.Add(d);
                        }
                }
            }
            catch { }
            if (_dsStatus != null)
            {
                string msg = "共 " + (_dsItems.Count > 0 ? _dsItems.Count : total) + " 个模板";
                if (_dsFlash.Length > 0) { msg = _dsFlash + " · " + msg; _dsFlash = ""; }
                _dsStatus.Text = msg;
            }
            RenderDrawStyles();
        }

        void RenderDrawStyles()
        {
            if (_dsHost == null) return;
            _dsHost.Children.Clear();
            foreach (var it in _dsItems)
            {
                string id = DlStr(it, "id");
                string name = DlStr(it, "name");
                string kind = DlStr(it, "kind");
                string prompt = DlStr(it, "prompt");
                string negative = DlStr(it, "negative");
                string note = DlStr(it, "note");
                string size = DlStr(it, "size");
                bool on = DlStr(it, "enabled").ToLower() != "false";
                bool builtin = DlStr(it, "builtin").ToLower() == "true";
                bool inuse = DlStr(it, "in_use").ToLower() == "true";

                string words = name;
                try
                {
                    var wa = (it.ContainsKey("words") ? it["words"] : null) as object[];
                    if (wa != null && wa.Length > 0)
                    {
                        var lw = new System.Collections.Generic.List<string>();
                        foreach (var w in wa) if (w != null) lw.Add(Convert.ToString(w));
                        words = string.Join(" / ", lw.ToArray());
                    }
                }
                catch { }

                var card = new Border();
                card.Background = _th.bBtn;
                card.BorderBrush = _th.bLine;
                card.BorderThickness = new Thickness(1);
                card.CornerRadius = new CornerRadius(10);
                card.Padding = new Thickness(11 * S, 9 * S, 11 * S, 9 * S);
                card.Margin = new Thickness(0, 0, 0, 8 * S);

                var sp = new StackPanel();

                string head = "▸ " + name + "   · " + DsKindName(kind)
                              + (builtin ? " · 内置" : "") + (inuse ? "" : " · 自定义")
                              + (on ? "" : "   【已停用】");
                sp.Children.Add(Txt(head, 13, true, on ? _th.bText : _th.bSub, 0));

                var kw = Txt("关键词：" + words, 10.5, false, _th.bSub, 0);
                kw.Margin = new Thickness(14 * S, 3 * S, 0, 0);
                kw.TextWrapping = TextWrapping.Wrap;
                sp.Children.Add(kw);

                if (size.Length > 0)
                {
                    var sz = Txt("尺寸：" + size + "（不给就用默认 768x768）", 10.5, false, _th.bSub, 0);
                    sz.Margin = new Thickness(14 * S, 2 * S, 0, 0);
                    sp.Children.Add(sz);
                }

                string ps = prompt.Replace("\r", " ").Replace("\n", " ");
                if (ps.Length > 110) ps = ps.Substring(0, 110) + "…";
                var pt = Txt("提示词：" + (ps.Length > 0 ? ps : "（空 = 一个风格词都不顶）"),
                             10.5, false, _th.bSub, 0);
                pt.Margin = new Thickness(14 * S, 2 * S, 0, 0);
                pt.TextWrapping = TextWrapping.Wrap;
                sp.Children.Add(pt);

                if (negative.Trim().Length > 0)
                {
                    string ns2 = negative.Replace("\r", " ").Replace("\n", " ");
                    if (ns2.Length > 80) ns2 = ns2.Substring(0, 80) + "…";
                    var nt = Txt("负向：" + ns2, 10.5, false, _th.bSub, 0);
                    nt.Margin = new Thickness(14 * S, 2 * S, 0, 0);
                    nt.TextWrapping = TextWrapping.Wrap;
                    sp.Children.Add(nt);
                }

                if (note.Trim().Length > 0)
                {
                    var nt2 = Txt(note, 10, false, _th.bSub, 0);
                    nt2.Margin = new Thickness(14 * S, 2 * S, 0, 0);
                    nt2.TextWrapping = TextWrapping.Wrap;
                    sp.Children.Add(nt2);
                }

                var ops = new WrapPanel();
                ops.Margin = new Thickness(14 * S, 7 * S, 0, 0);

                var edit = Btn("改", false, delegate { DrawStyleEdit(it); });
                edit.Padding = new Thickness(12 * S, 2 * S, 12 * S, 2 * S);
                edit.FontSize = 10.5 * S;
                edit.MinWidth = 0;
                edit.MinHeight = 0;
                ops.Children.Add(edit);

                var tg = Btn(on ? "停用" : "启用", false, delegate { DrawStyleToggle(it); });
                tg.Margin = new Thickness(6 * S, 0, 0, 0);
                tg.Padding = new Thickness(12 * S, 2 * S, 12 * S, 2 * S);
                tg.FontSize = 10.5 * S;
                tg.MinWidth = 0;
                tg.MinHeight = 0;
                ops.Children.Add(tg);

                var del = Btn("删", false, delegate { DrawStyleDel(id, name); });
                del.Margin = new Thickness(6 * S, 0, 0, 0);
                del.Padding = new Thickness(12 * S, 2 * S, 12 * S, 2 * S);
                del.FontSize = 10.5 * S;
                del.MinWidth = 0;
                del.MinHeight = 0;
                ops.Children.Add(del);

                sp.Children.Add(ops);
                card.Child = sp;
                _dsHost.Children.Add(card);
            }
            if (_dsItems.Count == 0)
                _dsHost.Children.Add(Txt("服务器上一个模板都没有（`/画图` 会把描述原样发出去）。"
                                         + "点「恢复 5 个默认」就能回来。", 11.5, false, _th.bSub, 6));
        }

        void DrawStyleToggle(Dictionary<string, object> it)
        {
            var p = new Dictionary<string, object>();
            p["id"] = DlStr(it, "id");
            p["name"] = DlStr(it, "name");
            p["kind"] = DlStr(it, "kind");
            // 别名是数组：拼回逗号串再发（漏了这一步会把别名清空 —— 禁用/启用一次词就没了）
            var lw = new System.Collections.Generic.List<string>();
            try
            {
                var wa = (it.ContainsKey("aliases") ? it["aliases"] : null) as object[];
                if (wa != null) foreach (var x in wa) if (x != null) lw.Add(Convert.ToString(x));
            }
            catch { }
            p["aliases"] = string.Join(", ", lw.ToArray());
            p["prompt"] = DlStr(it, "prompt");
            p["negative"] = DlStr(it, "negative");
            p["size"] = DlStr(it, "size");
            p["note"] = DlStr(it, "note");
            p["anime_neg"] = DlStr(it, "anime_neg").ToLower() == "true";
            p["enabled"] = DlStr(it, "enabled").ToLower() == "false";
            DrawStylePost("/api/draw_styles/save", p, "已切换启用状态");
        }

        void DrawStyleDel(string id, string name)
        {
            if (MessageBox.Show("真的删掉模板「" + name + "」？\n群里再打这个词就不会换画风了（描述会原样发出去）。",
                                "删除画图模板", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                != MessageBoxResult.OK) return;
            DrawStylePost("/api/draw_styles/del",
                          new Dictionary<string, object> { { "id", id } },
                          "已删除「" + name + "」");
        }

        /// <summary>新增 / 编辑模板。`it` 为 null = 新增。</summary>
        void DrawStyleEdit(Dictionary<string, object> it)
        {
            bool isNew = it == null;
            string id = isNew ? "" : DlStr(it, "id");

            var w = new Window();
            w.Title = isNew ? "新增画图模板" : ("编辑画图模板 · " + DlStr(it, "name"));
            w.Width = 660 * S;
            w.SizeToContent = SizeToContent.Height;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            w.Owner = this;
            w.Background = _th.bCard;
            w.MaxHeight = 900 * S;

            var sp = new StackPanel();
            sp.Margin = new Thickness(16 * S);

            sp.Children.Add(Txt("名字（群里就打这个词，也是列表里的标题）", 11.5, false, _th.bSub, 0));
            var tName = new TextBox();
            tName.Text = isNew ? "" : DlStr(it, "name");
            tName.FontSize = 13 * S;
            tName.Height = 32 * S;
            tName.Margin = new Thickness(0, 4 * S, 0, 0);
            tName.Background = _th.bBtn;
            tName.Foreground = _th.bText;
            tName.BorderBrush = _th.bLine;
            tName.Padding = new Thickness(8 * S, 0, 8 * S, 0);
            sp.Children.Add(tName);

            sp.Children.Add(Txt("种类", 11.5, false, _th.bSub, 10));
            var kindBox = new ComboBox();
            kindBox.FontSize = 12.5 * S;
            kindBox.Height = 30 * S;
            kindBox.Margin = new Thickness(0, 4 * S, 0, 0);
            kindBox.Background = _th.bBtn;
            kindBox.Foreground = _th.bText;
            kindBox.BorderBrush = _th.bLine;
            ComboStyle(kindBox);
            kindBox.Items.Add("角色风（和别的角色风互斥，写两个以最后一个为准）");
            kindBox.Items.Add("场景风（可以和别的场景风叠加）");
            kindBox.Items.Add("动作（整段替换提示词，不顶风格词；比如脱衣）");
            kindBox.Items.Add("换装（追加服装描述，不换画风；比如兔女郎 / JK / 女仆装）");
            string curKind = isNew ? "char" : DlStr(it, "kind");
            kindBox.SelectedIndex = curKind == "scene" ? 1 : (curKind == "act" ? 2
                                  : (curKind == "wear" ? 3 : 0));
            sp.Children.Add(kindBox);

            sp.Children.Add(Txt("别名（逗号分隔；比如二次元可以再认 动漫, 日系, anime）", 11.5, false, _th.bSub, 10));
            var tAlias = new TextBox();
            try
            {
                var wa = (isNew ? null : (it.ContainsKey("aliases") ? it["aliases"] : null)) as object[];
                if (wa != null)
                {
                    var lw = new System.Collections.Generic.List<string>();
                    foreach (var x in wa) if (x != null) lw.Add(Convert.ToString(x));
                    tAlias.Text = string.Join(", ", lw.ToArray());
                }
            }
            catch { }
            tAlias.FontSize = 13 * S;
            tAlias.Height = 32 * S;
            tAlias.Margin = new Thickness(0, 4 * S, 0, 0);
            tAlias.Background = _th.bBtn;
            tAlias.Foreground = _th.bText;
            tAlias.BorderBrush = _th.bLine;
            tAlias.Padding = new Thickness(8 * S, 0, 8 * S, 0);
            sp.Children.Add(tAlias);

            sp.Children.Add(Txt("正向提示词（顶到提示词最前面；动作用它整段替换）", 11.5, false, _th.bSub, 10));
            var tPrompt = new TextBox();
            tPrompt.Text = isNew ? "" : DlStr(it, "prompt");
            tPrompt.FontSize = 12.5 * S;
            tPrompt.Height = 150 * S;
            tPrompt.Margin = new Thickness(0, 4 * S, 0, 0);
            tPrompt.Background = _th.bBtn;
            tPrompt.Foreground = _th.bText;
            tPrompt.BorderBrush = _th.bLine;
            tPrompt.Padding = new Thickness(8 * S);
            tPrompt.TextWrapping = TextWrapping.Wrap;
            tPrompt.AcceptsReturn = true;
            tPrompt.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sp.Children.Add(tPrompt);

            sp.Children.Add(Txt("负向提示词（cfg=1 的 turbo 基本不吃，兜底用；可空）", 11.5, false, _th.bSub, 10));
            var tNeg = new TextBox();
            tNeg.Text = isNew ? "" : DlStr(it, "negative");
            tNeg.FontSize = 12.5 * S;
            tNeg.Height = 66 * S;
            tNeg.Margin = new Thickness(0, 4 * S, 0, 0);
            tNeg.Background = _th.bBtn;
            tNeg.Foreground = _th.bText;
            tNeg.BorderBrush = _th.bLine;
            tNeg.Padding = new Thickness(8 * S);
            tNeg.TextWrapping = TextWrapping.Wrap;
            tNeg.AcceptsReturn = true;
            sp.Children.Add(tNeg);

            sp.Children.Add(Txt("出图尺寸（留空 = 768x768；电影是 1376x576，高度要能被 16 整除）",
                                11.5, false, _th.bSub, 10));
            var tSize = new TextBox();
            tSize.Text = isNew ? "" : DlStr(it, "size");
            tSize.FontSize = 13 * S;
            tSize.Height = 32 * S;
            tSize.Margin = new Thickness(0, 4 * S, 0, 0);
            tSize.Background = _th.bBtn;
            tSize.Foreground = _th.bText;
            tSize.BorderBrush = _th.bLine;
            tSize.Padding = new Thickness(8 * S, 0, 8 * S, 0);
            sp.Children.Add(tSize);

            sp.Children.Add(Txt("备注（随便写，列表里会显示）", 11.5, false, _th.bSub, 10));
            var tNote = new TextBox();
            tNote.Text = isNew ? "" : DlStr(it, "note");
            tNote.FontSize = 12.5 * S;
            tNote.Height = 32 * S;
            tNote.Margin = new Thickness(0, 4 * S, 0, 0);
            tNote.Background = _th.bBtn;
            tNote.Foreground = _th.bText;
            tNote.BorderBrush = _th.bLine;
            tNote.Padding = new Thickness(8 * S, 0, 8 * S, 0);
            sp.Children.Add(tNote);

            var cbOn = new CheckBox();
            cbOn.Content = "启用（关了 = 群里打这个词不再换画风）";
            cbOn.FontSize = 12 * S;
            cbOn.Foreground = _th.bText;
            cbOn.Margin = new Thickness(0, 10 * S, 0, 0);
            cbOn.IsChecked = isNew || DlStr(it, "enabled").ToLower() != "false";
            sp.Children.Add(cbOn);

            var cbAnime = new CheckBox();
            cbAnime.Content = "顶「排除真人 / 写实」负向（角色风一般要勾；场景风不用）";
            cbAnime.FontSize = 12 * S;
            cbAnime.Foreground = _th.bText;
            cbAnime.Margin = new Thickness(0, 4 * S, 0, 0);
            cbAnime.IsChecked = isNew ? true : DlStr(it, "anime_neg").ToLower() == "true";
            sp.Children.Add(cbAnime);

            var okRow = new WrapPanel();
            okRow.Margin = new Thickness(0, 12 * S, 0, 0);
            okRow.Children.Add(Btn("保存", true, delegate
            {
                string nm = (tName.Text ?? "").Trim();
                if (nm.Length == 0) { MessageBox.Show("名字不能空（它同时也是群里打的关键词）。"); return; }
                var p = new Dictionary<string, object>();
                p["id"] = id;
                p["name"] = nm;
                p["kind"] = kindBox.SelectedIndex == 1 ? "scene"
                            : (kindBox.SelectedIndex == 2 ? "act"
                            : (kindBox.SelectedIndex == 3 ? "wear" : "char"));
                p["aliases"] = tAlias.Text ?? "";
                p["prompt"] = tPrompt.Text ?? "";
                p["negative"] = tNeg.Text ?? "";
                p["size"] = (tSize.Text ?? "").Trim();
                p["note"] = tNote.Text ?? "";
                p["enabled"] = cbOn.IsChecked == true;
                p["anime_neg"] = cbAnime.IsChecked == true;
                w.Close();
                DrawStylePost("/api/draw_styles/save", p,
                              (isNew ? "已新增「" : "已保存「") + nm + "」（群里下一条 /画图 就生效）");
            }));
            var cancel = Btn("取消", false, delegate { w.Close(); });
            cancel.Margin = new Thickness(8 * S, 0, 0, 0);
            okRow.Children.Add(cancel);
            sp.Children.Add(okRow);

            var sc = new ScrollViewer();
            sc.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sc.Content = sp;
            w.Content = sc;
            w.ShowDialog();
        }

        /// <summary>画图模板的 POST（和 DrawLibPost 一样：body 走 base64，中文不用转义）。</summary>
        void DrawStylePost(string path, Dictionary<string, object> payload, string okText)
        {
            if (_dsStatus != null) _dsStatus.Text = "正在保存…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string body = new JavaScriptSerializer().Serialize(payload);
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                string cmd = "echo " + b64 + " | base64 -d > /tmp/ds.json; " +
                             "curl -s -m 25 -X POST -H 'Content-Type: application/json' " +
                             "--data-binary @/tmp/ds.json '" + ApiBase + path + "?token=" + Token + "'";
                string r = Ssh.Run(_cfg, cmd, 45);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool ok = r.IndexOf("\"ok\": true") >= 0 || r.IndexOf("\"ok\":true") >= 0;
                    _dsFlash = ok ? okText : ("失败：" + FirstLine(r));
                    DrawStylesLoad();
                }));
            });
        }

        // ------------------------------------------------ 画图（本地 ComfyUI）
        // 2026-11-04 用户：「帮我把本地的 comfyui 接入肥鱼机器人」+「控件按钮加到电脑端的控制台里」。
        //
        // 链路：群里的 `/画图` → 服务器上的 fishbot → 127.0.0.1:18188（隧道）→
        //       这台电脑的网关 127.0.0.1:8190 → ComfyUI 127.0.0.1:8188（Qwen-Image 2.1 Turbo）
        // 隧道由 04-脚本\工具\comfy_tunnel.ps1 看门狗维持（断了 5 秒自动重连），这里只负责启停 + 看状态。
        TextBlock _drawStatus, _drawInfo;
        string _tunnelTail = "";   // 启动隧道后从 05-日志\comfy_tunnel.log 抓的尾巴（2026-11-11）

        /// <summary>隧道脚本自己的日志最后 n 行（脚本无论成功失败都会写）。</summary>
        string TunnelLogTail(int n)
        {
            try
            {
                string f = RepoFile(@"05-日志\comfy_tunnel.log");
                if (!File.Exists(f)) return "";
                var lines = File.ReadAllLines(f);
                if (lines.Length == 0) return "";
                int k = Math.Max(0, lines.Length - n);
                var sb = new StringBuilder();
                for (int i = k; i < lines.Length; i++) sb.AppendLine(lines[i]);
                return sb.ToString().TrimEnd();
            }
            catch { return ""; }
        }

        void BuildDrawCard(StackPanel b)
        {
            b.Children.Add(Txt("群里的 /画图 <描述> 用的是这台电脑的显卡（ComfyUI · Qwen-Image Turbo，一张约 40 秒；以图生图约 80 秒）。\n"
                               + "服务器在腾讯云，够不着家里的路由器，所以靠一条反向 SSH 隧道：\n"
                               + "本机 127.0.0.1:8190 → 服务器 127.0.0.1:18188。隧道断了或者电脑关机，机器人就画不出来。\n"
                               + "开关在群里：/画图 on / /画图 off（只有主人能开，默认全关）。",
                               11, false, _th.bSub, 2));

            _drawStatus = Txt("读取中…", 12, true, _th.bText, 6);
            b.Children.Add(_drawStatus);
            _drawInfo = Txt("", 11, false, _th.bSub, 4);
            b.Children.Add(_drawInfo);

            var r1 = Row();
            r1.Margin = new Thickness(0, 10 * S, 0, 0);
            r1.Children.Add(Btn("启动隧道", true, delegate { DrawLocal(@"04-脚本\工具\comfy_tunnel.ps1", ""); }));
            r1.Children.Add(Btn("停止隧道", false, delegate { DrawLocal(@"04-脚本\工具\comfy_tunnel.ps1", "-Stop"); }));
            r1.Children.Add(Btn("刷新状态", false, delegate { LoadDraw(); }));
            b.Children.Add(r1);

            var r2 = Row();
            r2.Margin = new Thickness(0, 8 * S, 0, 0);
            r2.Children.Add(Btn("开机自启（注册计划任务）", false, delegate { DrawLocal(@"04-脚本\工具\comfy_tunnel.ps1", "-Install"); }));
            r2.Children.Add(Btn("取消开机自启", false, delegate { DrawLocal(@"04-脚本\工具\comfy_tunnel.ps1", "-Uninstall"); }));
            r2.Children.Add(Btn("启动 / 重启 ComfyUI 后端", false, delegate { DrawRestartComfy(); }));
            b.Children.Add(r2);

            b.Children.Add(Txt("「启动隧道」会拉起看门狗（隐藏窗口跑），它自己会重连；想让电脑一登录就自动起，点「开机自启」。\n"
                               + "「启动 / 重启 ComfyUI 后端」既能拉起没起来的后端，也能在装完新模型 / 显存卡住时清一遍"
                               + "（走启动器 127.0.0.1:8290）。它要先停再启，一次 60~90 秒，别重复点。",
                               10.5, false, _th.bSub, 8));
            LoadDraw();
        }

        /// <summary>跑隧道脚本（本地 pwsh，隐藏窗口；-Stop / -Install / -Uninstall 走参数）。</summary>
        void DrawLocal(string rel, string extra)
        {
            string script = RepoFile(rel);
            if (!File.Exists(script)) { if (_drawInfo != null) _drawInfo.Text = "找不到脚本：" + script; return; }
            try
            {
                string pwsh = @"C:\Program Files\PowerShell\7\pwsh.exe";
                if (!File.Exists(pwsh)) pwsh = "pwsh";
                var psi = new ProcessStartInfo(pwsh,
                    (extra.Length > 0 ? "-NoProfile -File \"" + script + "\" " + extra
                                      : "-NoProfile -WindowStyle Hidden -File \"" + script + "\""));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                if (extra.Length > 0)
                {
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    using (var p = Process.Start(psi))
                    {
                        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                        p.WaitForExit(60000);
                        if (_drawInfo != null) _drawInfo.Text = (o ?? "").Trim().Replace("\r", "");
                    }
                    LoadDraw();
                }
                else
                {
                    Process.Start(psi);
                    // 2026-11-11：这条分支以前不管脚本是死是活都写「隧道已拉起」，
                    // 而脚本自己把原因写进 05-日志\comfy_tunnel.log（还是旧目录），用户只能看到"没反应"。
                    // 现在 8 秒后把脚本日志最后几行直接贴出来，成功失败都看得见。
                    _tunnelTail = "";
                    if (_drawInfo != null) _drawInfo.Text = "已请求启动隧道（看门狗后台跑）；8 秒后读日志 + 刷新状态…";
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                    t.Tick += delegate { t.Stop(); _tunnelTail = TunnelLogTail(4); LoadDraw(); };
                    t.Start();
                }
            }
            catch (Exception ex) { if (_drawInfo != null) _drawInfo.Text = "操作失败：" + ex.Message; }
        }

        /// <summary>启动 / 重启本地 ComfyUI 后端（启动器控制口 127.0.0.1:8290）。
        /// 2026-11-11 修「一键启动没反应」：原来 req.Timeout=60000，实测启动器一次重启要
        /// 68 秒（停 1.3s + 确认端口 + 等显存 + 重新拉起 65.7s）→ 客户端 60 秒就抛超时，
        /// 用户看到「重启失败：操作超时」，其实后端已经起来了。现在给 300 秒 + 读回执 + 后台跑，
        /// 不再卡 UI。</summary>
        void DrawRestartComfy()
        {
            if (_drawInfo != null) _drawInfo.Text = "已让启动器重启 ComfyUI，等它起来（70~90 秒，别重复点）…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string msg;
                try
                {
                    var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("http://127.0.0.1:8290/restart-comfy");
                    req.Method = "POST";
                    req.Timeout = 300000;                 // 关键：实测 68 秒，60 秒必假失败
                    req.ReadWriteTimeout = 300000;
                    req.ContentLength = 0;
                    using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        msg = RestartReportText(sr.ReadToEnd());
                    }
                }
                catch (System.Net.WebException wex)
                {
                    string detail = "";
                    try
                    {
                        if (wex.Response != null)
                            using (var sr = new StreamReader(wex.Response.GetResponseStream(), Encoding.UTF8))
                                detail = sr.ReadToEnd();
                    }
                    catch { }
                    if (wex.Status == System.Net.WebExceptionStatus.Timeout)
                        msg = "启动器 5 分钟没回话（它可能还在忙）。点「刷新状态」看结果；"
                            + "真起不来就跑 04-脚本\\工具\\restart-comfy.ps1 -Force。";
                    else if (wex.Response != null && detail.Length > 0)
                        msg = "重启失败：" + FirstLine(detail);
                    else
                        msg = "重启失败：" + wex.Message;
                }
                catch (Exception ex) { msg = "重启失败：" + ex.Message; }

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_drawInfo != null) _drawInfo.Text = msg;
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
                    t.Tick += delegate { t.Stop(); LoadDraw(); };
                    t.Start();
                }));
            });
        }

        /// <summary>把启动器 /restart-comfy 的回执（JSON）翻成一句人话。</summary>
        string RestartReportText(string json)
        {
            try
            {
                var o = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                if (o == null) return FirstLine(json);
                bool ok = o.ContainsKey("ok") && Convert.ToBoolean(o["ok"]);
                double sec = o.ContainsKey("durationMs") ? Convert.ToInt32(o["durationMs"]) / 1000.0 : 0;
                int freed = o.ContainsKey("freedMb") ? Convert.ToInt32(o["freedMb"]) : 0;
                string url = o.ContainsKey("comfyUrl") ? Convert.ToString(o["comfyUrl"]) : "";
                string err = o.ContainsKey("error") ? Convert.ToString(o["error"]) : "";
                if (ok)
                    return "ComfyUI 已重启：" + url + "，用时 " + Math.Round(sec, 1) + " 秒，释放显存 " + freed + " MB。";
                return "重启失败：" + (err.Length > 0 ? err : json);
            }
            catch { return FirstLine(json); }
        }

        void LoadDraw()
        {
            if (_drawStatus != null) _drawStatus.Text = "读取中…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/draw/status?token=" + Token, 30);
                Dispatcher.BeginInvoke(new Action(delegate { ApplyDraw(json); }));
            });
        }

        void ApplyDraw(string json)
        {
            try
            {
                var o = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                if (o == null) { if (_drawStatus != null) _drawStatus.Text = "状态读取失败：" + FirstLine(json); return; }
                bool tun = o.ContainsKey("tunnel") && Convert.ToBoolean(o["tunnel"]);
                bool comfy = o.ContainsKey("comfy") && Convert.ToBoolean(o["comfy"]);
                int used = o.ContainsKey("used") ? Convert.ToInt32(o["used"]) : 0;
                int daily = o.ContainsKey("daily") ? Convert.ToInt32(o["daily"]) : 30;
                int price = o.ContainsKey("price") ? Convert.ToInt32(o["price"]) : 250;
                int onN = o.ContainsKey("chats_on_count") ? Convert.ToInt32(o["chats_on_count"]) : 0;
                string err = o.ContainsKey("error") ? Convert.ToString(o["error"]) : "";
                string gw = o.ContainsKey("gateway_version") ? Convert.ToString(o["gateway_version"]) : "";
                if (_drawStatus != null)
                {
                    _drawStatus.Text = tun
                        ? (comfy ? "● 隧道通 · ComfyUI 在线（机器人现在能画图）"
                                 : "● 隧道通 · 但 ComfyUI 没起来 —— 点下面「启动 / 重启 ComfyUI 后端」")
                        : "○ 隧道不通 —— 机器人画不出来（点「启动隧道」）";
                    _drawStatus.Foreground = tun && comfy ? _th.bAccent : _th.bSub;
                }
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("今日已画 " + used + " / " + daily + " 张（还剩 " + Math.Max(0, daily - used) + " 张），每张 " + price + " 鲸圆券");
                sb.AppendLine("开了画图的会话：" + onN + " 个");
                if (o.ContainsKey("chats_on") && o["chats_on"] is object[])
                {
                    int k = 0;
                    foreach (var itv in (object[])o["chats_on"])
                    {
                        var it = itv as Dictionary<string, object>;
                        if (it == null) continue;
                        string ck = it.ContainsKey("chat") ? Convert.ToString(it["chat"]) : "";
                        string at = it.ContainsKey("at") ? Convert.ToString(it["at"]) : "";
                        if (k++ >= 12) { sb.Append("  …还有 " + (onN - 12) + " 个"); break; }
                        sb.Append("  · " + ck.Replace("group:", "群 ") + (at.Length > 0 ? "（" + at + " 开的）" : "") + "\n");
                    }
                }
                if (!tun) sb.AppendLine("隧道地址 " + (o.ContainsKey("url") ? Convert.ToString(o["url"]) : "") + " 连不上" + (err.Length > 0 ? "（" + err + "）" : ""));
                else if (gw.Length > 0) sb.AppendLine("网关版本 " + gw);
                if (_drawInfo != null)
                {
                    _drawInfo.Text = sb.ToString().TrimEnd();
                    if (_tunnelTail.Length > 0) _drawInfo.Text += "\n最近隧道日志：\n" + _tunnelTail;
                }
            }
            catch (Exception ex) { if (_drawStatus != null) _drawStatus.Text = "解析失败：" + ex.Message; }
        }

        // ------------------------------------------------ 她的声音（每个人格一套）
        // 2026-10-30 用户：「三个人格的语音音色都要独立，可以分别在控制台里配置」。
        // 数据存在注册表 data/personas.json（插件热读）：voice_bailian / voice_bailian_model。
        // 默认人格没配 → 回落 .env；**别的人格没配就是没音色**，不会借肥鱼的克隆音色。
        string _ctlSubId = "";

        void BuildVoiceCard(StackPanel b)
        {
            b.Children.Add(Txt("每个角色一条嗓子：音色写「百炼预置名」（Cherry / Serena…）"
                               + "或者「复刻音色的 voice_id」（qwen-audio-…）。\n"
                               + "复刻音色必须配 qwen-audio-3.1-tts-flash 这个模型，预置音色配 qwen3-tts-flash。"
                               + "改完下一条消息生效，不用重启。", 11, false, _th.bSub, 2));
            _vcList = new StackPanel();
            _vcList.Margin = new Thickness(0, 6 * S, 0, 0);
            b.Children.Add(_vcList);
            _vcStatus = Txt("读取中…", 11.5, false, _th.bSub, 4);
            b.Children.Add(_vcStatus);
            var r = new WrapPanel();
            r.Margin = new Thickness(0, 8 * S, 0, 0);
            r.Children.Add(Btn("重新读取", false, delegate { LoadVoiceOptions(); }));
            b.Children.Add(r);
            LoadVoiceOptions();
        }

        StackPanel _vcList;
        TextBlock _vcStatus;

        // ------------------------------------------------ 机器人名单（2026-10-30：手动维护）
        // 用户：「识别错误了，把人机识别改为我手动在控制台添加吧」——自动识别已关，
        // 名单只来自这个页面（和 /bot QQ号）。插件读同一个 data/bots.json。
        StackPanel _botList;
        TextBlock _botStatus;
        TextBox _botInput;

        void BuildBotListCard(StackPanel b)
        {
            b.Children.Add(Txt("名单里的号被当成机器人：她不会主动接它的话；它 @ 她 / 叫她名字时，"
                               + "只用一句专属回复（黑鱼的 BOT_AT_LINES 那套），不走 AI。\n"
                               + "⚠️ 自动识别已经关掉了（2026-10-30 误伤过真人），所以这里加一条就是一条。",
                               11, false, _th.bSub, 2));
            _botList = new StackPanel();
            _botList.Margin = new Thickness(0, 6 * S, 0, 0);
            b.Children.Add(_botList);
            _botStatus = Txt("读取中…", 11.5, false, _th.bSub, 4);
            b.Children.Add(_botStatus);
            var row = new WrapPanel();
            row.Margin = new Thickness(0, 8 * S, 0, 0);
            _botInput = ApInput("机器人的 QQ 号（纯数字）", 190);
            row.Children.Add(_botInput);
            row.Children.Add(Btn("加进名单", true, delegate { BotAdd(); }));
            row.Children.Add(Btn("重新读取", false, delegate { LoadBots(); }));
            b.Children.Add(row);
            LoadBots();
        }

        void LoadBots()
        {
            if (_botStatus != null) _botStatus.Text = "读取中…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/bots?token=" + Token, 30);
                Dispatcher.BeginInvoke(new Action(delegate { FillBots(json); }));
            });
        }

        void FillBots(string json)
        {
            if (_botList == null) return;
            _botList.Children.Clear();
            try
            {
                var o = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                var arr = o != null && o.ContainsKey("items") ? o["items"] as object[] : null;
                if (arr == null)
                {
                    if (_botStatus != null) _botStatus.Text = "拉不到名单：" + FirstLine(json);
                    return;
                }
                if (_botStatus != null)
                    _botStatus.Text = arr.Length == 0 ? "名单是空的（还没标过机器人）" : ("共 " + arr.Length + " 个");
                if (arr.Length == 0) return;
                foreach (var it in arr)
                {
                    var d = it as Dictionary<string, object>;
                    if (d == null) continue;
                    string uid = d.ContainsKey("uid") ? Convert.ToString(d["uid"]) : "";
                    string why = d.ContainsKey("why") ? Convert.ToString(d["why"]) : "";
                    string at = d.ContainsKey("at") ? Convert.ToString(d["at"]) : "";
                    var r = new WrapPanel();
                    r.Margin = new Thickness(0, 2 * S, 0, 2 * S);
                    var lb = Txt(uid + (string.IsNullOrEmpty(why) ? "" : "  ·  " + why)
                                 + (string.IsNullOrEmpty(at) ? "" : "（" + at + "）"), 12, false, _th.bText, 3);
                    lb.Width = 420 * S;
                    r.Children.Add(lb);
                    string u2 = uid;
                    var del = Btn("移除", false, delegate { BotRemove(u2); });
                    del.Padding = new Thickness(10 * S, 2 * S, 10 * S, 2 * S);
                    del.FontSize = 11.5 * S;
                    r.Children.Add(del);
                    _botList.Children.Add(r);
                }
            }
            catch (Exception ex)
            {
                if (_botStatus != null) _botStatus.Text = "解析失败：" + ex.Message;
            }
        }

        void BotAdd()
        {
            string uid = (_botInput != null ? _botInput.Text : "").Trim();
            uid = uid.TrimStart('@');
            bool digits = uid.Length > 0;
            foreach (char c in uid) { if (!char.IsDigit(c)) { digits = false; break; } }
            if (!digits)
            {
                if (_botStatus != null) _botStatus.Text = "填纯数字 QQ 号，例如 3866990744";
                return;
            }
            BotPost(uid, "add");
        }

        void BotRemove(string uid) { BotPost(uid, "remove"); }

        void BotPost(string uid, string action)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                var payload = new Dictionary<string, object>();
                payload["action"] = action;
                payload["uid"] = uid;
                string body = new JavaScriptSerializer().Serialize(payload);
                string r = PostJson("/api/bots?token=" + Token, body);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        var o = new JavaScriptSerializer().DeserializeObject(r) as Dictionary<string, object>;
                        bool ok = o != null && o.ContainsKey("ok") && Convert.ToBoolean(o["ok"]);
                        if (_botStatus != null)
                            _botStatus.Text = (o != null && o.ContainsKey("msg"))
                                ? Convert.ToString(o["msg"]) : (ok ? "好了" : FirstLine(r));
                    }
                    catch { if (_botStatus != null) _botStatus.Text = FirstLine(r); }
                    if (_botInput != null && action == "add") _botInput.Text = "";
                    LoadBots();
                }));
            });
        }

        void LoadVoiceOptions()
        {
            if (_vcStatus != null) _vcStatus.Text = "读取中…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/voice/options?token=" + Token, 30);
                Dispatcher.BeginInvoke(new Action(delegate { FillVoiceOptions(json); }));
            });
        }

        void FillVoiceOptions(string json)
        {
            if (_vcList == null) return;
            _vcList.Children.Clear();
            try
            {
                var ser = new JavaScriptSerializer();
                var o = ser.DeserializeObject(json) as Dictionary<string, object>;
                var arr = o != null && o.ContainsKey("items") ? o["items"] as object[] : null;
                if (arr == null)
                {
                    if (_vcStatus != null) _vcStatus.Text = "拉不到音色表：" + FirstLine(json);
                    return;
                }
                // 预置音色（点一下填进输入框）
                var presets = o.ContainsKey("presets") ? o["presets"] as object[] : null;
                var engines = o.ContainsKey("engine") ? Convert.ToString(o["engine"]) : "";
                if (_vcStatus != null)
                    _vcStatus.Text = "引擎：" + engines + " · " + arr.Length + " 个人格"
                        + (presets != null ? " · 预置音色 " + presets.Length + " 个" : "");
                foreach (var it in arr)
                {
                    var d = it as Dictionary<string, object>;
                    if (d == null) continue;
                    _vcList.Children.Add(VoiceRow(d, presets));
                }
            }
            catch (Exception ex)
            {
                if (_vcStatus != null) _vcStatus.Text = "解析失败：" + ex.Message;
            }
        }

        Border VoiceRow(Dictionary<string, object> d, object[] presets)
        {
            string pid = d.ContainsKey("id") ? Convert.ToString(d["id"]) : "";
            string pname = d.ContainsKey("name") ? Convert.ToString(d["name"]) : pid;
            bool vOn = !d.ContainsKey("voice") || Convert.ToBoolean(d["voice"]);
            string vb = d.ContainsKey("voice_bailian") ? Convert.ToString(d["voice_bailian"]) : "";
            string vm = d.ContainsKey("voice_bailian_model") ? Convert.ToString(d["voice_bailian_model"]) : "";
            string eff = d.ContainsKey("effective") ? Convert.ToString(d["effective"]) : "";
            string effModel = d.ContainsKey("effective_model") ? Convert.ToString(d["effective_model"]) : "";
            string src = d.ContainsKey("source") ? Convert.ToString(d["source"]) : "";

            StackPanel body;
            var card = Card(pname + "（" + pid + "）", out body);
            card.Margin = new Thickness(0, 0, 0, 8 * S);

            string srcTxt = src == "env" ? "吃 .env 的默认音色" : (string.IsNullOrEmpty(eff) ? "没有音色（/留言 会说没配音色）" : "注册表");
            if (eff == "__none__") srcTxt = "没有音色";
            body.Children.Add(Txt("现在实际用：" + (string.IsNullOrEmpty(eff) ? "（无）" : eff)
                                  + "  ·  模型：" + (string.IsNullOrEmpty(effModel) ? "（无）" : effModel)
                                  + "  ·  " + srcTxt, 10.5, false, _th.bSub, 0));

            var rowIn = new WrapPanel();
            rowIn.Margin = new Thickness(0, 8 * S, 0, 0);
            var tbVoice = ApInput("音色：Cherry 或 qwen-audio-… 的 voice_id", 300);
            tbVoice.Text = vb;
            var tbModel = ApInput("模型：qwen3-tts-flash", 190);
            tbModel.Text = vm;
            rowIn.Children.Add(tbVoice);
            rowIn.Children.Add(tbModel);
            body.Children.Add(rowIn);

            var rowPre = new WrapPanel();
            rowPre.Margin = new Thickness(0, 2 * S, 0, 0);
            if (presets != null)
            {
                rowPre.Children.Add(Txt("预置：", 11, false, _th.bSub, 4));
                foreach (var p in presets)
                {
                    var pd = p as Dictionary<string, object>;
                    if (pd == null) continue;
                    string pv = pd.ContainsKey("id") ? Convert.ToString(pd["id"]) : "";
                    string pm = pd.ContainsKey("model") ? Convert.ToString(pd["model"]) : "qwen3-tts-flash";
                    if (string.IsNullOrEmpty(pv)) continue;
                    var pb = Btn(pv, false, null);
                    pb.Padding = new Thickness(10 * S, 3 * S, 10 * S, 3 * S);
                    pb.FontSize = 11.5 * S;
                    pb.Click += delegate { tbVoice.Text = pv; tbModel.Text = pm; };
                    rowPre.Children.Add(pb);
                }
            }
            body.Children.Add(rowPre);

            var rowBtn = new WrapPanel();
            rowBtn.Margin = new Thickness(0, 8 * S, 0, 0);
            var btnVoiceOn = Btn(vOn ? "主动语音：开" : "主动语音：关", false, null);
            btnVoiceOn.Click += delegate
            {
                vOn = !vOn;
                btnVoiceOn.Content = vOn ? "主动语音：开" : "主动语音：关";
                btnVoiceOn.Background = vOn ? _th.bAccent : _th.bBtn;
                btnVoiceOn.Foreground = vOn ? Brushes.White : _th.bText;
            };
            btnVoiceOn.Background = vOn ? _th.bAccent : _th.bBtn;
            btnVoiceOn.Foreground = vOn ? Brushes.White : _th.bText;
            btnVoiceOn.ToolTip = "关 = 这个角色不主动甩语音（塔菲/黑鱼现在是关的）；/留言 只看有没有配音色。";
            rowBtn.Children.Add(btnVoiceOn);
            rowBtn.Children.Add(Btn("保存", true, delegate { SaveVoice(pid, vOn, tbVoice.Text, tbModel.Text); }));
            rowBtn.Children.Add(Btn("试听", false, delegate { PreviewVoice(pid, pname); }));
            rowBtn.Children.Add(Btn("清空（不配音色）", false, delegate
            {
                tbVoice.Text = "";
                tbModel.Text = "";
                SaveVoice(pid, vOn, "", "");
            }));
            body.Children.Add(rowBtn);

            body.Children.Add(Txt("提示：复刻音色要跟克隆时的模型一致（qwen-audio-3.1-tts-flash）；"
                                  + "预置音色用 qwen3-tts-flash。留空 = 这个角色没音色。",
                                  10.5, false, _th.bSub, 4));
            return card;
        }

        void SaveVoice(string pid, bool voiceOn, string voice, string model)
        {
            voice = (voice ?? "").Trim();
            model = (model ?? "").Trim();
            ThreadPool.QueueUserWorkItem(delegate
            {
                var payload = new Dictionary<string, object>();
                payload["id"] = pid;
                payload["voice"] = voiceOn;
                payload["voice_bailian"] = voice;
                payload["voice_bailian_model"] = model;
                string body = new JavaScriptSerializer().Serialize(payload);
                string r = PostJson("/api/personas/edit?token=" + Token, body);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool ok = r.IndexOf("\"ok\": true") >= 0 || r.IndexOf("\"ok\":true") >= 0;
                    if (_vcStatus != null)
                        _vcStatus.Text = ok
                            ? ("已保存 " + pid + "：" + (string.IsNullOrEmpty(voice) ? "（没音色）" : voice)
                               + (string.IsNullOrEmpty(model) ? "" : " / " + model) + " · 下一条消息生效")
                            : ("保存失败：" + FirstLine(r));
                    if (ok) LoadVoiceOptions();
                }));
            });
        }

        void PreviewVoice(string pid, string pname)
        {
            // 试听走服务器合成，丢给默认浏览器播（mp3 单文件，不经过协议端）
            string url = ApiBase + "/api/voice/preview?id=" + Uri.EscapeDataString(pid)
                         + "&text=" + Uri.EscapeDataString("你好，我是" + pname + "。")
                         + "&token=" + Token;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/voice/options?token=" + Token, 20);   // 顺手确认服务在线
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        // 8090 只在服务器本机可直连，本地要经 SSH 转发 → 用 plink 把 mp3 落到临时目录再播
                        string cmd = "curl -s -m 90 '" + ApiBase + "/api/voice/preview?id="
                                     + Uri.EscapeDataString(pid) + "&text="
                                     + Uri.EscapeDataString("你好，我是" + pname + "。")
                                     + "&token=" + Token + "' -o /tmp/vc.mp3; ls -l /tmp/vc.mp3";
                        if (_vcStatus != null) _vcStatus.Text = "试听中…（合成要 5~20 秒）";
                        Ssh.Run(_cfg, cmd, 120);
                        string b64 = Ssh.Run(_cfg, "base64 -w0 /tmp/vc.mp3", 60).Trim();
                        if (string.IsNullOrEmpty(b64) || b64.Length < 100)
                        {
                            if (_vcStatus != null) _vcStatus.Text = "试听失败（没拿到音频）";
                            return;
                        }
                        string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                            "fish_voice_" + pid + ".mp3");
                        System.IO.File.WriteAllBytes(tmp, Convert.FromBase64String(b64));
                        try { Process.Start(tmp); }
                        catch { Process.Start("explorer.exe", "\"" + tmp + "\""); }
                        if (_vcStatus != null) _vcStatus.Text = "试听文件：" + tmp;
                    }
                    catch (Exception ex)
                    {
                        if (_vcStatus != null) _vcStatus.Text = "试听失败：" + ex.Message;
                    }
                }));
            });
        }

        /// <summary>界面自检：-shot botctl out.png blacklist</summary>
        void ApplyBlackDemo()
        {
            _black.Clear();
            _black.Add(new BlackEntry { Uid = "123456789", Note = "隔壁群的自定义机器人", Ts = "2026-10-21 17:00" });
            _black.Add(new BlackEntry { Uid = "456789123", Note = "刷屏那个", Ts = "2026-10-21 17:02" });
            _black.Add(new BlackEntry { Uid = "789123456", Note = "", Ts = "2026-10-21 17:05" });
            _black.Add(new BlackEntry { Uid = "321654987", Note = "已测试解除", Ts = "2026-10-21 17:06", Removed = true });
            if (_blOpen) PaintBlacklist(); else BlackSetOpen(true);
            if (_blHint != null) _blHint.Text = "已解除 321654987（关掉列表后才会从名单里消失）";
        }

        // ------------------------------------------------ 沉默名单（拉黑别的机器人）
        /// <summary>
        /// 全局功能：填 QQ 号拉黑别的机器人，她就不再理会这些号的消息。
        /// 名单存在服务器 data/bot_blacklist.json，插件的 recorder 每条消息查一次（5 秒缓存）。
        /// </summary>
        void BuildBlacklistCard(StackPanel b)
        {
            b.Children.Add(Txt("填别的机器人的 QQ 号。这些号发的消息她一律当没看见——不记录、不学记忆、不回复。全局生效，不分群。",
                                12.5, false, _th.bText, 0));

            var rAdd = Row();
            rAdd.Margin = new Thickness(0, 12 * S, 0, 0);

            _blInput = new TextBox();
            _blInput.Width = 170 * S;
            _blInput.Height = 30 * S;
            _blInput.FontSize = 12.5 * S;
            _blInput.Margin = new Thickness(0, 0, 8 * S, 8 * S);
            _blInput.ToolTip = "要拉黑的机器人 QQ 号（纯数字）";
            StyleInput(_blInput);
            Watermark(_blInput, "机器人 QQ 号");
            rAdd.Children.Add(_blInput);

            _blNote = new TextBox();
            _blNote.Width = 190 * S;
            _blNote.Height = 30 * S;
            _blNote.FontSize = 12.5 * S;
            _blNote.Margin = new Thickness(0, 0, 8 * S, 8 * S);
            _blNote.ToolTip = "备注，可留空";
            StyleInput(_blNote);
            Watermark(_blNote, "备注（可不填）");
            rAdd.Children.Add(_blNote);

            var bAdd = Btn("拉黑", true, delegate { BlackAdd(); });
            rAdd.Children.Add(bAdd);

            _blToggle = Btn("展开名单 ▾", false, delegate { BlackSetOpen(!_blOpen); });
            rAdd.Children.Add(_blToggle);
            b.Children.Add(rAdd);

            b.Children.Add(Txt("左边填 QQ 号，右边可填备注（比如「某群的自定义机器人」），备注可以不填。",
                                11, false, _th.bSub, 0));

            _blHint = Txt("", 11.5, false, _th.bSub, 6);
            b.Children.Add(_blHint);

            // 下拉列表：画在页面里（不用 Popup，Popup 是独立窗口，-shot 截不到）
            _blPanel = new Border();
            _blPanel.Background = _th.bBg;
            _blPanel.BorderBrush = _th.bLine;
            _blPanel.BorderThickness = new Thickness(1);
            _blPanel.CornerRadius = new CornerRadius(8);
            _blPanel.Padding = new Thickness(8 * S);
            _blPanel.Margin = new Thickness(0, 8 * S, 0, 0);
            _blPanel.Visibility = Visibility.Collapsed;

            _blList = new StackPanel();
            _blPanel.Child = _blList;
            b.Children.Add(_blPanel);

            // 界面自检时别去拉真数据，否则异步返回会把演示条目覆盖掉
            if (ShotDemo != "blacklist") LoadBlacklist();
        }

        void BlackSetOpen(bool open)
        {
            // 关列表时才真正把「已解除」的条目丢掉（用户要的就是这个手感：
            // 点解除后它还留在原位，可以不慌不忙看完再关）
            if (!open && _blOpen) DropRemovedBlack();
            _blOpen = open;
            if (_blToggle != null)
                _blToggle.Content = open ? "收起名单 ▴" : "展开名单 ▾";
            PaintBlacklist();
        }

        void DropRemovedBlack()
        {
            int before = _black.Count;
            _black.RemoveAll(delegate(BlackEntry e) { return e.Removed; });
            if (before != _black.Count && _blHint != null)
                _blHint.Text = "已解除 " + (before - _black.Count) + " 个（关列表后从名单里移除）";
        }

        void PaintBlacklist()
        {
            if (_blPanel == null) return;
            _blPanel.Visibility = _blOpen ? Visibility.Visible : Visibility.Collapsed;
            if (_blToggle != null)
                _blToggle.Content = _blOpen ? "收起名单 ▴" : ("展开名单 ▾");
            if (!_blOpen) return;

            _blList.Children.Clear();
            int active = 0;
            foreach (var e in _black) if (!e.Removed) active++;

            if (_black.Count == 0)
            {
                _blList.Children.Add(Txt("名单是空的——她目前会正常理会所有号。", 11.5, false, _th.bSub, 6));
                return;
            }
            _blList.Children.Add(Txt("共 " + active + " 个在拉黑中" +
                                     (_black.Count != active ? "，" + (_black.Count - active) + " 个待移除" : ""),
                                     11, false, _th.bSub, 0));

            foreach (var e in _black)
            {
                var entry = e;
                var row = new DockPanel();
                row.Margin = new Thickness(0, 6 * S, 0, 0);

                var bUn = Btn(entry.Removed ? "已解除" : "解除", false,
                              delegate { BlackRemove(entry); });
                bUn.Padding = new Thickness(12 * S, 4 * S, 12 * S, 4 * S);
                bUn.Margin = new Thickness(8 * S, 0, 0, 0);
                bUn.IsEnabled = !entry.Removed;
                DockPanel.SetDock(bUn, Dock.Right);
                row.Children.Add(bUn);

                var txt = new TextBlock();
                txt.FontSize = 12 * S;
                txt.VerticalAlignment = VerticalAlignment.Center;
                txt.TextWrapping = TextWrapping.Wrap;
                string label = entry.Uid;
                if (!string.IsNullOrEmpty(entry.Note)) label += "  " + entry.Note;
                if (!string.IsNullOrEmpty(entry.Ts)) label += "  · " + entry.Ts;
                txt.Text = label;
                // 已解除但还没关列表的：灰掉 + 划掉，一眼看得出状态
                txt.Foreground = entry.Removed ? _th.bSub : _th.bText;
                if (entry.Removed) txt.TextDecorations = TextDecorations.Strikethrough;
                txt.Margin = new Thickness(2 * S, 0, 0, 0);
                row.Children.Add(txt);

                _blList.Children.Add(row);
            }
        }

        void LoadBlacklist()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/bot_blacklist?token=" + Token, 30);
                var list = ParseBlacklist(json);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    // 列表开着的时候别把「已解除待移除」的条目刷没了
                    var keep = new List<BlackEntry>();
                    if (_blOpen)
                        foreach (var e in _black) if (e.Removed) keep.Add(e);
                    _black.Clear();
                    _black.AddRange(list);
                    _black.AddRange(keep);
                    if (_blHint != null && _blHint.Text.Length == 0)
                        _blHint.Text = list.Count == 0 ? "" : ("当前拉黑 " + list.Count + " 个");
                    PaintBlacklist();
                }));
            });
        }

        List<BlackEntry> ParseBlacklist(string json)
        {
            var list = new List<BlackEntry>();
            try
            {
                var o = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                if (o == null || !o.ContainsKey("items")) return list;
                var items = o["items"] as object[];
                if (items == null) return list;
                foreach (var it in items)
                {
                    var d = it as Dictionary<string, object>;
                    if (d == null) continue;
                    var e = new BlackEntry();
                    e.Uid = d.ContainsKey("uid") ? Convert.ToString(d["uid"]) : "";
                    e.Note = d.ContainsKey("note") ? Convert.ToString(d["note"]) : "";
                    e.Ts = d.ContainsKey("ts") ? Convert.ToString(d["ts"]) : "";
                    if (e.Uid.Length > 0) list.Add(e);
                }
            }
            catch { }
            return list;
        }

        void BlackPost(string uid, string action, string note)
        {
            string chat = uid;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var payload = new Dictionary<string, object>();
                payload["action"] = action;
                payload["uid"] = chat;
                if (!string.IsNullOrEmpty(note)) payload["note"] = note;
                string body = new JavaScriptSerializer().Serialize(payload);
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                string cmd = "echo " + b64 + " | base64 -d > /tmp/bl.json; " +
                             "curl -s -m 20 -X POST -H 'Content-Type: application/json' " +
                             "--data-binary @/tmp/bl.json '" + ApiBase + "/api/bot_blacklist?token=" + Token + "'";
                string r = Ssh.Run(_cfg, cmd, 40);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool ok = r.IndexOf("\"ok\": true") >= 0 || r.IndexOf("\"ok\":true") >= 0;
                    if (!ok)
                    {
                        if (_blHint != null) _blHint.Text = "操作失败：" + FirstLine(r);
                        return;
                    }
                    if (action == "add")
                    {
                        if (_blHint != null) _blHint.Text = "已拉黑 " + chat + "，她不会再理这个号了";
                        LoadBlacklist();
                    }
                }));
            });
        }

        void BlackAdd()
        {
            if (_blInput == null) return;
            string uid = (_blInput.Text ?? "").Trim();
            if (uid.Length == 0) { if (_blHint != null) _blHint.Text = "先填 QQ 号"; return; }
            foreach (char ch in uid)
                if (ch < '0' || ch > '9')
                {
                    if (_blHint != null) _blHint.Text = "QQ 号只能是数字";
                    return;
                }
            foreach (var e in _black)
                if (e.Uid == uid && !e.Removed)
                {
                    if (_blHint != null) _blHint.Text = "这个号已经在名单里了";
                    return;
                }
            string note = _blNote == null ? "" : (_blNote.Text ?? "").Trim();
            _blInput.Text = "";
            if (_blNote != null) _blNote.Text = "";
            if (_blHint != null) _blHint.Text = "正在拉黑 " + uid + " …";
            BlackPost(uid, "add", note);
        }

        void BlackRemove(BlackEntry entry)
        {
            // 只做「标记」：条目留在列表里，等用户关掉列表才消失
            if (entry.Removed) return;
            entry.Removed = true;
            PaintBlacklist();
            if (_blHint != null) _blHint.Text = "已解除 " + entry.Uid + "（关掉列表后才会从名单里消失）";
            BlackPost(entry.Uid, "remove", null);
        }

        /// <summary>-shot 专用：把页面滚到底，好把「沉默名单」整张卡片拍全。</summary>
        public void ScrollDemoToBottom()
        {
            if (_scroll != null) _scroll.ScrollToEnd();
        }

        /// <summary>-shot botctl out.png persona：滚到「角色人格」卡片再拍。</summary>
        public void ScrollDemoToPersona()
        {
            try
            {
                if (_psList != null) _psList.BringIntoView();     // 先让列表滚到视野里
                if (_psPrompt != null) _psPrompt.BringIntoView(); // 再往下挪一点，连编辑框一起拍到
            }
            catch { }
        }

        /// <summary>-shot botctl out.png ambient：滚到「默默发表情」那一排再拍。</summary>
        public void ScrollDemoToAmbient()
        {
            try
            {
                if (_ambientRow != null) _ambientRow.BringIntoView();
            }
            catch { }
        }

        void AddVoiceBtn(WrapPanel panel, string id, string label)
        {
            var b = Btn(label, false, delegate { SetVoiceStyle(id); });
            b.Padding = new Thickness(14 * S, 7 * S, 14 * S, 7 * S);
            _btnVoice[b] = id;
            panel.Children.Add(b);
        }

        /// <summary>图库：她认得的东西。命中就直接按备注回答（比识图/搜图又快又准）。</summary>



        // ============================================================ 梗库
        TextBox _mkNames, _mkDesc, _mkJie;
        string _mkStickerB64 = "";          // 待上传的梗包（base64）
        TextBlock _mkStickerHint;
        System.Windows.Controls.Image _mkPreview;   // 已选图的预览
        TextBlock _mkStatus;
        StackPanel _mkList;

        void BuildMemeCard(StackPanel b)
        {
            b.Children.Add(Txt("纯文字。名字可用分号写多个（如 doro;桃乐丝）。\n"
                               + "· 随口提到 → 只回「接梗话」（不念介绍，像康神那样）\n"
                               + "· 真的在问（这是什么梗/啥意思） → 才查介绍 + 联网，用自己的话解释", 11, false, _th.bSub, 2));
            _mkStatus = Txt("读取中…", 12.5, true, _th.bText, 6);
            b.Children.Add(_mkStatus);
            _mkList = new StackPanel();
            _mkList.Margin = new Thickness(0, 4 * S, 0, 0);
            b.Children.Add(_mkList);

            var row1 = new WrapPanel();
            row1.Margin = new Thickness(0, 10 * S, 0, 0);
            _mkNames = ApInput("梗名字，多个用 ; 隔开", 260);
            row1.Children.Add(_mkNames);
            b.Children.Add(row1);

            _mkDesc = new TextBox();
            _mkDesc.Height = 64 * S;
            _mkDesc.FontSize = 12.5 * S;
            _mkDesc.Margin = new Thickness(0, 0, 8 * S, 8 * S);
            _mkDesc.Background = _th.bBtn;
            _mkDesc.Foreground = _th.bText;
            _mkDesc.BorderBrush = _th.bLine;
            _mkDesc.Padding = new Thickness(8 * S);
            _mkDesc.TextWrapping = TextWrapping.Wrap;
            _mkDesc.AcceptsReturn = true;
            StyleInput(_mkDesc);
            b.Children.Add(_mkDesc);

            b.Children.Add(Txt("接梗话（随口提一句时回这个，多个用 ; 分隔；空=用默认池）", 10.5, false, _th.bSub, 2));
            _mkJie = ApInput("如：哦这个啊;哈？又来", 340);
            b.Children.Add(_mkJie);

            var rowImg = new WrapPanel();
            rowImg.Margin = new Thickness(0, 2 * S, 0, 6 * S);
            rowImg.Children.Add(Btn("选图存梗包", false, delegate { MemePickSticker(); }));
            _mkStickerHint = Txt("没选图（随口提一句时只回上面的接梗话）", 10.5, false, _th.bSub, 4);
            _mkStickerHint.VerticalAlignment = VerticalAlignment.Center;
            _mkStickerHint.Margin = new Thickness(8 * S, 0, 0, 0);
            rowImg.Children.Add(_mkStickerHint);
            b.Children.Add(rowImg);

            _mkPreview = new System.Windows.Controls.Image();
            _mkPreview.Width = 76 * S;
            _mkPreview.Height = 76 * S;
            _mkPreview.Stretch = Stretch.Uniform;
            _mkPreview.Margin = new Thickness(0, 0, 0, 6 * S);
            _mkPreview.Visibility = Visibility.Collapsed;
            b.Children.Add(_mkPreview);

            var row2 = new WrapPanel();
            row2.Children.Add(Btn("保存梗", true, delegate { MemeSave(); }));
            row2.Children.Add(Btn("刷新", false, delegate { LoadMemes(); }));
            b.Children.Add(row2);
            LoadMemes();
        }

        void LoadMemes()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/memes?token=" + Token, 30);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        var ser = new JavaScriptSerializer();
                        var o = ser.DeserializeObject(json) as Dictionary<string, object>;
                        if (o == null)
                        {
                            if (_mkStatus != null) _mkStatus.Text = "读取失败：" + FirstLine(json);
                            return;
                        }
                        int cnt = o.ContainsKey("count") ? Convert.ToInt32(o["count"]) : 0;
                        if (_mkStatus != null) _mkStatus.Text = "梗库共 " + cnt + " 条";
                        if (_mkList == null) return;
                        _mkList.Children.Clear();
                        var items = o.ContainsKey("items") ? o["items"] as object[] : null;
                        if (items == null || items.Length == 0)
                        {
                            _mkList.Children.Add(Txt("还空着：下面填个名字和介绍就有一条了。", 11.5, false, _th.bSub, 4));
                            return;
                        }
                        foreach (var iv in items)
                        {
                            var it = iv as Dictionary<string, object>;
                            if (it == null) continue;
                            string id = it.ContainsKey("id") ? Convert.ToString(it["id"]) : "";
                            string desc = it.ContainsKey("desc") ? Convert.ToString(it["desc"]) : "";
                            string tags = "";
                            if (it.ContainsKey("tags") && it["tags"] is object[])
                            {
                                var tl = new System.Collections.Generic.List<string>();
                                foreach (var tg in (object[])it["tags"]) { string s = Convert.ToString(tg); if (s.Length > 0) tl.Add(s); }
                                tags = string.Join(",", tl.ToArray());
                            }
                            int hits = it.ContainsKey("hits") ? Convert.ToInt32(it["hits"]) : 0;
                            string nm = "";
                            var nv = it.ContainsKey("names") ? it["names"] as object[] : null;
                            if (nv != null) nm = string.Join("、", Array.ConvertAll(nv, x => Convert.ToString(x)));
                            var r = new WrapPanel();
                            r.Margin = new Thickness(0, 3 * S, 0, 3 * S);
                            r.Children.Add(Txt("· " + nm, 12, true, _th.bText, 0));
                            r.Children.Add(Txt("  命中 " + hits + " · " + (desc.Length > 40 ? desc.Substring(0, 40) + "…" : desc),
                                               10.5, false, _th.bSub, 0));
                            string jie = it.ContainsKey("jie") ? Convert.ToString(it["jie"]) : "";
                            if (jie.Length > 0)
                                r.Children.Add(Txt("  ▶接梗：" + jie, 10.5, false, _th.bAccent, 0));
                            bool hasStk = it.ContainsKey("has_sticker") && Convert.ToBoolean(it["has_sticker"]);
                            if (hasStk)
                            {
                                var thumb = new System.Windows.Controls.Image();
                                thumb.Width = 46 * S;
                                thumb.Height = 46 * S;
                                thumb.Stretch = Stretch.Uniform;
                                thumb.Margin = new Thickness(8 * S, 0, 0, 0);
                                thumb.Tag = id;
                                r.Children.Add(thumb);
                                MemeLoadThumb(id, thumb);
                            }
                            var ed = Btn("改", false, delegate { MemeEdit(id, nm, desc, jie); });
                            ed.Padding = new Thickness(9 * S, 2 * S, 9 * S, 2 * S);
                            ed.FontSize = 10.5 * S;
                            ed.Margin = new Thickness(8 * S, 0, 0, 0);
                            r.Children.Add(ed);
                            var del = Btn("删", false, delegate { MemeDel(id, nm); });
                            del.Padding = new Thickness(9 * S, 2 * S, 9 * S, 2 * S);
                            del.FontSize = 10.5 * S;
                            del.Margin = new Thickness(6 * S, 0, 0, 0);
                            r.Children.Add(del);
                            if (_mkList != null) _mkList.Children.Add(r);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_mkStatus != null) _mkStatus.Text = "解析失败：" + ex.Message;
                    }
                }));
            });
        }

        void MemeEdit(string id, string names, string desc, string jie)
        {
            if (_mkNames != null) _mkNames.Text = names.Replace("、", ";");
            if (_mkDesc != null) _mkDesc.Text = desc;
            if (_mkJie != null) _mkJie.Text = jie ?? "";
            if (_mkStatus != null) _mkStatus.Text = "改完点「保存梗」（同名会更新那一条）";
        }

        void MemePost(string path, Dictionary<string, object> payload, string okText)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string body = new JavaScriptSerializer().Serialize(payload);
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                string cmd = "echo " + b64 + " | base64 -d > /tmp/mk.json; " +
                             "curl -s -m 20 -X POST -H 'Content-Type: application/json' " +
                             "--data-binary @/tmp/mk.json '" + ApiBase + path + "?token=" + Token + "'";
                string r = Ssh.Run(_cfg, cmd, 40);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool ok = r.IndexOf("\"ok\": true") >= 0 || r.IndexOf("\"ok\":true") >= 0;
                    if (_mkStatus != null)
                        _mkStatus.Text = ok ? okText : ("失败：" + FirstLine(r));
                    if (ok)                                   // 存完清空，方便接着加下一条
                    {
                        if (_mkNames != null) _mkNames.Text = "";
                        if (_mkDesc != null) _mkDesc.Text = "";
                        if (_mkJie != null) _mkJie.Text = "";
                        _mkStickerB64 = "";
                        if (_mkPreview != null) { _mkPreview.Source = null; _mkPreview.Visibility = Visibility.Collapsed; }
                        if (_mkStickerHint != null) _mkStickerHint.Text = "没选图（随口提一句时只回接梗话）";
                    }
                    LoadMemes();
                }));
            });
        }



        void MemeLoadThumb(string id, System.Windows.Controls.Image img)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string b64 = Ssh.Run(_cfg,
                    "curl -s -m 15 'http://127.0.0.1:8090/api/memes/sticker?id=" + id
                    + "&token=" + Token + "' | base64 -w0", 30).Trim();
                if (string.IsNullOrEmpty(b64) || b64.Length < 100) return;
                try
                {
                    byte[] raw = Convert.FromBase64String(b64);
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        try
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.StreamSource = new MemoryStream(raw);
                            bmp.EndInit();
                            bmp.Freeze();
                            img.Source = bmp;
                        }
                        catch { }
                    }));
                }
                catch { }
            });
        }

        void MemeShowLocalPreview(byte[] raw)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(raw);
                bmp.EndInit();
                bmp.Freeze();
                if (_mkPreview != null)
                {
                    _mkPreview.Source = bmp;
                    _mkPreview.Visibility = Visibility.Visible;
                }
            }
            catch
            {
                if (_mkPreview != null) _mkPreview.Visibility = Visibility.Collapsed;
            }
        }

        void MemePickSticker()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Title = "选一张图当她对这个梗的表情包";
            dlg.Filter = "图片|*.jpg;*.jpeg;*.png;*.gif;*.webp|所有文件|*.*";
            if (dlg.ShowDialog() != true) return;
            try
            {
                byte[] raw = File.ReadAllBytes(dlg.FileName);
                if (raw.Length > 3 * 1024 * 1024)
                {
                    if (_mkStickerHint != null) _mkStickerHint.Text = "图太大了（>3MB），换张小点的";
                    return;
                }
                _mkStickerB64 = Convert.ToBase64String(raw);
                MemeShowLocalPreview(raw);
                if (_mkStickerHint != null)
                    _mkStickerHint.Text = "已选：" + Path.GetFileName(dlg.FileName) + "（保存即生效）";
            }
            catch (Exception ex)
            {
                if (_mkStickerHint != null) _mkStickerHint.Text = "读图失败：" + ex.Message;
            }
        }

        void MemeDelSticker(string id)
        {
            var p = new Dictionary<string, object>();
            p["id"] = id;
            p["del_sticker"] = "1";
            MemePost("/api/memes/edit", p, "已删掉这条的梗包");
        }

        void MemeSave()
        {
            string nm = _mkNames == null ? "" : _mkNames.Text.Trim();
            string ds = _mkDesc == null ? "" : _mkDesc.Text.Trim();
            if (nm.Length == 0 || ds.Length == 0)
            {
                if (_mkStatus != null) _mkStatus.Text = "名字和介绍都要填";
                return;
            }
            string jie = _mkJie == null ? "" : _mkJie.Text.Trim();
            var p = new Dictionary<string, object>();
            p["names"] = nm;
            p["desc"] = ds;
            if (jie.Length > 0) p["jie"] = jie;
            if (!string.IsNullOrEmpty(_mkStickerB64)) p["image_b64"] = _mkStickerB64;
            MemePost("/api/memes/add", p, "已保存「" + nm + "」");
        }

        void MemeDel(string id, string names)
        {
            var p = new Dictionary<string, object>();
            p["id"] = id;
            MemePost("/api/memes/del", p, "已删掉「" + names + "」");
        }


        // ======================================================== 公开版配置加载
        // 公开版源码里**没有任何凭据**（服务器 IP / SSH 密码 / 控制台 Token 一律不写死）。
        // 运行时按顺序找：
        //   1) 环境变量 FATBOT_CONSOLE_CONFIG 指向的 JSON
        //   2) 程序同目录 console-config.json
        //   3) %APPDATA%\fatbot\console.json      ← 首次配置窗口会写到这
        // JSON 字段：host / ssh_user / ssh_pass / token / port / base
        void LoadConsoleConfig()
        {
            var cands = new List<string>();
            string env = Environment.GetEnvironmentVariable("FATBOT_CONSOLE_CONFIG");
            if (!string.IsNullOrEmpty(env)) cands.Add(env);
            cands.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "console-config.json"));
            cands.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "fatbot", "console.json"));

            foreach (var p in cands)
            {
                if (!File.Exists(p)) continue;
                try
                {
                    var o = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(p, Encoding.UTF8))
                            as Dictionary<string, object>;
                    if (o == null) continue;
                    if (o.ContainsKey("host") && Convert.ToString(o["host"]).Length > 0)
                        _cfg.Host = Convert.ToString(o["host"]);
                    if (o.ContainsKey("ssh_user")) _cfg.User = Convert.ToString(o["ssh_user"]);
                    if (o.ContainsKey("ssh_pass")) _cfg.Pass = Convert.ToString(o["ssh_pass"]);
                    if (o.ContainsKey("token"))    Token = Convert.ToString(o["token"]);
                    if (o.ContainsKey("base"))
                    {
                        string b = Convert.ToString(o["base"]);
                        if (b.Length > 0)
                        {
                            ApiBase = b.TrimEnd('/');
                            if (_cfg.Host.Length == 0)
                            {
                                try { _cfg.Host = new Uri(b).Host; } catch { }
                            }
                        }
                    }
                    if (o.ContainsKey("webui_token")) _cfg.WebuiToken = Convert.ToString(o["webui_token"]);
                    if (_cfg.Host.Length > 0 && Token.Length > 0) return;
                }
                catch { /* 坏文件当没配 */ }
            }
        }

        /// <summary>没有配置就弹窗让用户填，填完存到 %APPDATA%\fatbot\console.json。</summary>
        void EnsureConfigured()
        {
            LoadConsoleConfig();
            if (!string.IsNullOrEmpty(_cfg.Host) && !string.IsNullOrEmpty(Token)) return;

            var w = new Window
            {
                Title = "首次配置 · fatbot 控制台",
                Width = 480, Height = 400,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = _th != null ? _th.bBg : Brushes.White
            };
            var g = new Grid { Margin = new Thickness(18) };
            for (int i = 0; i < 6; i++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var boxes = new Dictionary<string, FrameworkElement>();
            var labels = new[] { "服务器 IP", "SSH 用户", "SSH 密码", "控制台 Token", "控制台端口" };
            var keys   = new[] { "host", "ssh_user", "ssh_pass", "token", "port" };
            for (int i = 0; i < labels.Length; i++)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
                sp.Children.Add(new TextBlock
                {
                    Text = labels[i], Width = 100, VerticalAlignment = VerticalAlignment.Center
                });
                if (i == 2)
                {
                    var pb = new PasswordBox { Width = 310 };
                    sp.Children.Add(pb);
                    boxes[keys[i]] = pb;          // 读的时候用 Password 属性
                }
                else
                {
                    var tb = new TextBox
                    {
                        Width = 310,
                        Text = i == 4 ? "8090" : (i == 1 ? "ubuntu" : "")
                    };
                    sp.Children.Add(tb);
                    boxes[keys[i]] = tb;
                }
                Grid.SetRow(sp, i);
                g.Children.Add(sp);
            }

            var tip = new TextBlock
            {
                Text = "凭据只存在你自己的电脑上（%APPDATA%\\fatbot\\console.json），不会进仓库。",
                Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, Opacity = 0.7
            };
            Grid.SetRow(tip, 5); g.Children.Add(tip);

            var ok = new Button { Content = "保存并连接", Height = 36, Margin = new Thickness(0, 14, 0, 0) };
            ok.Click += delegate
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "fatbot");
                Directory.CreateDirectory(dir);
                var d = new Dictionary<string, object>();
foreach (var kv in boxes)
                {
                    var pb = kv.Value as PasswordBox;
                    d[kv.Key] = pb != null ? pb.Password.Trim() : ((TextBox)kv.Value).Text.Trim();
                }
                d["webui_token"] = d["token"];
                d["base"] = "http://" + d["host"] + ":" + d["port"];
                File.WriteAllText(Path.Combine(dir, "console.json"),
                                  new JavaScriptSerializer().Serialize(d), Encoding.UTF8);
                w.DialogResult = true;
                w.Close();
            };
            g.Children.Add(ok);

            w.Content = g;
            w.ShowDialog();
            LoadConsoleConfig();
        }
        // ============================================================ API 渠道
        TextBox _apName, _apBase, _apModel, _apKeyV;
        TextBlock _apStatus, _apLast;
        StackPanel _apList;

        TextBox ApInput(string hint, double width)
        {
            var t = new TextBox();
            t.Width = width * S;
            t.Height = 34 * S;              // 34 才放得下 12.5 号字 + 内边距
            t.FontSize = 12.5 * S;
            t.Margin = new Thickness(0, 0, 8 * S, 8 * S);
            t.Background = _th.bBtn;
            t.Foreground = _th.bText;
            t.BorderBrush = _th.bLine;
            t.VerticalContentAlignment = VerticalAlignment.Center;
            StyleInput(t);
            Watermark(t, hint);
            return t;
        }

        void BuildApiCard(StackPanel b)
        {
            b.Children.Add(Txt("多个渠道各自起名，点「用这个」立刻切过去（不用重启）。"
                               + "只想换 key：名字 + Key 填上就行，地址/模型留空 = 沿用 .env。"
                               + "编辑时 Key 留空 = 不改原来的 key。", 11, false, _th.bSub, 2));
            _apStatus = Txt("读取中…", 12.5, true, _th.bText, 6);
            b.Children.Add(_apStatus);
            _apLast = Txt("", 10.5, false, _th.bSub, 3);
            b.Children.Add(_apLast);
            _apList = new StackPanel();
            _apList.Margin = new Thickness(0, 4 * S, 0, 0);
            b.Children.Add(_apList);

            var row1 = new WrapPanel();
            row1.Margin = new Thickness(0, 10 * S, 0, 0);
            _apName = ApInput("名字，如 主渠道 / 备1", 150);
            _apBase = ApInput("地址，如 https://xxx/v1/chat/completions", 320);
            row1.Children.Add(_apName);
            row1.Children.Add(_apBase);
            b.Children.Add(row1);

            var row2 = new WrapPanel();
            _apModel = ApInput("模型名，如 deepseek-v4-flash-0731", 250);
            _apKeyV = ApInput("Key（留空=不改）", 250);
            row2.Children.Add(_apModel);
            row2.Children.Add(_apKeyV);
            b.Children.Add(row2);

            var row3 = new WrapPanel();
            row3.Children.Add(Btn("保存渠道", true, delegate { ApiSave(); }));
            row3.Children.Add(Btn("测试", false, delegate { ApiTest(); }));
            row3.Children.Add(Btn("刷新", false, delegate { LoadApis(); }));
            b.Children.Add(row3);
            LoadApis();
        }

        void LoadApis()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/apis?token=" + Token, 30);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        var ser = new JavaScriptSerializer();
                        var o = ser.DeserializeObject(json) as Dictionary<string, object>;
                        if (o == null)
                        {
                            if (_apStatus != null) _apStatus.Text = "读取失败：" + FirstLine(json);
                            return;
                        }
                        string active = o.ContainsKey("active") ? Convert.ToString(o["active"]) : "";
                        if (_apStatus != null)
                            _apStatus.Text = string.IsNullOrEmpty(active)
                                ? "当前：默认（.env）" : ("当前：「" + active + "」");
                        if (_apLast != null)
                        {
                            var lv = o.ContainsKey("last") ? o["last"] as Dictionary<string, object> : null;
                            if (lv == null || !lv.ContainsKey("at") || Convert.ToString(lv["at"]).Length == 0)
                                _apLast.Text = "上次调用：还没有记录（说句话就会记）";
                            else
                            {
                                bool lok = lv.ContainsKey("ok") && Convert.ToBoolean(lv["ok"]);
                                int lstatus = lv.ContainsKey("status") ? Convert.ToInt32(lv["status"]) : 0;
                                string lch = lv.ContainsKey("channel") ? Convert.ToString(lv["channel"]) : "";
                                string lat = lv.ContainsKey("at") ? Convert.ToString(lv["at"]) : "";
                                _apLast.Text = "上次调用：" + (lok ? "成功 200" : ("失败 HTTP " + lstatus))
                                    + "（" + lch + "）" + lat;
                                _apLast.Foreground = lok ? _th.bSub : _th.bBad;
                            }
                        }
                        if (_apList == null) return;
                        _apList.Children.Clear();
                        var items = o.ContainsKey("items") ? o["items"] as object[] : null;
                        if (items == null || items.Length == 0)
                        {
                            _apList.Children.Add(Txt("还没有渠道：填下面表单保存一个（第一个会自动设为当前）。",
                                                     11.5, false, _th.bSub, 4));
                            return;
                        }
                        foreach (var iv in items)
                        {
                            var it = iv as Dictionary<string, object>;
                            if (it == null) continue;
                            string nm = it.ContainsKey("name") ? Convert.ToString(it["name"]) : "";
                            string baseUrl = it.ContainsKey("base") ? Convert.ToString(it["base"]) : "";
                            string model = it.ContainsKey("model") ? Convert.ToString(it["model"]) : "";
                            string mask = it.ContainsKey("key_mask") ? Convert.ToString(it["key_mask"]) : "";
                            bool isActive = it.ContainsKey("active") && Convert.ToBoolean(it["active"]);
                            var r = new WrapPanel();
                            r.Margin = new Thickness(0, 3 * S, 0, 3 * S);
                            r.Children.Add(Txt((isActive ? "● " : "· ") + nm, 12, isActive, 
                                               isActive ? _th.bAccent : _th.bText, 0));
                            r.Children.Add(Txt("  " + model + (mask.Length > 0 ? "  key " + mask : "  key …"),
                                               10.5, false, _th.bSub, 0));
                            if (!isActive)
                            {
                                var use = Btn("用这个", false, delegate { ApiUse(nm); });
                                use.Padding = new Thickness(9 * S, 2 * S, 9 * S, 2 * S);
                                use.FontSize = 10.5 * S;
                                use.Margin = new Thickness(8 * S, 0, 0, 0);
                                r.Children.Add(use);
                            }
                            var ed = Btn("改", false, delegate { ApiEdit(nm, baseUrl, model); });
                            ed.Padding = new Thickness(9 * S, 2 * S, 9 * S, 2 * S);
                            ed.FontSize = 10.5 * S;
                            ed.Margin = new Thickness(6 * S, 0, 0, 0);
                            r.Children.Add(ed);
                            var del = Btn("删", false, delegate { ApiDel(nm); });
                            del.Padding = new Thickness(9 * S, 2 * S, 9 * S, 2 * S);
                            del.FontSize = 10.5 * S;
                            del.Margin = new Thickness(6 * S, 0, 0, 0);
                            r.Children.Add(del);
                            if (_apList != null) _apList.Children.Add(r);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_apStatus != null) _apStatus.Text = "解析失败：" + ex.Message;
                    }
                }));
            });
        }

        void ApiEdit(string name, string baseUrl, string model)
        {
            if (_apName != null) _apName.Text = name;
            if (_apBase != null) _apBase.Text = baseUrl;
            if (_apModel != null) _apModel.Text = model;
            if (_apKeyV != null) _apKeyV.Text = "";
            if (_apStatus != null) _apStatus.Text = "改了「" + name + "」的地址/模型后点「保存渠道」";
        }

        void ApiPost(string path, Dictionary<string, object> payload, string okText)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string body = new JavaScriptSerializer().Serialize(payload);
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                string cmd = "echo " + b64 + " | base64 -d > /tmp/ap.json; " +
                             "curl -s -m 20 -X POST -H 'Content-Type: application/json' " +
                             "--data-binary @/tmp/ap.json '" + ApiBase + path + "?token=" + Token + "'";
                string r = Ssh.Run(_cfg, cmd, 40);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool ok = r.IndexOf("\"ok\": true") >= 0 || r.IndexOf("\"ok\":true") >= 0;
                    if (_apStatus != null)
                        _apStatus.Text = ok ? okText : ("失败：" + FirstLine(r));
                    LoadApis();
                }));
            });
        }

        void ApiSave()
        {
            string nm = _apName == null ? "" : _apName.Text.Trim();
            string bs = _apBase == null ? "" : _apBase.Text.Trim();
            string md = _apModel == null ? "" : _apModel.Text.Trim();
            string ky = _apKeyV == null ? "" : _apKeyV.Text.Trim();
            // 2026-11-11 修「改 key 功能用不了」：这里原来强制要求 名字+地址+模型 全填，
            // 可界面提示明明写着"只想换 key：名字 + Key 填上就行" —— 于是只填名字+Key 会被自己拦住。
            // 现在只要求名字，其余交给后端（后端对已存在的渠道是"留空=沿用"）。
            if (nm.Length == 0)
            {
                if (_apStatus != null) _apStatus.Text = "至少填个名字（它同时也是渠道的标识）";
                return;
            }
            if (bs.Length == 0 && md.Length == 0 && ky.Length == 0)
            {
                if (_apStatus != null) _apStatus.Text = "至少填一项：地址 / 模型 / Key";
                return;
            }
            var p = new Dictionary<string, object>();
            p["name"] = nm;
            if (bs.Length > 0) p["base"] = bs;
            if (md.Length > 0) p["model"] = md;
            if (ky.Length > 0) p["key"] = ky;
            ApiPost("/api/apis/save", p, "已保存「" + nm + "」");
        }

        /// <summary>真打一次验证渠道/key（后端 /api/apis/test）。名字空 = 测当前生效渠道。</summary>
        void ApiTest()
        {
            string nm = _apName == null ? "" : _apName.Text.Trim();
            string ky = _apKeyV == null ? "" : _apKeyV.Text.Trim();
            string bs = _apBase == null ? "" : _apBase.Text.Trim();
            string md = _apModel == null ? "" : _apModel.Text.Trim();
            var p = new Dictionary<string, object>();
            if (nm.Length > 0) p["name"] = nm;
            if (bs.Length > 0) p["base"] = bs;
            if (md.Length > 0) p["model"] = md;
            if (ky.Length > 0) p["key"] = ky;      // 用表单里刚填的 key 试，不必先保存
            if (_apStatus != null) _apStatus.Text = "测试中…（打一次真实请求）";
            string payload = new JavaScriptSerializer().Serialize(p);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
                string cmd = "echo " + b64 + " | base64 -d > /tmp/apt.json; " +
                             "curl -s -m 40 -X POST -H 'Content-Type: application/json' " +
                             "--data-binary @/tmp/apt.json '" + ApiBase + "/api/apis/test?token=" + Token + "'";
                string json = Ssh.Run(_cfg, cmd, 60);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    string msg = "";
                    bool ok = false;
                    try
                    {
                        var o = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                        if (o != null)
                        {
                            ok = o.ContainsKey("ok") && Convert.ToBoolean(o["ok"]);
                            msg = o.ContainsKey("msg") ? Convert.ToString(o["msg"]) : FirstLine(json);
                        }
                        else msg = FirstLine(json);
                    }
                    catch { msg = FirstLine(json); }
                    if (_apStatus != null)
                    {
                        _apStatus.Text = (ok ? "✅ " : "❌ ") + msg;
                    }
                }));
            });
        }

        void ApiUse(string name)
        {
            var p = new Dictionary<string, object>();
            p["name"] = name;
            ApiPost("/api/apis/use", p, "已切到「" + name + "」，下一条消息就走这个渠道");
        }

        void ApiDel(string name)
        {
            var p = new Dictionary<string, object>();
            p["name"] = name;
            ApiPost("/api/apis/del", p, "已删掉「" + name + "」");
        }

        // ============================================================ 每日 token 用量
        TextBlock _tkStatus;
        StackPanel _tkList;
        DispatcherTimer _tkTimer;

        static string FmtTok(long n)
        {
            if (n >= 1000000) return (n / 1000000.0).ToString("0.00") + "M";
            if (n >= 1000) return (n / 1000.0).ToString("0.0") + "k";
            return n.ToString();
        }

        void BuildTokenCard()
        {
            StackPanel outer;
            var card = Card("她每天用了多少 token", out outer);
            _tkStatus = Txt("读取中…", 13, true, _th.bText, 0);
            outer.Children.Add(_tkStatus);

            var inner = new StackPanel();
            inner.Margin = new Thickness(0, 8 * S, 0, 0);
            var toggle = Btn("展开 7 天明细 ▾", false, null);
            toggle.Padding = new Thickness(12 * S, 4 * S, 12 * S, 4 * S);
            toggle.FontSize = 11.5 * S;
            toggle.HorizontalAlignment = HorizontalAlignment.Left;
            toggle.Margin = new Thickness(0, 8 * S, 0, 0);
            toggle.Click += delegate
            {
                bool show = inner.Visibility != Visibility.Visible;
                inner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                toggle.Content = show ? "收起明细 ▴" : "展开 7 天明细 ▾";
            };
            inner.Visibility = Visibility.Collapsed;
            outer.Children.Add(toggle);

            _tkList = new StackPanel();
            _tkList.Margin = new Thickness(0, 6 * S, 0, 0);
            inner.Children.Add(_tkList);
            var row = new WrapPanel();
            row.Margin = new Thickness(0, 6 * S, 0, 0);
            row.Children.Add(Btn("刷新用量", false, delegate { LoadTokens(); }));
            inner.Children.Add(row);
            inner.Children.Add(Txt("口径：每次 AI 调用的 usage 累加（含缓存命中），从 2026-10-22 开始统计；"
                                   + "只有她在说话才会有数字。", 10.5, false, _th.bSub, 6));
            outer.Children.Add(inner);
            _page.Children.Add(card);
            LoadTokens();
            // 自动刷新：60 秒一次，看着像"实时"（一次就一条 curl，很轻）
            if (_tkTimer == null)
            {
                _tkTimer = new DispatcherTimer();
                _tkTimer.Interval = TimeSpan.FromSeconds(60);
                _tkTimer.Tick += delegate
                {
                    if (_tkList != null && _tkList.IsVisible) LoadTokens();
                };
                _tkTimer.Start();
            }
        }

        void LoadTokens()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/tokens?days=7&token=" + Token, 30);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        var ser = new JavaScriptSerializer();
                        var o = ser.DeserializeObject(json) as Dictionary<string, object>;
                        if (o == null)
                        {
                            if (_tkStatus != null) _tkStatus.Text = "用量读取失败：" + FirstLine(json);
                            return;
                        }
                        Dictionary<string, object> today = o.ContainsKey("today")
                            ? o["today"] as Dictionary<string, object> : null;
                        long tin = today != null && today.ContainsKey("in") ? Convert.ToInt64(today["in"]) : 0;
                        long tout = today != null && today.ContainsKey("out") ? Convert.ToInt64(today["out"]) : 0;
                        long tcache = today != null && today.ContainsKey("cached") ? Convert.ToInt64(today["cached"]) : 0;
                        long tcalls = today != null && today.ContainsKey("calls") ? Convert.ToInt64(today["calls"]) : 0;
                        if (_tkStatus != null)
                            _tkStatus.Text = "今天 " + FmtTok(tin + tout) + " token"
                                + "（输入 " + FmtTok(tin) + " / 输出 " + FmtTok(tout)
                                + " / 缓存命中 " + FmtTok(tcache) + "）· " + tcalls + " 次调用";
                        if (_tkList == null) return;
                        _tkList.Children.Clear();
                        var days = o.ContainsKey("days") ? o["days"] as object[] : null;
                        if (days == null || days.Length == 0) return;
                        long maxv = 1;
                        foreach (var dv in days)
                        {
                            var dd = dv as Dictionary<string, object>;
                            if (dd == null) continue;
                            long tot = (dd.ContainsKey("in") ? Convert.ToInt64(dd["in"]) : 0)
                                     + (dd.ContainsKey("out") ? Convert.ToInt64(dd["out"]) : 0);
                            if (tot > maxv) maxv = tot;
                        }
                        foreach (var dv in days)
                        {
                            var dd = dv as Dictionary<string, object>;
                            if (dd == null) continue;
                            string date = dd.ContainsKey("date") ? Convert.ToString(dd["date"]) : "";
                            long din = dd.ContainsKey("in") ? Convert.ToInt64(dd["in"]) : 0;
                            long dout = dd.ContainsKey("out") ? Convert.ToInt64(dd["out"]) : 0;
                            long dcalls = dd.ContainsKey("calls") ? Convert.ToInt64(dd["calls"]) : 0;
                            long tot = din + dout;
                            var g = new Grid();
                            g.Margin = new Thickness(0, 2 * S, 0, 2 * S);
                            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52 * S) });
                            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96 * S) });
                            var dl = Txt(date.Length >= 5 ? date.Substring(5) : date, 11, false, _th.bSub, 0);
                            dl.VerticalAlignment = VerticalAlignment.Center;
                            Grid.SetColumn(dl, 0);
                            g.Children.Add(dl);
                            // 条形：两列按比例分（不需要等布局就能画）
                            var bar = new Grid();
                            bar.Height = 12 * S;
                            bar.Margin = new Thickness(0, 0, 8 * S, 0);
                            bar.VerticalAlignment = VerticalAlignment.Center;
                            double ratio = tot <= 0 ? 0.0 : Math.Max(0.02, (double)tot / maxv);
                            bar.ColumnDefinitions.Add(new ColumnDefinition
                            {
                                Width = new GridLength(ratio, GridUnitType.Star)
                            });
                            bar.ColumnDefinitions.Add(new ColumnDefinition
                            {
                                Width = new GridLength(Math.Max(0.0001, 1 - ratio), GridUnitType.Star)
                            });
                            var fill = new Border();
                            fill.CornerRadius = new CornerRadius(6 * S);
                            fill.Background = _th.bAccent;
                            fill.Opacity = date == DateTime.Now.ToString("yyyy-MM-dd") ? 1.0 : 0.55;
                            Grid.SetColumn(fill, 0);
                            bar.Children.Add(fill);
                            var tail = new Border();
                            tail.CornerRadius = new CornerRadius(6 * S);
                            tail.Background = _th.bBtn;
                            Grid.SetColumn(tail, 1);
                            bar.Children.Add(tail);
                            Grid.SetColumn(bar, 1);
                            g.Children.Add(bar);
                            var vl = Txt(FmtTok(tot) + (dcalls > 0 ? "  · " + dcalls + " 次" : ""),
                                         10.5, false, _th.bSub, 0);
                            vl.VerticalAlignment = VerticalAlignment.Center;
                            vl.HorizontalAlignment = HorizontalAlignment.Right;
                            Grid.SetColumn(vl, 2);
                            g.Children.Add(vl);
                            _tkList.Children.Add(g);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_tkStatus != null) _tkStatus.Text = "用量解析失败：" + ex.Message;
                    }
                }));
            });
        }

        void BuildPhotoLibCard(StackPanel b)
        {
            b.Children.Add(Txt("存一张图 + 一句备注，她就记住了。以后任何人再发同一张图"
                               + "（转发、压缩、缩放都算），她先查图库、命中就直接按备注说，不去瞎猜。",
                               11, false, _th.bSub, 2));

            var row = new WrapPanel();
            row.Margin = new Thickness(0, 10 * S, 0, 0);
            _plName = new TextBox();
            _plName.Width = 150 * S;
            _plName.Height = 30 * S;
            _plName.FontSize = 12.5 * S;
            _plName.Margin = new Thickness(0, 0, 8 * S, 8 * S);
            _plName.Background = _th.bBtn;
            _plName.Foreground = _th.bText;
            _plName.BorderBrush = _th.bLine;
            _plName.Padding = new Thickness(8 * S, 0, 8 * S, 0);
            StyleInput(_plName);
            Watermark(_plName, "名字（如 doro）");
            row.Children.Add(_plName);

            _plDesc = new TextBox();
            _plDesc.Width = 320 * S;
            _plDesc.Height = 30 * S;
            _plDesc.FontSize = 12.5 * S;
            _plDesc.Margin = new Thickness(0, 0, 8 * S, 8 * S);
            _plDesc.Background = _th.bBtn;
            _plDesc.Foreground = _th.bText;
            _plDesc.BorderBrush = _th.bLine;
            _plDesc.Padding = new Thickness(8 * S, 0, 8 * S, 0);
            StyleInput(_plDesc);
            Watermark(_plDesc, "描述（她会照这个说）");
            row.Children.Add(_plDesc);

            _plTags = new TextBox();
            _plTags.Width = 240 * S;
            _plTags.Height = 30 * S;
            _plTags.FontSize = 12.5 * S;
            _plTags.Margin = new Thickness(0, 0, 8 * S, 8 * S);
            _plTags.Background = _th.bBtn;
            _plTags.Foreground = _th.bText;
            _plTags.BorderBrush = _th.bLine;
            _plTags.Padding = new Thickness(8 * S, 0, 8 * S, 0);
            StyleInput(_plTags);
            Watermark(_plTags, "标签（逗号分隔：白毛,猫耳）");
            row.Children.Add(_plTags);

            row.Children.Add(Btn("选图并加入", true, delegate { PhotoLibAdd(); }));
            row.Children.Add(Btn("刷新", false, delegate { LoadPhotoLib(); LoadPhotoTags(); }));
            var clrBtn = Btn("清缓存", false, delegate { PhotoLibClearCache(); });
            clrBtn.Margin = new Thickness(8 * S, 0, 0, 0);
            row.Children.Add(clrBtn);
            b.Children.Add(row);

            // ---- AI 自动打标签（2026-11-02 用户要求）----
            // 开关 = 新图要不要进「待识别」队列；识别**只在你点「立即识别」时**才跑（本机 DSH headless 干活）。
            var aiRow = new WrapPanel();
            aiRow.Margin = new Thickness(0, 8 * S, 0, 0);
            _plAutoBtn = Btn("自动识图：…", false, delegate { ToggleAutoTag(); });
            _plAutoBtn.Padding = new Thickness(10 * S, 4 * S, 10 * S, 4 * S);
            _plAutoBtn.FontSize = 11 * S;
            _plAutoBtn.MinWidth = 0;
            _plAutoBtn.MinHeight = 0;
            _plAutoBtn.ToolTip = "开：新传的图自动进「待识别」队列（不会自己跑）；关：连队列都不进。";
            aiRow.Children.Add(_plAutoBtn);

            _plAutoRun = Btn("立即识别", true, delegate { RunAutoTag(); });
            _plAutoRun.Margin = new Thickness(8 * S, 0, 0, 0);
            _plAutoRun.Padding = new Thickness(12 * S, 4 * S, 12 * S, 4 * S);
            _plAutoRun.FontSize = 11 * S;
            _plAutoRun.MinWidth = 0;
            _plAutoRun.MinHeight = 0;
            _plAutoRun.ToolTip = "叫本机 DSH 来看图打标签（一批一起跑，不逐张）";
            aiRow.Children.Add(_plAutoRun);

            _plAutoInfo = Txt("", 10.5, false, _th.bSub, 0);
            _plAutoInfo.Margin = new Thickness(10 * S, 2 * S, 0, 0);
            aiRow.Children.Add(_plAutoInfo);
            b.Children.Add(aiRow);

            _plAutoLog = Txt("", 10, false, _th.bSub, 0);
            _plAutoLog.TextWrapping = TextWrapping.Wrap;
            _plAutoLog.MaxHeight = 130 * S;
            _plAutoLog.Margin = new Thickness(0, 6 * S, 0, 0);
            _plAutoLog.Visibility = Visibility.Collapsed;
            b.Children.Add(_plAutoLog);

            // 标签库：把图库里用过的标签列出来，点一下就加/去掉（2026-11-02 用户要求）
            // 2026-11-02 晚用户：「把这个标签放到抽屉里吧，这么多」→ 收成一行，点开才铺开
            var tagHead = new WrapPanel();
            tagHead.Margin = new Thickness(0, 4 * S, 0, 0);
            _plTagToggle = Btn("标签库 ▸", false, delegate { ToggleTagDrawer(!_plTagOpen); });
            _plTagToggle.Padding = new Thickness(10 * S, 3 * S, 10 * S, 3 * S);
            _plTagToggle.FontSize = 11 * S;
            _plTagToggle.MinWidth = 0;
            _plTagToggle.MinHeight = 0;
            _plTagToggle.ToolTip = "172 个标签太占地方，点开/收起（收起来不影响用：输入框照样能写字）";
            tagHead.Children.Add(_plTagToggle);

            // 标签搜索：中文、全拼、首字母都认（baimao / bm / 白毛 → 白毛）
            _plTagSearch = new TextBox();
            _plTagSearch.Width = 150 * S;
            _plTagSearch.Height = 26 * S;
            _plTagSearch.FontSize = 11.5 * S;
            _plTagSearch.Margin = new Thickness(8 * S, 0, 8 * S, 0);
            _plTagSearch.Background = _th.bBtn;
            _plTagSearch.Foreground = _th.bText;
            _plTagSearch.BorderBrush = _th.bLine;
            _plTagSearch.Padding = new Thickness(8 * S, 0, 8 * S, 0);
            _plTagSearch.VerticalAlignment = VerticalAlignment.Center;
            StyleInput(_plTagSearch);
            Watermark(_plTagSearch, "搜标签（模糊：中文/拼音/首字母）");
            _plTagSearch.TextChanged += delegate
            {
                _plTagQuery = (_plTagSearch.Text ?? "").Trim().ToLower();
                if (_plTagQuery.Length > 0) ToggleTagDrawer(true);   // 一搜就自动展开抽屉
                RenderTagChips();
            };
            tagHead.Children.Add(_plTagSearch);
            _plModeBtn = Btn("点标签：加进输入框", false, delegate
            {
                _plTagFilterMode = !_plTagFilterMode;
                if (_plModeBtn != null)
                    _plModeBtn.Content = _plTagFilterMode ? "点标签：只看这张" : "点标签：加进输入框";
                if (!_plTagFilterMode) SetPhotoLibFilter("");
            });
            _plModeBtn.Padding = new Thickness(10 * S, 3 * S, 10 * S, 3 * S);
            _plModeBtn.FontSize = 10.5 * S;
            _plModeBtn.MinWidth = 0;
            _plModeBtn.MinHeight = 0;
            tagHead.Children.Add(_plModeBtn);
            _plFilterInfo = Txt("", 10.5, false, _th.bSub, 0);
            _plFilterInfo.Margin = new Thickness(10 * S, 2 * S, 0, 0);
            tagHead.Children.Add(_plFilterInfo);
            b.Children.Add(tagHead);

            // 抽屉本体：默认收起，展开后固定高度（210）自己滚，撑不爆整页
            _plTagDrawer = new StackPanel();
            _plTagDrawer.Visibility = Visibility.Collapsed;

            _plTagBar = new WrapPanel();
            _plTagBar.Margin = new Thickness(0, 2 * S, 6 * S, 2 * S);
            var tagScroll = new ScrollViewer();
            tagScroll.Content = _plTagBar;
            tagScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            tagScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            tagScroll.MaxHeight = 210 * S;
            tagScroll.Margin = new Thickness(0, 4 * S, 0, 2 * S);
            tagScroll.Background = Brushes.Transparent;
            _plTagDrawer.Children.Add(tagScroll);

            _plTagHint = new WrapPanel();
            _plTagHint.Margin = new Thickness(0, 0, 0, 2 * S);
            _plTagDrawer.Children.Add(_plTagHint);
            b.Children.Add(_plTagDrawer);

            _plStatus = Txt("", 11.5, false, _th.bSub, 2);
            b.Children.Add(_plStatus);

            _plList = new WrapPanel();
            _plList.Margin = new Thickness(0, 8 * S, 0, 0);
            b.Children.Add(_plList);
            // 自检模糊搜索：`-shot botctl <png> tag:bm` → 预填搜索词再拍
            bool tagShot = ShotDemo != null && ShotDemo.StartsWith("tag:");
            if (ShotDemo != "photolib" && ShotDemo != "photolib2" && !tagShot)
            {
                LoadPhotoLib();
                LoadPhotoTags();
                LoadAutoTag();
            }
            else if (ShotDemo == "photolib2")
            {
                // 自检：**拉真数据**（含库列表），用来拍同名角色抽屉的样子
                LoadPhotoTags();
                LoadAutoTag();
                LoadPhotoLib();
            }
            else
            {
                // 自检截图：只拉标签（快），不拉整页缩略图
                LoadPhotoTags();
                LoadAutoTag();
                ToggleTagDrawer(true);
                if (tagShot && _plTagSearch != null)
                {
                    string q = ShotDemo.Substring(4);
                    // 等标签从服务器拉回来再填（拉完会自己重画）
                    var tmrSearch = new DispatcherTimer();
                    tmrSearch.Interval = TimeSpan.FromMilliseconds(700);
                    tmrSearch.Tick += delegate
                    {
                        if (_plTagData.Count == 0) return;      // 还没拉到，再等
                        tmrSearch.Stop();
                        _plTagSearch.Text = q;
                        _plTagQuery = q.Trim().ToLower();
                        ToggleTagDrawer(true);
                        RenderTagChips();
                    };
                    tmrSearch.Start();
                }
            }
        }

        /// <summary>图库里的一张：预览图 + 名字 + 标签（2026-11-02 用户要的样式）。
        ///
        /// `sameName` 非空 = 这个名字还有别的图 → 名字后面加一个小三角 ▸，
        /// 点它开一个单独窗口看这个角色的所有图（2026-11-03 用户要求）。
        /// </summary>
        FrameworkElement PhotoLibTile(string id, string name, string desc, string tags,
                                       int hits, bool hasThumb)
        {
            return PhotoLibTile(id, name, desc, tags, hits, hasThumb, null, null, 0);
        }

        FrameworkElement PhotoLibTile(string id, string name, string desc, string tags,
                                       int hits, bool hasThumb, string sameName, string albumId)
        {
            return PhotoLibTile(id, name, desc, tags, hits, hasThumb, sameName, albumId, 0);
        }

        FrameworkElement PhotoLibTile(string id, string name, string desc, string tags,
                                       int hits, bool hasThumb, string sameName, string albumId,
                                       int filePx)
        {
            var card = new Border();
            card.Width = 164 * S;
            card.Margin = new Thickness(0, 0, 12 * S, 12 * S);
            card.CornerRadius = new CornerRadius(12 * S);
            card.Background = _th.bBtn;
            card.BorderBrush = _th.bLine;
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(7 * S);
            if (desc.Length > 0) card.ToolTip = desc;
            // 多选模式：整张卡点一下就选中/取消（2026-11-03「多选移动」）
            if (_plSelMode)
            {
                bool sel = _plSel.Contains(id);
                if (sel)
                {
                    card.BorderBrush = _th.bAccent;
                    card.BorderThickness = new Thickness(2);
                }
                card.Cursor = Cursors.Hand;
                string cid = id;
                card.MouseLeftButtonUp += delegate
                {
                    if (_plSel.Contains(cid)) _plSel.Remove(cid); else _plSel.Add(cid);
                    RenderPhotoLib();
                };
            }

            var sp = new StackPanel();

            var pic = new Border();
            pic.CornerRadius = new CornerRadius(9 * S);
            pic.Background = _th.bBg;
            pic.Height = 148 * S;
            pic.ClipToBounds = true;
            if (hasThumb)
            {
                var img = new Image();
                img.Stretch = Stretch.UniformToFill;
                img.Height = 148 * S;
                img.Cursor = System.Windows.Input.Cursors.Hand;
                img.ToolTip = "点一下看大图";
                img.MouseLeftButtonUp += delegate { PhotoLibShowBig(img); };
                pic.Child = img;
                PhotoLibLoadThumb(id, img);
            }
            else
            {
                var ph = Txt("没有预览", 11, false, _th.bSub, 0);
                ph.HorizontalAlignment = HorizontalAlignment.Center;
                ph.VerticalAlignment = VerticalAlignment.Center;
                pic.Child = ph;
            }
            sp.Children.Add(pic);

            var nmRow = new WrapPanel();
            var nm = Txt(name, 12.5, true, _th.bText, 0);
            nm.TextWrapping = TextWrapping.Wrap;
            nm.Margin = new Thickness(0, 7 * S, 0, 0);
            nmRow.Children.Add(nm);
            if (!string.IsNullOrEmpty(sameName))
            {
                // 小三角：点开单独窗口看这个角色的所有图
                string capName = sameName;
                var tri = Txt(" ▸", 12, true, _th.bAccent, 0);
                tri.Cursor = Cursors.Hand;
                tri.ToolTip = "看「" + capName + "」的所有图";
                tri.Margin = new Thickness(2 * S, 7 * S, 0, 0);
                tri.MouseLeftButtonUp += delegate { ShowSameNameWindow(capName); };
                nmRow.Children.Add(tri);
            }
            sp.Children.Add(nmRow);

            var tg = Txt(tags, 10.5, false, _th.bAccent, 0);
            tg.TextWrapping = TextWrapping.Wrap;
            tg.Margin = new Thickness(0, 2 * S, 0, 0);
            tg.Visibility = tags.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            sp.Children.Add(tg);

            var ds = Txt(desc.Length > 16 ? desc.Substring(0, 16) + "…" : desc, 10, false, _th.bSub, 0);
            ds.TextWrapping = TextWrapping.Wrap;
            ds.Margin = new Thickness(0, 3 * S, 0, 0);
            ds.Visibility = desc.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            sp.Children.Add(ds);

            var meta = Txt("命中 " + hits, 10, false, _th.bSub, 0);
            meta.Margin = new Thickness(0, 2 * S, 0, 0);
            sp.Children.Add(meta);

            // 2026-11-03 用户：「图库里的图还是很模糊」——标出落盘分辨率；
            // 512 的那批当年被压过，重传一次原图就自动升级（`photolib_add` 的升级逻辑）
            if (filePx > 0 && filePx < 800)
            {
                var warn = Txt("⚠ " + filePx + "p 糊（重传原图可升级）", 9.5, false, _th.bSub, 0);
                warn.Margin = new Thickness(0, 1 * S, 0, 0);
                warn.TextWrapping = TextWrapping.Wrap;
                sp.Children.Add(warn);
            }
            else if (filePx > 0)
            {
                var okp = Txt(filePx + "p", 9.5, false, _th.bSub, 0);
                okp.Margin = new Thickness(0, 1 * S, 0, 0);
                sp.Children.Add(okp);
            }

            var btm = new WrapPanel();
            btm.Margin = new Thickness(0, 6 * S, 0, 0);
            var ed = Btn("改", false, delegate { PhotoLibEdit(id, name, desc, tags, albumId); });
            ed.Padding = new Thickness(9 * S, 2 * S, 9 * S, 2 * S);
            ed.FontSize = 10.5 * S;
            ed.MinWidth = 0;
            ed.MinHeight = 0;
            btm.Children.Add(ed);
            var del = Btn("删", false, delegate { PhotoLibDel(id, name); });
            del.Padding = new Thickness(9 * S, 2 * S, 9 * S, 2 * S);
            del.FontSize = 10.5 * S;
            del.MinWidth = 0;
            del.MinHeight = 0;
            del.Margin = new Thickness(6 * S, 0, 0, 0);
            btm.Children.Add(del);
            sp.Children.Add(btm);

            card.Child = sp;

            // 2026-11-02 用户：改一张标签就整页刷新太晃 —— 记住这张卡的文本控件，改完就地更新
            _plCardById[id] = card;
            _plTextById[id] = delegate(string nn, string dd, string tt)
            {
                nm.Text = nn;
                string tt2 = (tt ?? "").Trim();
                tg.Text = tt2;
                tg.Visibility = tt2.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                string dd2 = (dd ?? "").Trim();
                ds.Text = dd2.Length > 16 ? dd2.Substring(0, 16) + "…" : dd2;
                ds.Visibility = dd2.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                card.ToolTip = dd2.Length > 0 ? dd2 : null;
            };
            return card;
        }

        // ------------------------------------------------ AI 自动打标签（2026-11-02）
        /// <summary>从仓库根往下找工作目录里的脚本（跟 Plink() 一样不写死盘符）。</summary>
        static string RepoFile(string rel)
        {
            var tries = new List<string>();
            try
            {
                var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                for (int i = 0; i < 6 && d != null; i++, d = d.Parent)
                    tries.Add(Path.Combine(d.FullName, rel));
            }
            catch { }
            tries.Add(Path.Combine(@"F:\肥鱼机器人", rel));
            foreach (var t in tries) if (File.Exists(t)) return t;
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, rel);
        }

        void LoadAutoTag()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/photolib/autotag?token=" + Token, 25);
                Dispatcher.BeginInvoke(new Action(delegate { ApplyAutoTag(json); }));
            });
        }

        void ApplyAutoTag(string json)
        {
            try
            {
                var o = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                if (o == null) return;
                bool on = o.ContainsKey("enabled") && Convert.ToBoolean(o["enabled"]);
                int q = o.ContainsKey("queue") ? Convert.ToInt32(o["queue"]) : 0;
                int empty = o.ContainsKey("empty") ? Convert.ToInt32(o["empty"]) : 0;
                _plAutoEnabled = on;
                if (_plAutoBtn != null)
                {
                    _plAutoBtn.Content = on ? "自动识图：开（新图入队）" : "自动识图：关";
                    _plAutoBtn.Foreground = on ? _th.bAccent : _th.bSub;
                }
                if (_plAutoRun != null && !_plAutoRunning)
                    _plAutoRun.Content = q > 0 ? ("立即识别（" + q + " 张）") : "立即识别";
                string last = "";
                var l = (o.ContainsKey("last") ? o["last"] : null) as Dictionary<string, object>;
                if (l != null && l.Count > 0 && l.ContainsKey("at"))
                    last = " · 上次 " + Convert.ToString(l["at"])
                         + " 成功 " + Convert.ToString(l.ContainsKey("ok") ? l["ok"] : 0)
                         + " / 失败 " + Convert.ToString(l.ContainsKey("fail") ? l["fail"] : 0);
                if (_plAutoInfo != null)
                    _plAutoInfo.Text = "待识别 " + q + " 张 · 没标签 " + empty + " 张" + last;
            }
            catch { }
        }

        void AppendAutoLog(string line)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_plAutoLog == null) return;
                _plAutoLog.Visibility = Visibility.Visible;
                string t = _plAutoLog.Text;
                t = string.IsNullOrEmpty(t) ? line : (t + "\n" + line);
                var rows = t.Split('\n');
                if (rows.Length > 60) t = string.Join("\n", rows, rows.Length - 60, 60);
                _plAutoLog.Text = t;
            }));
        }

        void ToggleAutoTag()
        {
            bool want = !_plAutoEnabled;
            if (_plAutoInfo != null) _plAutoInfo.Text = "正在保存开关…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string body = "{\"enabled\": " + (want ? "true" : "false") + "}";
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                string cmd =
                    "printf '%s' '" + b64 + "' | base64 -d > /tmp/_at.json\n" +
                    "/opt/qqbot/.venv/bin/python - <<'PYEOF'\n" +
                    "import urllib.request\n" +
                    "b=open('/tmp/_at.json','rb').read()\n" +
                    "r=urllib.request.Request('http://127.0.0.1:8090/api/photolib/autotag?token=" + Token + "',data=b,headers={'Content-Type':'application/json','X-Token':'" + Token + "'})\n" +
                    "print(urllib.request.urlopen(r,timeout=30).read().decode())\n" +
                    "PYEOF";
                string r = Ssh.Run(_cfg, cmd, 60);
                Dispatcher.BeginInvoke(new Action(delegate { ApplyAutoTag(r); }));
            });
        }

        void RunAutoTag()
        {
            if (_plAutoRunning) return;
            _plAutoRunning = true;
            if (_plAutoRun != null) { _plAutoRun.IsEnabled = false; _plAutoRun.Content = "识别中…"; }
            if (_plAutoLog != null) { _plAutoLog.Text = ""; _plAutoLog.Visibility = Visibility.Visible; }
            AppendAutoLog("叫本机 DSH 看图打标签（手动触发；每批 12 张，自动跑到队列空）…");

            string script = RepoFile(@"04-脚本\工具\photolib_autotag.ps1");
            string scope = "queue";
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    // 队列空、但库里有没标签的 → 这次把没标签的补上。
                    // （2026-11-03 踩过：之前只跑一批就退出，83 张只处理 12 张，看着像失败）
                    if (_plAutoInfo != null && _plAutoInfo.Text.IndexOf("待识别 0 张") >= 0)
                        scope = "empty";
                    AppendAutoLog("范围：" + (scope == "queue" ? "待识别队列" : "所有没标签的")
                                  + "（脚本会一批批跑完）");
                    var psi = new ProcessStartInfo("pwsh",
                        "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -Scope " + scope)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    using (var p = Process.Start(psi))
                    {
                        string line;
                        while ((line = p.StandardOutput.ReadLine()) != null)
                        {
                            AppendAutoLog(line);
                            // 「第 N 批」那行顺便更新按钮文字，让人看得出在推进
                            if (line.IndexOf("批：队列里还有") >= 0 && _plAutoRun != null)
                            {
                                string l2 = line.Trim();
                                Dispatcher.BeginInvoke(new Action(delegate
                                {
                                    if (_plAutoRun != null) _plAutoRun.Content = "识别中…";
                                }));
                            }
                        }
                        string err = p.StandardError.ReadToEnd();
                        if (!string.IsNullOrEmpty(err)) AppendAutoLog("stderr: " + FirstLine(err));
                        p.WaitForExit();
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            AppendAutoLog(p.ExitCode == 0 ? "完成 ✅" : ("失败（退出码 " + p.ExitCode + "）"));
                        }));
                    }
                }
                catch (Exception ex) { AppendAutoLog("跑不起来：" + ex.Message); }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _plAutoRunning = false;
                    if (_plAutoRun != null) _plAutoRun.IsEnabled = true;
                    LoadAutoTag();
                    LoadPhotoLib();
                    LoadPhotoTags();
                }));
            });
        }

        /// <summary>`q` 是不是 `s` 的**子序列**（不要求连续、不要求顺序连续）。
        /// 2026-11-03 用户：「把标签搜索改成模糊搜索」——`bm` 能搜到 baimao、`bmo` 也能。</summary>
        static bool IsSubseq(string s, string q)
        {
            if (string.IsNullOrEmpty(q)) return true;
            if (string.IsNullOrEmpty(s)) return false;
            int j = 0;
            for (int i = 0; i < s.Length && j < q.Length; i++)
                if (s[i] == q[j]) j++;
            return j == q.Length;
        }

        /// <summary>单个关键词的贴合度：0 = 不匹配，越大越贴。
        /// 完全相等 100 → 开头 80 → 包含 60 → 子序列 30（模糊兜底）。
        ///
        /// 子序列只在**关键词够长**（≥3 个字母）或**含中文**时才用 ——
        /// 否则 `bm` 会把「半透明洋装」(bantouming…) 也匹配进来，噪音太大。
        /// </summary>
        static int TagScoreOne(string tag, string py, string ini, string q)
        {
            string t = (tag ?? "").ToLowerInvariant();
            string p = (py ?? "").ToLowerInvariant();
            string n = (ini ?? "").ToLowerInvariant();
            if (t == q || p == q || n == q) return 100;
            if (t.StartsWith(q) || n.StartsWith(q) || p.StartsWith(q)) return 80;
            if (t.Contains(q) || p.Contains(q) || n.Contains(q)) return 60;
            bool allowSub = q.Length >= 3;
            if (!allowSub)
            {
                foreach (char c in q) if (c > 127) { allowSub = true; break; }   // 中文够精确
            }
            if (allowSub && (IsSubseq(t, q) || IsSubseq(p, q) || IsSubseq(n, q))) return 30;
            return 0;
        }

        /// <summary>整条查询的贴合度：空格分隔多个词，**每个词都得中**（AND，都算模糊）。
        /// 返回 0 表示不匹配。</summary>
        static int TagScore(string tag, string py, string ini, string q)
        {
            if (string.IsNullOrEmpty(q)) return 1;
            int total = 0;
            foreach (var piece in q.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int s = TagScoreOne(tag, py, ini, piece);
                if (s == 0) return 0;
                total += s;
            }
            return total;
        }

        /// <summary>标签命中（模糊）。留着给老调用点用。</summary>
        static bool TagHit(string tag, string py, string ini, string q)
        {
            return TagScore(tag, py, ini, q) > 0;
        }

        /// <summary>按搜索框重画标签胶囊（2026-11-02 用户：「可以拼音搜索也可以中文搜索」）。</summary>
        /// <summary>标签属于哪个「首字母抽屉」：英文/数字走自身，中文用后端给的 py1 首字母。</summary>
        string TagGroupOf(string tag)
        {
            string[] py;
            if (_plTagPy.TryGetValue(tag, out py) && py != null && py.Length > 1 && py[1].Length > 0)
            {
                string c = py[1].Substring(0, 1).ToUpperInvariant();
                if (c.Length > 0 && c[0] >= 'A' && c[0] <= 'Z') return c;
            }
            if (!string.IsNullOrEmpty(tag))
            {
                char c0 = char.ToUpperInvariant(tag[0]);
                if (c0 >= 'A' && c0 <= 'Z') return c0.ToString();
                if (c0 >= '0' && c0 <= '9') return "#";
            }
            return "#";                     // 认不出拼音的（生僻字/生字）都归「#」
        }

        /// <summary>标签库按首字母分组，返回 首字母 → 排好序的标签列表。
        ///
        /// 2026-11-03 用户：「把标签搜索改成模糊搜索」——
        /// 搜索时**按贴合度排**（完全相等 > 开头 > 包含 > 子序列），同分再按拼音 A→Z。
        /// </summary>
        System.Collections.Generic.SortedDictionary<string, System.Collections.Generic.List<object[]>>
            GroupTags(string query)
        {
            bool searching = !string.IsNullOrEmpty(query);
            var g = new System.Collections.Generic.SortedDictionary<string, System.Collections.Generic.List<object[]>>(
                StringComparer.Ordinal);
            var scores = new System.Collections.Generic.Dictionary<object[], int>();
            foreach (var row in _plTagData)
            {
                string tag = Convert.ToString(row[0]);
                int sc = 0;
                if (searching)
                {
                    sc = TagScore(tag, Convert.ToString(row[2]), Convert.ToString(row[3]), query);
                    if (sc <= 0) continue;
                }
                string key = TagGroupOf(tag);
                System.Collections.Generic.List<object[]> list;
                if (!g.TryGetValue(key, out list))
                {
                    list = new System.Collections.Generic.List<object[]>();
                    g[key] = list;
                }
                list.Add(row);
                scores[row] = sc;
            }
            // 搜索时每组内部按贴合度重排（同分不动，保持原来的拼音顺序）
            if (searching)
            {
                foreach (var kv in g)
                {
                    var lst = kv.Value;
                    lst.Sort(delegate (object[] a, object[] b)
                    {
                        int sa, sb;
                        scores.TryGetValue(a, out sa);
                        scores.TryGetValue(b, out sb);
                        if (sa != sb) return sb.CompareTo(sa);   // 分高的在前
                        return 0;                                // 同分：稳定排序，保持拼音序
                    });
                }
            }
            // 「#」永远排最后（SortedDictionary 里 '#' 会在字母前面）
            if (g.ContainsKey("#"))
            {
                var sharp = g["#"];
                g.Remove("#");
                g["#"] = sharp;
            }
            return g;
        }

        void RenderTagChips()
        {
            if (_plTagBar == null) return;
            _plTagBar.Children.Clear();
            if (_plTagData.Count == 0)
            {
                _plTagBar.Children.Add(Txt(_plTagQuery.Length > 0 ? "没有匹配的标签" : "还没有标签：给图填一次就有了",
                                           10.5, false, _th.bSub, 0));
                return;
            }
            var groups = GroupTags(_plTagQuery);
            if (groups.Count == 0)
            {
                _plTagBar.Children.Add(Txt("没有匹配的标签（模糊搜：少打几个字母/打乱顺序也认，空格分开多个词）", 10.5, false, _th.bSub, 0));
                return;
            }
            // 搜索时直接全铺开（结果通常很少，再点抽屉就烦了）；
            // 不搜索时走通讯录式：顶部一排字母，点哪个就在下面铺开哪一组。
            bool searching = _plTagQuery.Length > 0;
            if (searching) _plTagGroupOpen = "";

            // 顶部「字母条」：每个字母一个，点一下就展开/收起那一组
            var bar = new WrapPanel();
            bar.Margin = new Thickness(0, 0, 0, 4 * S);
            foreach (var kv in groups)
            {
                string letter = kv.Key;
                var lb = new Button();
                lb.Content = letter + " " + kv.Value.Count;
                lb.Padding = new Thickness(7 * S, 2 * S, 7 * S, 3 * S);
                lb.Margin = new Thickness(0, 0, 4 * S, 4 * S);
                lb.MinWidth = 0;
                lb.MinHeight = 0;
                lb.FontSize = 10.5 * S;
                lb.Cursor = Cursors.Hand;
                lb.BorderThickness = new Thickness(1);
                lb.BorderBrush = _th.bLine;
                bool on = _plTagGroupOpen == letter;
                lb.Background = on ? _th.bAccent : _th.bBtn;
                lb.Foreground = on ? Brushes.White : _th.bSub;
                lb.ToolTip = "点开这一组（" + letter + " 开头，共 " + kv.Value.Count + " 个）";
                lb.Template = BtnTemplate();
                string cap = letter;
                lb.Click += delegate
                {
                    _plTagGroupOpen = (_plTagGroupOpen == cap) ? "" : cap;
                    RenderTagChips();
                };
                bar.Children.Add(lb);
            }
            _plTagBar.Children.Add(bar);

            int totalShown = 0;
            foreach (var kv in groups)
            {
                string letter = kv.Key;
                bool open = searching || _plTagGroupOpen == letter;
                if (!open) continue;
                foreach (var row in kv.Value)
                {
                    _plTagBar.Children.Add(TagChip(Convert.ToString(row[0]), Convert.ToInt32(row[1])));
                    totalShown++;
                }
            }
            if (_plTagGroupOpen.Length == 0 && !searching)
                totalShown = 0;

            _plTagBar.Children.Add(Txt(searching
                                       ? "匹配 " + totalShown + " 个 / 共 " + _plTagData.Count + " 个（已全部展开）"
                                       : (_plTagGroupOpen.Length > 0
                                          ? "「" + _plTagGroupOpen + "」组 " + totalShown + " 个 · 共 "
                                            + _plTagData.Count + " 个标签 · 再点一次那个字母收起"
                                          : "共 " + _plTagData.Count + " 个标签 · " + groups.Count
                                            + " 个字母抽屉，点上面任一字母展开"),
                                       10, false, _th.bSub, 0));
        }

        /// <summary>标签库抽屉：收起/展开（2026-11-02 晚用户：「这么多，放抽屉里」）。</summary>
        void ToggleTagDrawer(bool open)
        {
            _plTagOpen = open;
            if (_plTagDrawer != null)
                _plTagDrawer.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (_plTagToggle != null)
            {
                int n = _plTagNames.Count;
                _plTagToggle.Content = (n > 0 ? "标签库（" + n + " 个）" : "标签库") + (open ? " ▾" : " ▸");
            }
        }

        /// <summary>标签库：/api/photolib/tags 里已有标签，做成可点的小胶囊。</summary>
        void LoadPhotoTags()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/photolib/tags?token=" + Token, 25);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_plTagBar == null) return;
                    _plTagBar.Children.Clear();
                    try
                    {
                        var ser = new JavaScriptSerializer();
                        var o = ser.DeserializeObject(json) as Dictionary<string, object>;
                        var arr = (o != null && o.ContainsKey("tags")) ? o["tags"] as object[] : null;
                        if (arr == null || arr.Length == 0)
                        {
                            _plTagNames.Clear();
                            _plTagData.Clear();
                            _plTagPy.Clear();
                            RenderTagChips();
                            return;
                        }
                        _plTagNames.Clear();
                        _plTagData.Clear();
                        _plTagPy.Clear();
                        foreach (var itv in arr)
                        {
                            var it = itv as Dictionary<string, object>;
                            if (it == null) continue;
                            string tag = it.ContainsKey("tag") ? Convert.ToString(it["tag"]) : "";
                            int cnt = it.ContainsKey("count") ? Convert.ToInt32(it["count"]) : 0;
                            if (tag.Length == 0) continue;
                            string py = it.ContainsKey("py") ? Convert.ToString(it["py"]) : "";
                            string py1 = it.ContainsKey("py1") ? Convert.ToString(it["py1"]) : "";
                            _plTagNames.Add(tag);
                            _plTagData.Add(new object[] { tag, cnt, (py ?? "").ToLower(), (py1 ?? "").ToLower() });
                            _plTagPy[tag] = new string[] { (py ?? "").ToLower(), (py1 ?? "").ToLower() };
                        }
                        RenderTagChips();
                        // 抽屉按钮上显示个数（收起时也能看出有多少个）
                        if (_plTagToggle != null)
                            _plTagToggle.Content = "标签库（" + _plTagNames.Count + " 个）" + (_plTagOpen ? " ▾" : " ▸");
                        // 相近标签 → 一键合并
                        if (_plTagHint != null)
                        {
                            _plTagHint.Children.Clear();
                            var sim = (o != null && o.ContainsKey("similar")) ? o["similar"] as object[] : null;
                            if (sim != null && sim.Length > 0)
                            {
                                _plTagHint.Children.Add(Txt("很像的标签：", 10.5, false, _th.bSub, 0));
                                int shown = 0;
                                foreach (var sv in sim)
                                {
                                    var sp2 = sv as Dictionary<string, object>;
                                    if (sp2 == null) continue;
                                    string a = Convert.ToString(sp2["a"]), b = Convert.ToString(sp2["b"]);
                                    if (a.Length == 0 || b.Length == 0) continue;
                                    string keep = a.Length >= b.Length ? a : b;
                                    string drop = a.Length >= b.Length ? b : a;
                                    var mergeBtn = Btn("把「" + drop + "」并成「" + keep + "」", false,
                                                       delegate { MergeTag(drop, keep); });
                                    mergeBtn.Padding = new Thickness(8 * S, 2 * S, 8 * S, 2 * S);
                                    mergeBtn.FontSize = 10 * S;
                                    mergeBtn.MinWidth = 0;
                                    mergeBtn.MinHeight = 0;
                                    mergeBtn.Margin = new Thickness(0, 0, 6 * S, 4 * S);
                                    _plTagHint.Children.Add(mergeBtn);
                                    shown++;
                                    if (shown >= 4) break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _plTagBar.Children.Add(Txt("标签库读取失败：" + ex.Message, 10.5, false, _th.bSub, 0));
                    }
                }));
            });
        }

        FrameworkElement TagChip(string tag, int count)
        {
            return TagChip(tag, count, null);
        }

        FrameworkElement TagChip(string tag, int count, Action onClick)
        {
            return TagChip(tag, count, onClick, false);
        }

        /// <summary>strong=true：蓝白高对比（编辑弹窗里用，深色底上看久了不累）。</summary>
        FrameworkElement TagChip(string tag, int count, Action onClick, bool strong)
        {
            var bd = new Border();
            bd.CornerRadius = new CornerRadius(11 * S);
            // 蓝白高对比（编辑弹窗）；普通标签沿用主题色
            Brush bgChip = strong ? (Brush)new SolidColorBrush(Color.FromRgb(0x1B, 0x2A, 0x4A)) : _th.bBtn;
            Brush bdChip = strong ? (Brush)new SolidColorBrush(Color.FromRgb(0x3E, 0x5E, 0x93)) : _th.bLine;
            Brush fgChip = strong ? (Brush)new SolidColorBrush(Color.FromRgb(0xDF, 0xEB, 0xFF)) : _th.bAccent;
            Brush bgHover = new SolidColorBrush(Color.FromRgb(0x27, 0x3C, 0x66));
            bd.Background = bgChip;
            bd.BorderBrush = bdChip;
            bd.BorderThickness = new Thickness(1);
            bd.MouseEnter += delegate { if (strong) bd.Background = bgHover; };
            bd.MouseLeave += delegate { if (strong) bd.Background = bgChip; };
            bd.Padding = new Thickness(9 * S, 3 * S, 9 * S, 4 * S);
            bd.Margin = new Thickness(0, 0, 6 * S, 6 * S);
            bd.Cursor = Cursors.Hand;
            bd.ToolTip = "点一下加进标签框（已经在里面就去掉）";
            var sp = new WrapPanel();
            sp.Children.Add(Txt(tag, strong ? 12 : 11.5, false, fgChip, 0));
            if (count > 1)
            {
                var c = Txt(" " + count, 10, false, _th.bSub, 0);
                c.VerticalAlignment = VerticalAlignment.Center;
                sp.Children.Add(c);
            }
            bd.Child = sp;
            if (onClick != null) bd.MouseLeftButtonUp += delegate { onClick(); };
            else bd.MouseLeftButtonUp += delegate { OnTagClick(tag); };
            return bd;
        }

        void MergeTag(string from, string to)
        {
            if (_plStatus != null) _plStatus.Text = "正在把「" + from + "」并成「" + to + "」…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string fb = Convert.ToBase64String(Encoding.UTF8.GetBytes(from));
                string tb = Convert.ToBase64String(Encoding.UTF8.GetBytes(to));
                string cmd =
                    "printf '%s' '" + fb + "' | base64 -d > /tmp/tg_from.txt\n" +
                    "printf '%s' '" + tb + "' | base64 -d > /tmp/tg_to.txt\n" +
                    "/opt/qqbot/.venv/bin/python - <<'PYEOF'\n" +
                    "import json,urllib.request\n" +
                    "a=open('/tmp/tg_from.txt',encoding='utf-8').read().strip()\n" +
                    "b=open('/tmp/tg_to.txt',encoding='utf-8').read().strip()\n" +
                    "body=json.dumps({'from':a,'to':b}).encode()\n" +
                    "req=urllib.request.Request('http://127.0.0.1:8090/api/photolib/tag_merge?token=" + Token + "', data=body, headers={'Content-Type':'application/json'})\n" +
                    "print(urllib.request.urlopen(req,timeout=40).read().decode())\n" +
                    "PYEOF";
                string r = Ssh.Run(_cfg, cmd, 60);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool ok = r.IndexOf("\"ok\": true") >= 0 || r.IndexOf("\"ok\":true") >= 0;
                    if (_plStatus != null)
                        _plStatus.Text = ok ? ("已把「" + from + "」并成「" + to + "」") : ("合并失败：" + FirstLine(r));
                    if (ok) { LoadPhotoLib(); LoadPhotoTags(); }
                }));
            });
        }

        void OnTagClick(string tag)
        {
            // 筛选模式：点标签 = 只看这张；再点同一个 = 取消
            if (_plTagFilterMode)
            {
                SetPhotoLibFilter(_plFilterTag == tag ? "" : tag);
                return;
            }
            ToggleTag(tag);
        }

        void ToggleTag(string tag)
        {
            if (_plTags == null) return;
            var list = new System.Collections.Generic.List<string>();
            foreach (var p in (_plTags.Text ?? "").Split(new char[] { ',', '，', ' ', '、', ';', '；' }))
            {
                string t = p.Trim();
                if (t.Length > 0 && !list.Contains(t)) list.Add(t);
            }
            if (list.Contains(tag)) list.Remove(tag);
            else list.Add(tag);
            _plTags.Text = string.Join(",", list.ToArray());
        }

        void PhotoLibLoadThumb(string id, System.Windows.Controls.Image img)
        {
            BitmapImage hit;
            if (_plBmp.TryGetValue(id, out hit))     // 内存缓存
            {
                img.Source = hit;
                img.Tag = hit;
                return;
            }
            try
            {
                string fp = PhotoLibThumbPath(id);
                if (System.IO.File.Exists(fp))      // 磁盘缓存（换次打开控制台也秒出）
                {
                    var bm0 = new BitmapImage();
                    bm0.BeginInit();
                    bm0.CacheOption = BitmapCacheOption.OnLoad;
                    bm0.UriSource = new Uri(fp);
                    bm0.EndInit();
                    bm0.Freeze();
                    _plBmp[id] = bm0;
                    img.Source = bm0;
                    img.Tag = bm0;
                    return;
                }
            }
            catch { }
            ThreadPool.QueueUserWorkItem(delegate
            {
                string b64 = Ssh.Run(_cfg,
                    "curl -s -m 15 'http://127.0.0.1:8090/api/photolib/thumb?id=" + id
                    + "&token=" + Token + "' | base64 -w0", 30).Trim();
                if (string.IsNullOrEmpty(b64) || b64.Length < 100) return;
                try
                {
                    byte[] raw = Convert.FromBase64String(b64);
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        try
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.StreamSource = new MemoryStream(raw);
                            bmp.EndInit();
                            bmp.Freeze();
                            img.Source = bmp;
                            img.Tag = bmp;
                            _plBmp[id] = bmp;
                            try { System.IO.File.WriteAllBytes(PhotoLibThumbPath(id), raw); } catch { }
                        }
                        catch { }
                    }));
                }
                catch { }
            });
        }

        void PhotoLibShowBig(System.Windows.Controls.Image img)
        {
            try
            {
                var bm = img.Tag as BitmapImage;
                if (bm != null) ShowImageViewer(bm, "图库预览");
            }
            catch { }
        }

        /// <summary>按当前筛选把（缓存的）条目画一遍；缩略图有缓存就不重新下。
        ///
        /// 2026-11-03 用户：「顺便把同名角色也弄成抽屉」——同名多张的收成一行
        /// `▸ 调月莉音 9 张`，点开才铺开那 9 张卡；名字只有 1 张的直接平铺。
        /// </summary>
        /// <summary>图集名字（找不到返回空）。</summary>
        string AlbumNameOf(string aid)
        {
            foreach (var a in _plAlbums)
                if (Convert.ToString(a[0]) == str(aid)) return Convert.ToString(a[1]);
            return "";
        }

        static string str(string s) { return s ?? ""; }

        /// <summary>进某个图集：拉这个集的图并重画。</summary>
        void EnterAlbum(string aid)
        {
            _plAlbumId = aid ?? "";
            _plFilterTag = "";
            if (_plStatus != null) _plStatus.Text = "正在加载「" + AlbumNameOf(_plAlbumId) + "」…";
            LoadPhotoLib();
        }

        /// <summary>相册首页：一格一个图集（封面 + 名字 + 张数），点进去看图（2026-11-03 用户要求）。</summary>
        void RenderAlbumGrid()
        {
            if (_plList == null) return;
            if (_plAlbums.Count == 0)
            {
                _plList.Children.Add(Txt("图集还没读出来（点「刷新」试试）。", 11.5, false, _th.bSub, 4));
                return;
            }
            // 顶部操作行：新建图集
            var ops = new WrapPanel();
            ops.Margin = new Thickness(0, 0, 0, 10 * S);
            var nb = Btn("＋ 新建图集", false, delegate { AlbumCreateDialog(); });
            nb.Padding = new Thickness(10 * S, 3 * S, 10 * S, 3 * S);
            nb.FontSize = 10.5 * S;
            nb.MinWidth = 0;
            nb.MinHeight = 0;
            ops.Children.Add(nb);
            ops.Children.Add(Txt("  共 " + _plAlbums.Count + " 个图集，点封面进去看",
                                 10.5, false, _th.bSub, 0));
            _plList.Children.Add(ops);

            var flow = new WrapPanel();
            foreach (var a in _plAlbums)
            {
                string aid = Convert.ToString(a[0]);
                string anm = Convert.ToString(a[1]);
                int acnt = Convert.ToInt32(a[2]);
                flow.Children.Add(AlbumTile(aid, anm, acnt));
            }
            _plList.Children.Add(flow);
            if (_plStatus != null)
                _plStatus.Text = "图集 " + _plAlbums.Count + " 个 · 图库共 " + _plLastCount;
        }

        /// <summary>「未分类」集的 id（它不能删，是兜底的）。</summary>
        string DefaultAlbumId()
        {
            foreach (var a in _plAlbums)
                if (Convert.ToString(a[1]) == "未分类") return Convert.ToString(a[0]);
            return "";
        }

        /// <summary>删图集前确认：里面的图会回到「未分类」。</summary>
        void AlbumDeleteConfirm(string aid, string name, int count)
        {
            var w = new Window();
            w.Title = "删除图集";
            w.Width = 400 * S;
            w.SizeToContent = SizeToContent.Height;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            w.Owner = this;
            w.Background = _th.bCard;
            var sp = new StackPanel();
            sp.Margin = new Thickness(16 * S);
            var t = Txt("删掉图集「" + name + "」？", 13, true, _th.bText, 0);
            t.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(t);
            var d = Txt("里面的 " + count + " 张图**不会删**，会回到「未分类」。",
                        11.5, false, _th.bSub, 8);
            d.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(d);
            var row = new WrapPanel();
            row.Margin = new Thickness(0, 10 * S, 0, 0);
            row.Children.Add(Btn("确定删除", true, delegate
            {
                w.Close();
                AlbumPost("/api/photolib/album/del", "{\"id\":\"" + JsonEsc(aid) + "\"}");
            }));
            var cc = Btn("取消", false, delegate { w.Close(); });
            cc.Margin = new Thickness(8 * S, 0, 0, 0);
            row.Children.Add(cc);
            sp.Children.Add(row);
            w.Content = sp;
            w.ShowDialog();
        }

        /// <summary>一个图集格子：封面 + 名字 + 张数；右键/长按出「改名 / 删除」。</summary>
        FrameworkElement AlbumTile(string aid, string name, int count)
        {
            var card = new Border();
            card.Width = 150 * S;
            card.Margin = new Thickness(0, 0, 12 * S, 12 * S);
            card.CornerRadius = new CornerRadius(12 * S);
            card.Background = _th.bBtn;
            card.BorderBrush = _th.bLine;
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(7 * S);
            card.Cursor = Cursors.Hand;
            card.ToolTip = "点进去看这 " + count + " 张";
            var sp = new StackPanel();

            var pic = new Border();
            pic.CornerRadius = new CornerRadius(9 * S);
            pic.Background = _th.bBg;
            pic.Height = 120 * S;
            pic.ClipToBounds = true;
            string cover = AlbumCoverOf(aid);
            if (cover.Length > 0)
            {
                var img = new Image();
                img.Stretch = Stretch.UniformToFill;
                img.Height = 120 * S;
                pic.Child = img;
                PhotoLibLoadThumb(cover, img);
            }
            else
            {
                var ph = Txt("空图集", 11, false, _th.bSub, 0);
                ph.HorizontalAlignment = HorizontalAlignment.Center;
                ph.VerticalAlignment = VerticalAlignment.Center;
                pic.Child = ph;
            }
            sp.Children.Add(pic);

            var nm = Txt(name, 12.5, true, _th.bText, 0);
            nm.TextWrapping = TextWrapping.Wrap;
            nm.Margin = new Thickness(0, 6 * S, 0, 0);
            sp.Children.Add(nm);

            var cnt = Txt(count + " 张", 10.5, false, _th.bSub, 0);
            cnt.Margin = new Thickness(0, 1 * S, 0, 0);
            sp.Children.Add(cnt);

            var ops2 = new WrapPanel();
            ops2.Margin = new Thickness(0, 4 * S, 0, 0);
            var rn = Btn("改名", false, delegate { AlbumRenameDialog(aid, name); });
            rn.Padding = new Thickness(8 * S, 2 * S, 8 * S, 2 * S);
            rn.FontSize = 9.5 * S;
            rn.MinWidth = 0;
            rn.MinHeight = 0;
            ops2.Children.Add(rn);
            // 「未分类」是兜底的，不给删
            if (aid != DefaultAlbumId())
            {
                var dl = Btn("删除", false, delegate { AlbumDeleteConfirm(aid, name, count); });
                dl.Padding = new Thickness(8 * S, 2 * S, 8 * S, 2 * S);
                dl.FontSize = 9.5 * S;
                dl.MinWidth = 0;
                dl.MinHeight = 0;
                dl.Margin = new Thickness(5 * S, 0, 0, 0);
                dl.Foreground = _th.bSub;
                ops2.Children.Add(dl);
            }
            sp.Children.Add(ops2);

            card.Child = sp;
            string cap = aid;
            card.MouseLeftButtonUp += delegate { EnterAlbum(cap); };
            return card;
        }

        /// <summary>这个图集的封面 id（从当前缓存的条目里找，命中最高那张）。</summary>
        string AlbumCoverOf(string aid)
        {
            foreach (var itv in _plItems)
            {
                var it = itv as Dictionary<string, object>;
                if (it == null) continue;
                if (Convert.ToString(it.ContainsKey("album") ? it["album"] : "") != aid) continue;
                string pid = Convert.ToString(it.ContainsKey("id") ? it["id"] : "");
                if (pid.Length > 0) return pid;
            }
            return "";
        }

        void AlbumCreateDialog()
        {
            string n = SimpleInput("新建图集", "图集名字（比如「白毛」「夏日」）", "");
            if (n == null || n.Trim().Length == 0) return;
            AlbumPost("/api/photolib/album/new", "{\"name\":\"" + JsonEsc(n.Trim()) + "\"}");
        }

        void AlbumRenameDialog(string aid, string old)
        {
            string n = SimpleInput("图集改名", "新名字", old);
            if (n == null || n.Trim().Length == 0 || n.Trim() == old) return;
            AlbumPost("/api/photolib/album/rename",
                      "{\"id\":\"" + JsonEsc(aid) + "\",\"name\":\"" + JsonEsc(n.Trim()) + "\"}");
        }

        /// <summary>改图集之后刷新（列表 + 图集）。</summary>
        void AlbumPost(string api, string jsonBody)
        {
            if (_plStatus != null) _plStatus.Text = "正在保存…";
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(jsonBody));
            ThreadPool.QueueUserWorkItem(delegate
            {
                string cmd =
                    "printf '%s' '" + b64 + "' | base64 -d > /tmp/_alb.json\n" +
                    "/opt/qqbot/.venv/bin/python - <<'PYEOF'\n" +
                    "import urllib.request\n" +
                    "b=open('/tmp/_alb.json','rb').read()\n" +
                    "r=urllib.request.Request('http://127.0.0.1:8090" + api + "?token=" + Token + "',data=b,headers={'Content-Type':'application/json','X-Token':'" + Token + "'})\n" +
                    "print(urllib.request.urlopen(r,timeout=40).read().decode())\n" +
                    "PYEOF";
                string r = Ssh.Run(_cfg, cmd, 60);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_plStatus != null) _plStatus.Text = "已保存";
                    LoadPhotoLib();
                }));
            });
        }

        static string JsonEsc(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>一个简单的单行输入弹窗。</summary>
        string SimpleInput(string title, string label, string def)
        {
            var w = new Window();
            w.Title = title;
            w.Width = 380 * S;
            w.SizeToContent = SizeToContent.Height;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            w.Owner = this;
            w.Background = _th.bCard;
            var sp = new StackPanel();
            sp.Margin = new Thickness(16 * S);
            sp.Children.Add(Txt(label, 11.5, false, _th.bSub, 0));
            var tb = new TextBox();
            tb.Text = def ?? "";
            tb.FontSize = 13 * S;
            tb.Height = 32 * S;
            tb.Margin = new Thickness(0, 6 * S, 0, 0);
            tb.Background = _th.bBtn;
            tb.Foreground = _th.bText;
            tb.BorderBrush = _th.bLine;
            tb.Padding = new Thickness(8 * S, 0, 8 * S, 0);
            sp.Children.Add(tb);
            string res = null;
            var row = new WrapPanel();
            row.Margin = new Thickness(0, 12 * S, 0, 0);
            row.Children.Add(Btn("确定", true, delegate { res = tb.Text; w.Close(); }));
            var cc = Btn("取消", false, delegate { w.Close(); });
            cc.Margin = new Thickness(8 * S, 0, 0, 0);
            row.Children.Add(cc);
            sp.Children.Add(row);
            w.Content = sp;
            w.ShowDialog();
            return res;
        }

        void RenderPhotoLib()
        {
            if (_plList == null) return;
            _plList.Children.Clear();
            _plCardById.Clear();
            _plTextById.Clear();
            string want = (_plFilterTag ?? "").Trim();

            // 2026-11-03 用户：「照搬手机相册」——没进图集时显示**相册首页**（一格一个封面的图集）
            if ((_plAlbumId ?? "").Length == 0 && want.Length == 0)
            {
                RenderAlbumGrid();
                return;
            }
            if ((_plAlbumId ?? "").Length > 0)
            {
                var back = new WrapPanel();
                back.Margin = new Thickness(0, 0, 0, 8 * S);
                var bb = Btn("← 相册", false, delegate
                {
                    _plAlbumId = "";
                    _plFilterTag = "";
                    if (_plStatus != null) _plStatus.Text = "";
                    LoadPhotoLib();
                    RenderPhotoLib();
                });
                bb.Padding = new Thickness(10 * S, 3 * S, 10 * S, 3 * S);
                bb.FontSize = 11 * S;
                bb.MinWidth = 0;
                bb.MinHeight = 0;
                back.Children.Add(bb);
                string an = AlbumNameOf(_plAlbumId);
                back.Children.Add(Txt("  图集：" + an, 11.5, true, _th.bText, 0));

                // 多选移动（2026-11-03 用户：「可以随时多选移动图集」）
                var selBtn = Btn(_plSelMode ? "退出多选" : "多选", false, delegate
                {
                    _plSelMode = !_plSelMode;
                    if (!_plSelMode) _plSel.Clear();
                    RenderPhotoLib();
                });
                selBtn.Padding = new Thickness(9 * S, 3 * S, 9 * S, 3 * S);
                selBtn.FontSize = 10.5 * S;
                selBtn.MinWidth = 0;
                selBtn.MinHeight = 0;
                selBtn.Margin = new Thickness(10 * S, 0, 0, 0);
                back.Children.Add(selBtn);

                if (_plSelMode)
                {
                    var mv = new ComboBox();
                    mv.FontSize = 11 * S;
                    mv.Height = 26 * S;
                    mv.MinWidth = 110 * S;
                    mv.Margin = new Thickness(8 * S, 0, 0, 0);
                    mv.Background = _th.bBtn;
                    mv.Foreground = _th.bText;
                    mv.BorderBrush = _th.bLine;
                    ComboStyle(mv);
                    var mvid = new System.Collections.Generic.List<string>();
                    foreach (var a in _plAlbums)
                    {
                        mvid.Add(Convert.ToString(a[0]));
                        mv.Items.Add(Convert.ToString(a[1]) + "（" + Convert.ToInt32(a[2]) + " 张）");
                    }
                    if (mv.Items.Count > 0) mv.SelectedIndex = 0;
                    back.Children.Add(mv);
                    var go = Btn("移到这（" + _plSel.Count + " 张）", true, delegate
                    {
                        if (_plSel.Count == 0) return;
                        if (mv.SelectedIndex < 0 || mv.SelectedIndex >= mvid.Count) return;
                        var ids = new System.Text.StringBuilder();
                        foreach (var s2 in _plSel)
                        {
                            if (ids.Length > 0) ids.Append(",");
                            ids.Append("\"" + JsonEsc(s2) + "\"");
                        }
                        string target = mvid[mv.SelectedIndex];
                        AlbumPost("/api/photolib/album/move",
                                  "{\"ids\":[" + ids + "],\"album\":\"" + JsonEsc(target) + "\"}");
                        _plSel.Clear();
                        _plSelMode = false;
                    });
                    go.Padding = new Thickness(9 * S, 3 * S, 9 * S, 3 * S);
                    go.FontSize = 10.5 * S;
                    go.MinWidth = 0;
                    go.MinHeight = 0;
                    go.Margin = new Thickness(8 * S, 0, 0, 0);
                    back.Children.Add(go);
                    back.Children.Add(Txt("  点图片选中（" + _plSel.Count + " 张）", 10.5, false, _th.bSub, 0));
                }
                _plList.Children.Add(back);
            }

            // 先把命中筛选的条目按名字分组（保持原来的先后顺序）
            var order = new System.Collections.Generic.List<string>();
            var groups = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<object>>();
            foreach (var itv in _plItems)
            {
                var it = itv as Dictionary<string, object>;
                if (it == null) continue;
                string tags = "";
                if (it.ContainsKey("tags") && it["tags"] is object[])
                {
                    var tl = new System.Collections.Generic.List<string>();
                    foreach (var tg in (object[])it["tags"]) { string sg = Convert.ToString(tg); if (sg.Length > 0) tl.Add(sg); }
                    tags = string.Join(" ", tl.ToArray());
                }
                if (want.Length > 0)
                {
                    bool hit = false;
                    foreach (var tg in tags.Split(' '))
                        if (tg == want) { hit = true; break; }
                    if (!hit) continue;
                }
                string nm = it.ContainsKey("name") ? Convert.ToString(it["name"]) : "";
                if (!groups.ContainsKey(nm))
                {
                    groups[nm] = new System.Collections.Generic.List<object>();
                    order.Add(nm);
                }
                groups[nm].Add(new object[] { it, tags });
            }

            int shown = 0;
            // 2026-11-03 用户：「我想要的是还是之前的样子，只不过每张缩略图后面有一个小三角，
            // 点开之后是单独的窗口展示同名角色」→ 恢复**平铺**，分组只做在弹窗里。
            foreach (var nm in order)
            {
                int same = groups[nm].Count;      // 这个名字一共有几张
                foreach (var o in groups[nm])
                {
                    var arr = o as object[];
                    var it = (arr != null && arr.Length > 0) ? arr[0] as Dictionary<string, object> : null;
                    string tags2 = (arr != null && arr.Length > 1) ? Convert.ToString(arr[1]) : "";
                    _plList.Children.Add(it == null
                        ? Txt("（这条读不出来）", 11, false, _th.bSub, 0)
                        : TileFrom2(it, tags2, same > 1 ? nm : null));
                    shown++;
                }
            }

            if (_plStatus != null)
            {
                _plStatus.Text = want.Length > 0
                    ? ("只看「" + want + "」：" + shown + " / " + _plLastCount)
                    : _plLastCount;
            }
            if (_plFilterInfo != null)
                _plFilterInfo.Text = want.Length > 0
                    ? ("筛选：" + want + "（点同一个标签可取消）")
                    : ("");
            if (shown == 0 && _plItems.Count > 0)
                _plList.Children.Add(Txt("没有带「" + want + "」标签的图。", 11.5, false, _th.bSub, 4));
            else if (_plItems.Count == 0)
                _plList.Children.Add(Txt("还是空的：上面填名字和描述，点「选图并加入」挑一张图。",
                                         11.5, false, _th.bSub, 4));
        }

        /// <summary>一条 → 图库卡片。`sameName` 非空表示它还有同名伙伴（加小三角）。</summary>
        FrameworkElement TileFrom2(Dictionary<string, object> it, string tags, string sameName)
        {
            string id = it.ContainsKey("id") ? Convert.ToString(it["id"]) : "";
            string name = it.ContainsKey("name") ? Convert.ToString(it["name"]) : "";
            string desc = it.ContainsKey("desc") ? Convert.ToString(it["desc"]) : "";
            bool hasThumb = it.ContainsKey("thumb") && Convert.ToBoolean(it["thumb"]);
            int hits = it.ContainsKey("hits") ? Convert.ToInt32(it["hits"]) : 0;
            string albumId = it.ContainsKey("album") ? Convert.ToString(it["album"]) : "";
            int filePx = it.ContainsKey("px") ? Convert.ToInt32(it["px"]) : 0;
            return PhotoLibTile(id, name, desc, tags, hits, hasThumb, sameName, albumId, filePx);
        }

        /// <summary>同名角色窗口：单独一个窗，把这个名字的所有图铺开（2026-11-03 用户要求）。
        ///
        /// 不动主界面：点卡片名字后面的小三角才开，关掉就没了。
        /// </summary>
        void ShowSameNameWindow(string name)
        {
            Window win = BuildSameNameWindow(name);
            if (win == null) return;
            win.Owner = this;
            win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            win.Show();
        }

        /// <summary>自检用：只构造窗口不 Show，交给截图器渲染（`-shot samewin:名字`）。</summary>
        public Window BuildSameNameWindow(string name)
        {
            var hits = new System.Collections.Generic.List<object[]>();
            foreach (var itv in _plItems)
            {
                var it = itv as Dictionary<string, object>;
                if (it == null) continue;
                string nm = it.ContainsKey("name") ? Convert.ToString(it["name"]) : "";
                if (nm != name) continue;
                string tags = "";
                if (it.ContainsKey("tags") && it["tags"] is object[])
                {
                    var tl = new System.Collections.Generic.List<string>();
                    foreach (var tg in (object[])it["tags"]) { string sg = Convert.ToString(tg); if (sg.Length > 0) tl.Add(sg); }
                    tags = string.Join(" ", tl.ToArray());
                }
                hits.Add(new object[] { it, tags });
            }
            if (hits.Count == 0) return null;
            var win = new Window();
            win.Title = name + "（" + hits.Count + " 张）";
            win.Width = 900;
            win.Height = 640;
            win.Background = _th.bBg;
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var head = new TextBlock();
            head.Text = "「" + name + "」一共 " + hits.Count + " 张（点图看大图；改/删都在卡片上）";
            head.FontSize = 12 * S;
            head.Foreground = _th.bSub;
            head.Margin = new Thickness(14 * S, 10 * S, 14 * S, 4 * S);
            Grid.SetRow(head, 0);
            root.Children.Add(head);
            var flow = new WrapPanel();
            flow.Margin = new Thickness(10 * S, 0, 10 * S, 10 * S);
            foreach (var o in hits)
            {
                var arr = o as object[];
                flow.Children.Add(TileFrom2(arr[0] as Dictionary<string, object>, Convert.ToString(arr[1]), null));
            }
            var sv = new ScrollViewer();
            sv.Content = flow;
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            Grid.SetRow(sv, 1);
            root.Children.Add(sv);
            win.Content = root;
            return win;
        }

        void SetPhotoLibFilter(string tag)
        {
            _plFilterTag = (tag ?? "").Trim();
            RenderPhotoLib();
        }

        static string PhotoLibCacheDir()
        {
            string d = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FishConsole", "photolib");
            try { System.IO.Directory.CreateDirectory(d); } catch { }
            return d;
        }

        static string PhotoLibThumbPath(string id)
        {
            string safe = (id ?? "").Trim();
            foreach (var ch in System.IO.Path.GetInvalidFileNameChars()) safe = safe.Replace(ch, '_');
            return System.IO.Path.Combine(PhotoLibCacheDir(), safe + ".jpg");
        }

        static string PhotoLibListCachePath()
        {
            return System.IO.Path.Combine(PhotoLibCacheDir(), "list.json");
        }

        void PhotoLibSaveListCache(string json)
        {
            try
            {
                if (!string.IsNullOrEmpty(json) && json.Length > 20)
                    System.IO.File.WriteAllText(PhotoLibListCachePath(), json, Encoding.UTF8);
            }
            catch { }
        }

        string PhotoLibReadListCache()
        {
            try
            {
                string p = PhotoLibListCachePath();
                if (System.IO.File.Exists(p)) return System.IO.File.ReadAllText(p, Encoding.UTF8);
            }
            catch { }
            return "";
        }

        /// <summary>把 /api/photolib 的 JSON 变成卡片（实时拉的和磁盘缓存共用）。</summary>
        void PhotoLibApplyJson(string json)
        {
            try
            {
                var ser = new JavaScriptSerializer();
                var o = ser.DeserializeObject(json) as Dictionary<string, object>;
                if (o == null)
                {
                    if (_plStatus != null) _plStatus.Text = "图库读取失败：" + FirstLine(json);
                    return;
                }
                int count = o.ContainsKey("count") ? Convert.ToInt32(o["count"]) : 0;
                var items = o.ContainsKey("items") ? o["items"] as object[] : null;
                _plItems.Clear();
                if (items != null) foreach (var x in items) _plItems.Add(x);
                _plLastCount = "图库共 " + count + " 张";
                RenderPhotoLib();
            }
            catch (Exception ex)
            {
                if (_plStatus != null) _plStatus.Text = "图库解析失败：" + ex.Message;
            }
        }

        void LoadPhotoLib()
        {
            // 先用磁盘缓存把界面画出来（进图库瞬间就有内容），再去服务器对一遍
            if (_plItems.Count == 0)
            {
                string cached = PhotoLibReadListCache();
                if (cached.Length > 20) PhotoLibApplyJson(cached);
            }
            string alb = _plAlbumId ?? "";
            // 2026-11-07：列表和相册**并行**拉（以前一前一后，等两次 SSH 的往返时间）
            string json = null;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    json = Ssh.Curl(_cfg, "/api/photolib" +
                                    (alb.Length > 0 ? "?album=" + Uri.EscapeDataString(alb) : "") +
                                    (alb.Length > 0 ? "&" : "?") + "token=" + Token, 30);
                }
                catch { json = "{}"; }
            });
            ThreadPool.QueueUserWorkItem(delegate
            {
                string aj = null;
                try { aj = Ssh.Curl(_cfg, "/api/photolib/albums?token=" + Token, 30); }
                catch { aj = "{}"; }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    ApplyAlbums(aj);
                    string j = json;
                    if (string.IsNullOrEmpty(j))
                    {
                        // 列表还没回来：再等它一下（多数情况两边差不多同时到）
                        ThreadPool.QueueUserWorkItem(delegate
                        {
                            for (int i = 0; i < 60 && string.IsNullOrEmpty(json); i++)
                                System.Threading.Thread.Sleep(100);
                            string j2 = json;
                            Dispatcher.BeginInvoke(new Action(delegate
                            {
                                if (!string.IsNullOrEmpty(j2))
                                {
                                    PhotoLibApplyJson(j2);
                                    PhotoLibSaveListCache(j2);
                                }
                            }));
                        });
                    }
                    else
                    {
                        PhotoLibApplyJson(j);
                        PhotoLibSaveListCache(j);
                    }
                }));
            });
        }

        /// <summary>只重新拉一次相册（格子/计数），**不碰图库列表** —— 换相册后用。</summary>
        void LoadPhotoAlbumsOnly()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string aj = Ssh.Curl(_cfg, "/api/photolib/albums?token=" + Token, 30);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    ApplyAlbums(aj);
                    RenderPhotoLib();
                }));
            });
        }

        /// <summary>把 /api/photolib/albums 的结果存进 _plAlbums（编辑弹窗的下拉也用它）。</summary>
        void ApplyAlbums(string json)
        {
            try
            {
                var o = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                if (o == null) return;
                var arr = (o.ContainsKey("albums") ? o["albums"] : null) as object[];
                if (arr == null) return;
                _plAlbums.Clear();
                foreach (var itv in arr)
                {
                    var a = itv as Dictionary<string, object>;
                    if (a == null) continue;
                    _plAlbums.Add(new object[] {
                        Convert.ToString(a.ContainsKey("id") ? a["id"] : ""),
                        Convert.ToString(a.ContainsKey("name") ? a["name"] : ""),
                        a.ContainsKey("count") ? Convert.ToInt32(a["count"]) : 0
                    });
                }
            }
            catch { }
        }

        /// <summary>清掉磁盘缓存（缩略图 + 列表），下次进来重新拉一遍。</summary>
        void PhotoLibClearCache()
        {
            try
            {
                string d = PhotoLibCacheDir();
                foreach (var f in System.IO.Directory.GetFiles(d))
                {
                    try { System.IO.File.Delete(f); } catch { }
                }
                _plBmp.Clear();
                if (_plStatus != null) _plStatus.Text = "缓存清掉了（下次进来重新拉）";
            }
            catch (Exception ex)
            {
                if (_plStatus != null) _plStatus.Text = "清缓存失败：" + ex.Message;
            }
        }

        /// <summary>改名字/描述的小弹窗。返回 [名字, 描述]，取消返回 null。</summary>
        string[] PromptEdit(string name, string desc, string tags, string albumId)
        {
            var w = new Window();
            w.Title = "编辑图库条目";
            w.Width = 500 * S;
            // 内容有多少就多高（加了标签框之后固定 300 会把按钮挤出去）
            w.SizeToContent = SizeToContent.Height;
            w.MaxHeight = 720 * S;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            w.Owner = this;
            w.Background = _th.bCard;
            var sp = new StackPanel();
            sp.Margin = new Thickness(16 * S);
            sp.Children.Add(Txt("名字（同一个名字可以挂多张图）", 11.5, false, _th.bSub, 0));
            var t1 = new TextBox();
            t1.Text = name;
            t1.FontSize = 13 * S;
            t1.Height = 32 * S;
            t1.Margin = new Thickness(0, 4 * S, 0, 0);
            t1.Background = _th.bBtn;
            t1.Foreground = _th.bText;
            t1.BorderBrush = _th.bLine;
            t1.Padding = new Thickness(8 * S, 0, 8 * S, 0);
            sp.Children.Add(t1);
            sp.Children.Add(Txt("描述（她照这个说；也可以写上特征，方便按描述词认出来）", 11.5, false, _th.bSub, 10));
            var t2 = new TextBox();
            t2.Text = desc;
            t2.FontSize = 13 * S;
            t2.Height = 78 * S;
            t2.Margin = new Thickness(0, 4 * S, 0, 0);
            t2.Background = _th.bBtn;
            t2.Foreground = _th.bText;
            t2.BorderBrush = _th.bLine;
            t2.Padding = new Thickness(8 * S);
            t2.TextWrapping = TextWrapping.Wrap;
            t2.AcceptsReturn = true;
            sp.Children.Add(t2);
            sp.Children.Add(Txt("标签（逗号分隔：白毛,猫耳；不会写就留空）", 11.5, false, _th.bSub, 10));
            var t3 = new TextBox();
            t3.Text = tags;
            t3.FontSize = 13 * S;
            t3.Height = 32 * S;
            t3.Margin = new Thickness(0, 4 * S, 0, 0);
            t3.Background = _th.bBtn;
            t3.Foreground = _th.bText;
            t3.BorderBrush = _th.bLine;
            t3.Padding = new Thickness(8 * S, 0, 8 * S, 0);
            sp.Children.Add(t3);

            // 图集下拉（2026-11-03 用户：「改图片标签的时候一键点击分类到哪个相册」）
            var albumBox = new ComboBox();
            albumBox.FontSize = 12.5 * S;
            albumBox.Height = 30 * S;
            albumBox.Margin = new Thickness(0, 4 * S, 0, 0);
            albumBox.Background = _th.bBtn;
            albumBox.Foreground = _th.bText;
            albumBox.BorderBrush = _th.bLine;
            ComboStyle(albumBox);
            var albumIds = new System.Collections.Generic.List<string>();
            if (_plAlbums.Count > 0)
            {
                sp.Children.Add(Txt("图集（放进哪个相册）", 11.5, false, _th.bSub, 10));
                int sel = 0;
                foreach (var a in _plAlbums)
                {
                    string aid = Convert.ToString(a[0]);
                    string anm = Convert.ToString(a[1]);
                    int acnt = Convert.ToInt32(a[2]);
                    albumBox.Items.Add(anm + (acnt > 0 ? "（" + acnt + " 张）" : ""));
                    albumIds.Add(aid);
                    if (aid == albumId) sel = albumIds.Count - 1;
                }
                albumBox.SelectedIndex = sel;
                sp.Children.Add(albumBox);
            }

            // 弹窗里也能点标签库（2026-11-02 用户：都要）
            // 2026-11-03 用户：「像手机通讯录一样，每个字母一个抽屉」——同一份分组逻辑。
            if (_plTagNames.Count > 0)
            {
                sp.Children.Add(Txt("标签库（点一下加进上面这行；点字母抽屉展开）", 11, false, _th.bSub, 10));
                var dlgBox = new StackPanel();
                string dlgOpen = _plTagGroupOpen;      // 弹窗里独立记住展开哪个字母
                Action renderDlg = null;
                renderDlg = delegate
                {
                    dlgBox.Children.Clear();
                    var groups = GroupTags("");
                    // 字母条
                    var bar2 = new WrapPanel();
                    foreach (var kv in groups)
                    {
                        string letter = kv.Key;
                        var lb = new Button();
                        lb.Content = letter + "·" + kv.Value.Count;
                        lb.Padding = new Thickness(6 * S, 1 * S, 6 * S, 2 * S);
                        lb.Margin = new Thickness(0, 0, 3 * S, 3 * S);
                        lb.MinWidth = 0;
                        lb.MinHeight = 0;
                        lb.FontSize = 9.5 * S;
                        lb.Cursor = Cursors.Hand;
                        lb.BorderThickness = new Thickness(1);
                        lb.BorderBrush = _th.bLine;
                        bool on2 = dlgOpen == letter;
                        lb.Background = on2 ? _th.bAccent : _th.bBtn;
                        lb.Foreground = on2 ? Brushes.White : _th.bSub;
                        lb.Template = BtnTemplate();
                        string cap2 = letter;
                        lb.Click += delegate
                        {
                            dlgOpen = (dlgOpen == cap2) ? "" : cap2;
                            renderDlg();
                        };
                        bar2.Children.Add(lb);
                    }
                    dlgBox.Children.Add(bar2);
                    // 各组
                    foreach (var kv in groups)
                    {
                        string letter = kv.Key;
                        bool open2 = dlgOpen == letter;
                        var head2 = new WrapPanel();
                        var hl = Txt((open2 ? "▾ " : "▸ ") + letter + "  " + kv.Value.Count + " 个",
                                     10.5, true, open2 ? _th.bAccent : _th.bText, 0);
                        hl.Margin = new Thickness(0, 3 * S, 0, 2 * S);
                        hl.Cursor = Cursors.Hand;
                        string cap3 = letter;
                        hl.MouseLeftButtonUp += delegate
                        {
                            dlgOpen = (dlgOpen == cap3) ? "" : cap3;
                            renderDlg();
                        };
                        head2.Children.Add(hl);
                        dlgBox.Children.Add(head2);
                        if (!open2) continue;
                        var row2 = new WrapPanel();
                        foreach (var r2 in kv.Value)
                        {
                            string tg = Convert.ToString(r2[0]);
                            string tname = tg;
                            var box2 = t3;
                            row2.Children.Add(TagChip(tg, 0, delegate
                            {
                                var list = new System.Collections.Generic.List<string>();
                                foreach (var p in (box2.Text ?? "").Split(new char[] { ',', '，', ' ', '、', ';', '；' }))
                                {
                                    string tt = p.Trim();
                                    if (tt.Length > 0 && !list.Contains(tt)) list.Add(tt);
                                }
                                if (list.Contains(tname)) list.Remove(tname);
                                else list.Add(tname);
                                box2.Text = string.Join(",", list.ToArray());
                            }, true));
                        }
                        dlgBox.Children.Add(row2);
                    }
                };
                renderDlg();
                var chipScroll = new ScrollViewer();
                chipScroll.Content = dlgBox;
                chipScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;   // 标签再多也能滚
                chipScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                chipScroll.Height = 150 * S;        // 固定高度 → 弹窗不会再被标签撑大
                chipScroll.Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1C, 0x2B));
                chipScroll.Padding = new Thickness(6 * S);
                chipScroll.Margin = new Thickness(0, 2 * S, 0, 0);
                sp.Children.Add(chipScroll);
            }

            string[] res = null;
            var row = new WrapPanel();
            row.Margin = new Thickness(0, 14 * S, 0, 6 * S);
            row.Children.Add(Btn("保存", true, delegate
            {
                string aid = "";
                if (albumIds.Count > 0 && albumBox.SelectedIndex >= 0 && albumBox.SelectedIndex < albumIds.Count)
                    aid = albumIds[albumBox.SelectedIndex];
                res = new string[] { t1.Text.Trim(), t2.Text.Trim(), t3.Text.Trim(), aid };
                w.Close();
            }));
            var cancel = Btn("取消", false, delegate { w.Close(); });
            cancel.Margin = new Thickness(8 * S, 0, 0, 0);
            row.Children.Add(cancel);
            sp.Children.Add(row);
            w.Content = sp;
            w.ShowDialog();
            return res;
        }

        void PhotoLibEdit(string id, string name, string desc, string tags, string albumId)
        {
            string[] r = PromptEdit(name, desc, tags, albumId ?? "");
            if (r == null) return;
            if (_plStatus != null) _plStatus.Text = "正在保存…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string ib = Convert.ToBase64String(Encoding.UTF8.GetBytes(id));
                string nb = Convert.ToBase64String(Encoding.UTF8.GetBytes(r[0]));
                string db = Convert.ToBase64String(Encoding.UTF8.GetBytes(r[1]));
                string tb = Convert.ToBase64String(Encoding.UTF8.GetBytes(r[2]));
                string ab = Convert.ToBase64String(Encoding.UTF8.GetBytes(r.Length > 3 ? (r[3] ?? "") : ""));
                string cmd =
                    "printf '%s' '" + ib + "' | base64 -d > /tmp/pl_id.txt\n" +
                    "printf '%s' '" + nb + "' | base64 -d > /tmp/pl_n.txt\n" +
                    "printf '%s' '" + db + "' | base64 -d > /tmp/pl_d.txt\n" +
                    "printf '%s' '" + tb + "' | base64 -d > /tmp/pl_t.txt\n" +
                    "printf '%s' '" + ab + "' | base64 -d > /tmp/pl_a.txt\n" +
                    "/opt/qqbot/.venv/bin/python - <<'PYEOF'\n" +
                    "import json,urllib.request\n" +
                    "i=open('/tmp/pl_id.txt',encoding='utf-8').read().strip()\n" +
                    "n=open('/tmp/pl_n.txt',encoding='utf-8').read().strip()\n" +
                    "d=open('/tmp/pl_d.txt',encoding='utf-8').read().strip()\n" +
                    "tg=open('/tmp/pl_t.txt',encoding='utf-8').read().strip()\n" +
                    "al=open('/tmp/pl_a.txt',encoding='utf-8').read().strip()\n" +
                    "body=json.dumps({'id':i,'name':n,'desc':d,'tags':tg,'album':al,'lean':True}).encode()\n" +
                    "req=urllib.request.Request('http://127.0.0.1:8090/api/photolib/edit?token=" + Token + "', data=body, headers={'Content-Type':'application/json'})\n" +
                    "print(urllib.request.urlopen(req,timeout=30).read().decode())\n" +
                    "PYEOF";
                string outStr = Ssh.Run(_cfg, cmd, 60);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool ok = outStr.IndexOf("\"ok\": true") >= 0 || outStr.IndexOf("\"ok\":true") >= 0;
                    if (!ok)
                    {
                        if (_plStatus != null) _plStatus.Text = "保存失败：" + FirstLine(outStr);
                        return;
                    }
                    // 先原地改一下（立刻有反馈）
                    Action<string, string, string> upd;
                    if (_plTextById.TryGetValue(id, out upd))
                        upd(r[0], r[1], (r[2] ?? "").Replace(",", " "));
                    if (_plStatus != null) _plStatus.Text = "已保存";
                    // 2026-11-07 用户：「改图片名称和换相册的时候总是改完要卡一下」
                    // 原因：保存完这里会 LoadPhotoLib()（2 次 SSH）+ LoadPhotoTags()（1 次 SSH），
                    // 回来还要 Clear 整个列表重画 —— 改一个字段付全量代价。现在：
                    //   · 改名/改描述/改标签 → 上面就地改完就结束，**0 次请求**
                    //   · 换相册 → 只有"当前正停在某个相册里、且被移走了"才从本地列表摘掉，
                    //     在相册首页换相册只轻刷一次相册格子（一个请求，几 KB）
                    string newAlb = r.Length > 3 ? (r[3] ?? "") : "";
                    string curAlb = _plAlbumId ?? "";
                    bool moved = newAlb.Length > 0 && newAlb != curAlb;
                    if (moved)
                    {
                        if (curAlb.Length > 0)
                        {
                            for (int i = _plItems.Count - 1; i >= 0; i--)
                            {
                                var di = _plItems[i] as System.Collections.Generic.Dictionary<string, object>;
                                if (di != null && Convert.ToString(di.ContainsKey("id") ? di["id"] : "") == id)
                                {
                                    _plItems.RemoveAt(i);
                                    break;
                                }
                            }
                            RenderPhotoLib();          // 只重画本地数据，不联网
                        }
                        else
                        {
                            LoadPhotoAlbumsOnly();     // 相册首页：只刷格子
                        }
                    }
                }));
            });
        }

        /// <summary>上传单张到图库（批量上传也是逐张调这个）。</summary>
        bool PhotoLibUploadOne(string local, string name, string desc, string tags, out string msg)
        {
            msg = "";
            string up = Ssh.PutFile(_cfg, local, "/tmp/photolib_up.img", 90);
            bool upOk = up.Trim() == "ok" || up.Trim().Length == 0;
            if (!upOk)
            {
                msg = "[上传失败] " + FirstLine(up);
                return false;
            }
            string nb = Convert.ToBase64String(Encoding.UTF8.GetBytes(name));
            string db = Convert.ToBase64String(Encoding.UTF8.GetBytes(desc));
            string tb = Convert.ToBase64String(Encoding.UTF8.GetBytes(tags));
            string cmd =
                "printf '%s' '" + nb + "' | base64 -d > /tmp/pl_name.txt\n" +
                "printf '%s' '" + db + "' | base64 -d > /tmp/pl_desc.txt\n" +
                "printf '%s' '" + tb + "' | base64 -d > /tmp/pl_tags.txt\n" +
                "/opt/qqbot/.venv/bin/python - <<'PYEOF'\n" +
                "import base64,json,urllib.request\n" +
                "raw=open('/tmp/photolib_up.img','rb').read()\n" +
                "name=open('/tmp/pl_name.txt',encoding='utf-8').read().strip()\n" +
                "desc=open('/tmp/pl_desc.txt',encoding='utf-8').read().strip()\n" +
                "tags=open('/tmp/pl_tags.txt',encoding='utf-8').read().strip()\n" +
                "body=json.dumps({'name':name,'desc':desc,'tags':tags,'by':'wpf','image_b64':base64.b64encode(raw).decode()}).encode()\n" +
                "req=urllib.request.Request('http://127.0.0.1:8090/api/photolib/add?token=" + Token + "', data=body, headers={'Content-Type':'application/json'})\n" +
                "print(urllib.request.urlopen(req,timeout=60).read().decode())\n" +
                "PYEOF";
            string r = Ssh.Run(_cfg, cmd, 120);
            msg = r;
            return r.IndexOf("\"ok\": true") >= 0 || r.IndexOf("\"ok\":true") >= 0;
        }

        /// <summary>批量上传：可以一次选很多张，名字/描述/标签对这批都生效。</summary>
        void PhotoLibAdd()
        {
            string name = _plName == null ? "" : _plName.Text.Trim();
            string desc = _plDesc == null ? "" : _plDesc.Text.Trim();
            string tags = _plTags == null ? "" : _plTags.Text.Trim();
            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Filter = "图片|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|所有文件|*.*";
            dlg.Title = "选图加入图库（按住 Ctrl / Shift 可以多选）";
            dlg.Multiselect = true;
            if (dlg.ShowDialog() != true) return;
            string[] files = dlg.FileNames;
            if (files == null || files.Length == 0) return;
            int total = files.Length;
            if (_plStatus != null) _plStatus.Text = "准备上传 " + total + " 张…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                int okN = 0;
                var fails = new System.Collections.Generic.List<string>();
                for (int i = 0; i < files.Length; i++)
                {
                    string local = files[i];
                    string fn = System.IO.Path.GetFileName(local);
                    int idx = i + 1;
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (_plStatus != null) _plStatus.Text = "正在上传 " + idx + "/" + total + "：" + fn;
                    }));
                    try
                    {
                        string msg;
                        bool ok = PhotoLibUploadOne(local, name, desc, tags, out msg);
                        if (ok) okN++;
                        else fails.Add(fn + "（" + FirstLine(msg) + "）");
                    }
                    catch (Exception ex)
                    {
                        fails.Add(fn + "（" + ex.Message + "）");
                    }
                }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_plStatus != null)
                    {
                        string tail = fails.Count == 0 ? "" :
                            "；失败 " + fails.Count + " 张：" + string.Join("，", fails.ToArray());
                        _plStatus.Text = "加了 " + okN + "/" + total + " 张" + tail;
                    }
                    if (okN > 0)
                    {
                        if (_plName != null) _plName.Text = "";
                        if (_plDesc != null) _plDesc.Text = "";
                        if (_plTags != null) _plTags.Text = "";
                    }
                    LoadPhotoLib();
                    LoadPhotoTags();
                }));
            });
        }

        void PhotoLibDel(string id, string name)
        {
            if (_plStatus != null) _plStatus.Text = "正在删除「" + name + "」…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string ib64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(id));
                string cmd = "printf '%s' '" + ib64 + "' | base64 -d > /tmp/pl_id.txt\n" +
                             "K=$(cat /tmp/pl_id.txt)\n" +
                             "curl -s -m 20 -X POST -H 'Content-Type: application/json' " +
                             "--data-binary \"{\\\"key\\\":\\\"$K\\\"}\" " +
                             "'http://127.0.0.1:8090/api/photolib/del?token=" + Token + "'";
                string r = Ssh.Run(_cfg, cmd, 40);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool ok = r.IndexOf("\"ok\": true") >= 0 || r.IndexOf("\"ok\":true") >= 0;
                    if (!ok)
                    {
                        if (_plStatus != null) _plStatus.Text = "删失败：" + FirstLine(r);
                        return;
                    }
                    // 就地摘掉这一张，别整页重建
                    FrameworkElement card;
                    if (_plList != null && _plCardById.TryGetValue(id, out card))
                    {
                        _plList.Children.Remove(card);
                        _plCardById.Remove(id);
                        _plTextById.Remove(id);
                        try
                        {
                            var mm = System.Text.RegularExpressions.Regex.Match(_plLastCount, "(\\d+)");
                            if (mm.Success)
                            {
                                int left = Math.Max(0, Convert.ToInt32(mm.Groups[1].Value) - 1);
                                _plLastCount = "图库共 " + left + " 张";
                            }
                        }
                        catch { }
                        if (_plStatus != null) _plStatus.Text = "已删掉「" + name + "」";
                        LoadPhotoLib();
                        LoadPhotoTags();
                    }
                    else
                    {
                        if (_plStatus != null) _plStatus.Text = "已删掉「" + name + "」";
                        LoadPhotoLib();
                    }
                }));
            });
        }

        /// <summary>性能模式：一条可拖的流光控制条（全局热生效，插件读文件，不用重启）。</summary>
        void BuildPerfCard(StackPanel b)
        {
            b.Children.Add(Txt("调她「想多深」。拖下面这条，松手就生效，不用重启。", 11, false, _th.bSub, 2));

            _perfGrid = new Grid();
            _perfGrid.Height = 48 * S;
            _perfGrid.Margin = new Thickness(0, 12 * S, 0, 0);
            _perfGrid.Background = Brushes.Transparent;
            _perfGrid.Cursor = Cursors.Hand;

            var track = new Border();                       // 底槽
            track.CornerRadius = new CornerRadius(11 * S);
            track.Height = 22 * S;
            track.VerticalAlignment = VerticalAlignment.Center;
            track.Background = _th.bBtn;
            _perfGrid.Children.Add(track);

            // 填充：**永远全宽**，宽度靠 ScaleTransform.ScaleX（纯变换，动画在合成线程，60fps 很顺）。
            // 早先用 Clip + 每帧改 Width 的方案会重排布局，所以卡；改成缩放后拖/切都丝滑。
            _perfFill = new Border();
            _perfFill.CornerRadius = new CornerRadius(11 * S);
            _perfFill.Height = 22 * S;
            _perfFill.VerticalAlignment = VerticalAlignment.Center;
            _perfStops = new[]
            {
                new GradientStop(Color.FromRgb(0x3b, 0x8c, 0xff), 0.00),
                new GradientStop(Color.FromRgb(0x3b, 0x6c, 0xff), 0.35),
                new GradientStop(Color.FromRgb(0x4f, 0xc3, 0xff), 0.70),
                new GradientStop(Color.FromRgb(0x3b, 0x8c, 0xff), 1.00),   // 首尾同色，循环才无缝
            };
            _perfGrad = new LinearGradientBrush();
            _perfGrad.StartPoint = new Point(0, 0.5);
            _perfGrad.EndPoint = new Point(1, 0.5);
            foreach (var st in _perfStops) _perfGrad.GradientStops.Add(st);
            _perfFill.Background = _perfGrad;               // 静态渐变，不再动它
            _perfFillScale = new ScaleTransform(1, 1);
            _perfFill.RenderTransform = _perfFillScale;
            _perfFill.RenderTransformOrigin = new Point(0, 0.5);   // 从左往右长
            _perfGrid.Children.Add(_perfFill);

            // 星星层：横跨整条（裁在圆角内），星星各自速度往右飘，带拖尾
            _perfStarLayer = new Canvas();
            _perfStarLayer.Height = 22 * S;
            _perfStarLayer.VerticalAlignment = VerticalAlignment.Center;
            _perfStarLayer.IsHitTestVisible = false;
            _perfBarClip = new RectangleGeometry();
            _perfBarClip.RadiusX = 11 * S;
            _perfBarClip.RadiusY = 11 * S;
            _perfStarLayer.Clip = _perfBarClip;
            _perfStarLayer.SizeChanged += delegate { BuildStars(); };
            _perfGrid.Children.Add(_perfStarLayer);

            _perfThumbLayer = new Canvas();                 // 滑块单独一层，别被裁掉
            _perfThumbLayer.IsHitTestVisible = false;
            _perfGrid.Children.Add(_perfThumbLayer);
            _perfThumb = new Border();
            _perfThumb.Width = 28 * S;
            _perfThumb.Height = 28 * S;
            _perfThumb.CornerRadius = new CornerRadius(14 * S);
            _perfThumb.Background = Brushes.White;
            _perfThumb.BorderThickness = new Thickness(1);
            _perfThumb.BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0, 0, 0));
            _perfThumbLayer.Children.Add(_perfThumb);

            _perfGrid.SizeChanged += delegate { PaintPerf(); };
            _perfGrid.MouseDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (_perfDragging) return;
                _perfDragging = true;
                try { _perfGrid.CaptureMouse(); } catch { }      // 拖出条子也不丢
                PerfDragStart();
                PerfBarMouse(e);
            };
            _perfGrid.MouseMove += delegate(object s, MouseEventArgs e) { if (_perfDragging) PerfBarMouse(e); };
            _perfGrid.MouseLeftButtonUp += delegate
            {
                if (!_perfDragging) return;
                _perfDragging = false;
                try { _perfGrid.ReleaseMouseCapture(); } catch { }
                PerfBarCommit();                                // 松手才真正下发
            };
            b.Children.Add(_perfGrid);

            var marks = new Grid();                         // 三档刻度
            marks.Margin = new Thickness(0, 2 * S, 0, 0);
            for (int i = 0; i < 3; i++) marks.ColumnDefinitions.Add(new ColumnDefinition());
            string[] names = { "低", "正常", "高" };
            for (int i = 0; i < 3; i++)
            {
                var t = Txt(names[i], 11, false, _th.bSub, 0);
                t.HorizontalAlignment = i == 0 ? HorizontalAlignment.Left
                                     : (i == 1 ? HorizontalAlignment.Center : HorizontalAlignment.Right);
                Grid.SetColumn(t, i);
                marks.Children.Add(t);
            }
            b.Children.Add(marks);

            _perfVal = Txt("当前：低（省钱档）", 11.5, true, _th.bAccent, 8);
            b.Children.Add(_perfVal);
            _perfHint = Txt("", 11, false, _th.bSub, 2);
            b.Children.Add(_perfHint);
            b.Children.Add(Txt("往左更省 token（就是现在的模式），往右更聪明、更快消耗额度。", 10.5, false, _th.bSub, 4));

            if (ShotDemo != "perf") LoadPerf();
        }

        static double PerfFrac(string mode)
        {
            return mode == "high" ? 1.0 : (mode == "deft" ? 0.66 : 0.33);
        }

        /// <summary>
        /// 星星 + 拖尾：每颗都是「渐变尾巴 + 亮点」，长度/速度/高度/亮度全不一样，
        /// 所以看起来是参差不齐地往右飘，不是整齐划一。
        /// 动画：合成线程 Transform（不设低帧率上限 = 跟随显示器，通常 60fps）。
        /// </summary>
        void BuildStars()
        {
            if (_perfStarLayer == null || _perfGrid == null) return;
            double w = _perfGrid.ActualWidth;
            if (w <= 0) return;
            double h = _perfStarLayer.Height > 1 ? _perfStarLayer.Height : 22 * S;
            // 星星只出现在「亮起来」的那段：裁剪宽度跟着当前比例走
            double fNow = _perfDragFrac >= 0 ? _perfDragFrac : _perfVisualFrac;
            _perfBarClip.Rect = new Rect(0, 0, Math.Max(1.0, w * fNow), h);
            _perfStarLayer.Children.Clear();

            var rnd = new Random(20261022);
            int n = 16;
            for (int i = 0; i < n; i++)
            {
                // 尾巴越长 → 越像流星；短的就像小星点
                double trail = (10 + rnd.NextDouble() * 46) * S;
                double thick = (1.4 + rnd.NextDouble() * 1.4) * S;
                double headD = thick + (1.2 + rnd.NextDouble() * 1.2) * S;
                double dur = 1.4 + rnd.NextDouble() * 5.6;        // 参差不齐的速度
                double alpha = 0.35 + rnd.NextDouble() * 0.6;

                var mv = new Canvas();
                mv.Width = trail + headD;
                mv.Height = headD + 2;
                mv.IsHitTestVisible = false;
                mv.Opacity = alpha;
                mv.CacheMode = new BitmapCache();                 // 缓存成位图，几十颗也便宜

                var tail = new Border();
                tail.Width = trail;
                tail.Height = thick;
                tail.CornerRadius = new CornerRadius(thick / 2);
                var tb = new LinearGradientBrush();
                tb.StartPoint = new Point(0, 0.5);
                tb.EndPoint = new Point(1, 0.5);
                tb.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xff, 0xff, 0xff), 0.00));  // 尾梢完全淡出
                tb.GradientStops.Add(new GradientStop(Color.FromArgb(0x66, 0xff, 0xff, 0xff), 0.60));
                tb.GradientStops.Add(new GradientStop(Color.FromArgb(0xdd, 0xff, 0xff, 0xff), 1.00));
                tail.Background = tb;
                Canvas.SetLeft(tail, 0);
                Canvas.SetTop(tail, (mv.Height - thick) / 2);
                mv.Children.Add(tail);

                var head = new Border();
                head.Width = headD;
                head.Height = headD;
                head.CornerRadius = new CornerRadius(headD / 2);
                head.Background = Brushes.White;
                Canvas.SetLeft(head, trail - headD * 0.5);
                Canvas.SetTop(head, (mv.Height - headD) / 2);
                mv.Children.Add(head);

                var tt = new TranslateTransform(0, 0);
                mv.RenderTransform = tt;

                Canvas.SetLeft(mv, 0);
                Canvas.SetTop(mv, Math.Max(0, Math.Min(h - mv.Height, rnd.NextDouble() * h - mv.Height / 2)));
                _perfStarLayer.Children.Add(mv);

                // 起点各不相同（BeginTime 用负值），一开始就已经撒满整条，不用等
                var anim = new DoubleAnimation(-mv.Width, w + mv.Width * 0.1,
                                               TimeSpan.FromSeconds(dur));
                anim.RepeatBehavior = RepeatBehavior.Forever;
                anim.BeginTime = TimeSpan.FromSeconds(-rnd.NextDouble() * dur);
                tt.BeginAnimation(TranslateTransform.XProperty, anim);
            }
        }

        /// <summary>把填充比例设成 f：animate=true 时 260ms 缓动（切档丝滑）；false 立刻跟手（拖动）。</summary>
        void SetFillFrac(double f, bool animate)
        {
            f = Math.Max(0.02, Math.Min(1.0, f));
            if (_perfFillScale == null) return;
            var prop = ScaleTransform.ScaleXProperty;
            if (animate)
            {
                var a = new DoubleAnimation(f, TimeSpan.FromMilliseconds(260));
                a.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
                _perfFillScale.BeginAnimation(prop, a);
            }
            else
            {
                // 拖动时不反复 BeginAnimation(null)（那会一直打属性系统），直接改值最快
                if (_perfFillScale.HasAnimatedProperties) _perfFillScale.BeginAnimation(prop, null);
                _perfFillScale.ScaleX = f;
            }
        }

        void SetThumbX(double x, bool animate)
        {
            if (_perfThumb == null) return;
            var prop = Canvas.LeftProperty;
            if (animate)
            {
                var a = new DoubleAnimation(x, TimeSpan.FromMilliseconds(260));
                a.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
                _perfThumb.BeginAnimation(prop, a);
            }
            else
            {
                if (_perfThumb.HasAnimatedProperties) _perfThumb.BeginAnimation(prop, null);
                Canvas.SetLeft(_perfThumb, x);
            }
        }

        /// <summary>星星只画在「已经亮起来」的那段：把星星层的裁剪宽度跟填充一起走。</summary>
        void UpdateStarClip(double f)
        {
            if (_perfBarClip == null || _perfGrid == null) return;
            double w = _perfGrid.ActualWidth;
            if (w <= 0) return;
            double h = _perfStarLayer != null && _perfStarLayer.Height > 1 ? _perfStarLayer.Height : 22 * S;
            _perfBarClip.Rect = new Rect(0, 0, Math.Max(1.0, w * f), h);
        }

        /// <summary>按下鼠标：解掉填充/滑块的缓动动画，进入「直接跟手」模式。
        /// 星星**默认不冻**（冻住会像卡住）；要是哪天又觉得拖起来卡，把
        /// _perfFreezeStarsOnDrag 改成 true 就行。</summary>
        void PerfDragStart()
        {
            if (_perfFillScale != null && _perfFillScale.HasAnimatedProperties)
                _perfFillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            if (_perfThumb != null && _perfThumb.HasAnimatedProperties)
                _perfThumb.BeginAnimation(Canvas.LeftProperty, null);
            if (_perfFreezeStarsOnDrag) FreezeStars();
        }

        void FreezeStars()
        {
            if (_perfStarLayer == null) return;
            foreach (UIElement el in _perfStarLayer.Children)
            {
                var tt = el.RenderTransform as TranslateTransform;
                if (tt == null) continue;
                double cur = tt.X;                                  // 先读当前动画值
                tt.BeginAnimation(TranslateTransform.XProperty, null);
                tt.X = cur;                                         // 冻在原地，别跳回去
            }
        }

        void PerfBarMouse(MouseEventArgs e)
        {
            double w = _perfGrid == null ? 0 : _perfGrid.ActualWidth;
            if (w <= 0) return;
            double f = Math.Max(0, Math.Min(1, e.GetPosition(_perfGrid).X / w));
            // 60fps 够了，鼠标事件比这密的时候没必要每帧都重画
            int now = Environment.TickCount;
            if (now - _perfLastPaint < 16 && Math.Abs(f - _perfDragFrac) < 0.02) return;
            _perfLastPaint = now;
            _perfDragFrac = f;
            PaintPerf();                 // 立刻跟手，不等 SSH 回包（之前卡就卡在这）
        }

        void PerfBarCommit()
        {
            double f = _perfDragFrac;
            _perfDragFrac = -1;
            if (f < 0) return;
            _perfVisualFrac = f;              // 拖到哪里就停在哪里（不吸回三档格点）
            string m = f < 0.34 ? "low" : (f < 0.72 ? "deft" : "high");
            if (m != _perfMode)
            {
                _perfDropFrac = f;            // 切档成功后沿用它
                SetPerf(m);
            }
            else
            {
                PaintPerf();
                if (_perfFreezeStarsOnDrag) BuildStars();   // 只有冻过才需要解冻
            }
        }

        static string PerfLabel(string m)
        {
            if (m == "high") return "高（全开）";
            if (m == "deft") return "正常";
            return "低（省钱档）";
        }

        static string PerfDesc(string m)
        {
            if (m == "high") return "中等思考 · 问题先联网 · 看图必查 · 历史 32 条 · 1200 token";
            if (m == "deft") return "轻思考 · 历史 24 条 · 600 token · 看图 90% 联网";
            return "不思考 · 历史 16 条 · 300~800 token · 图搜 8 秒";
        }

        void LoadPerf()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/perf?token=" + Token, 30);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        var ser = new JavaScriptSerializer();
                        var o = ser.DeserializeObject(json) as Dictionary<string, object>;
                        if (o != null && o.ContainsKey("mode"))
                            _perfMode = Convert.ToString(o["mode"]);
                        if (_perfMode != "low" && _perfMode != "deft" && _perfMode != "high")
                            _perfMode = "low";
                        _perfVisualFrac = PerfFrac(_perfMode);
                        _perfColorMode = "";
                        PaintPerf(true);
                        BuildStars();
                    }
                    catch { }
                }));
            });
        }

        void SetPerf(string mode)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                var payload = new Dictionary<string, object>();
                payload["mode"] = mode;
                string body = new JavaScriptSerializer().Serialize(payload);
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                string cmd = "echo " + b64 + " | base64 -d > /tmp/perf.json; " +
                             "curl -s -m 20 -X POST -H 'Content-Type: application/json' " +
                             "--data-binary @/tmp/perf.json '" + ApiBase + "/api/perf?token=" + Token + "'";
                string r = Ssh.Run(_cfg, cmd, 40);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool ok = r.IndexOf("\"ok\": true") >= 0 || r.IndexOf("\"ok\":true") >= 0;
                    if (ok)
                    {
                        _perfMode = mode;
                        // 停在松手的位置；如果是别处改的档（没有松手位置），就走到格点
                        _perfVisualFrac = _perfDropFrac >= 0 ? _perfDropFrac : PerfFrac(mode);
                        _perfDropFrac = -1;
                        // 注意：这里**不要**清 _perfColorMode，否则颜色会直接跳到目标色、没有过渡
                        PaintPerf(true);    // true → 颜色 320ms 渐变过去
                        if (_perfFreezeStarsOnDrag) BuildStars();   // 冻过才解冻，否则重建会让星星瞬移
                        if (_ctlStatus != null) _ctlStatus.Text = "性能已切到「" + PerfLabel(mode) + "」，已生效";
                    }
                    else
                    {
                        if (_ctlStatus != null) _ctlStatus.Text = "切性能失败：" + FirstLine(r);
                    }
                }));
            });
        }

        void PaintPerf()
        {
            PaintPerf(false);
        }

        /// <summary>画控制条。animate=true → 填充/滑块 260ms 缓动（切档丝滑）；false → 立刻跟手（拖动）。</summary>
        void PaintPerf(bool animate)
        {
            if (_perfGrid == null || _perfThumb == null) return;
            double w = _perfGrid.ActualWidth, h = _perfGrid.ActualHeight;
            if (w <= 0) return;
            // 渐变色只在档位变了才重设（以前每帧都设 4 个 GradientStop → 每帧重画整条渐变，这是拖动卡的一大原因）
            // 换档时用 ColorAnimation 过渡（320ms），首次绘制/重排直接用目标色
            if (_perfColorMode != _perfMode)
            {
                ApplyPerfColors(_perfMode, animate && _perfColorMode != "");
                _perfColorMode = _perfMode;
            }
            bool dragging = _perfDragFrac >= 0;
            double f = dragging ? _perfDragFrac : _perfVisualFrac;   // 停在拖到的地方
            SetFillFrac(f, animate && !dragging);
            UpdateStarClip(f);                                       // 黑的那段不出星星
            double thumbW = _perfThumb.Width > 0 ? _perfThumb.Width : 28 * S;
            double left = Math.Max(0, Math.Min(w - thumbW, w * f - thumbW / 2.0));
            SetThumbX(left, animate && !dragging);
            Canvas.SetTop(_perfThumb, Math.Max(0, (h - _perfThumb.Height) / 2.0));
            if (_perfVal != null)
            {
                if (dragging)                 // 拖动中：告诉人松手才生效
                {
                    string near = _perfDragFrac < 0.34 ? "low" : (_perfDragFrac < 0.72 ? "deft" : "high");
                    _perfVal.Text = "松手切到：" + PerfLabel(near);
                }
                else _perfVal.Text = "当前：" + PerfLabel(_perfMode);
            }
            if (_perfHint != null) _perfHint.Text = PerfDesc(_perfMode);
        }

        /// <summary>每档一套渐变色：低=蓝青，正常=蓝紫，高=紫粉（越右边越"烫"）。</summary>
        static Color[] PerfColors(string mode)
        {
            if (mode == "high")
                return new[] { Color.FromRgb(0x7b, 0x5c, 0xff), Color.FromRgb(0xc0, 0x5c, 0xff),
                               Color.FromRgb(0xff, 0x5c, 0xa8) };
            if (mode == "deft")
                return new[] { Color.FromRgb(0x3b, 0x6c, 0xff), Color.FromRgb(0x6a, 0x5c, 0xff),
                               Color.FromRgb(0x9c, 0x5c, 0xff) };
            return new[] { Color.FromRgb(0x3b, 0x8c, 0xff), Color.FromRgb(0x3b, 0x6c, 0xff),
                           Color.FromRgb(0x4f, 0xc3, 0xff) };
        }

        /// <summary>
        /// 上色。animate=true 时用 ColorAnimation 把 4 个色标逐帧插值过去（约 320ms），
        /// 所以换档是"渐变过去"而不是啪一下变色；false 直接落到目标色（首次绘制 / 重排）。
        /// </summary>
        void ApplyPerfColors(string mode, bool animate)
        {
            if (_perfStops == null || _perfStops.Length < 4) return;
            Color[] c3 = PerfColors(mode);
            Color[] target = { c3[0], c3[1], c3[2], c3[0] };   // 首尾同色，流动才无缝
            var dur = TimeSpan.FromMilliseconds(1000);   // 换档颜色过渡时长（太快会显得生硬）
            for (int i = 0; i < 4; i++)
            {
                var st = _perfStops[i];
                if (st == null) continue;
                if (animate)
                {
                    try
                    {
                        var ca = new ColorAnimation(target[i], dur);   // From 留空 = 从当前色开始
                        ca.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
                        st.BeginAnimation(GradientStop.ColorProperty, ca);
                    }
                    catch
                    {
                        // 万一画刷被冻住了（WPF 某些布局会 Freeze），退回直接上色，别崩
                        st.BeginAnimation(GradientStop.ColorProperty, null);
                        st.Color = target[i];
                    }
                }
                else
                {
                    if (st.HasAnimatedProperties) st.BeginAnimation(GradientStop.ColorProperty, null);
                    st.Color = target[i];
                }
            }
        }

        void LoadVoiceStyle()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/voice_style?token=" + Token, 30);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        var ser = new JavaScriptSerializer();
                        var o = ser.DeserializeObject(json) as Dictionary<string, object>;
                        if (o != null && o.ContainsKey("current"))
                            _voiceStyle = Convert.ToString(o["current"]);
                        PaintCtl();
                    }
                    catch { }
                }));
            });
        }

        void SetVoiceStyle(string id)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                var payload = new Dictionary<string, object>();
                payload["style"] = id;
                string body = new JavaScriptSerializer().Serialize(payload);
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                string cmd = "echo " + b64 + " | base64 -d > /tmp/vs.json; " +
                             "curl -s -m 20 -X POST -H 'Content-Type: application/json' " +
                             "--data-binary @/tmp/vs.json '" + ApiBase + "/api/voice_style?token=" + Token + "'";
                string r = Ssh.Run(_cfg, cmd, 40);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool ok = r.IndexOf("\"ok\": true") >= 0 || r.IndexOf("\"ok\":true") >= 0;
                    if (ok)
                    {
                        _voiceStyle = id;
                        PaintCtl();
                        if (_ctlStatus != null) _ctlStatus.Text = "音色已切到「肥鱼」（唯一音色），已生效";
                    }
                    else
                    {
                        if (_ctlStatus != null) _ctlStatus.Text = "切音色失败：" + FirstLine(r);
                    }
                }));
            });
        }

        void PaintCtl()
        {
            if (_btnAt != null)
            {
                _btnAt.Content = _onlyAt ? "已开启" : "关闭";
                _btnAt.Background = _onlyAt ? _th.bAccent : _th.bBtn;
                _btnAt.Foreground = _onlyAt ? Brushes.White : _th.bText;
            }
            if (_btnAi != null)
            {
                _btnAi.Content = _aiMode == 1 ? "本群开" : (_aiMode == 0 ? "本群关" : "跟随全局");
                _btnAi.Background = _aiMode == 1 ? _th.bAccent : _th.bBtn;
                _btnAi.Foreground = _aiMode == 1 ? Brushes.White
                                  : (_aiMode == 0 ? (Brush)new SolidColorBrush(Color.FromRgb(0xff, 0xc2, 0xc2)) : _th.bText);
            }
            if (_btnBurst != null)
            {
                _btnBurst.Content = _burstMode == 1 ? "本群开" : (_burstMode == 0 ? "本群关" : "跟随全局");
                _btnBurst.Background = _burstMode == 1 ? _th.bAccent : _th.bBtn;
                _btnBurst.Foreground = _burstMode == 1 ? Brushes.White
                                     : (_burstMode == 0 ? (Brush)new SolidColorBrush(Color.FromRgb(0xff, 0xc2, 0xc2)) : _th.bText);
            }
            if (_lenVal != null) _lenVal.Text = "当前：" + _maxLen + " 字";
            MarkLen(_btn14, 14); MarkLen(_btn26, 26); MarkLen(_btn40, 40);            if (_speedVal != null) _speedVal.Text = _speed.ToString("0.00") + "x";
            if (_pokeVal != null) _pokeVal.Text = ((int)Math.Round(_pokeRate * 100)) + "%";
            foreach (var kv in _btnPoke) MarkPoke(kv.Key, kv.Value);
            // 默默发表情：-1 = 没单独设过（用默认 3%），其余按挡位高亮
            if (_ambientVal != null)
                _ambientVal.Text = _ambient < 0 ? "用默认 3%" : ((int)Math.Round(_ambient * 100)) + "%";
            foreach (var kv in _btnAmbient) MarkAmbient(kv.Key, kv.Value);
            if (_btnAmbientFollow != null)
            {
                bool follow = _ambient < 0;
                _btnAmbientFollow.Content = follow ? "用默认 ✓" : "用默认 3%";
                _btnAmbientFollow.Background = follow ? _th.bAccent : _th.bBtn;
                _btnAmbientFollow.Foreground = follow ? Brushes.White : _th.bText;
            }
            foreach (var kv in _btnVoice)
            {
                bool on = (_voiceStyle == kv.Value);
                kv.Key.Background = on ? _th.bAccent : _th.bBtn;
                kv.Key.Foreground = on ? Brushes.White : _th.bText;
            }
            if (_voiceVal != null)
                _voiceVal.Text = "当前：肥鱼";
            PaintPerf();
            if (_rateVal != null)
            {
                // 以前「跟全局」时直接把滑动条 IsEnabled=false，划不动；想启用还得先去点按钮，语义反了。
                // 现在滑动条永远可拖，拖一下就自动脱离「跟全局」。
                if (_slRate != null) _slRate.IsEnabled = true;
                if (_rate < 0) _rateVal.Text = _onlyAt ? "（跟全局 · 只回@）" : "（跟全局 · 仅@关 → 按 35% 接）";
                else _rateVal.Text = ((int)Math.Round(_rate * 100)) + "%";
                if (_btnRateFollow != null)
                {
                    bool follow = (_rate < 0);
                    _btnRateFollow.Content = follow ? "跟全局 ✓" : "跟全局";
                    _btnRateFollow.Background = follow ? _th.bAccent : _th.bBtn;
                    _btnRateFollow.Foreground = follow ? Brushes.White : _th.bText;
                }
            }
        }

        void MarkLen(Button b, int val)
        {
            if (b == null) return;
            bool on = _maxLen == val;
            b.Background = on ? _th.bAccentSoft : _th.bBtn;
            b.Foreground = on ? (Brush)new SolidColorBrush(Color.FromRgb(0xdb, 0xe4, 0xff)) : _th.bText;
        }

        void MarkPoke(Button b, double val)
        {
            if (b == null) return;
            bool on = Math.Abs(_pokeRate - val) < 0.005;
            b.Background = on ? _th.bAccent : _th.bBtn;
            b.Foreground = on ? Brushes.White : _th.bText;
            b.BorderThickness = new Thickness(1);
        }

        void MarkAmbient(Button b, double val)
        {
            if (b == null) return;
            bool on = _ambient >= 0 && Math.Abs(_ambient - val) < 0.005;
            b.Background = on ? _th.bAccent : _th.bBtn;
            b.Foreground = on ? Brushes.White : _th.bText;
            b.BorderThickness = new Thickness(1);
        }

        /// <summary>把任意概率吸附到最近的挡位（读回旧值/手改 json 时用）。</summary>
        static double SnapPoke(double v)
        {
            if (v <= 0) return 0.01;
            double best = POKE_STEPS[0];
            double gap = Math.Abs(v - best);
            foreach (double s in POKE_STEPS)
            {
                double g = Math.Abs(v - s);
                if (g < gap) { best = s; gap = g; }
            }
            return best;
        }

        /// <summary>默默发表情的挡位吸附：<0 保持「用默认」，0 就是 0（这个群关掉）。</summary>
        static double SnapAmbient(double v)
        {
            if (v < 0) return -1;
            double best = AMBIENT_STEPS[0];
            double gap = Math.Abs(v - best);
            foreach (double s in AMBIENT_STEPS)
            {
                double g = Math.Abs(v - s);
                if (g < gap) { best = s; gap = g; }
            }
            return best;
        }

        /// <summary>让滑动条不再被鼠标滚轮改值（默认行为：鼠标停在上面滚一下就变）。
        /// 想调就老老实实拖动或点两侧，避免手滑改坏眼里的设置。</summary>
        void NoWheel(Slider sl)
        {
            if (sl == null) return;
            sl.PreviewMouseWheel += delegate(object s, MouseWheelEventArgs e)
            {
                e.Handled = true;        // 吞掉滚轮，交给外层页面滚动
            };
        }

        static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
                    .Replace("&amp;", "&").Replace("&#39;", "'");
        }

        /// <summary>细深色滚动条：WPF 默认那份是浅色 Aero2 样式，深色主题里很刺眼。
        /// 用 XAML 字符串解析（Track 的子元素必须通过它的三个属性设置，代码构造不了）。</summary>
        void ApplyScrollBarStyle()
        {
            try
            {
                string xaml =
                  "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ScrollBar'>" +
                  "<Setter Property='Width' Value='11'/>" +
                  "<Setter Property='Background' Value='Transparent'/>" +
                  "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'>" +
                  "<Track x:Name='PART_Track' IsDirectionReversed='True' " +
                  "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' " +
                  "Maximum='{TemplateBinding Maximum}' Minimum='{TemplateBinding Minimum}' " +
                  "Value='{TemplateBinding Value}' ViewportSize='{TemplateBinding ViewportSize}'>" +
                  "<Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Focusable='False' Height='0' Width='0'>" +
                  "<RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template>" +
                  "</RepeatButton></Track.DecreaseRepeatButton>" +
                  "<Track.Thumb><Thumb MinHeight='26'>" +
                  "<Thumb.Template><ControlTemplate TargetType='Thumb'>" +
                  "<Border CornerRadius='4' Margin='2' Background='#444851'/>" +
                  "</ControlTemplate></Thumb.Template></Thumb></Track.Thumb>" +
                  "<Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Focusable='False' Height='0' Width='0'>" +
                  "<RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template>" +
                  "</RepeatButton></Track.IncreaseRepeatButton>" +
                  "</Track></ControlTemplate></Setter.Value></Setter>" +
                  "</Style>";
                var st = (Style)System.Windows.Markup.XamlReader.Parse(xaml);
                Resources[typeof(ScrollBar)] = st;
            }
            catch (Exception ex) { Console.WriteLine("滚动条样式失败：" + ex.Message); }
        }
        void FillGroups(List<string[]> items)
        {
            if (_grpWrap == null) return;
            _grpWrap.Children.Clear();
            foreach (var it in items)
            {
                string gid = it[0];
                var b = Btn(it[1], false, null);
                b.Tag = gid;
                b.FontSize = 12 * S;
                b.Padding = new Thickness(12 * S, 5 * S, 12 * S, 5 * S);
                b.Click += delegate(object s2, RoutedEventArgs e2)
                {
                    var bb = s2 as Button;
                    if (bb == null) return;
                    _selGid = (string)bb.Tag;
                    PaintGroupButtons();
                    LoadRulesFor("群 " + _selGid);
                };
                _grpWrap.Children.Add(b);
            }
            PaintGroupButtons();
        }

        void PaintGroupButtons()
        {
            if (_grpWrap == null) return;
            foreach (UIElement el in _grpWrap.Children)
            {
                var b = el as Button;
                if (b == null) continue;
                bool on = (string)b.Tag == _selGid;
                b.Background = on ? _th.bAccent : _th.bBtn;
                b.Foreground = on ? Brushes.White : _th.bText;
            }
        }

        void LoadGroups()
        {
            if (_ctlStatus != null) _ctlStatus.Text = "拉群列表…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/groups?token=" + Token, 45);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        var ser = new JavaScriptSerializer();
                        var o = ser.DeserializeObject(json) as Dictionary<string, object>;
                        var items = o["items"] as object[];
                        var list = new List<string[]>();
                        foreach (var it in items)
                        {
                            var d = it as Dictionary<string, object>;
                            string gid = Convert.ToString(d["gid"]);
                            string nm = Convert.ToString(d["name"]);
                            list.Add(new string[] { gid, "群 " + gid + (string.IsNullOrEmpty(nm) ? "" : " · " + Unescape(nm)) });
                        }
                        FillGroups(list);
                        if (list.Count > 0)
                        {
                            _selGid = list[0][0];
                            PaintGroupButtons();
                            LoadRulesFor("群 " + _selGid);
                        }
                        else if (_ctlStatus != null) _ctlStatus.Text = "没拿到群列表（协议端没连上？）";
                    }
                    catch (Exception ex)
                    {
                        if (_ctlStatus != null) _ctlStatus.Text = "群列表解析失败：" + ex.Message;
                    }
                }));
            });
        }

        void LoadGroupById()
        {
            string g = "";
            if (_gidInput != null)
                foreach (char ch in (_gidInput.Text ?? ""))
                    if (char.IsDigit(ch)) g += ch;
            if (g.Length < 5) { if (_ctlStatus != null) _ctlStatus.Text = "群号看着不对"; return; }
            LoadRulesFor("群 " + g);
        }

        void LoadRulesFor(string chat)
        {
            _chat = chat;
            if (_ctlStatus != null) _ctlStatus.Text = "读取 " + chat + " …";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/botctl?token=" + Token + "&chat=" + Uri.EscapeDataString(chat), 40);
                Dispatcher.BeginInvoke(new Action(delegate { ApplyRules(json, chat); }));
            });
        }

        void LoadRules()
        {
            if (_chat.Length == 0) { if (_ctlStatus != null) _ctlStatus.Text = "先选一个群"; return; }
            LoadRulesFor(_chat);
        }

        void ApplyRules(string json, string chat)
        {
            try
            {
                var ser = new JavaScriptSerializer();
                var o = ser.DeserializeObject(json) as Dictionary<string, object>;
                var r = (o.ContainsKey("rules") ? o["rules"] : null) as Dictionary<string, object>;
                if (r != null)
                {
                    _onlyAt = r.ContainsKey("only_at") && Convert.ToBoolean(r["only_at"]);
                    _maxLen = r.ContainsKey("max_len") ? Convert.ToInt32(r["max_len"]) : 26;
                    if (_maxLen <= 0) _maxLen = 26;
                    _speed = r.ContainsKey("speed") ? Convert.ToDouble(r["speed"]) : 1.0;
                    if (_speed <= 0) _speed = 1.0;
                    _rate = r.ContainsKey("chat_rate") ? Convert.ToDouble(r["chat_rate"]) : -1.0;
                    _pokeRate = r.ContainsKey("poke_rate") ? SnapPoke(Convert.ToDouble(r["poke_rate"])) : 1.0;
                    _ambient = r.ContainsKey("ambient") ? SnapAmbient(Convert.ToDouble(r["ambient"])) : -1;
                    if (!r.ContainsKey("ai") || r["ai"] == null) _aiMode = -1;
                    else _aiMode = Convert.ToBoolean(r["ai"]) ? 1 : 0;
                    if (!r.ContainsKey("burst") || r["burst"] == null) _burstMode = -1;
                    else _burstMode = Convert.ToBoolean(r["burst"]) ? 1 : 0;
                }
                if (_slSpeed != null) _slSpeed.Value = Math.Max(_slSpeed.Minimum, Math.Min(_slSpeed.Maximum, _speed));
                if (_slRate != null && _rate >= 0) _slRate.Value = Math.Max(0, Math.Min(1, _rate));
                PaintCtl();
                if (_ctlStatus != null) _ctlStatus.Text = "已载入 " + chat;
            }
            catch (Exception ex)
            {
                if (_ctlStatus != null) _ctlStatus.Text = "解析失败：" + ex.Message;
            }
        }

        void SaveRules()
        {
            if (_chat.Length == 0) { if (_ctlStatus != null) _ctlStatus.Text = "先选一个群"; return; }
            if (_ctlStatus != null) _ctlStatus.Text = "保存中…";
            string chat = _chat;
            bool at = _onlyAt; int len = _maxLen; double sp = _speed; double rt = _rate; int ai = _aiMode;
            int burst = _burstMode;
            double pk = _pokeRate;
            double am = _ambient;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var payload = new Dictionary<string, object>();
                payload["chat"] = chat;
                payload["only_at"] = at;
                payload["max_len"] = len;
                payload["speed"] = Math.Round(sp, 2);
                // 2026-10-28：关了「仅@」但频率还是 -1 时，落成 35% 再存 ——
                // 不然 group_rules.json 里永远悬着一个「-1」，看文件根本看不出她到底接不接话。
                double rtSave = rt;
                if (!at && rt < 0) rtSave = 0.35;
                payload["chat_rate"] = Math.Round(rtSave, 2);
                payload["poke_rate"] = Math.Round(pk, 2);
                payload["ambient"] = am < 0 ? -1.0 : Math.Round(am, 2);
                if (ai < 0) payload["ai"] = null; else payload["ai"] = (ai == 1);
                if (burst < 0) payload["burst"] = null; else payload["burst"] = (burst == 1);
                string body = new JavaScriptSerializer().Serialize(payload);
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                string cmd = "echo " + b64 + " | base64 -d > /tmp/botctl.json; " +
                             "curl -s -m 25 -X POST -H 'Content-Type: application/json' --data-binary @/tmp/botctl.json " +
                             "'" + ApiBase + "/api/botctl?token=" + Token + "'";
                string r = Ssh.Run(_cfg, cmd, 50);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool ok = r.IndexOf("\"ok\": true") >= 0 || r.IndexOf("\"ok\":true") >= 0;
                    string extra = (!at && rt < 0) ? "（仅@关 → 聊天频率落成 35%）" : "";
                    if (_ctlStatus != null)
                        _ctlStatus.Text = ok ? ("已保存：" + chat + extra) : ("保存失败：" + FirstLine(r));
                }));
            });
        }

        // ------------------------------------------------ 聊天记录（内置）
        ScrollViewer _convScroll, _msgScroll;
        StackPanel _convList, _msgList;
        TextBox _chatInput;
        bool _sendVoice = false;                 // 发消息时用语音（和手机端一致）
        Button _voiceToggle;
        TextBlock _chatHeadTitle, _chatHeadSub, _chatStatus;
        string _curChat = "";
        string _curChatTitle = "";
        string _msgSig = "";
        DispatcherTimer _chatTimer;

        // @ / 引用
        StackPanel _composeChips;             // 输入框上方：引用条 + @ 芯片
        Border _memberPanel;                  // 成员选择浮层（画在页面里，截得到）
        StackPanel _memberList;
        TextBox _memberSearch;
        Button _atBtn;
        readonly List<AtTarget> _atTargets = new List<AtTarget>();
        readonly List<Member> _members = new List<Member>();
        string _membersChat = "";
        MsgInfo _quoteMsg;
        public string ShotDemo = "";          // -shot 时用假数据验证界面
        public static bool ShotMode;          // -shot 自检进程：不建托盘、不隐藏

        // ---- 表情包（聊天页发表情，界面照着 QQ 做：5 列 × 4 行 + 底部翻页圆点）----
        const int STK_PER_PAGE = 20;
        const int STK_COLS = 5;
        Border _stkPanel;
        TextBox _stkSearch;
        WrapPanel _stkDots;
        TextBlock _stkHint, _stkPageText;
        Button _stkBtn;
        Grid _stkGrid;                        // 格子只建一次，翻页只换图（和 QQ 一样不闪）
        readonly Border[] _stkTile = new Border[STK_PER_PAGE];
        readonly Image[] _stkImg = new Image[STK_PER_PAGE];
        readonly string[] _stkAt = new string[STK_PER_PAGE];      // 每格现在放的是哪张
        readonly List<StkItem> _stks = new List<StkItem>();
        readonly Dictionary<string, BitmapImage> _stkBmp = new Dictionary<string, BitmapImage>();
        readonly HashSet<string> _stkReq = new HashSet<string>();  // 正在下载的缩略图，别重复请求
        string _stkSig = "";
        int _stkPage = 0;
        bool _stkLoading;

        class ConvInfo
        {
            public string Chat, Title, Last, Time, Dir;
            public int Count;
        }
        class MsgInfo
        {
            public string Dir, Who, Text, Time, Kind, Dt;
            public string Img = "";      // 图片外链（别人发的图 / QQ 表情包，2026-10-28）
            public string Stk = "";      // 自己发的表情包 id（服务器 stickers 目录里那张）
            public string Loc = "";      // 她自己发的图片（服务器 data/chatimg 里的文件名）
            public string QuoteWho = "", QuoteText = "";
            public bool Quoted;          // 这条消息带引用（原文可能没有，例如插件发的只记了 [引用]）
            public bool HasQuote { get { return Quoted || QuoteWho.Length > 0 || QuoteText.Length > 0; } }
        }
        class Member
        {
            public string Uid, Name, Nick, Role;
        }
        class AtTarget
        {
            public string Uid, Name;
        }
        class StkItem
        {
            public string Id, Mood, Desc, Scene, Tags;   // Tags：2026-10-30 自定义标签
            public int Kb;
        }

        void BuildChat()
        {
            double vh = _scroll.ActualHeight - _scroll.Padding.Top - _scroll.Padding.Bottom - 8;
            var root = new Grid();
            root.Height = Math.Max(430, vh);
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(292) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 左：会话列表
            var left = new Border();
            left.Background = _th.bCard;
            left.CornerRadius = new CornerRadius(12);
            left.BorderBrush = _th.bLine;
            left.BorderThickness = new Thickness(1);
            left.Padding = new Thickness(8 * S);
            left.Margin = new Thickness(0, 0, 12 * S, 0);

            var leftDock = new DockPanel();
            var lh = Txt("会话", 13, true, _th.bText, 0);
            lh.Margin = new Thickness(6 * S, 2 * S, 0, 8 * S);
            DockPanel.SetDock(lh, Dock.Top);
            leftDock.Children.Add(lh);
            var lbtn = Btn("刷新", false, delegate { LoadConversations(); });
            lbtn.Padding = new Thickness(10 * S, 3 * S, 10 * S, 3 * S);
            lbtn.HorizontalAlignment = HorizontalAlignment.Left;
            lbtn.Margin = new Thickness(4 * S, 0, 0, 8 * S);
            DockPanel.SetDock(lbtn, Dock.Top);
            leftDock.Children.Add(lbtn);

            _convScroll = new ScrollViewer();
            _convScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _convScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _convList = new StackPanel();
            _convScroll.Content = _convList;
            leftDock.Children.Add(_convScroll);
            left.Child = leftDock;
            Grid.SetColumn(left, 0);
            root.Children.Add(left);

            // 右：消息 + 输入
            var right = new Border();
            right.Background = _th.bCard;
            right.CornerRadius = new CornerRadius(12);
            right.BorderBrush = _th.bLine;
            right.BorderThickness = new Thickness(1);
            var rgrid = new Grid();
            rgrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rgrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rgrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var head = new Border();
            head.Padding = new Thickness(14 * S, 10 * S, 14 * S, 10 * S);
            head.BorderBrush = _th.bLine;
            head.BorderThickness = new Thickness(0, 0, 0, 1);
            var hsp = new StackPanel();
            _chatHeadTitle = Txt("选一个会话", 14, true, _th.bText, 0);
            _chatHeadSub = Txt("左边点一个群或好友", 11.5, false, _th.bSub, 2);
            hsp.Children.Add(_chatHeadTitle);
            hsp.Children.Add(_chatHeadSub);
            head.Child = hsp;
            Grid.SetRow(head, 0);
            rgrid.Children.Add(head);

            _msgScroll = new ScrollViewer();
            _msgScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _msgScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _msgScroll.Padding = new Thickness(10 * S, 10 * S, 10 * S, 6 * S);
            _msgList = new StackPanel();
            _msgScroll.Content = _msgList;
            Grid.SetRow(_msgScroll, 1);
            rgrid.Children.Add(_msgScroll);

            var bottom = new Border();
            bottom.Padding = new Thickness(12 * S, 8 * S, 12 * S, 10 * S);
            bottom.BorderBrush = _th.bLine;
            bottom.BorderThickness = new Thickness(0, 1, 0, 0);
            var bstack = new StackPanel();

            // 引用条 / @ 芯片：有内容才显示
            _composeChips = new StackPanel();
            _composeChips.Margin = new Thickness(2 * S, 0, 2 * S, 6 * S);
            _composeChips.Visibility = Visibility.Collapsed;
            bstack.Children.Add(_composeChips);

            // 2026-10-30 用户：「按钮把输入框挤没了」→ 工具按钮改成**悬浮在输入框右侧**的小钮，
            // 输入框占满整行，只有「发送」留在外面。
            var inputGrid = new Grid();
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var inputWrap = new Grid();

            var sendBtn = Btn("发送", true, delegate { SendChat(); });
            sendBtn.Margin = new Thickness(8 * S, 0, 0, 0);
            sendBtn.Padding = new Thickness(18 * S, 6 * S, 18 * S, 6 * S);

            var atBtn = Btn("＠", false, delegate { ToggleMembers(); });
            atBtn.Padding = new Thickness(7 * S, 2 * S, 7 * S, 2 * S);
            atBtn.Margin = new Thickness(3 * S, 0, 0, 0);
            atBtn.FontSize = 12 * S;
            atBtn.MinWidth = 30 * S;
            atBtn.ToolTip = "选一个要 @ 的人（群聊才有）";
            _atBtn = atBtn;

            // 语音开关：勾上之后发出去的是语音（走 8090 的 TTS→SILK 链路）
            var voiceBtn = Btn("🎤", false, delegate
            {
                _sendVoice = !_sendVoice;
                PaintVoiceToggle();
            });
            voiceBtn.Padding = new Thickness(7 * S, 2 * S, 7 * S, 2 * S);
            voiceBtn.Margin = new Thickness(3 * S, 0, 0, 0);
            voiceBtn.FontSize = 12 * S;
            voiceBtn.MinWidth = 30 * S;
            voiceBtn.ToolTip = "勾上之后发语音（用钩子克隆的音色念出来）";
            _voiceToggle = voiceBtn;
            PaintVoiceToggle();

            // 表情包：点开是她自己的那套表情（/opt/qqbot/stickers），点一下就发出去
            var stkBtn = Btn("😀", false, delegate { ToggleStickers(); });
            stkBtn.Padding = new Thickness(7 * S, 2 * S, 7 * S, 2 * S);
            stkBtn.Margin = new Thickness(3 * S, 0, 0, 0);
            stkBtn.FontSize = 12 * S;
            stkBtn.MinWidth = 30 * S;
            stkBtn.ToolTip = "发她表情包库里的图（点一下就发）";
            _stkBtn = stkBtn;

            // 群聊黑话（2026-10-30 加；2026-11-01 改成选群 + 逐条增删改查的编辑器）
            var slangBtn = Btn("🗣", false, delegate { EditSlang(); });
            slangBtn.Padding = new Thickness(7 * S, 2 * S, 7 * S, 2 * S);
            slangBtn.Margin = new Thickness(3 * S, 0, 0, 0);
            slangBtn.FontSize = 12 * S;
            slangBtn.MinWidth = 30 * S;
            slangBtn.ToolTip = "群聊黑话：选群 + 逐条增删改（词 = 说明）";

            var floatBar = new StackPanel();
            floatBar.Orientation = Orientation.Horizontal;
            floatBar.HorizontalAlignment = HorizontalAlignment.Right;
            floatBar.VerticalAlignment = VerticalAlignment.Center;
            floatBar.Margin = new Thickness(0, 0, 8 * S, 0);
            floatBar.Children.Add(slangBtn);
            floatBar.Children.Add(stkBtn);
            floatBar.Children.Add(voiceBtn);
            floatBar.Children.Add(atBtn);

            _chatInput = new TextBox();
            _chatInput.FontSize = 12.5 * S;
            StyleInput(_chatInput);
            // 右边留出悬浮按钮的宽度，字不会被压在按钮底下
            _chatInput.Padding = new Thickness(10 * S, 7 * S, 148 * S, 7 * S);
            _chatInput.KeyDown += delegate(object s2, KeyEventArgs e2)
            {
                if (e2.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                {
                    SendChat();
                    e2.Handled = true;
                }
            };
            inputWrap.Children.Add(_chatInput);
            inputWrap.Children.Add(floatBar);      // 悬浮在输入框上层

            Grid.SetColumn(inputWrap, 0);
            inputGrid.Children.Add(inputWrap);
            Grid.SetColumn(sendBtn, 1);
            inputGrid.Children.Add(sendBtn);

            bstack.Children.Add(inputGrid);
            bottom.Child = bstack;
            Grid.SetRow(bottom, 2);
            rgrid.Children.Add(bottom);

            // 成员浮层（画在页面里而不是 Popup：Popup 是独立窗口，-shot 截不到）
            var mp = new Border();
            mp.Background = _th.bCard;
            mp.BorderBrush = _th.bLine;
            mp.BorderThickness = new Thickness(1);
            mp.CornerRadius = new CornerRadius(10);
            mp.Padding = new Thickness(10 * S);
            mp.Margin = new Thickness(10 * S, 0, 10 * S, 10 * S);
            mp.MaxHeight = 340 * S;
            mp.VerticalAlignment = VerticalAlignment.Bottom;
            mp.Visibility = Visibility.Collapsed;
            var mpd = new Grid();
            mpd.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mpd.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mpd.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var mh = new DockPanel();
            var mclose = Btn("✕", false, delegate { HideMembers(); });
            mclose.Padding = new Thickness(9 * S, 1 * S, 9 * S, 1 * S);
            mclose.Margin = new Thickness(0);
            DockPanel.SetDock(mclose, Dock.Right);
            mh.Children.Add(mclose);
            mh.Children.Add(Txt("选一个要 ＠ 的人（可输入名字筛选）", 12, true, _th.bText, 0));
            Grid.SetRow(mh, 0);
            mpd.Children.Add(mh);

            _memberSearch = new TextBox();
            _memberSearch.FontSize = 12 * S;
            StyleInput(_memberSearch);
            _memberSearch.Padding = new Thickness(8 * S, 5 * S, 8 * S, 5 * S);
            _memberSearch.Margin = new Thickness(0, 8 * S, 0, 6 * S);
            _memberSearch.TextChanged += delegate { FillMembers(); };
            _memberSearch.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetRow(_memberSearch, 1);
            mpd.Children.Add(_memberSearch);

            var msv = new ScrollViewer();
            msv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            msv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            msv.MaxHeight = 240 * S;
            _memberList = new StackPanel();
            msv.Content = _memberList;
            Grid.SetRow(msv, 2);
            mpd.Children.Add(msv);

            mp.Child = mpd;
            Grid.SetRow(mp, 1);
            rgrid.Children.Add(mp);
            _memberPanel = mp;

            // ---------------------------------------------------- 表情包面板（仿 QQ）
            // QQ 的样子：浮在输入框上方，5 列 × 4 行缩略图，底部一排翻页圆点，点一下就发出去。
            // 画在页面里而不是 Popup —— Popup 是独立窗口，-shot 截不到（和成员浮层同一个理由）。
            var stk = new Border();
            stk.Background = _th.bCard;
            stk.BorderBrush = _th.bLine;
            stk.BorderThickness = new Thickness(1);
            stk.CornerRadius = new CornerRadius(10);
            stk.Padding = new Thickness(10 * S);
            stk.Width = 420 * S;
            stk.Height = 470 * S;          // 固定高度 + 内部滚动，别让面板撑出聊天区（2026-10-30）
            stk.HorizontalAlignment = HorizontalAlignment.Right;
            stk.VerticalAlignment = VerticalAlignment.Bottom;
            stk.Margin = new Thickness(0, 0, 12 * S, 12 * S);
            stk.Visibility = Visibility.Collapsed;

            var stkG = new Grid();
            stkG.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题
            stkG.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 搜索
            stkG.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // 网格（可滚动）
            stkG.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 翻页

            var stkHead = new DockPanel();
            var stkClose = Btn("✕", false, delegate { HideStickers(); });
            stkClose.Padding = new Thickness(8 * S, 0, 8 * S, 0);
            stkClose.Margin = new Thickness(0);
            stkClose.FontSize = 10 * S;
            DockPanel.SetDock(stkClose, Dock.Right);
            stkHead.Children.Add(stkClose);
            _stkHint = Txt("表情 · 点一下就发出去", 11.5, true, _th.bText, 0);
            _stkHint.VerticalAlignment = VerticalAlignment.Center;
            stkHead.Children.Add(_stkHint);
            Grid.SetRow(stkHead, 0);
            stkG.Children.Add(stkHead);

            _stkSearch = new TextBox();
            StyleInput(_stkSearch);
            _stkSearch.FontSize = 11.5 * S;
            _stkSearch.Padding = new Thickness(8 * S, 5 * S, 8 * S, 5 * S);
            _stkSearch.Margin = new Thickness(0, 7 * S, 0, 0);
            _stkSearch.HorizontalAlignment = HorizontalAlignment.Stretch;
            _stkSearch.Background = _th.bBtn;
            _stkSearch.Foreground = _th.bText;
            _stkSearch.BorderBrush = _th.bLine;
            Watermark(_stkSearch, "搜情绪 / 画面，比如「生气」「摊手」");
            _stkSearch.TextChanged += delegate { _stkPage = 0; FillStickers(); };
            Grid.SetRow(_stkSearch, 1);
            stkG.Children.Add(_stkSearch);

            _stkGrid = new Grid();
            _stkGrid.Margin = new Thickness(0, 6 * S, 0, 2 * S);
            for (int sc = 0; sc < STK_COLS; sc++)
                _stkGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int sr = 0; sr < STK_PER_PAGE / STK_COLS; sr++)
                _stkGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int si = 0; si < STK_PER_PAGE; si++)
            {
                var cell = new Border();
                cell.Width = 68 * S;
                cell.Height = 68 * S;
                cell.CornerRadius = new CornerRadius(8);
                cell.Background = Brushes.Transparent;
                cell.HorizontalAlignment = HorizontalAlignment.Center;
                cell.VerticalAlignment = VerticalAlignment.Center;
                cell.Margin = new Thickness(2 * S);
                cell.Cursor = Cursors.Hand;
                var im = new Image();
                im.Stretch = Stretch.Uniform;
                im.Margin = new Thickness(4 * S);
                cell.Child = im;
                cell.Visibility = Visibility.Collapsed;
                int slot = si;
                cell.MouseEnter += delegate { if (cell.Visibility == Visibility.Visible) cell.Background = _th.bBtn; };
                cell.MouseLeave += delegate { cell.Background = Brushes.Transparent; };
                cell.MouseLeftButtonUp += delegate { StkClick(slot); };
                Grid.SetColumn(cell, si % STK_COLS);
                Grid.SetRow(cell, si / STK_COLS);
                _stkGrid.Children.Add(cell);
                _stkTile[si] = cell;
                _stkImg[si] = im;
            }
            // 2026-10-30 修「长宽不对、图卡进去」：格子原来是星号行列，面板被压扁时
            // 每行只分到一点点高度，图就溢到别的行里（看起来重叠）。现在改成 Auto 网格 +
            // 外面套一层可滚动区域，面板再矮也只是滚动，不会挤在一起。
            var stkScroll = new ScrollViewer();
            stkScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            stkScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            stkScroll.Content = _stkGrid;
            stkScroll.MaxHeight = (STK_PER_PAGE / STK_COLS) * 74 * S;
            stkScroll.Margin = new Thickness(0, 2 * S, 0, 0);
            Grid.SetRow(stkScroll, 2);
            stkG.Children.Add(stkScroll);

            var stkFoot = new DockPanel();
            _stkPageText = Txt("", 10.5, false, _th.bSub, 0);
            _stkPageText.VerticalAlignment = VerticalAlignment.Center;
            _stkPageText.Margin = new Thickness(6 * S, 0, 0, 0);
            DockPanel.SetDock(_stkPageText, Dock.Right);
            stkFoot.Children.Add(_stkPageText);

            var stkNext = Btn("›", false, delegate { StkPage(_stkPage + 1); });
            stkNext.Padding = new Thickness(9 * S, 1 * S, 9 * S, 1 * S);
            stkNext.FontSize = 12 * S;
            DockPanel.SetDock(stkNext, Dock.Right);
            stkFoot.Children.Add(stkNext);

            var stkPrev = Btn("‹", false, delegate { StkPage(_stkPage - 1); });
            stkPrev.Padding = new Thickness(9 * S, 1 * S, 9 * S, 1 * S);
            stkPrev.FontSize = 12 * S;
            DockPanel.SetDock(stkPrev, Dock.Left);
            stkFoot.Children.Add(stkPrev);

            _stkDots = new WrapPanel();
            _stkDots.VerticalAlignment = VerticalAlignment.Center;
            _stkDots.HorizontalAlignment = HorizontalAlignment.Center;
            _stkDots.Margin = new Thickness(4 * S, 0, 4 * S, 0);
            stkFoot.Children.Add(_stkDots);
            Grid.SetRow(stkFoot, 3);
            stkG.Children.Add(stkFoot);

            stk.Child = stkG;
            Grid.SetRow(stk, 1);
            rgrid.Children.Add(stk);
            _stkPanel = stk;

            right.Child = rgrid;
            Grid.SetColumn(right, 1);
            root.Children.Add(right);

            _chatStatus = Txt("", 11, false, _th.bSub, 6);
            _page.Children.Add(root);
            _page.Children.Add(_chatStatus);

            // -shot 的假数据模式（quote / members）：别再拉真数据，不然截图会被异步返回覆盖掉
            bool chatDemo = !string.IsNullOrEmpty(ShotDemo)
                            && !ShotDemo.StartsWith("chat:") && ShotDemo != "stickers";
            _chatTimer = new DispatcherTimer();
            _chatTimer.Interval = TimeSpan.FromSeconds(4);
            _chatTimer.Tick += delegate { if (_curPageId == "chat") LoadMessages(true); };
            if (!chatDemo) _chatTimer.Start();          // 假数据模式别再定时刷新，否则 4 秒后demo 被真数据顶掉
            if (!chatDemo)
            {
                LoadConversations();
                // 切页面回来（或者刚启动）时，上次看的会话直接重新拉一遍，别留个空白页
                if (_curChat.Length > 0)
                {
                    _msgSig = "";
                    LoadMessages(false);
                }
            }
            if (!string.IsNullOrEmpty(ShotDemo)) ApplyChatDemo();
        }

        /// <summary>界面自检用假数据：-shot chat out.png quote / members / chat:群 123（真数据）</summary>
        void ApplyChatDemo()
        {
            if (ShotDemo.StartsWith("chat:"))
            {
                // 真数据：直接把某个会话拉下来渲染，用来验证线上 JSON 到底长什么样
                _curChat = ShotDemo.Substring(5).Trim();
                _curChatTitle = _curChat;
                LoadMessages(false);
                return;
            }
            if (ShotDemo == "stickers")
            {
                // 真数据：拉一遍表情包列表 + 第一页缩略图，验证面板排版（选第一个会话当背景）
                if (_curChat.Length == 0)
                {
                    _curChat = "群 1000000004";
                    _curChatTitle = "Phoebe Hub";
                }
                ShowStickers();
                return;
            }
            _curChat = "群 1000000004";
            _curChatTitle = "Phoebe Hub";
            var list = new List<MsgInfo>();

            var a = new MsgInfo();
            a.Dir = "in"; a.Who = "Cat"; a.Time = "14:59:07"; a.Dt = "2026-10-20 14:59:07";
            a.Text = "菲比啾比"; a.Kind = "text";
            list.Add(a);

            var b = new MsgInfo();
            b.Dir = "out"; b.Who = "我"; b.Time = "14:59:11"; b.Dt = "2026-10-20 14:59:11";
            b.Text = "@Cat 我也要"; b.Kind = "text";
            b.QuoteWho = "Cat";
            b.QuoteText = "菲比啾比，但是今天群里有人在吵架，我先去劝一下，回来再跟你们聊这个话题。";
            b.Quoted = true;
            list.Add(b);

            var c = new MsgInfo();
            c.Dir = "in"; c.Who = "就是梅狸猫"; c.Time = "14:59:13"; c.Dt = "2026-10-20 14:59:13";
            c.Text = "[图片]"; c.Kind = "image";
            // -shot 演示：一张外网图 + 一张自己的表情包，两条加载路径都走一遍
            c.Img = "https://www.baidu.com/img/flexible/logo/pc/result.png";
            list.Add(c);

            var c2 = new MsgInfo();
            c2.Dir = "out"; c2.Who = "我"; c2.Time = "14:59:20"; c2.Dt = "2026-10-20 14:59:20";
            c2.Text = "[表情]"; c2.Kind = "sticker";
            c2.Stk = "st001.png";
            list.Add(c2);

            var d = new MsgInfo();
            d.Dir = "out"; d.Who = "我"; d.Time = "15:00:02"; d.Dt = "2026-10-20 15:00:02";
            d.Text = "就可爱一秒，多一秒收钱。"; d.Kind = "text";
            d.QuoteText = ""; d.QuoteWho = "";
            list.Add(d);

            // 她自己发的图（file:// 存档 → /api/imgfile）：-shot 时验证这条链路
            var e2 = new MsgInfo();
            e2.Dir = "out"; e2.Who = "我"; e2.Time = "15:00:09"; e2.Dt = "2026-10-20 15:00:09";
            e2.Text = "[图片]"; e2.Kind = "image";
            e2.Loc = "f589354e7caab820.jpg";
            list.Add(e2);

            FillMsgs(list, _curChat, _curChatTitle, false);
            SetQuote(a);
            AddAt("2859325884", "云");

            if (ShotDemo == "members")
            {
                _members.Clear();
                string[] names = new string[] { "云", "Cat", "凑肥鱼", "凑企鹅", "谢曼蒂斯⚔️", "就是梅狸猫", "格子", "糯糯" };
                string[] uids = new string[] { "2859325884", "1310414336", "154857325", "82997741",
                                               "1000000005", "1000000002", "1000000003", "1000000004" };
                for (int i = 0; i < names.Length; i++)
                    _members.Add(new Member { Uid = uids[i], Name = names[i], Nick = names[i] + "_nick", Role = i == 0 ? "owner" : "member" });
                _membersChat = _curChat;
                ShowMembers("");
                _memberSearch.Text = "云";
            }
        }

        List<ConvInfo> ParseConvs(string json)
        {
            var list = new List<ConvInfo>();
            try
            {
                var ser = new JavaScriptSerializer();
                var o = ser.DeserializeObject(json) as Dictionary<string, object>;
                var items = o["items"] as object[];
                if (items == null) return list;
                foreach (var it in items)
                {
                    var d = it as Dictionary<string, object>;
                    var c = new ConvInfo();
                    c.Chat = Convert.ToString(d["chat"]);
                    c.Title = d.ContainsKey("title") ? Convert.ToString(d["title"]) : c.Chat;
                    c.Last = d.ContainsKey("last") ? Convert.ToString(d["last"]) : "";
                    c.Time = d.ContainsKey("ts") ? Convert.ToString(d["ts"]) : "";
                    c.Dir = d.ContainsKey("dir") ? Convert.ToString(d["dir"]) : "";
                    c.Count = d.ContainsKey("count") ? Convert.ToInt32(d["count"]) : 0;
                    list.Add(c);
                }
            }
            catch { }
            return list;
        }

        void LoadConversations()
        {
            if (_chatStatus != null) _chatStatus.Text = "拉会话列表…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/conversations?token=" + Token, 45);
                var list = ParseConvs(json);
                Dispatcher.BeginInvoke(new Action(delegate { FillConvs(list); }));
            });
        }

        void FillConvs(List<ConvInfo> list)
        {
            if (_convList == null) return;
            _convList.Children.Clear();
            if (list.Count == 0)
            {
                _convList.Children.Add(Txt("没有会话（日志里还没抓到消息）", 11.5, false, _th.bSub, 8));
                return;
            }
            // 进来先自动选中第一个会话，免得右边一片空白
            if (_curChat.Length == 0 && list.Count > 0)
            {
                _curChat = list[0].Chat;
                _curChatTitle = list[0].Title;
                _msgSig = "";
                LoadMessages(false);
            }

            foreach (var c in list)
            {
                var b = new Border();
                b.CornerRadius = new CornerRadius(8);
                b.Padding = new Thickness(9 * S, 7 * S, 9 * S, 7 * S);
                b.Margin = new Thickness(0, 1 * S, 0, 1 * S);
                b.Background = c.Chat == _curChat ? _th.bAccentSoft : Brushes.Transparent;
                b.Cursor = Cursors.Hand;
                b.Tag = c;

                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38 * S) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var av = new Border();
                av.Width = 32 * S;
                av.Height = 32 * S;
                av.CornerRadius = new CornerRadius(16 * S);
                av.Background = new SolidColorBrush(AvatarColor(c.Chat));
                av.VerticalAlignment = VerticalAlignment.Center;
                var avt = new TextBlock();
                avt.Text = FirstChar(c.Title);
                avt.Foreground = Brushes.White;
                avt.FontSize = 13 * S;
                avt.FontWeight = FontWeights.Bold;
                avt.HorizontalAlignment = HorizontalAlignment.Center;
                avt.VerticalAlignment = VerticalAlignment.Center;
                av.Child = avt;
                Grid.SetColumn(av, 0);
                g.Children.Add(av);

                var mid = new StackPanel();
                var t1 = new TextBlock();
                t1.Text = c.Title;
                t1.FontSize = 12.5 * S;
                t1.FontWeight = FontWeights.SemiBold;
                t1.Foreground = _th.bText;
                t1.TextTrimming = TextTrimming.CharacterEllipsis;
                var t2 = new TextBlock();
                t2.Text = (c.Dir == "out" ? "我：" : "") + c.Last;
                t2.FontSize = 11 * S;
                t2.Foreground = _th.bSub;
                t2.Margin = new Thickness(0, 1 * S, 0, 0);
                t2.TextTrimming = TextTrimming.CharacterEllipsis;
                mid.Children.Add(t1);
                mid.Children.Add(t2);
                Grid.SetColumn(mid, 1);
                g.Children.Add(mid);

                b.Child = g;
                b.MouseLeftButtonUp += delegate(object s2, MouseButtonEventArgs e2)
                {
                    var bb = s2 as Border;
                    var cc = bb.Tag as ConvInfo;
                    if (cc == null) return;
                    _curChat = cc.Chat;
                    _curChatTitle = cc.Title;
                    _msgSig = "";
                    _quoteMsg = null;            // 换会话：别把对 A 群的引用带到 B 群
                    _atTargets.Clear();
                    if (_memberSearch != null) _memberSearch.Text = "";
                    HideMembers();
                    RebuildChips();
                    FillConvs(list);            // 重画选中态
                    LoadMessages(false);
                };
                _convList.Children.Add(b);
            }
        }

        static Color AvatarColor(string key)
        {
            Color[] cols = new Color[]
            {
                Color.FromRgb(0x3f,0x6a,0xef), Color.FromRgb(0x4c,0xaf,0x50), Color.FromRgb(0xf5,0x7c,0x00),
                Color.FromRgb(0x9c,0x27,0xb0), Color.FromRgb(0xe9,0x1e,0x63), Color.FromRgb(0x00,0xac,0xc1),
                Color.FromRgb(0x7e,0x57,0xc2), Color.FromRgb(0x2e,0x9e,0x8f)
            };
            int h = 0;
            foreach (char ch in (key ?? "")) h = (h * 31 + ch) & 0x7fffffff;
            return cols[h % cols.Length];
        }

        static string FirstChar(string s)
        {
            if (string.IsNullOrEmpty(s)) return "?";
            return s.Substring(0, 1);
        }

        List<MsgInfo> ParseMsgs(string json)
        {
            var list = new List<MsgInfo>();
            try
            {
                var ser = new JavaScriptSerializer();
                var o = ser.DeserializeObject(json) as Dictionary<string, object>;
                var arr = o["messages"] as object[];
                if (arr == null) return list;
                foreach (var it in arr)
                {
                    var d = it as Dictionary<string, object>;
                    var m = new MsgInfo();
                    m.Dir = Convert.ToString(d["dir"]);
                    m.Who = d.ContainsKey("who") ? Convert.ToString(d["who"]) : "";
                    m.Text = d.ContainsKey("text") ? Convert.ToString(d["text"]) : "";
                    m.Time = d.ContainsKey("ts") ? Convert.ToString(d["ts"]) : "";
                    m.Kind = d.ContainsKey("kind") ? Convert.ToString(d["kind"]) : "text";
                    m.Dt = d.ContainsKey("dt") ? Convert.ToString(d["dt"]) : "";
                    if (d.ContainsKey("img")) m.Img = Convert.ToString(d["img"]);
                    if (d.ContainsKey("stk")) m.Stk = Convert.ToString(d["stk"]);
                    if (d.ContainsKey("loc")) m.Loc = Convert.ToString(d["loc"]);
                    if (d.ContainsKey("quote") && d["quote"] is Dictionary<string, object>)
                    {
                        m.Quoted = true;
                        var qd = d["quote"] as Dictionary<string, object>;
                        m.QuoteWho = qd.ContainsKey("who") ? Convert.ToString(qd["who"]) : "";
                        m.QuoteText = qd.ContainsKey("text") ? Convert.ToString(qd["text"]) : "";
                    }
                    list.Add(m);
                }
            }
            catch { }
            return list;
        }

        void LoadMessages(bool silent)
        {
            if (_curChat.Length == 0)
            {
                if (_chatHeadTitle != null) _chatHeadTitle.Text = "选一个会话";
                if (_chatHeadSub != null) _chatHeadSub.Text = "左边点一个群或好友";
                return;
            }
            string chat = _curChat;
            string title = _curChatTitle;
            if (!silent && _chatStatus != null) _chatStatus.Text = "读取 " + chat + " …";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/messages?token=" + Token +
                                            "&limit=120&chat=" + Uri.EscapeDataString(chat), 40);
                var list = ParseMsgs(json);
                Dispatcher.BeginInvoke(new Action(delegate { FillMsgs(list, chat, title, silent); }));
            });
        }

        // ---- 聊天里的图（2026-10-28 用户：「看不到别人发的图和表情包」）----
        // 服务器接口(/api)走 SSH 取，QQ 图床直接下载；都按 URL 缓存在内存里，4 秒刷新不会重复下载。
        readonly Dictionary<string, BitmapImage> _chatImgCache = new Dictionary<string, BitmapImage>();

        static bool ChatImgPlaceholder(string s)
        {
            return string.IsNullOrEmpty(s) || s == "[图片]" || s == "[表情]";
        }

        string ChatImgUrl(MsgInfo m)
        {
            if (!string.IsNullOrEmpty(m.Img)) return m.Img;
            if (!string.IsNullOrEmpty(m.Loc))
                return ApiBase + "/api/imgfile?f=" + Uri.EscapeDataString(m.Loc) + "&token=" + Token;
            if (!string.IsNullOrEmpty(m.Stk))
                return ApiBase + "/api/stickers/thumb?id=" + Uri.EscapeDataString(m.Stk) + "&token=" + Token;
            return "";
        }

        static string StripSpace(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (!char.IsWhiteSpace(c)) sb.Append(c);
            return sb.ToString();
        }

        byte[] FetchChatImg(string url)
        {
            try
            {
                if (url.IndexOf("/api/", StringComparison.Ordinal) >= 0)
                {
                    string b64 = Ssh.Run(_cfg, "curl -s -m 25 '" + url + "' | base64 -w0", 40);
                    string clean = StripSpace(b64);
                    if (clean.Length < 32) return null;
                    return Convert.FromBase64String(clean);
                }
                // QQ 的图床是 https：老系统默认只开 TLS1.0，这里显式打开
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                using (var wc = new System.Net.WebClient())
                {
                    wc.Headers["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
                    return wc.DownloadData(url);
                }
            }
            catch { return null; }
        }

        void LoadChatImage(MsgInfo m, System.Windows.Controls.Image iv, TextBlock fallback)
        {
            string url = ChatImgUrl(m);
            if (url.Length == 0) return;
            BitmapImage hit;
            if (_chatImgCache.TryGetValue(url, out hit))
            {
                iv.Source = hit;
                if (fallback != null && ChatImgPlaceholder(fallback.Text)) fallback.Visibility = Visibility.Collapsed;
                return;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                byte[] raw = FetchChatImg(url);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (raw == null || raw.Length < 64)
                    {
                        if (fallback != null)
                        {
                            fallback.Text = (m.Kind == "sticker" ? "[表情]" : "[图片]") + "（加载失败，链接可能已过期）";
                            fallback.Visibility = Visibility.Visible;
                        }
                        return;
                    }
                    try
                    {
                        var bm = new BitmapImage();
                        bm.BeginInit();
                        bm.StreamSource = new MemoryStream(raw);
                        bm.CacheOption = BitmapCacheOption.OnLoad;
                        bm.DecodePixelWidth = 640;              // 缩到 640 宽就够看，省内存
                        bm.EndInit();
                        bm.Freeze();
                        if (_chatImgCache.Count > 240) _chatImgCache.Clear();
                        _chatImgCache[url] = bm;
                        iv.Source = bm;
                        if (fallback != null && ChatImgPlaceholder(fallback.Text)) fallback.Visibility = Visibility.Collapsed;
                    }
                    catch { }
                }));
            });
        }

        // ---- 双击看图（2026-10-28 用户：「图片可以双击放大，但不能误触」）----
        // 防误触的三条：
        //   ① 只有**图片本身**认双击（气泡正文双击仍是引用，事件到这里就 Handled，不再冒泡）
        //   ② 大图窗口刚开的前 350ms 不理任何点击 —— 双击的第二下不会立刻把它关掉
        //   ③ 关闭只有「Esc / 右键 / 点图外面的黑边」，点图片本身不关（想再看两眼时不会误关）
        Window _viewer;
        System.Windows.Controls.Image _viewerImg;
        ScaleTransform _viewerScale;
        TranslateTransform _viewerMove;
        Point _viewerFrom;
        bool _viewerDragging;
        DateTime _viewerAt = DateTime.MinValue;
        public Window ShotExtra;                 // -shot big：截图对象换成看图窗口

        void ShowImageViewer(BitmapImage bm, string url)
        {
            if (_viewer != null) return;         // 已经开着一张就别再叠一张
            if (bm == null && string.IsNullOrEmpty(url)) return;

            var win = new Window();
            win.WindowStyle = WindowStyle.None;
            win.WindowState = WindowState.Maximized;
            win.ResizeMode = ResizeMode.NoResize;
            win.Background = Brushes.Black;
            win.ShowInTaskbar = false;
            win.Owner = this;

            var root = new Grid();
            root.Background = Brushes.Black;

            var img = new System.Windows.Controls.Image();
            img.Stretch = Stretch.Uniform;
            img.HorizontalAlignment = HorizontalAlignment.Center;
            img.VerticalAlignment = VerticalAlignment.Center;
            img.MaxWidth = SystemParameters.PrimaryScreenWidth * 0.92;
            img.MaxHeight = SystemParameters.PrimaryScreenHeight * 0.92;
            img.Source = bm;
            img.Cursor = Cursors.SizeAll;

            var scale = new ScaleTransform(1, 1);
            var move = new TranslateTransform(0, 0);
            var tg = new TransformGroup();
            tg.Children.Add(scale);
            tg.Children.Add(move);
            img.RenderTransform = tg;
            img.RenderTransformOrigin = new Point(0.5, 0.5);
            root.Children.Add(img);

            var hint = new TextBlock();
            hint.Text = "滚轮缩放 · 拖动平移 · 双击图片复位 · Esc / 右键 / 点黑边关闭";
            hint.FontSize = 11.5 * S;
            hint.Foreground = new SolidColorBrush(Color.FromArgb(0xb0, 0xff, 0xff, 0xff));
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.VerticalAlignment = VerticalAlignment.Bottom;
            hint.Margin = new Thickness(0, 0, 0, 18 * S);
            hint.IsHitTestVisible = false;
            root.Children.Add(hint);

            win.Content = root;

            Action close = delegate
            {
                if ((DateTime.Now - _viewerAt).TotalMilliseconds < 350) return;   // 防误触②
                try { win.Close(); } catch { }
            };
            root.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.OriginalSource == root) close();      // 只有点黑边才关
            };
            root.MouseRightButtonUp += delegate { close(); };
            win.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) close();
            };
            root.MouseWheel += delegate(object s, MouseWheelEventArgs e)
            {
                double z = scale.ScaleX * (e.Delta > 0 ? 1.15 : 1 / 1.15);
                z = Math.Max(0.4, Math.Min(5.0, z));
                scale.ScaleX = scale.ScaleY = z;
                e.Handled = true;
            };
            img.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ClickCount == 2)              // 双击图片 = 复位（缩放/平移归零）
                {
                    scale.ScaleX = scale.ScaleY = 1;
                    move.X = move.Y = 0;
                    e.Handled = true;
                    return;
                }
                _viewerDragging = true;
                _viewerFrom = e.GetPosition(root);
                img.CaptureMouse();
                e.Handled = true;
            };
            img.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (!_viewerDragging) return;
                Point p = e.GetPosition(root);
                move.X += p.X - _viewerFrom.X;
                move.Y += p.Y - _viewerFrom.Y;
                _viewerFrom = p;
            };
            img.MouseLeftButtonUp += delegate
            {
                _viewerDragging = false;
                img.ReleaseMouseCapture();
            };
            win.Closed += delegate
            {
                _viewer = null;
                _viewerImg = null;
                _viewerScale = null;
                _viewerMove = null;
                if (ShotExtra == win) ShotExtra = null;
            };

            _viewer = win;
            _viewerImg = img;
            _viewerScale = scale;
            _viewerMove = move;
            _viewerAt = DateTime.Now;
            win.Show();
            win.Activate();

            // 双击时图可能还没下载完：先开窗，再后台补上
            if (bm == null && !string.IsNullOrEmpty(url))
            {
                string u = url;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    byte[] raw = FetchChatImg(u);
                    if (raw == null || raw.Length < 64) return;
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        try
                        {
                            var b2 = new BitmapImage();
                            b2.BeginInit();
                            b2.StreamSource = new MemoryStream(raw);
                            b2.CacheOption = BitmapCacheOption.OnLoad;
                            b2.DecodePixelWidth = 1600;
                            b2.EndInit();
                            b2.Freeze();
                            if (_viewerImg != null) _viewerImg.Source = b2;
                        }
                        catch { }
                    }));
                });
            }
        }

        /// <summary>-shot big：直接开一张大图出来截图（走真实接口取图）。</summary>
        public void ShowBigForShot()
        {
            string url = ApiBase + "/api/stickers/thumb?id=st001.png&token=" + Token;
            byte[] raw = FetchChatImg(url);
            BitmapImage bm = null;
            try
            {
                if (raw != null && raw.Length > 64)
                {
                    bm = new BitmapImage();
                    bm.BeginInit();
                    bm.StreamSource = new MemoryStream(raw);
                    bm.CacheOption = BitmapCacheOption.OnLoad;
                    bm.EndInit();
                    bm.Freeze();
                }
            }
            catch { }
            ShowImageViewer(bm, url);
            ShotExtra = _viewer;
        }

        void FillMsgs(List<MsgInfo> list, string chat, string title, bool silent)
        {
            if (_msgList == null || chat != _curChat) return;
            var sig = new StringBuilder();
            sig.Append(list.Count);
            foreach (var m in list) sig.Append("|").Append(m.Time).Append(m.Text.Length).Append(m.QuoteText.Length).Append(m.Quoted ? "q" : "");
            if (silent && sig.ToString() == _msgSig) return;    // 没新消息就别重画（不然滚动位置会跳）
            _msgSig = sig.ToString();

            if (_chatHeadTitle != null) _chatHeadTitle.Text = title;
            if (_chatHeadSub != null)
                _chatHeadSub.Text = chat + "  ·  " + list.Count + " 条  ·  双击/右键气泡可引用，＠ 选人";
            if (_chatStatus != null) _chatStatus.Text = silent ? "" : "已载入 " + chat;

            _msgList.Children.Clear();
            string lastWho = "";
            foreach (var m in list)
            {
                bool mine = m.Dir == "out";
                if (!mine && m.Who != lastWho && m.Who.Length > 0)
                {
                    var who = Txt(m.Who, 10.5, false, _th.bSub, 4);
                    who.Margin = new Thickness(6 * S, 6 * S, 0, 2 * S);
                    _msgList.Children.Add(who);
                }
                lastWho = mine ? "" : m.Who;

                var bub = new Border();
                bub.CornerRadius = new CornerRadius(9);
                bub.Padding = new Thickness(10 * S, 7 * S, 10 * S, 7 * S);
                bub.Margin = new Thickness(mine ? 60 * S : 0, 2 * S, mine ? 0 : 60 * S, 2 * S);
                bub.HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                bub.Background = mine ? _th.bAccent : _th.bBtn;
                bub.MaxWidth = 460 * S;

                var inner = new StackPanel();
                // 引用了别人（或自己）的话：气泡里先画一条引用（和手机端一个样式）
                if (m.HasQuote)
                {
                    var qb = new Border();
                    qb.CornerRadius = new CornerRadius(6);
                    qb.Padding = new Thickness(8 * S, 5 * S, 8 * S, 5 * S);
                    qb.Margin = new Thickness(0, 0, 0, 6 * S);
                    qb.Background = mine
                        ? new SolidColorBrush(Color.FromArgb(0x46, 0x0b, 0x10, 0x20))
                        : _th.bCard;
                    qb.BorderBrush = mine ? Brushes.White : _th.bAccent;
                    qb.BorderThickness = new Thickness(3, 0, 0, 0);

                    var qsp = new StackPanel();
                    if (!string.IsNullOrEmpty(m.QuoteWho))
                    {
                        var qw = new TextBlock();
                        qw.Text = m.QuoteWho;
                        qw.FontSize = 10.5 * S;
                        qw.FontWeight = FontWeights.SemiBold;
                        qw.Foreground = mine ? Brushes.White : _th.bAccent;
                        qw.Margin = new Thickness(0, 0, 0, 1 * S);
                        qsp.Children.Add(qw);
                    }
                    var qt = new TextBlock();
                    qt.Text = m.QuoteText.Length > 0 ? m.QuoteText : "引用了一条消息";
                    qt.FontSize = 11 * S;
                    qt.Foreground = mine
                        ? new SolidColorBrush(Color.FromArgb(0xee, 0xff, 0xff, 0xff))
                        : _th.bText;
                    qt.TextWrapping = TextWrapping.Wrap;
                    qt.MaxWidth = 400 * S;
                    qsp.Children.Add(qt);
                    qb.Child = qsp;
                    inner.Children.Add(qb);
                }
                var t = new TextBlock();
                t.Text = m.Text;
                t.FontSize = 12.5 * S;
                t.Foreground = mine ? Brushes.White : _th.bText;
                t.TextWrapping = TextWrapping.Wrap;
                // 有图就先画图（别人发的图片 / 表情包），文字只当兜底：加载成功就把"[图片]"藏起来
                if (m.Img.Length > 0 || m.Stk.Length > 0 || m.Loc.Length > 0)
                {
                    var iv = new System.Windows.Controls.Image();
                    iv.Stretch = Stretch.Uniform;
                    // 表情包画小一点（但别像以前那么小），照片给大一点
                    bool isSticker = m.Img.Length == 0 && (m.Stk.Length > 0 || m.Loc.Length > 0);
                    double cap = (isSticker ? 220 : 340) * S;
                    iv.MaxWidth = cap;
                    iv.MaxHeight = cap;
                    iv.HorizontalAlignment = HorizontalAlignment.Left;
                    var frame = new Border();
                    frame.CornerRadius = new CornerRadius(8);
                    frame.HorizontalAlignment = HorizontalAlignment.Left;
                    frame.Margin = new Thickness(0, 0, 0, 3 * S);
                    frame.Child = iv;
                    inner.Children.Add(frame);
                    LoadChatImage(m, iv, t);
                    // 双击图片放大（2026-10-28 用户要求）。**只有图本身认双击**：
                    // 气泡别的区域双击照旧是「引用」，而且这里 e.Handled 一设，事件不再冒泡到气泡，
                    // 所以不会出现「想引用却弹了个大图」这种误触。
                    iv.Cursor = Cursors.Hand;
                    iv.ToolTip = "双击放大";
                    iv.MouseLeftButtonDown += delegate(object s4, MouseButtonEventArgs e4)
                    {
                        if (e4.ClickCount < 2) return;
                        e4.Handled = true;
                        ShowImageViewer(iv.Source as BitmapImage, ChatImgUrl(m));
                    };
                }
                inner.Children.Add(t);
                bub.Child = inner;

                // 右键：引用 / 复制 / ＠他；双击也能直接引用（右键菜单不如双击顺手）
                var cm = BubbleMenu(m);
                bub.ContextMenu = cm;
                bub.MouseLeftButtonDown += delegate(object s3, MouseButtonEventArgs e3)
                {
                    if (e3.ClickCount == 2) SetQuote(m);
                };

                var wrap = new StackPanel();
                wrap.HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                wrap.Children.Add(bub);
                var time = Txt(m.Time, 9.5, false, _th.bSub, 1);
                time.HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                wrap.Children.Add(time);
                _msgList.Children.Add(wrap);
            }
            if (list.Count == 0)
                _msgList.Children.Add(Txt("这个会话还没有抓到消息", 11.5, false, _th.bSub, 10));

            _msgScroll.ScrollToEnd();
            // 刚塞进控件时布局还没算完，ScrollToEnd 有时差一截；再补一次，保证看到最新一条
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_msgScroll != null) _msgScroll.ScrollToEnd();
            }), DispatcherPriority.Background);
        }

        // ------------------------------------------------ 引用 / ＠ 某人
        static bool IsGroupChat(string chat)
        {
            return (chat ?? "").TrimStart().StartsWith("群");
        }

        ContextMenu BubbleMenu(MsgInfo m)
        {
            var cm = new ContextMenu();
            cm.Background = _th.bCard;
            cm.Foreground = _th.bText;
            cm.BorderBrush = _th.bLine;

            var miQuote = new MenuItem();
            miQuote.Header = "引用这条";
            miQuote.Foreground = _th.bText;
            miQuote.Background = _th.bCard;
            miQuote.Click += delegate { SetQuote(m); };
            cm.Items.Add(miQuote);

            var miCopy = new MenuItem();
            miCopy.Header = "复制文字";
            miCopy.Foreground = _th.bText;
            miCopy.Background = _th.bCard;
            miCopy.Click += delegate
            {
                try { Clipboard.SetText(m.Text ?? ""); } catch { }
                if (_chatStatus != null) _chatStatus.Text = "已复制";
            };
            cm.Items.Add(miCopy);

            if (IsGroupChat(_curChat) && !string.IsNullOrEmpty(m.Who) && m.Who != "我")
            {
                var miAt = new MenuItem();
                miAt.Header = "＠" + m.Who;
                miAt.Foreground = _th.bText;
                miAt.Background = _th.bCard;
                miAt.Click += delegate { ShowMembers(m.Who); };
                cm.Items.Add(miAt);
            }
            return cm;
        }

        void SetQuote(MsgInfo m)
        {
            _quoteMsg = m;
            RebuildChips();
            if (_chatStatus != null)
                _chatStatus.Text = "将引用 " + (string.IsNullOrEmpty(m.Who) ? "这条消息" : m.Who) + "，写完点发送";
            if (_chatInput != null) _chatInput.Focus();
        }

        void AddAt(string uid, string name)
        {
            if (string.IsNullOrEmpty(uid)) return;
            if (!IsGroupChat(_curChat))
            {
                if (_chatStatus != null) _chatStatus.Text = "私聊没法 ＠ 人";
                return;
            }
            foreach (var a in _atTargets)
                if (a.Uid == uid) { RebuildChips(); return; }
            if (_atTargets.Count >= 5)
            {
                if (_chatStatus != null) _chatStatus.Text = "一次最多 ＠ 5 个人";
                return;
            }
            _atTargets.Add(new AtTarget { Uid = uid, Name = name });
            RebuildChips();
            if (_chatInput != null) _chatInput.Focus();
        }

        /// <summary>输入框上方那排「引用条 / ＠芯片」，有内容才显示。</summary>
        void RebuildChips()
        {
            if (_composeChips == null) return;
            _composeChips.Children.Clear();
            var wrap = new WrapPanel();
            if (_quoteMsg != null)
            {
                string raw = _quoteMsg.Text ?? "";
                string snippet = raw.Length > 40 ? raw.Substring(0, 40) + "…" : raw;
                string label = "引用 " + (string.IsNullOrEmpty(_quoteMsg.Who) ? "" : _quoteMsg.Who + "：") + snippet;
                wrap.Children.Add(Chip(label, delegate { _quoteMsg = null; RebuildChips(); }));
            }
            foreach (var a in new List<AtTarget>(_atTargets))
            {
                var target = a;
                wrap.Children.Add(Chip("＠" + a.Name, delegate
                {
                    _atTargets.Remove(target);
                    RebuildChips();
                }));
            }
            _composeChips.Children.Add(wrap);
            _composeChips.Visibility = (_quoteMsg != null || _atTargets.Count > 0)
                ? Visibility.Visible : Visibility.Collapsed;
            if (_atBtn != null) _atBtn.IsEnabled = IsGroupChat(_curChat);
        }

        FrameworkElement Chip(string text, RoutedEventHandler onClose)
        {
            var bd = new Border();
            bd.Background = _th.bBtn;
            bd.BorderBrush = _th.bLine;
            bd.BorderThickness = new Thickness(1);
            bd.CornerRadius = new CornerRadius(8);
            bd.Padding = new Thickness(8 * S, 3 * S, 4 * S, 3 * S);
            bd.Margin = new Thickness(0, 0, 6 * S, 4 * S);

            var sp = new DockPanel();
            var x = Btn("✕", false, onClose);
            x.Padding = new Thickness(6 * S, 0, 6 * S, 0);
            x.Margin = new Thickness(6 * S, 0, 0, 0);
            x.FontSize = 9.5 * S;
            x.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(x, Dock.Right);
            sp.Children.Add(x);

            var tb = new TextBlock();
            tb.Text = text;
            tb.FontSize = 11 * S;
            tb.Foreground = _th.bText;
            tb.VerticalAlignment = VerticalAlignment.Center;
            tb.TextTrimming = TextTrimming.CharacterEllipsis;
            tb.MaxWidth = 300 * S;
            sp.Children.Add(tb);

            bd.Child = sp;
            return bd;
        }

        void ToggleMembers()
        {
            if (_memberPanel == null) return;
            if (_memberPanel.Visibility == Visibility.Visible) { HideMembers(); return; }
            ShowMembers("");
        }

        void ShowMembers(string prefill)
        {
            if (!IsGroupChat(_curChat))
            {
                if (_chatStatus != null) _chatStatus.Text = "私聊没法 ＠ 人";
                return;
            }
            if (_memberPanel == null) return;
            _memberPanel.Visibility = Visibility.Visible;
            if (_memberSearch != null)
            {
                _memberSearch.Text = prefill ?? "";
                _memberSearch.Focus();
            }
            LoadMembers();
            FillMembers();
        }

        void HideMembers()
        {
            if (_memberPanel != null) _memberPanel.Visibility = Visibility.Collapsed;
        }

        void LoadMembers()
        {
            string chat = _curChat;
            if (chat.Length == 0) return;
            if (_membersChat == chat && _members.Count > 0) { FillMembers(); return; }
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/members?token=" + Token +
                                            "&chat=" + Uri.EscapeDataString(chat), 45);
                var list = ParseMembers(json);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (chat != _curChat) return;
                    if (list.Count > 0)
                    {
                        _members.Clear();
                        _members.AddRange(list);
                        _membersChat = chat;
                    }
                    FillMembers();
                }));
            });
        }

        List<Member> ParseMembers(string json)
        {
            var list = new List<Member>();
            try
            {
                var o = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                var items = o.ContainsKey("items") ? o["items"] as object[] : null;
                if (items == null) return list;
                foreach (var it in items)
                {
                    var d = it as Dictionary<string, object>;
                    if (d == null) continue;
                    var mb = new Member();
                    mb.Uid = d.ContainsKey("uid") ? Convert.ToString(d["uid"]) : "";
                    mb.Name = d.ContainsKey("name") ? Convert.ToString(d["name"]) : mb.Uid;
                    mb.Nick = d.ContainsKey("nick") ? Convert.ToString(d["nick"]) : "";
                    mb.Role = d.ContainsKey("role") ? Convert.ToString(d["role"]) : "";
                    if (mb.Uid.Length > 0) list.Add(mb);
                }
            }
            catch { }
            return list;
        }

        void FillMembers()
        {
            if (_memberList == null) return;
            _memberList.Children.Clear();
            string q = _memberSearch == null ? "" : (_memberSearch.Text ?? "").Trim();
            int shown = 0;
            if (_members.Count == 0)
            {
                _memberList.Children.Add(Txt("还没拉到成员列表（检查 SSH / SnowLuma）", 11.5, false, _th.bSub, 6));
                return;
            }
            foreach (var mb in _members)
            {
                if (mb.Role == "owner" || mb.Role == "admin")
                {
                    // 群主/管理排前面：不改变顺序也能一眼看到，这里只做标注
                }
                if (q.Length > 0 && mb.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                    && mb.Nick.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                    && mb.Uid.IndexOf(q) < 0) continue;
                if (shown >= 80) break;
                shown++;

                string label = mb.Name;
                if (mb.Nick.Length > 0 && mb.Nick != mb.Name) label += "（" + mb.Nick + "）";
                label += "  " + mb.Uid;

                var b = Btn(label, false, null);
                b.Template = FlatTemplate();          // 左对齐（BtnTemplate 的内容是居中的）
                b.HorizontalAlignment = HorizontalAlignment.Stretch;
                b.HorizontalContentAlignment = HorizontalAlignment.Left;
                b.Margin = new Thickness(0, 1 * S, 0, 1 * S);
                b.Padding = new Thickness(8 * S, 5 * S, 8 * S, 5 * S);
                var target = mb;
                b.Click += delegate { AddAt(target.Uid, target.Name); HideMembers(); };
                _memberList.Children.Add(b);
            }
            if (shown == 0)
                _memberList.Children.Add(Txt("没有匹配的人", 11.5, false, _th.bSub, 6));
        }

        void PaintVoiceToggle()
        {
            if (_voiceToggle == null) return;
            _voiceToggle.Content = _sendVoice ? "🎤✓" : "🎤";
            _voiceToggle.Background = _sendVoice ? _th.bAccent : _th.bBtn;
            _voiceToggle.Foreground = _sendVoice ? Brushes.White : _th.bText;
        }

        void SendChat()
        {
            if (_curChat.Length == 0) { if (_chatStatus != null) _chatStatus.Text = "先选一个会话"; return; }
            if (_chatInput == null) return;
            string text = (_chatInput.Text ?? "").Trim();
            if (text.Length == 0)
            {
                if (_chatStatus != null)
                    _chatStatus.Text = _atTargets.Count > 0 ? "光 ＠ 不算一条消息，写点字" : "";
                return;
            }
            string chat = _curChat;
            var ats = new List<AtTarget>(_atTargets);
            var quote = _quoteMsg;
            bool voice = _sendVoice;
            _chatInput.Text = "";
            if (_chatStatus != null) _chatStatus.Text = voice ? "合成语音中…" : "发送中…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                var payload = new Dictionary<string, object>();
                payload["chat"] = chat;
                payload["text"] = text;
                if (voice) payload["voice"] = true;      // 走 8090 的 TTS → SILK
                if (ats.Count > 0)
                {
                    var arr = new List<object>();
                    foreach (var a in ats)
                        arr.Add(new Dictionary<string, object> { { "uid", a.Uid }, { "name", a.Name } });
                    payload["at"] = arr;
                }
                if (quote != null)
                {
                    payload["quote"] = new Dictionary<string, object>
                    {
                        { "who", quote.Who ?? "" },
                        { "text", quote.Text ?? "" },
                        { "ts", quote.Time ?? "" },
                        { "dt", quote.Dt ?? "" }
                    };
                }
                string body = new JavaScriptSerializer().Serialize(payload);
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                string cmd = "echo " + b64 + " | base64 -d > /tmp/fysend.json; " +
                             "curl -s -m 25 -X POST -H 'Content-Type: application/json' --data-binary @/tmp/fysend.json " +
                             "'" + ApiBase + "/api/send?token=" + Token + "'";
                string r = Ssh.Run(_cfg, cmd, 45);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool ok = r.IndexOf("\"ok\": true") >= 0 || r.IndexOf("\"ok\":true") >= 0;
                    if (ok)
                    {
                        _atTargets.Clear();
                        _quoteMsg = null;
                        RebuildChips();
                        if (_chatStatus != null) _chatStatus.Text = "已发送 → " + chat;
                    }
                    else
                    {
                        if (_chatInput != null && _chatInput.Text.Length == 0) _chatInput.Text = text;
                        if (_chatStatus != null) _chatStatus.Text = "发送失败：" + FirstLine(r);
                    }
                    _msgSig = "";
                    LoadMessages(false);
                }));
            });
        }

        // ------------------------------------------------ 表情包（发表情，界面仿 QQ）
        // 数据源就是她自己的那套：服务器 /opt/qqbot/stickers（287 张，带「情绪|描述」索引）。
        // 缩略图由后端批量生成，本机再存一份缓存，第二次打开就是秒开。
        /// <summary>黑话的一行（词 / 说明 / 是否勾选）。2026-11-01：加了勾选，用来只复制挑中的几条。</summary>
        class SlangRow
        {
            public string K = "";
            public string V = "";
            public bool Sel = true;
        }

        /// <summary>群聊黑话编辑器（2026-11-01 重写：选群 + 逐条增删改查，和网页版一致）</summary>
        void EditSlang()
        {
            string curGid = "";
            if ((_curChat ?? "").StartsWith("group:")) curGid = _curChat.Substring(6).Trim();

            var w = new Window();
            w.Title = "群聊黑话 · 按群存「词 = 说明」";
            w.Width = 780 * S;
            w.Height = 640 * S;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            w.Owner = this;
            w.Background = _th.bCard;

            var grid = new Grid();
            grid.Margin = new Thickness(14 * S);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ---- 顶部：选群 + 搜索 ----
            var top = new WrapPanel();
            top.Margin = new Thickness(0, 0, 0, 8 * S);
            var gsel = new ComboBox();
            gsel.Width = 330 * S;
            gsel.ToolTip = "要编辑哪个群的黑话（带条数）";
            ComboStyle(gsel);
            top.Children.Add(gsel);
            var qbox = new TextBox();
            qbox.Width = 210 * S;
            qbox.FontSize = 12.5 * S;
            qbox.Margin = new Thickness(8 * S, 0, 0, 0);
            qbox.ToolTip = "按词或说明过滤";
            StyleInput(qbox);
            top.Children.Add(qbox);
            // 复制：从别的群（或全局）拉进来 / 把这份同时存到多个群（2026-11-01 用户要的）
            var csel = new ComboBox();
            csel.Width = 230 * S;
            csel.Margin = new Thickness(8 * S, 0, 8 * S, 8 * S);
            csel.ToolTip = "要复制谁的黑话（来源）";
            ComboStyle(csel);
            top.Children.Add(csel);
            var pullBtn = Btn("拉进来", false, null);
            pullBtn.Margin = new Thickness(8 * S, 0, 0, 0);
            pullBtn.ToolTip = "把来源那份并进当前群（同名词用来源的），再点保存";
            top.Children.Add(pullBtn);
            var pushBtn = Btn("存到多个群", false, null);
            pushBtn.Margin = new Thickness(6 * S, 0, 0, 0);
            pushBtn.ToolTip = "把当前群这份同时写进勾选的多个群";
            top.Children.Add(pushBtn);
            Grid.SetRow(top, 0);
            grid.Children.Add(top);

            // ---- 中间：一行一条 ----
            var scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            var rowsPanel = new StackPanel();
            scroll.Content = rowsPanel;
            Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);

            // ---- 底部：按钮 + 状态 ----
            var bottom = new StackPanel();
            var status = Txt("正在读取…", 11, false, _th.bSub, 0);
            bottom.Children.Add(status);
            var btns = new StackPanel();
            btns.Orientation = Orientation.Horizontal;
            btns.HorizontalAlignment = HorizontalAlignment.Right;
            btns.Margin = new Thickness(0, 8 * S, 0, 0);
            bottom.Children.Add(btns);
            Grid.SetRow(bottom, 2);
            grid.Children.Add(bottom);

            w.Content = grid;

            // ---- 数据 ----
            var ALL = new Dictionary<string, Dictionary<string, string>>();
            var NAME = new Dictionary<string, string>();
            var rows = new List<SlangRow>();
            var gid = curGid;
            var busy = false;

            Action syncStatus = delegate
            {
                var n = 0;
                var sel = 0;
                foreach (var r in rows)
                {
                    if (r.K.Trim().Length > 0 && r.V.Trim().Length > 0)
                    {
                        n++;
                        if (r.Sel) sel++;
                    }
                }
                var total = 0;
                foreach (var kv in ALL) total += kv.Value.Count;
                status.Text = "这个群 " + n + " 条（勾选 " + sel + " 条，复制只带勾选的）· 已载入 "
                              + ALL.Count + " 个群 / " + total + " 条";
            };

            Action renderRows = null;
            renderRows = delegate
            {
                rowsPanel.Children.Clear();
                var kw = (qbox.Text ?? "").Trim();
                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    var k = row.K;
                    var v = row.V;
                    if (kw.Length > 0
                        && k.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0
                        && v.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var idx = i;
                    // 2026-11-02 用户：「文字顶到按钮边上」——一行改成 Grid（勾选 / 词 / 说明* / 删），
                    // 说明列自适应窗口宽度，右下角那个 ✕ 再也不会被挤出窗口。
                    var line = new Grid();
                    line.Margin = new Thickness(0, 0, 0, 6 * S);
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(175 * S) });
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    // 勾选：复制时只带勾中的那几条（2026-11-01 用户要的）
                    var sel = new CheckBox();
                    sel.IsChecked = row.Sel;
                    sel.VerticalAlignment = VerticalAlignment.Center;
                    sel.Margin = new Thickness(0, 0, 8 * S, 0);
                    sel.ToolTip = "勾上：复制时带上这条";
                    // 用 Checked/Unchecked（鼠标点、UIAutomation 的 Toggle 都会触发；Click 在自动化下不一定来）
                    sel.Checked += delegate { row.Sel = true; syncStatus(); };
                    sel.Unchecked += delegate { row.Sel = false; syncStatus(); };
                    Grid.SetColumn(sel, 0);
                    line.Children.Add(sel);

                    var kk = new TextBox();
                    kk.Text = k;
                    kk.MaxLength = 20;
                    kk.FontSize = 12.5 * S;
                    kk.HorizontalAlignment = HorizontalAlignment.Stretch;
                    kk.ToolTip = "词（最多 20 字）";
                    StyleInput(kk);
                    kk.TextChanged += delegate { row.K = kk.Text; syncStatus(); };
                    Grid.SetColumn(kk, 1);
                    line.Children.Add(kk);

                    var vv = new TextBox();
                    vv.Text = v;
                    vv.MaxLength = 80;
                    vv.FontSize = 12.5 * S;
                    vv.HorizontalAlignment = HorizontalAlignment.Stretch;
                    vv.Margin = new Thickness(8 * S, 0, 8 * S, 0);
                    vv.ToolTip = "说明：什么意思 / 什么场合用 / 褒还是贬（最多 80 字）";
                    StyleInput(vv);
                    vv.TextChanged += delegate { row.V = vv.Text; syncStatus(); };
                    Grid.SetColumn(vv, 2);
                    line.Children.Add(vv);

                    var del = Btn("✕", false, delegate
                    {
                        if (idx >= 0 && idx < rows.Count) rows.RemoveAt(idx);
                        renderRows();
                    });
                    del.Margin = new Thickness(0);
                    del.MinWidth = 34 * S;
                    del.MinHeight = 30 * S;
                    del.Padding = new Thickness(8 * S, 3 * S, 8 * S, 3 * S);
                    del.ToolTip = "删掉这条（记得点保存）";
                    Grid.SetColumn(del, 3);
                    line.Children.Add(del);

                    rowsPanel.Children.Add(line);
                }
                if (rowsPanel.Children.Count == 0)
                    rowsPanel.Children.Add(Txt(
                        kw.Length > 0 ? "没有匹配的词" : "这个群还没有黑话，点「＋ 加一条」开始",
                        12, false, _th.bSub, 8));
                syncStatus();
            };

            Action<string> loadGroup = delegate(string g)
            {
                rows.Clear();
                Dictionary<string, string> m;
                if (g.Length > 0 && ALL.TryGetValue(g, out m) && m != null)
                    foreach (var kv in m)
                        rows.Add(new SlangRow { K = kv.Key, V = kv.Value, Sel = true });
                if (rows.Count == 0) rows.Add(new SlangRow());
                renderRows();
            };

            // ---- 顶部事件 ----
            qbox.TextChanged += delegate { renderRows(); };
            gsel.SelectionChanged += delegate
            {
                if (busy) return;
                var it = gsel.SelectedItem as ComboBoxItem;
                var g = (it != null && it.Tag != null) ? Convert.ToString(it.Tag) : "";
                if (g.Length == 0) return;
                gid = g;
                loadGroup(g);
            };

            // ---- 复制：拉进来 / 存到多个群（2026-11-01） ----
            Action snapshot = null;

            pullBtn.Click += delegate
            {
                var it = csel.SelectedItem as ComboBoxItem;
                var src = (it != null && it.Tag != null) ? Convert.ToString(it.Tag) : "";
                if (src.Length == 0) { status.Text = "先在「复制自」里选一个来源"; return; }
                if (src == gid) { status.Text = "来源就是当前群，换个来源"; return; }
                Dictionary<string, string> sm;
                if (!ALL.TryGetValue(src, out sm) || sm == null || sm.Count == 0)
                { status.Text = "来源没有黑话可拉"; return; }
                var added = 0;
                var updated = 0;
                foreach (var kv in sm)
                {
                    var at = -1;
                    for (var i = 0; i < rows.Count; i++)
                        if (rows[i].K.Trim() == kv.Key) { at = i; break; }
                    if (at >= 0)
                    {
                        if (rows[at].V != kv.Value) { rows[at].V = kv.Value; updated++; }
                    }
                    else { rows.Add(new SlangRow { K = kv.Key, V = kv.Value, Sel = true }); added++; }
                }
                renderRows();
                status.Text = "从 " + (src == "*" ? "全局" : src) + " 拉了 " + added + " 条、更新 " + updated + " 条（记得点保存）";
            };

            pushBtn.Click += delegate
            {
                // 只带**勾选**的那几条（2026-11-01 用户：「我不想每条都复制」）
                var srcMap = new Dictionary<string, string>();
                var notSel = 0;
                foreach (var r in rows)
                {
                    var k = r.K.Trim();
                    var v = r.V.Trim();
                    if (k.Length == 0 || v.Length == 0) continue;
                    if (!r.Sel) { notSel++; continue; }
                    srcMap[k] = v;
                }
                if (srcMap.Count == 0) { status.Text = "一条都没勾（先勾上要复制的词）"; return; }
                if (notSel > 0) status.Text = "只带勾选的 " + srcMap.Count + " 条（没勾的 " + notSel + " 条不复制）";

                // 目标群列表（所有群 + 只出现在黑话里的群）
                var targets = new List<string>();
                foreach (var k2 in NAME.Keys) if (!targets.Contains(k2)) targets.Add(k2);
                foreach (var k2 in ALL.Keys) if (!targets.Contains(k2)) targets.Add(k2);
                targets.Remove(gid);
                targets.Remove("*");
                if (targets.Count == 0) { status.Text = "没有别的群可以存"; return; }

                var pw = new Window();
                pw.Title = "存到多个群";
                pw.Width = 440 * S;
                pw.Height = 560 * S;
                pw.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                pw.Owner = w;
                pw.Background = _th.bCard;
                var pg = new Grid();
                pg.Margin = new Thickness(14 * S);
                pg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                pg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                pg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var head = Txt("把当前这份（" + srcMap.Count + " 条）写进下面勾选的群：", 12, false, _th.bSub, 0);
                Grid.SetRow(head, 0);
                pg.Children.Add(head);

                var listPanel = new StackPanel();
                var checks = new List<CheckBox>();
                foreach (var tg in targets)
                {
                    var nm3 = NAME.ContainsKey(tg) ? NAME[tg] : "";
                    Dictionary<string, string> tm;
                    var cnt3 = (ALL.TryGetValue(tg, out tm) && tm != null) ? tm.Count : 0;
                    var cb = new CheckBox();
                    cb.Content = (nm3.Length > 0 ? nm3 : "群") + " · " + tg + "（" + cnt3 + " 条）";
                    cb.FontSize = 12.5 * S;
                    cb.Foreground = _th.bText;
                    cb.Margin = new Thickness(0, 3 * S, 0, 3 * S);
                    cb.Tag = tg;
                    listPanel.Children.Add(cb);
                    checks.Add(cb);
                }
                var sc2 = new ScrollViewer();
                sc2.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                sc2.Content = listPanel;
                Grid.SetRow(sc2, 1);
                pg.Children.Add(sc2);

                var bot2 = new StackPanel();
                var owCb = new CheckBox();
                owCb.Content = "覆盖目标群里同名的词（不勾 = 目标已有的保留）";
                owCb.FontSize = 12 * S;
                owCb.Foreground = _th.bSub;
                owCb.Margin = new Thickness(0, 6 * S, 0, 6 * S);
                bot2.Children.Add(owCb);
                var row2 = new StackPanel();
                row2.Orientation = Orientation.Horizontal;
                row2.HorizontalAlignment = HorizontalAlignment.Right;
                row2.Children.Add(Btn("取消", false, delegate { pw.Close(); }));
                row2.Children.Add(Btn("写入勾选的群", true, delegate
                {
                    var picked = new List<string>();
                    foreach (var c2 in checks)
                        if (c2.IsChecked == true && c2.Tag != null) picked.Add(Convert.ToString(c2.Tag));
                    if (picked.Count == 0) { status.Text = "一个群都没勾"; return; }
                    var ov = owCb.IsChecked == true;
                    foreach (var tg2 in picked)
                    {
                        Dictionary<string, string> tm2;
                        var m3 = (ALL.TryGetValue(tg2, out tm2) && tm2 != null)
                                 ? new Dictionary<string, string>(tm2) : new Dictionary<string, string>();
                        foreach (var kv3 in srcMap)
                            if (ov || !m3.ContainsKey(kv3.Key)) m3[kv3.Key] = kv3.Value;
                        ALL[tg2] = m3;
                        var o3 = new Dictionary<string, object>();
                        foreach (var kv4 in m3) o3[kv4.Key] = kv4.Value;
                        SlangSave(tg2, o3, null);
                    }
                    pw.Close();
                    status.Text = "已写入 " + picked.Count + " 个群（" + string.Join("、", picked.ToArray()) + "），几秒后生效；重开这个窗口能看到新条数";
                }));
                bot2.Children.Add(row2);
                Grid.SetRow(bot2, 2);
                pg.Children.Add(bot2);

                pw.Content = pg;
                pw.Show();
            };

            // ---- 底部按钮 ----
            btns.Children.Add(Btn("关闭", false, delegate { w.Close(); }));
            btns.Children.Add(Btn("清空这个群", false, delegate
            {
                if (gid.Length == 0) { status.Text = "先选一个群"; return; }
                if (MessageBox.Show("清空「" + gid + "」的全部黑话？", "确认",
                        MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                rows.Clear();
                rows.Add(new SlangRow());
                renderRows();
                ALL[gid] = new Dictionary<string, string>();
                SlangSave(gid, new Dictionary<string, object>(), status);
            }));
            btns.Children.Add(Btn("加一条", false, delegate
            {
                rows.Add(new SlangRow());
                qbox.Text = "";
                renderRows();
                if (rowsPanel.Children.Count > 0)
                {
                    var last = rowsPanel.Children[rowsPanel.Children.Count - 1] as StackPanel;
                    if (last != null && last.Children.Count > 0)
                    {
                        var tb = last.Children[0] as TextBox;
                        if (tb != null) tb.Focus();
                    }
                }
            }));
            btns.Children.Add(Btn("保存这个群", true, delegate
            {
                if (gid.Length == 0) { status.Text = "先选一个群"; return; }
                var items = new Dictionary<string, object>();
                var local = new Dictionary<string, string>();
                var skipped = 0;
                foreach (var r in rows)
                {
                    var k = r.K.Trim();
                    var v = r.V.Trim();
                    if (k.Length > 0 && v.Length > 0) { items[k] = v; local[k] = v; }
                    else if (k.Length > 0 || v.Length > 0) skipped++;
                }
                if (skipped > 0) status.Text = "有 " + skipped + " 行没填全，跳过了";
                ALL[gid] = local;
                SlangSave(gid, items, status);
            }));

            w.Show();

            // ---- 读数据（整库 + 群列表） ----
            ThreadPool.QueueUserWorkItem(delegate
            {
                var js = Ssh.Curl(_cfg, "/api/slang?token=" + Token, 25);
                var jg = Ssh.Curl(_cfg, "/api/groups?token=" + Token, 25);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    var ser = new JavaScriptSerializer();
                    try
                    {
                        var o = ser.DeserializeObject(js) as Dictionary<string, object>;
                        object gsObj = null;
                        if (o != null && o.ContainsKey("groups")) gsObj = o["groups"];
                        var gs = gsObj as Dictionary<string, object>;
                        if (gs != null)
                        {
                            foreach (var kv in gs)
                            {
                                var d = new Dictionary<string, string>();
                                var m = kv.Value as Dictionary<string, object>;
                                if (m != null)
                                    foreach (var p in m)
                                        d[Convert.ToString(p.Key)] = Convert.ToString(p.Value);
                                ALL[kv.Key] = d;
                            }
                        }
                    }
                    catch { }
                    try
                    {
                        var o2 = ser.DeserializeObject(jg) as Dictionary<string, object>;
                        object arrObj = null;
                        if (o2 != null && o2.ContainsKey("items")) arrObj = o2["items"];
                        var arr = arrObj as object[];
                        if (arr != null)
                        {
                            foreach (var it in arr)
                            {
                                var g3 = it as Dictionary<string, object>;
                                if (g3 == null) continue;
                                var id = Convert.ToString(g3.ContainsKey("gid") ? g3["gid"] : "");
                                var nm = Convert.ToString(g3.ContainsKey("name") ? g3["name"] : "");
                                if (id.Length > 0 && !NAME.ContainsKey(id)) NAME[id] = nm;
                            }
                        }
                    }
                    catch { }

                    // 下拉：所有群 + 只出现在黑话里的群，按群号排序（提取成 renderGroups，复制后要刷新条数）
                    var all = new List<string>();
                    foreach (var k in NAME.Keys) if (!all.Contains(k)) all.Add(k);
                    foreach (var k in ALL.Keys) if (!all.Contains(k)) all.Add(k);
                    all.Sort(StringComparer.Ordinal);
                    if (all.Count == 0) { status.Text = "一个群都没有（协议端没连上？）"; return; }

                    busy = true;
                    gsel.Items.Clear();
                    csel.Items.Clear();
                    ComboBoxItem pick = null;
                    // 全局（2026-11-01 用户要的）：所有群都认，排最上面
                    Dictionary<string, string> gm;
                    var gcount = (ALL.TryGetValue("*", out gm) && gm != null) ? gm.Count : 0;
                    var gci = new ComboBoxItem();
                    gci.Content = "全局（所有群）（" + gcount + " 条）";
                    gci.Tag = "*";
                    gsel.Items.Add(gci);
                    var gci2 = new ComboBoxItem();
                    gci2.Content = "全局（所有群）（" + gcount + " 条）";
                    gci2.Tag = "*";
                    csel.Items.Add(gci2);
                    csel.SelectedItem = gci2;
                    if (curGid == "*") pick = gci;
                    foreach (var g4 in all)
                    {
                        var nm2 = NAME.ContainsKey(g4) ? NAME[g4] : "";
                        Dictionary<string, string> m2;
                        var cnt = (ALL.TryGetValue(g4, out m2) && m2 != null) ? m2.Count : 0;
                        var label = (nm2.Length > 0 ? nm2 : "群") + " · " + g4 + "（" + cnt + " 条）";
                        var ci = new ComboBoxItem();
                        ci.Content = label;
                        ci.Tag = g4;
                        gsel.Items.Add(ci);
                        var ci7 = new ComboBoxItem();
                        ci7.Content = label;
                        ci7.Tag = g4;
                        csel.Items.Add(ci7);
                        if (g4 == curGid) pick = ci;
                    }
                    if (pick == null)
                    {
                        // 没指定群（聊天页没选群）→ 默认选「黑话最多」的那个（含全局），比空群有用
                        var bestGid = "";
                        var bestN = gcount;      // 全局条目数先当基准
                        foreach (var g5 in all)
                        {
                            Dictionary<string, string> mm;
                            var c3 = (ALL.TryGetValue(g5, out mm) && mm != null) ? mm.Count : 0;
                            if (c3 > bestN) { bestN = c3; bestGid = g5; }
                        }
                        foreach (var o5 in gsel.Items)
                        {
                            var ci5 = o5 as ComboBoxItem;
                            if (ci5 != null && Convert.ToString(ci5.Tag) == bestGid) { pick = ci5; break; }
                        }
                        if (pick == null) pick = gsel.Items[0] as ComboBoxItem;
                    }
                    gsel.SelectedItem = pick;
                    busy = false;
                    if (pick != null)
                    {
                        gid = Convert.ToString(pick.Tag);
                        loadGroup(gid);
                    }
                }));
            });
        }

        void SlangSave(string gid, Dictionary<string, object> items, TextBlock status)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                var p = new Dictionary<string, object>();
                p["gid"] = gid;
                p["items"] = items;
                string body = new JavaScriptSerializer().Serialize(p);
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                string cmd = "echo " + b64 + " | base64 -d > /tmp/slang.json; " +
                             "curl -s -m 20 -X POST -H 'Content-Type: application/json' " +
                             "--data-binary @/tmp/slang.json '" + ApiBase + "/api/slang?token=" + Token + "'";
                string r = Ssh.Run(_cfg, cmd, 40);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool ok = r.IndexOf("\"ok\": true") >= 0 || r.IndexOf("\"ok\":true") >= 0;
                    if (status != null)
                        status.Text = ok ? ("已保存 " + items.Count + " 条（她下条消息就认）")
                                         : ("保存失败：" + FirstLine(r));
                }));
            });
        }

        void ToggleStickers()
        {
            if (_stkPanel == null) return;
            if (_stkPanel.Visibility == Visibility.Visible) { HideStickers(); return; }
            ShowStickers();
        }

        void ShowStickers()
        {
            if (_stkPanel == null) return;
            HideMembers();
            // 2026-10-30 第二次修：面板高度必须跟着「聊天区那行」走。
            // 之前写死 470*S，DPI 放大（S>1）或窗口变矮时面板比行还高，
            // 因为它底部对齐，超出部分是从**上面**被裁掉的 —— 看起来就是搜索框被吃掉、图压到标题上。
            try
            {
                var g = _stkPanel.Parent as Grid;
                double rowH = (g != null && g.RowDefinitions.Count > 1)
                              ? g.RowDefinitions[1].ActualHeight : 0;
                double rowW = g != null ? g.ActualWidth : 0;
                if (rowH > 0)
                    _stkPanel.Height = Math.Max(230 * S, Math.Min(470 * S, rowH - 14 * S));
                if (rowW > 0)
                    _stkPanel.Width = Math.Max(300 * S, Math.Min(420 * S, rowW - 16 * S));
            }
            catch { }
            if (_curChat.Length == 0 && _chatStatus != null) _chatStatus.Text = "先选一个会话，再挑表情包";
            _stkPanel.Visibility = Visibility.Visible;
            if (_stks.Count == 0) LoadStickerList(false);
            else FillStickers();
        }

        void HideStickers()
        {
            if (_stkPanel != null) _stkPanel.Visibility = Visibility.Collapsed;
        }

        string StkCacheDir()
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "FishBotConsole", "stk", string.IsNullOrEmpty(_stkSig) ? "none" : _stkSig);
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }

        BitmapImage StkBmpFrom(byte[] raw)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(raw);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        BitmapImage StkGet(string id)
        {
            lock (_stkBmp)
            {
                BitmapImage b;
                return _stkBmp.TryGetValue(id, out b) ? b : null;
            }
        }

        void StkPut(string id, BitmapImage b)
        {
            if (b == null) return;
            lock (_stkBmp) { _stkBmp[id] = b; }
        }

        void LoadStickerList(bool force)
        {
            if (_stkLoading) return;
            _stkLoading = true;
            if (_stkHint != null) _stkHint.Text = "拉表情包列表…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/stickers?token=" + Token + (force ? "&fresh=1" : ""), 40);
                var items = new List<StkItem>();
                string sig = "";
                try
                {
                    var o = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                    if (o != null)
                    {
                        if (o.ContainsKey("sig")) sig = Convert.ToString(o["sig"]);
                        var arr = o.ContainsKey("items") ? o["items"] as object[] : null;
                        if (arr != null)
                        {
                            foreach (var x in arr)
                            {
                                var d = x as Dictionary<string, object>;
                                if (d == null) continue;
                                var it = new StkItem();
                                it.Id = d.ContainsKey("id") ? Convert.ToString(d["id"]) : "";
                                it.Mood = d.ContainsKey("mood") ? Convert.ToString(d["mood"]) : "";
                                it.Desc = d.ContainsKey("desc") ? Convert.ToString(d["desc"]) : "";
                                it.Scene = d.ContainsKey("scene") ? Convert.ToString(d["scene"]) : "";
                                it.Tags = d.ContainsKey("tags") ? Convert.ToString(d["tags"]) : "";
                                it.Kb = d.ContainsKey("kb") ? Convert.ToInt32(d["kb"]) : 0;
                                if (it.Id.Length > 0) items.Add(it);
                            }
                        }
                    }
                }
                catch { }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _stkLoading = false;
                    if (items.Count == 0)
                    {
                        if (_stkHint != null) _stkHint.Text = "表情包拉不到（检查 SSH / fishweb）";
                        return;
                    }
                    bool changed = sig != _stkSig;
                    if (changed)
                    {
                        _stkSig = sig;
                        lock (_stkBmp) { _stkBmp.Clear(); }
                        _stkReq.Clear();
                    }
                    _stks.Clear();
                    _stks.AddRange(items);
                    if (_stkHint != null) _stkHint.Text = "表情 · 点一下就发出去";
                    FillStickers();
                    if (changed) PreloadStkCache();
                }));
            });
        }

        /// <summary>本机缓存里已经有的缩略图先铺上（背景线程解码，解码完冻住再交给 UI）。</summary>
        void PreloadStkCache()
        {
            string dir = StkCacheDir();
            var list = new List<StkItem>(_stks);
            ThreadPool.QueueUserWorkItem(delegate
            {
                foreach (var it in list)
                {
                    if (StkGet(it.Id) != null) continue;
                    try
                    {
                        string p = Path.Combine(dir, it.Id + ".jpg");
                        if (!File.Exists(p)) continue;
                        StkPut(it.Id, StkBmpFrom(File.ReadAllBytes(p)));
                    }
                    catch { }
                }
                Dispatcher.BeginInvoke(new Action(delegate { FillStickers(); }));
            });
        }

        List<StkItem> StkFiltered()
        {
            string q = _stkSearch == null ? "" : (_stkSearch.Text ?? "").Trim();
            if (q.Length == 0) return _stks;
            var outList = new List<StkItem>();
            foreach (var it in _stks)
            {
                if (it.Mood.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    || it.Desc.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    || (it.Tags ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    || it.Id.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    outList.Add(it);
            }
            return outList;
        }

        /// <summary>改一张表情包的情绪/画面/场合/自定义标签（她按标签挑图，2026-10-30）</summary>
        void EditStickerTags(StkItem it)
        {
            if (it == null) return;
            var w = new Window();
            w.Title = "改标签 · " + it.Id;
            w.Width = 470 * S;
            w.Height = 470 * S;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            w.Owner = this;
            w.Background = _th.bCard;
            var sp = new StackPanel();
            sp.Margin = new Thickness(16 * S);

            Func<string, string, TextBox> mk = delegate(string label, string val)
            {
                sp.Children.Add(Txt(label, 11.5, false, _th.bSub, 0));
                var t = new TextBox();
                t.Text = val ?? "";
                t.FontSize = 13 * S;
                t.Height = 32 * S;
                t.Margin = new Thickness(0, 4 * S, 0, 10 * S);
                t.Background = _th.bBtn;
                t.Foreground = _th.bText;
                t.BorderBrush = _th.bLine;
                t.Padding = new Thickness(8 * S, 0, 8 * S, 0);
                sp.Children.Add(t);
                return t;
            };
            var tMood = mk("情绪（比如 生气 / 开心）", it.Mood);
            var tDesc = mk("画面（一句话说清图上发生了什么）", it.Desc);
            var tScene = mk("场合（什么情况下发，比如 被催更时）", it.Scene);
            var tTags = mk("标签（逗号分隔，她按这个挑图）比如：米饭,吃饭,投喂", it.Tags);

            var row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.HorizontalAlignment = HorizontalAlignment.Right;
            row.Margin = new Thickness(0, 6 * S, 0, 0);
            row.Children.Add(Btn("取消", false, delegate { w.Close(); }));
            row.Children.Add(Btn("保存", true, delegate
            {
                var p = new Dictionary<string, object>();
                p["id"] = it.Id;
                p["mood"] = tMood.Text.Trim();
                p["desc"] = tDesc.Text.Trim();
                p["scene"] = tScene.Text.Trim();
                p["tags"] = tTags.Text.Trim();
                StkTagPost(p);
                w.Close();
            }));
            sp.Children.Add(row);
            w.Content = sp;
            w.ShowDialog();
        }

        /// <summary>把标签存到服务器（同一套 curl-over-SSH，不走本地 8090）</summary>
        void StkTagPost(Dictionary<string, object> payload)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string body = new JavaScriptSerializer().Serialize(payload);
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                string cmd = "echo " + b64 + " | base64 -d > /tmp/stk.json; " +
                             "curl -s -m 20 -X POST -H 'Content-Type: application/json' " +
                             "--data-binary @/tmp/stk.json '" + ApiBase + "/api/stickers/tags?token=" + Token + "'";
                string r = Ssh.Run(_cfg, cmd, 40);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool ok = r.IndexOf("\"ok\": true") >= 0 || r.IndexOf("\"ok\":true") >= 0;
                    if (_stkHint != null)
                        _stkHint.Text = ok ? "标签已保存（她下次挑图就用新的了）" : ("保存失败：" + FirstLine(r));
                    if (ok) _stkSig = "";      // 下次刷新重拉
                }));
            });
        }

        void FillStickers()
        {
            if (_stkGrid == null) return;
            var list = StkFiltered();
            int pages = Math.Max(1, (list.Count + STK_PER_PAGE - 1) / STK_PER_PAGE);
            if (_stkPage < 0) _stkPage = 0;
            if (_stkPage > pages - 1) _stkPage = pages - 1;

            var missing = new List<string>();
            for (int i = 0; i < STK_PER_PAGE; i++)
            {
                int idx = _stkPage * STK_PER_PAGE + i;
                if (idx >= list.Count)
                {
                    _stkAt[i] = "";
                    _stkImg[i].Source = null;
                    _stkTile[i].Visibility = Visibility.Collapsed;
                    continue;
                }
                var it = list[idx];
                _stkAt[i] = it.Id;
                _stkTile[i].Visibility = Visibility.Visible;
                _stkTile[i].ToolTip = it.Mood + (it.Desc.Length > 0 ? "｜" + it.Desc : "")
                                      + ((it.Tags ?? "").Length > 0 ? "\n标签：" + it.Tags + "（右键改）" : "\n右键改标签");
                // 2026-10-30：右键改标签（她按标签判断什么时候发哪张）
                var _menu = new ContextMenu();
                var _mi = new MenuItem();
                _mi.Header = "改标签…";
                var _it = it;
                _mi.Click += delegate { EditStickerTags(_it); };
                _menu.Items.Add(_mi);
                _stkTile[i].ContextMenu = _menu;
                var bmp = StkGet(it.Id);
                _stkImg[i].Source = bmp;
                if (bmp == null) missing.Add(it.Id);
            }
            PaintStkDots(pages);
            if (_stkPageText != null)
                _stkPageText.Text = (_stkPage + 1) + "/" + pages + " 页 · " + list.Count + " 张";
            if (missing.Count > 0) LoadStkThumbs(missing);
        }

        void PaintStkDots(int pages)
        {
            if (_stkDots == null) return;
            _stkDots.Children.Clear();
            if (pages <= 1) return;
            if (pages > 24)     // 页太多就不画点了（右边还有「x/y 页」）
                return;
            for (int i = 0; i < pages; i++)
            {
                var dot = new Border();
                dot.Width = 7 * S;
                dot.Height = 7 * S;
                dot.CornerRadius = new CornerRadius(4);
                dot.Margin = new Thickness(3 * S, 0, 3 * S, 0);
                dot.Background = (i == _stkPage) ? _th.bAccent : _th.bLine;
                dot.Cursor = Cursors.Hand;
                int target = i;
                dot.MouseLeftButtonUp += delegate { StkPage(target); };
                _stkDots.Children.Add(dot);
            }
        }

        void StkPage(int p)
        {
            var list = StkFiltered();
            int pages = Math.Max(1, (list.Count + STK_PER_PAGE - 1) / STK_PER_PAGE);
            if (p < 0) p = 0;
            if (p > pages - 1) p = pages - 1;
            if (p == _stkPage) return;
            _stkPage = p;
            FillStickers();
        }

        void StkClick(int slot)
        {
            if (slot < 0 || slot >= STK_PER_PAGE) return;
            string id = _stkAt[slot];
            if (!string.IsNullOrEmpty(id)) SendSticker(id);
        }

        /// <summary>一批缩略图（20 张一次，别一页发 20 条 SSH）。</summary>
        void LoadStkThumbs(List<string> ids)
        {
            var job = new List<string>();
            foreach (var id in ids)
            {
                if (!_stkReq.Contains(id)) { _stkReq.Add(id); job.Add(id); }
            }
            if (job.Count == 0) return;
            string dir = StkCacheDir();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg,
                    "/api/stickers/thumbs?token=" + Token + "&ids=" + string.Join(",", job.ToArray()), 60);
                var got = new List<KeyValuePair<string, byte[]>>();
                try
                {
                    var o = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                    var arr = o != null && o.ContainsKey("items") ? o["items"] as object[] : null;
                    if (arr != null)
                    {
                        foreach (var x in arr)
                        {
                            var d = x as Dictionary<string, object>;
                            if (d == null || !d.ContainsKey("b64")) continue;
                            string id = d.ContainsKey("id") ? Convert.ToString(d["id"]) : "";
                            if (id.Length == 0) continue;
                            try { got.Add(new KeyValuePair<string, byte[]>(id, Convert.FromBase64String(Convert.ToString(d["b64"])))); }
                            catch { }
                        }
                    }
                }
                catch { }
                foreach (var kv in got)
                {
                    StkPut(kv.Key, StkBmpFrom(kv.Value));
                    try { File.WriteAllBytes(Path.Combine(dir, kv.Key + ".jpg"), kv.Value); } catch { }
                }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    foreach (var id in job) _stkReq.Remove(id);
                    for (int i = 0; i < STK_PER_PAGE; i++)
                    {
                        string id = _stkAt[i];
                        if (string.IsNullOrEmpty(id)) continue;
                        var bmp = StkGet(id);
                        if (bmp != null && !object.ReferenceEquals(_stkImg[i].Source, bmp)) _stkImg[i].Source = bmp;
                    }
                }));
            });
        }

        void SendSticker(string id)
        {
            if (_curChat.Length == 0)
            {
                if (_chatStatus != null) _chatStatus.Text = "先选一个会话";
                return;
            }
            string chat = _curChat;
            var ats = new List<AtTarget>(_atTargets);
            var quote = _quoteMsg;
            if (_stkHint != null) _stkHint.Text = "发表情包中…";
            if (_chatStatus != null) _chatStatus.Text = "发表情包中…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                var payload = new Dictionary<string, object>();
                payload["chat"] = chat;
                payload["ids"] = new object[] { id };
                if (ats.Count > 0)
                {
                    var arr = new List<object>();
                    foreach (var a in ats)
                        arr.Add(new Dictionary<string, object> { { "uid", a.Uid }, { "name", a.Name } });
                    payload["at"] = arr;
                }
                if (quote != null)
                {
                    payload["quote"] = new Dictionary<string, object>
                    {
                        { "who", quote.Who ?? "" },
                        { "text", quote.Text ?? "" },
                        { "ts", quote.Time ?? "" },
                        { "dt", quote.Dt ?? "" }
                    };
                }
                string body = new JavaScriptSerializer().Serialize(payload);
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                string cmd = "echo " + b64 + " | base64 -d > /tmp/fystk.json; " +
                             "curl -s -m 40 -X POST -H 'Content-Type: application/json' --data-binary @/tmp/fystk.json " +
                             "'" + ApiBase + "/api/stickers/send?token=" + Token + "'";
                string r = Ssh.Run(_cfg, cmd, 60);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool ok = r.IndexOf("\"ok\": true") >= 0 || r.IndexOf("\"ok\":true") >= 0;
                    if (ok)
                    {
                        _atTargets.Clear();
                        _quoteMsg = null;
                        RebuildChips();
                        if (_stkHint != null) _stkHint.Text = "已发出 " + id;
                        if (_chatStatus != null) _chatStatus.Text = "已发表情包 → " + chat;
                    }
                    else
                    {
                        string msg = "发送失败：" + FirstLine(r);
                        if (_stkHint != null) _stkHint.Text = msg;
                        if (_chatStatus != null) _chatStatus.Text = msg;
                    }
                    _msgSig = "";
                    LoadMessages(false);
                }));
            });
        }


        // ------------------------------------------------ 角色人格（多套人设，灰度中）
        // 一个「人格」= personas/<id>/persona_prompt.txt（人设正文）+ wordbank.json（它自己的文案）。
        // 灰度：只有 群 1000000006 / 私聊 1000000002 会换成选中的那个，其余群一直是默认嘴。
        StackPanel _psList;
        TextBlock _psStatus, _psMeta, _psHint;
        TextBox _psPrompt, _psIdIn, _psNameIn;
        string _psSel = "";

        void BuildPersonaCard(StackPanel b)
        {
            b.Children.Add(Txt("一套人设 = 一份「人设正文」+ 它自己的一套文案（指令回复、好感度、投喂、"
                               + "戳一戳、接梗…）。\n"
                               + "**灰度中**：只有 群 1000000006 和超管(1000000002)的私聊会用选中的这一个人格，"
                               + "其他群完全不受影响，还是原来那只肥鱼。", 11, false, _th.bSub, 2));
            _psHint = Txt("读取中…", 12.5, true, _th.bText, 6);
            b.Children.Add(_psHint);

            _psList = new StackPanel();
            _psList.Margin = new Thickness(0, 4 * S, 0, 0);
            b.Children.Add(_psList);

            var rowNew = new WrapPanel();
            rowNew.Margin = new Thickness(0, 10 * S, 0, 0);
            _psIdIn = ApInput("新人格 id（字母/数字/下划线）", 190);
            _psNameIn = ApInput("显示名", 150);
            rowNew.Children.Add(_psIdIn);
            rowNew.Children.Add(_psNameIn);
            rowNew.Children.Add(Btn("新建（空模板）", false, delegate { PersonaNew(); }));
            b.Children.Add(rowNew);

            b.Children.Add(Txt("人设正文（写在这个人格自己的 persona_prompt.txt 里）", 12, false, _th.bText, 12));
            _psMeta = Txt("", 11.5, true, _th.bAccent, 2);
            b.Children.Add(_psMeta);
            _psPrompt = new TextBox();
            _psPrompt.FontSize = 12 * S;
            _psPrompt.Height = 190 * S;
            _psPrompt.AcceptsReturn = true;
            _psPrompt.TextWrapping = TextWrapping.Wrap;
            _psPrompt.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _psPrompt.Background = _th.bBtn;
            _psPrompt.Foreground = _th.bText;
            _psPrompt.BorderBrush = _th.bLine;
            _psPrompt.Padding = new Thickness(8 * S, 6 * S, 8 * S, 6 * S);
            StyleInput(_psPrompt);
            _psPrompt.Margin = new Thickness(0, 4 * S, 0, 6 * S);
            Watermark(_psPrompt, "（这个角色还没写正文）");
            b.Children.Add(_psPrompt);

            var rowP = new WrapPanel();
            rowP.Children.Add(Btn("保存正文", true, delegate { PersonaSavePrompt(); }));
            rowP.Children.Add(Btn("读取选中的", false, delegate { if (_psSel.Length > 0) PersonaLoadPrompt(_psSel); }));
            b.Children.Add(rowP);

            _psStatus = Txt("", 11.5, false, _th.bSub, 4);
            b.Children.Add(_psStatus);
            LoadPersonas();
        }

        void LoadPersonas()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/personas?token=" + Token, 40);
                Dispatcher.BeginInvoke(new Action(delegate { FillPersonas(json); }));
            });
        }

        void FillPersonas(string json)
        {
            if (_psList == null) return;
            _psList.Children.Clear();
            try
            {
                var o = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                var arr = o != null && o.ContainsKey("items") ? o["items"] as object[] : null;
                if (arr == null) { if (_psHint != null) _psHint.Text = "拉不到人格列表（" + FirstLine(json) + "）"; return; }
                string active = o.ContainsKey("active") ? Convert.ToString(o["active"]) : "";
                if (_psHint != null) _psHint.Text = "人格 " + arr.Length + " 个 · 选中：" + active;
                foreach (var x in arr)
                {
                    var d = x as Dictionary<string, object>;
                    if (d == null) continue;
                    string id = d.ContainsKey("id") ? Convert.ToString(d["id"]) : "";
                    string name = d.ContainsKey("name") ? Convert.ToString(d["name"]) : id;
                    int have = d.ContainsKey("have") ? Convert.ToInt32(d["have"]) : 0;
                    int total = d.ContainsKey("total") ? Convert.ToInt32(d["total"]) : 0;
                    int plen = d.ContainsKey("prompt_len") ? Convert.ToInt32(d["prompt_len"]) : 0;
                    bool isActive = id == active;

                    var row = new Border();
                    row.Background = isActive ? _th.bAccentSoft : Brushes.Transparent;
                    row.BorderBrush = _th.bLine;
                    row.BorderThickness = new Thickness(0, 0, 0, 1);
                    row.Padding = new Thickness(6 * S, 4 * S, 6 * S, 4 * S);
                    var sp = new DockPanel();

                    var btns = new WrapPanel();
                    string idc = id;
                    var bUse = Btn(isActive ? "正在用 ✓" : "切到这个", false, delegate { PersonaUse(idc); });
                    bUse.Padding = new Thickness(9 * S, 3 * S, 9 * S, 3 * S);
                    bUse.FontSize = 11 * S;
                    btns.Children.Add(bUse);
                    var bShow = Btn("读正文", false, delegate { PersonaLoadPrompt(idc); });
                    bShow.Padding = new Thickness(9 * S, 3 * S, 9 * S, 3 * S);
                    bShow.FontSize = 11 * S;
                    btns.Children.Add(bShow);
                    if (id != "feiyu" && !isActive)
                    {
                        var bDel = Btn("删", false, delegate { PersonaDel(idc); });
                        bDel.Padding = new Thickness(9 * S, 3 * S, 9 * S, 3 * S);
                        bDel.FontSize = 11 * S;
                        btns.Children.Add(bDel);
                    }
                    DockPanel.SetDock(btns, Dock.Right);
                    sp.Children.Add(btns);

                    string info = "● " + name + "  (" + id + ")"
                                  + "    文案 " + have + "/" + total
                                  + "  ·  人设 " + plen + " 字";
                    if (have < total) info += "  ·  ⚠ 缺 " + (total - have) + " 项（先用中立兜底）";
                    var tb = Txt(info, 12, isActive, _th.bText, 0);
                    tb.VerticalAlignment = VerticalAlignment.Center;
                    tb.Margin = new Thickness(0, 0, 8 * S, 0);
                    sp.Children.Add(tb);
                    row.Child = sp;
                    _psList.Children.Add(row);
                }
            }
            catch (Exception ex)
            {
                if (_psHint != null) _psHint.Text = "解析失败：" + ex.Message;
            }
        }

        void PersonaPost(string path, Dictionary<string, object> payload, string okText)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string body = new JavaScriptSerializer().Serialize(payload);
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                string cmd = "echo " + b64 + " | base64 -d > /tmp/ps.json; " +
                             "curl -s -m 25 -X POST -H 'Content-Type: application/json' " +
                             "--data-binary @/tmp/ps.json '" + ApiBase + path + "?token=" + Token + "'";
                string r = Ssh.Run(_cfg, cmd, 45);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool ok = r.IndexOf("\"ok\": true") >= 0 || r.IndexOf("\"ok\":true") >= 0;
                    if (_psStatus != null)
                        _psStatus.Text = ok ? okText : ("失败：" + FirstLine(r));
                    LoadPersonas();
                }));
            });
        }

        void PersonaUse(string id)
        {
            if (_psStatus != null) _psStatus.Text = "切换中…";
            var p = new Dictionary<string, object>();
            p["id"] = id;
            PersonaPost("/api/personas/use", p, "已切到 " + id + "（灰度会话立刻生效）");
        }

        void PersonaDel(string id)
        {
            var p = new Dictionary<string, object>();
            p["id"] = id;
            PersonaPost("/api/personas/del", p, "已删除 " + id);
        }

        void PersonaNew()
        {
            string id = _psIdIn == null ? "" : _psIdIn.Text.Trim();
            string nm = _psNameIn == null ? "" : _psNameIn.Text.Trim();
            if (id.Length == 0) { if (_psStatus != null) _psStatus.Text = "先填 id"; return; }
            var p = new Dictionary<string, object>();
            p["id"] = id;
            p["name"] = nm.Length > 0 ? nm : id;
            if (_psStatus != null) _psStatus.Text = "新建中…";
            PersonaPost("/api/personas/new", p, "已新建 " + id + "（人设正文待填）");
            if (_psIdIn != null) _psIdIn.Text = "";
            if (_psNameIn != null) _psNameIn.Text = "";
        }

        void PersonaLoadPrompt(string id)
        {
            _psSel = id;
            if (_psMeta != null) _psMeta.Text = "读取 " + id + " 的人设正文…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/personas/prompt?token=" + Token
                                            + "&id=" + Uri.EscapeDataString(id), 40);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        var o = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                        string text = o != null && o.ContainsKey("text") ? Convert.ToString(o["text"]) : "";
                        if (_psPrompt != null) _psPrompt.Text = text;
                        if (_psMeta != null)
                            _psMeta.Text = "编辑中：" + id + "（" + text.Length + " 字）";
                        if (_psStatus != null) _psStatus.Text = "已读取 " + id;
                    }
                    catch (Exception ex)
                    {
                        if (_psMeta != null) _psMeta.Text = "读取失败：" + ex.Message;
                    }
                }));
            });
        }

        void PersonaSavePrompt()
        {
            if (_psSel.Length == 0) { if (_psStatus != null) _psStatus.Text = "先在列表里点「读正文」选一个人格"; return; }
            var p = new Dictionary<string, object>();
            p["id"] = _psSel;
            p["text"] = _psPrompt == null ? "" : _psPrompt.Text;
            if (_psStatus != null) _psStatus.Text = "保存中…";
            PersonaPost("/api/personas/prompt", p, "已保存 " + _psSel + " 的人设正文");
        }

        // ------------------------------------------------ 底层人设（注入在所有人设之前）
        // 2026-11-08 用户要求：一段公共设定，注入在其他人设**之前**；可以只给指定人格注入；
        // 存服务器 `data/base_prompt.json`，插件按 mtime **热读** → 保存后下一条消息就生效。
        TextBox _bpText;
        TextBlock _bpStatus, _bpMeta;
        CheckBox _bpOn;
        StackPanel _bpTargets;
        readonly Dictionary<string, CheckBox> _bpBoxes = new Dictionary<string, CheckBox>();

        void BuildBasePrompt()
        {
            StackPanel body;
            var card = Card("底层人设（注入在所有人设之前）", out body);
            _page.Children.Add(card);

            body.Children.Add(Txt("这段文字会拼在每个人设正文的最前面，像一层公共底座："
                                  + "人设自己写不到的东西（说话习惯、禁忌、当前活动、临时状态…）放这里，"
                                  + "改完保存下一条消息就生效，不用重启机器人。\n"
                                  + "下面的人格一个都不勾 = 所有人格都注入。", 11, false, _th.bSub, 2));

            _bpMeta = Txt("读取中…", 12.5, true, _th.bText, 6);
            body.Children.Add(_bpMeta);

            _bpOn = new CheckBox();
            _bpOn.Content = "启用这段底层设定";
            _bpOn.Foreground = _th.bText;
            _bpOn.FontSize = 12.5 * S;
            _bpOn.Margin = new Thickness(0, 8 * S, 0, 4 * S);
            body.Children.Add(_bpOn);

            body.Children.Add(Txt("注入给谁（一个都不勾 = 所有人格）", 11.5, true, _th.bSub, 8));
            _bpTargets = new StackPanel();
            _bpTargets.Margin = new Thickness(0, 2 * S, 0, 6 * S);
            body.Children.Add(_bpTargets);

            _bpText = new TextBox();
            _bpText.FontSize = 12 * S;
            _bpText.Height = 320 * S;
            _bpText.AcceptsReturn = true;
            _bpText.TextWrapping = TextWrapping.Wrap;
            _bpText.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _bpText.Background = _th.bBtn;
            _bpText.Foreground = _th.bText;
            _bpText.BorderBrush = _th.bLine;
            _bpText.Padding = new Thickness(8 * S, 6 * S, 8 * S, 6 * S);
            StyleInput(_bpText);
            _bpText.Margin = new Thickness(0, 4 * S, 0, 6 * S);
            Watermark(_bpText, "（这里写公共设定：例如语气底线、当前活动、临时的称呼规则…）");
            body.Children.Add(_bpText);

            var row = new WrapPanel();
            row.Margin = new Thickness(0, 8 * S, 0, 0);
            row.Children.Add(Btn("保存", true, delegate { BasePromptSave(); }));
            row.Children.Add(Btn("刷新（从服务器重读）", false, delegate { BasePromptLoad(true); }));
            row.Children.Add(Btn("全选人格", false, delegate { BasePromptAll(true); }));
            row.Children.Add(Btn("全不选（= 所有人格）", false, delegate { BasePromptAll(false); }));
            row.Children.Add(Btn("测试：现在谁在用", false, delegate { BasePromptPreview(); }));
            body.Children.Add(row);

            _bpStatus = Txt("", 11.5, false, _th.bSub, 4);
            body.Children.Add(_bpStatus);

            BasePromptLoad(false);
        }

        void BasePromptLoad(bool toast)
        {
            if (_bpStatus != null) _bpStatus.Text = toast ? "重新读取中…" : "读取中…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string personsJson = Ssh.Curl(_cfg, "/api/personas?token=" + Token, 40);
                string cfgJson = Ssh.Curl(_cfg, "/api/base_prompt?token=" + Token, 40);
                Dispatcher.BeginInvoke(new Action(delegate { BasePromptFill(personsJson, cfgJson); }));
            });
        }

        void BasePromptFill(string personsJson, string cfgJson)
        {
            // 人格列表
            var ids = new List<string>();
            try
            {
                var po = new JavaScriptSerializer().DeserializeObject(personsJson) as Dictionary<string, object>;
                var arr = po != null && po.ContainsKey("items") ? po["items"] as object[] : null;
                if (arr != null)
                    foreach (var x in arr)
                    {
                        var d = x as Dictionary<string, object>;
                        if (d == null) continue;
                        string id = d.ContainsKey("id") ? Convert.ToString(d["id"]) : "";
                        string nm = d.ContainsKey("name") ? Convert.ToString(d["name"]) : id;
                        if (id.Length > 0) ids.Add(id + "|" + nm);
                    }
            }
            catch { }

            var targets = new List<string>();
            try
            {
                var co = new JavaScriptSerializer().DeserializeObject(cfgJson) as Dictionary<string, object>;
                if (co != null)
                {
                    if (co.ContainsKey("enabled") && _bpOn != null) _bpOn.IsChecked = Convert.ToBoolean(co["enabled"]);
                    if (co.ContainsKey("text") && _bpText != null) _bpText.Text = Convert.ToString(co["text"]);
                    var ta = co.ContainsKey("targets") ? co["targets"] as object[] : null;
                    if (ta != null) foreach (var x in ta) targets.Add(Convert.ToString(x));
                    if (_bpMeta != null)
                        _bpMeta.Text = "服务器："
                            + (co.ContainsKey("len") ? Convert.ToString(co["len"]) : "0") + " 字"
                            + (co.ContainsKey("at") && Convert.ToString(co["at"]).Length > 0
                               ? " · 上次保存 " + Convert.ToString(co["at"]) : "");
                }
                else if (_bpMeta != null) _bpMeta.Text = "读不到配置（" + FirstLine(cfgJson) + "）";
            }
            catch (Exception ex)
            {
                if (_bpMeta != null) _bpMeta.Text = "解析失败：" + ex.Message;
            }

            // 人格勾选框
            if (_bpTargets != null)
            {
                _bpTargets.Children.Clear();
                _bpBoxes.Clear();
                foreach (var pair in ids)
                {
                    string[] kv = pair.Split('|');
                    string id = kv[0];
                    string nm = kv.Length > 1 ? kv[1] : id;
                    var cb = new CheckBox();
                    cb.Content = nm + "（" + id + "）";
                    cb.Foreground = _th.bText;
                    cb.FontSize = 12 * S;
                    cb.Margin = new Thickness(0, 2 * S, 18 * S, 2 * S);
                    cb.IsChecked = targets.Contains(id);
                    _bpBoxes[id] = cb;
                    _bpTargets.Children.Add(cb);
                }
                if (_bpBoxes.Count == 0 && _bpStatus != null) _bpStatus.Text = "拉不到人格列表（" + FirstLine(personsJson) + "）";
            }
            if (_bpStatus != null && _bpStatus.Text.EndsWith("中…")) _bpStatus.Text = "已读取。";
        }

        void BasePromptAll(bool on)
        {
            foreach (var kv in _bpBoxes) kv.Value.IsChecked = on;
        }

        void BasePromptSave()
        {
            var targets = new List<string>();
            foreach (var kv in _bpBoxes)
                if (kv.Value.IsChecked == true) targets.Add(kv.Key);
            var p = new Dictionary<string, object>();
            p["enabled"] = _bpOn != null && _bpOn.IsChecked == true;
            p["targets"] = targets;
            p["text"] = _bpText == null ? "" : _bpText.Text;
            if (_bpStatus != null) _bpStatus.Text = "保存中…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string r = PostJson("/api/base_prompt/save?token=" + Token,
                                    new JavaScriptSerializer().Serialize(p));
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    string msg = "已保存";
                    try
                    {
                        var o = new JavaScriptSerializer().DeserializeObject(r) as Dictionary<string, object>;
                        if (o != null && o.ContainsKey("msg")) msg = Convert.ToString(o["msg"]);
                    }
                    catch { msg = FirstLine(r); }
                    if (_bpStatus != null) _bpStatus.Text = msg + "（下一条消息生效）";
                    if (_bpMeta != null)
                        _bpMeta.Text = "本地：" + (p["text"] == null ? 0 : Convert.ToString(p["text"]).Length)
                            + " 字 · " + (targets.Count == 0 ? "所有人格" : "只给 " + string.Join("、", targets.ToArray()));
                }));
            });
        }

        void BasePromptPreview()
        {
            // 挑第一个勾上的人格；没勾就问当前活跃人格
            string who = "";
            foreach (var kv in _bpBoxes) if (kv.Value.IsChecked == true) { who = kv.Key; break; }
            if (_bpStatus != null) _bpStatus.Text = "测试中…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string r = Ssh.Curl(_cfg, "/api/base_prompt/preview?token=" + Token
                                         + "&persona=" + Uri.EscapeDataString(who), 40);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        var o = new JavaScriptSerializer().DeserializeObject(r) as Dictionary<string, object>;
                        string pid = o != null && o.ContainsKey("persona") ? Convert.ToString(o["persona"]) : who;
                        bool inject = o != null && o.ContainsKey("inject") && Convert.ToBoolean(o["inject"]);
                        string why = o != null && o.ContainsKey("why") ? Convert.ToString(o["why"]) : FirstLine(r);
                        string chars = o != null && o.ContainsKey("chars") ? Convert.ToString(o["chars"]) : "0";
                        if (_bpStatus != null)
                            _bpStatus.Text = (inject ? "✅ " : "❌ ") + "人格 " + pid + "："
                                + (inject ? "会被注入 " + chars + " 字（" + why + "）" : "不会注入 —— " + why);
                    }
                    catch { if (_bpStatus != null) _bpStatus.Text = FirstLine(r); }
                }));
            });
        }

        // ------------------------------------------------ 花名册
        // ------------------------------------------------ 她的 QQ（资料 + 说说）
        // 为什么要单独一页：昵称/签名/头像/说说都是"对外可见"的东西，
        // 放在聊天里让 AI 随手改太危险，改成控制台里手动操作。
        void BuildQQProfile()
        {
            // ---- 资料卡 ----
            StackPanel pb;
            var card = Card("她的 QQ 主页", out pb);
            _page.Children.Add(card);

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112 * S) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var avaWrap = new Border();
            avaWrap.Width = 96 * S;
            avaWrap.Height = 96 * S;
            avaWrap.CornerRadius = new CornerRadius(48 * S);
            avaWrap.Background = _th.bBtn;
            avaWrap.BorderBrush = _th.bLine;
            avaWrap.BorderThickness = new Thickness(1);
            avaWrap.HorizontalAlignment = HorizontalAlignment.Left;
            avaWrap.VerticalAlignment = VerticalAlignment.Top;
            avaWrap.Clip = null;
            _qqAvatar = new System.Windows.Controls.Image();
            _qqAvatar.Width = 96 * S;
            _qqAvatar.Height = 96 * S;
            _qqAvatar.Stretch = Stretch.UniformToFill;
            var round = new Border();
            round.CornerRadius = new CornerRadius(48 * S);
            round.ClipToBounds = true;
            round.Child = _qqAvatar;
            avaWrap.Child = round;
            Grid.SetColumn(avaWrap, 0);
            head.Children.Add(avaWrap);

            var info = new StackPanel();
            info.Margin = new Thickness(4 * S, 2 * S, 0, 0);
            _qqName = Txt("读取中…", 21, true, _th.bText, 0);
            info.Children.Add(_qqName);
            _qqNum = Txt("", 11.5, false, _th.bSub, 3);
            info.Children.Add(_qqNum);
            _qqSign = Txt("", 12.5, false, _th.bText, 8);
            _qqSign.TextWrapping = TextWrapping.Wrap;
            info.Children.Add(_qqSign);
            _qqStatus = Txt("", 11, false, _th.bSub, 6);
            info.Children.Add(_qqStatus);
            Grid.SetColumn(info, 1);
            head.Children.Add(info);
            pb.Children.Add(head);

            var btns = Row();
            btns.Margin = new Thickness(0, 14 * S, 0, 0);
            btns.Children.Add(Btn("换头像", true, delegate { QQPickAvatar(); }));
            btns.Children.Add(Btn("刷新", false, delegate { LoadQQProfile(); }));
            pb.Children.Add(btns);

            // ---- 改昵称 / 签名 ----
            StackPanel eb;
            var editCard = Card("改昵称 / 签名", out eb);
            _page.Children.Add(editCard);

            eb.Children.Add(Txt("改完立刻生效，不用重启。签名最多 100 字、昵称最多 24 字。", 11, false, _th.bSub, 0));
            eb.Children.Add(Txt("注意：这个协议端改签名会同时往说说里发一条（已经在尾部做了说明）。",
                                10.5, false, _th.bSub, 3));

            var r1 = Row();
            _qqNickIn = ApInput("新昵称，留空=不改", 240);
            r1.Children.Add(_qqNickIn);
            eb.Children.Add(r1);

            var r2 = Row();
            _qqSignIn = new TextBox();
            _qqSignIn.Width = 520 * S;
            _qqSignIn.Height = 62 * S;
            _qqSignIn.FontSize = 12.5 * S;
            _qqSignIn.Margin = new Thickness(0, 0, 8 * S, 8 * S);
            _qqSignIn.Background = _th.bBtn;
            _qqSignIn.Foreground = _th.bText;
            _qqSignIn.BorderBrush = _th.bLine;
            _qqSignIn.AcceptsReturn = true;
            _qqSignIn.TextWrapping = TextWrapping.Wrap;
            _qqSignIn.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            StyleInput(_qqSignIn);
            Watermark(_qqSignIn, "新签名，留空=不改");
            r2.Children.Add(_qqSignIn);
            eb.Children.Add(r2);

            var r3 = Row();
            r3.Children.Add(Btn("保存", true, delegate { QQSaveProfile(); }));
            eb.Children.Add(r3);

            // ---- 说说 ----
            StackPanel qb;
            var qCard = Card("说说", out qb);
            _page.Children.Add(qCard);

            qb.Children.Add(Txt("发一条说说（会显示在她的 QQ 空间/个性签名）。", 11, false, _th.bSub, 0));
            var r4 = Row();
            _qqPostIn = new TextBox();
            _qqPostIn.Width = 520 * S;
            _qqPostIn.Height = 76 * S;
            _qqPostIn.FontSize = 12.5 * S;
            _qqPostIn.Margin = new Thickness(0, 0, 8 * S, 8 * S);
            _qqPostIn.Background = _th.bBtn;
            _qqPostIn.Foreground = _th.bText;
            _qqPostIn.BorderBrush = _th.bLine;
            _qqPostIn.AcceptsReturn = true;
            _qqPostIn.TextWrapping = TextWrapping.Wrap;
            _qqPostIn.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            StyleInput(_qqPostIn);
            Watermark(_qqPostIn, "想说什么…（最多 1500 字）");
            r4.Children.Add(_qqPostIn);
            qb.Children.Add(r4);

            _qqImgStrip = new WrapPanel();
            _qqImgStrip.Margin = new Thickness(0, 6 * S, 0, 0);
            qb.Children.Add(_qqImgStrip);

            var r5 = Row();
            r5.Children.Add(Btn("选图（最多 9 张）", false, delegate { QQPickPostImages(); }));
            r5.Children.Add(Btn("发布说说", true, delegate { QQPost(); }));
            r5.Children.Add(Btn("刷新列表", false, delegate { LoadQZone(); }));
            qb.Children.Add(r5);

            _qqList = new StackPanel();
            _qqList.Margin = new Thickness(0, 8 * S, 0, 0);
            qb.Children.Add(_qqList);

            LoadQQProfile();
            LoadQZone();
        }

        void LoadQQProfile()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/profile?token=" + Token, 30);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        var o = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                        if (o == null) { if (_qqStatus != null) _qqStatus.Text = "读取失败：" + FirstLine(json); return; }
                        string nick = o.ContainsKey("nickname") ? Convert.ToString(o["nickname"]) : "";
                        string sign = o.ContainsKey("signature") ? Convert.ToString(o["signature"]) : "";
                        string uin = o.ContainsKey("uin") ? Convert.ToString(o["uin"]) : "";
                        int lv = o.ContainsKey("level") ? Convert.ToInt32(o["level"] ?? 0) : 0;
                        if (_qqName != null) _qqName.Text = string.IsNullOrEmpty(nick) ? "(没读到昵称)" : nick;
                        if (_qqNum != null) _qqNum.Text = "QQ " + uin + (lv > 0 ? ("　Lv." + lv) : "");
                        if (_qqSign != null)
                            _qqSign.Text = string.IsNullOrEmpty(sign) ? "（还没写签名）" : ("「" + sign + "」");
                        if (_qqStatus != null)
                            _qqStatus.Text = o.ContainsKey("warn") ? Convert.ToString(o["warn"]) : "同步正常";
                        if (o.ContainsKey("avatar_b64"))
                        {
                            string b64 = Convert.ToString(o["avatar_b64"]);
                            if (!string.IsNullOrEmpty(b64) && b64 != _qqAvatarB64)
                            {
                                _qqAvatarB64 = b64;
                                QQSetAvatar(Convert.FromBase64String(b64));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_qqStatus != null) _qqStatus.Text = "解析失败：" + FirstLine(ex.Message);
                    }
                }));
            });
        }

        void QQSetAvatar(byte[] raw)
        {
            try
            {
                var bm = new BitmapImage();
                bm.BeginInit();
                bm.StreamSource = new MemoryStream(raw);
                bm.CacheOption = BitmapCacheOption.OnLoad;
                bm.EndInit();
                bm.Freeze();
                if (_qqAvatar != null)
                {
                    _qqAvatar.Source = bm;
                    // Border 的 CornerRadius 不会裁子元素，必须自己给一个椭圆 clip
                    double r = 48 * S;
                    _qqAvatar.Clip = new EllipseGeometry(new Point(r, r), r, r);
                }
            }
            catch { }
        }

        void QQSaveProfile()
        {
            string nick = _qqNickIn == null ? "" : _qqNickIn.Text.Trim();
            string sign = _qqSignIn == null ? "" : _qqSignIn.Text.Trim();
            if (nick.Length == 0 && sign.Length == 0)
            {
                if (_qqStatus != null) _qqStatus.Text = "昵称和签名都空着，没东西可改";
                return;
            }
            var payload = new Dictionary<string, object>();
            if (nick.Length > 0) payload["nickname"] = nick;
            if (sign.Length > 0) payload["signature"] = sign;
            if (_qqStatus != null) _qqStatus.Text = "保存中…";
            QQPost("/api/profile/save", payload, "改好了");
        }

        void QQPost(string path, Dictionary<string, object> payload, string okText)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string body = new JavaScriptSerializer().Serialize(payload);
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                string cmd = "echo " + b64 + " | base64 -d > /tmp/qqp.json; " +
                             "curl -s -m 30 -X POST -H 'Content-Type: application/json' " +
                             "--data-binary @/tmp/qqp.json '" + ApiBase + path + "?token=" + Token + "'";
                string r = Ssh.Run(_cfg, cmd, 45);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool ok = r.IndexOf("\"ok\": true") >= 0 || r.IndexOf("\"ok\":true") >= 0;
                    if (_qqStatus != null)
                        _qqStatus.Text = ok ? okText : ("失败：" + FirstLine(r));
                    if (ok)
                    {
                        if (_qqNickIn != null) _qqNickIn.Text = "";
                        if (_qqSignIn != null) _qqSignIn.Text = "";
                        _qqAvatarB64 = "";                  // 头像可能变了，下次重新取
                    }
                    LoadQQProfile();
                    LoadQZone();
                }));
            });
        }

        void QQPostSay()
        {
            if (_qqPostIn == null) return;
            string c = _qqPostIn.Text.Trim();
            if (c.Length == 0 && _qqPostImgs.Count == 0)
            {
                if (_qqStatus != null) _qqStatus.Text = "内容和图片都空着";
                return;
            }
            var payload = new Dictionary<string, object>();
            payload["content"] = c;
            if (_qqPostImgs.Count > 0)
            {
                var list = new List<string>();
                foreach (var b in _qqPostImgs) list.Add(Convert.ToBase64String(b));
                payload["images"] = list.ToArray();
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                string body = new JavaScriptSerializer().Serialize(payload);
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                string cmd = "echo " + b64 + " | base64 -d > /tmp/qqs.json; " +
                             "curl -s -m 40 -X POST -H 'Content-Type: application/json' " +
                             "--data-binary @/tmp/qqs.json '" + ApiBase + "/api/qzone/post?token=" + Token + "'";
                string r = Ssh.Run(_cfg, cmd, 55);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool ok = r.IndexOf("\"ok\": true") >= 0 || r.IndexOf("\"ok\":true") >= 0;
                    if (ok)
                    {
                        if (_qqPostIn != null) _qqPostIn.Text = "";
                        _qqPostImgs.Clear();
                        QQRepaintImgStrip();
                        LoadQZone();
                        LoadQQProfile();
                    }
                    else if (_qqStatus != null) _qqStatus.Text = "发说说失败：" + FirstLine(r);
                }));
            });
        }

        void QQPost() { QQPostSay(); }

        void QQDelSay(string tid)
        {
            var payload = new Dictionary<string, object>();
            payload["tid"] = tid;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string body = new JavaScriptSerializer().Serialize(payload);
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
                string cmd = "echo " + b64 + " | base64 -d > /tmp/qqd.json; " +
                             "curl -s -m 30 -X POST -H 'Content-Type: application/json' " +
                             "--data-binary @/tmp/qqd.json '" + ApiBase + "/api/qzone/del?token=" + Token + "'";
                string r = Ssh.Run(_cfg, cmd, 45);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    // 以前这里静默刷新，失败了也看不出来。现在把服务端的原话显示出来。
                    bool ok = r.IndexOf("\"ok\": true") >= 0 || r.IndexOf("\"ok\":true") >= 0;
                    if (_qqStatus != null)
                    {
                        string msg = "";
                        try
                        {
                            var o = new JavaScriptSerializer().DeserializeObject(r) as Dictionary<string, object>;
                            if (o != null && o.ContainsKey("msg")) msg = Convert.ToString(o["msg"]);
                        }
                        catch { }
                        if (string.IsNullOrEmpty(msg)) msg = FirstLine(r);
                        _qqStatus.Text = (ok ? "" : "删除失败：") + msg;
                    }
                    LoadQZone();
                }));
            });
        }

        void LoadQZone()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/qzone?token=" + Token, 40);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_qqList == null) return;
                    _qqList.Children.Clear();
                    Dictionary<string, object> o = null;
                    try { o = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>; }
                    catch { }
                    if (o == null)
                    {
                        _qqList.Children.Add(Txt("读取说说失败：" + FirstLine(json), 11.5, false, _th.bSub, 4));
                        return;
                    }
                    if (o.ContainsKey("msg") && !Convert.ToBoolean(o.ContainsKey("ok") ? o["ok"] : false))
                    {
                        _qqList.Children.Add(Txt(Convert.ToString(o["msg"]), 11.5, false, _th.bSub, 4));
                        return;
                    }
                    var items = o.ContainsKey("items") ? o["items"] as object[] : null;
                    if (items == null || items.Length == 0)
                    {
                        _qqList.Children.Add(Txt("还没有说说。", 11.5, false, _th.bSub, 4));
                        return;
                    }
                    foreach (var it in items)
                    {
                        var d = it as Dictionary<string, object>;
                        if (d == null) continue;
                        string tid = d.ContainsKey("tid") ? Convert.ToString(d["tid"]) : "";
                        string content = d.ContainsKey("content") ? Convert.ToString(d["content"]) : "";
                        long ts = d.ContainsKey("time") ? Convert.ToInt64(d["time"] ?? 0) : 0;
                        int cm = d.ContainsKey("comments") ? Convert.ToInt32(d["comments"] ?? 0) : 0;
                        string when = "";
                        try { when = DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().ToString("MM-dd HH:mm"); }
                        catch { }

                        var rowBd = new Border();
                        rowBd.Background = _th.bBtn;
                        rowBd.CornerRadius = new CornerRadius(10);
                        rowBd.Padding = new Thickness(12 * S, 10 * S, 12 * S, 10 * S);
                        rowBd.Margin = new Thickness(0, 0, 0, 8 * S);
                        var rowSp = new StackPanel();

                        var topRow = new WrapPanel();
                        topRow.Children.Add(Txt(when, 10.5, false, _th.bSub, 0));
                        if (cm > 0) topRow.Children.Add(Txt("　" + cm + " 条评论", 10.5, false, _th.bSub, 0));
                        rowSp.Children.Add(topRow);

                        rowSp.Children.Add(Txt(content, 12.5, false, _th.bText, 5));

                        var acts = new WrapPanel();
                        acts.Margin = new Thickness(0, 6 * S, 0, 0);
                        string cap = tid;
                        acts.Children.Add(Btn("删除", false, delegate { QQDelSay(cap); }));
                        rowSp.Children.Add(acts);

                        rowBd.Child = rowSp;
                        _qqList.Children.Add(rowBd);
                    }
                }));
            });
        }

        // ---- 说说配图 ----
        void QQPickPostImages()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Title = "选几张图一起发（最多 9 张）";
            dlg.Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp";
            dlg.Multiselect = true;
            if (dlg.ShowDialog() != true) return;
            int room = 9 - _qqPostImgs.Count;
            if (room <= 0)
            {
                if (_qqStatus != null) _qqStatus.Text = "最多 9 张，先撤掉几张";
                return;
            }
            int added = 0;
            foreach (var f in dlg.FileNames)
            {
                if (added >= room) break;
                byte[] raw = null;
                try { raw = File.ReadAllBytes(f); } catch { continue; }
                byte[] jpg = QQShrinkJpeg(raw, 1280);
                if (jpg == null) continue;
                if (jpg.Length > 8 * 1024 * 1024) continue;
                _qqPostImgs.Add(jpg);
                added++;
            }
            if (_qqStatus != null)
                _qqStatus.Text = added > 0
                    ? ("已选 " + _qqPostImgs.Count + " 张图" + (added < dlg.FileNames.Length ? "（超出的没加）" : ""))
                    : "没读到可用图片";
            QQRepaintImgStrip();
        }

        /// <summary>把图缩到 maxSide 以内并转 JPEG（原图几 MB，直接发会撑爆请求）</summary>
        byte[] QQShrinkJpeg(byte[] raw, int maxSide)
        {
            try
            {
                var bm = new BitmapImage();
                bm.BeginInit();
                bm.StreamSource = new MemoryStream(raw);
                bm.CacheOption = BitmapCacheOption.OnLoad;
                bm.EndInit();
                bm.Freeze();
                double scale = 1.0;
                double big = Math.Max(bm.PixelWidth, bm.PixelHeight);
                if (big > maxSide) scale = maxSide / big;
                BitmapSource outSrc = bm;
                if (scale < 1.0)
                {
                    var tb = new TransformedBitmap(bm, new ScaleTransform(scale, scale));
                    tb.Freeze();
                    outSrc = tb;
                }
                var enc = new JpegBitmapEncoder();
                enc.QualityLevel = 86;
                enc.Frames.Add(BitmapFrame.Create(outSrc));
                using (var ms = new MemoryStream())
                {
                    enc.Save(ms);
                    return ms.ToArray();
                }
            }
            catch { return null; }
        }

        void QQRepaintImgStrip()
        {
            if (_qqImgStrip == null) return;
            _qqImgStrip.Children.Clear();
            for (int i = 0; i < _qqPostImgs.Count; i++)
            {
                var bd = new Border();
                bd.Width = 64 * S;
                bd.Height = 64 * S;
                bd.CornerRadius = new CornerRadius(8);
                bd.Background = _th.bBtn;
                bd.Margin = new Thickness(0, 0, 8 * S, 6 * S);
                bd.ClipToBounds = true;

                var img = new System.Windows.Controls.Image();
                img.Stretch = Stretch.UniformToFill;
                try
                {
                    var bm = new BitmapImage();
                    bm.BeginInit();
                    bm.StreamSource = new MemoryStream(_qqPostImgs[i]);
                    bm.CacheOption = BitmapCacheOption.OnLoad;
                    bm.EndInit();
                    bm.Freeze();
                    img.Source = bm;
                }
                catch { }
                bd.Child = img;

                // 鼠标点上就撤掉这张
                int idx = i;
                bd.ToolTip = "点一下撤掉这张";
                bd.Cursor = Cursors.Hand;
                bd.MouseLeftButtonUp += delegate
                {
                    if (idx >= 0 && idx < _qqPostImgs.Count)
                    {
                        _qqPostImgs.RemoveAt(idx);
                        QQRepaintImgStrip();
                    }
                };
                _qqImgStrip.Children.Add(bd);
            }
        }

        void QQPickAvatar()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Title = "选一张新头像";
            dlg.Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.gif";
            if (dlg.ShowDialog() != true) return;
            string path = dlg.FileName;
            try
            {
                var fi = new FileInfo(path);
                if (fi.Length > 6 * 1024 * 1024)
                {
                    if (_qqStatus != null) _qqStatus.Text = "图片太大（上限 6MB）";
                    return;
                }
            }
            catch { }
            if (_qqStatus != null) _qqStatus.Text = "上传头像中…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                // 走原始字节上传：Windows 命令行只有 32KB，不能把图 base64 塞进命令
                string up = Ssh.PutFile(_cfg, path, "/tmp/fish_new_avatar.bin", 60);
                string r = Ssh.Run(_cfg,
                    "curl -s -m 60 -X POST -H 'Content-Type: application/octet-stream' " +
                    "--data-binary @/tmp/fish_new_avatar.bin '" + ApiBase +
                    "/api/profile/avatar/raw?token=" + Token + "'; rm -f /tmp/fish_new_avatar.bin", 80);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool ok = r.IndexOf("\"ok\": true") >= 0 || r.IndexOf("\"ok\":true") >= 0;
                    if (_qqStatus != null) _qqStatus.Text = ok ? "头像换好了" : ("换头像失败：" + FirstLine(r));
                    if (ok) _qqAvatarB64 = "";
                    LoadQQProfile();
                }));
            });
        }

        // 塔菲专属花名册：同一个页面 + persona=taffy（粉色皮肤、只显示塔菲自己的数据）
        string TaffyRosterUrl()
        {
            return WebBase() + "/roster.html?token=" + Token + "&persona=taffy";
        }

        void BuildRoster()
        {
            StackPanel b;
            var card = Card("花名册", out b);
            b.Children.Add(Txt("名字 / 好感度 / 说话方式，QQNT 双栏样式，可搜索；好感度为 0 的默认折叠。",
                               12.5, false, _th.bText, 0));
            b.Children.Add(Txt(WebBase() + "/roster.html", 11.5, false, _th.bSub, 6));
            var r = Row();
            r.Margin = new Thickness(0, 12 * S, 0, 0);
            r.Children.Add(Btn("用浏览器打开", true, delegate { OpenUrl(WebBase() + "/roster.html?token=" + Token); }));
            r.Children.Add(Btn("复制链接", false, delegate
            {
                try { Clipboard.SetText(WebBase() + "/roster.html?token=" + Token); } catch { }
            }));
            b.Children.Add(r);
            b.Children.Add(Txt("链接里带 token，别发给别人。", 11, false, _th.bSub, 4));
            _page.Children.Add(card);

            // ---- 塔菲专属（2026-10-24 用户要求）----
            StackPanel b2;
            var cardT = Card("塔菲的花名册", out b2);
            b2.Children.Add(Txt("粉色那套：只看塔菲自己的聊天记录 / 灯牌 / 粉词云，和肥鱼的数据完全分开。",
                                12.5, false, _th.bText, 0));
            b2.Children.Add(Txt(WebBase() + "/roster.html?persona=taffy", 11.5, false, _th.bSub, 6));
            var r2 = Row();
            r2.Margin = new Thickness(0, 12 * S, 0, 0);
            r2.Children.Add(Btn("打开塔菲花名册", true, delegate { OpenUrl(TaffyRosterUrl()); }));
            r2.Children.Add(Btn("复制链接", false, delegate
            {
                try { Clipboard.SetText(TaffyRosterUrl()); } catch { }
            }));
            b2.Children.Add(r2);
            b2.Children.Add(Txt("塔菲的花名册从 2026-10-24 起单独记账，之前混在肥鱼那份里的记录不搬过来。",
                                11, false, _th.bSub, 4));
            _page.Children.Add(cardT);
        }

        // ------------------------------------------------ 设置
        void BuildSettings()
        {
            StackPanel b;
            var card = Card("连接（SSH）", out b);
            var tHost = Input(b, "IP", _cfg.Host);
            var tUser = Input(b, "用户名", _cfg.User);
            var tPass = Input(b, "密码", _cfg.Pass);
            tPass.IsReadOnly = false;
            var tFp = Input(b, "指纹", _cfg.HostKey);
            var r = Row();
            r.Margin = new Thickness(0, 10 * S, 0, 0);
            r.Children.Add(Btn("保存", true, delegate
            {
                _cfg.Host = tHost.Text.Trim();
                _cfg.User = tUser.Text.Trim();
                _cfg.Pass = tPass.Text;
                _cfg.HostKey = tFp.Text.Trim();
                _cfg.Save();
                ShowPage("settings");
            }));
            b.Children.Add(r);
            b.Children.Add(Txt("SSH 用 plink（PuTTY），远程命令 base64 传输，-hostkey 固定指纹防中间人。",
                               11, false, _th.bSub, 4));
            _page.Children.Add(card);

            StackPanel b2;
            var card2 = Card("外观 / 托盘", out b2);
            var r2 = Row();
            r2.Children.Add(Btn(_cfg.Dark ? "切到白天" : "切到夜间", false, delegate
            {
                _cfg.Dark = !_cfg.Dark; _cfg.Save(); RebuildAll();
            }));
            b2.Children.Add(r2);
            b2.Children.Add(Txt("字号", 12.5, false, _th.bText, 8));
            var sl = new Slider();
            sl.Minimum = 0.8; sl.Maximum = 1.5; sl.Value = _cfg.Scale;
            sl.Width = 260 * S;
            sl.TickFrequency = 0.1;
            sl.ValueChanged += delegate
            {
                _cfg.Scale = Math.Round(sl.Value, 2);
                _cfg.Save();
            };
            NoWheel(sl);
            b2.Children.Add(sl);
            b2.Children.Add(Txt("改完字号重新打开一遍最稳。", 11, false, _th.bSub, 2));

            var r3 = Row();
            r3.Margin = new Thickness(0, 10 * S, 0, 0);
            var bTray = Btn("最小化收托盘：" + (_cfg.Tray ? "开" : "关"), false, null);
            bTray.Click += delegate { _cfg.Tray = !_cfg.Tray; _cfg.Save(); bTray.Content = "最小化收托盘：" + (_cfg.Tray ? "开" : "关"); };
            r3.Children.Add(bTray);
            var bClose = Btn("点关闭收托盘：" + (_cfg.TrayClose ? "开" : "关"), false, null);
            bClose.Click += delegate { _cfg.TrayClose = !_cfg.TrayClose; _cfg.Save(); bClose.Content = "点关闭收托盘：" + (_cfg.TrayClose ? "开" : "关"); };
            r3.Children.Add(bClose);
            var bStart = Btn("启动就隐藏：" + (_cfg.TrayStart ? "开" : "关"), false, null);
            bStart.Click += delegate { _cfg.TrayStart = !_cfg.TrayStart; _cfg.Save(); bStart.Content = "启动就隐藏：" + (_cfg.TrayStart ? "开" : "关"); };
            r3.Children.Add(bStart);
            b2.Children.Add(r3);
            _page.Children.Add(card2);
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void DarkTitleBar()
        {
            try
            {
                var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int v = _cfg.Dark ? 1 : 0;
                DwmSetWindowAttribute(h, 20, ref v, sizeof(int));
                DwmSetWindowAttribute(h, 19, ref v, sizeof(int));
            }
            catch { }
        }

        /// <summary>输入框统一套模板：不然 WPF 默认样式在深色下是白底 + 系统蓝聚焦框。</summary>
        void StyleInput(TextBox tb)
        {
            tb.Background = _th.bBtn;
            tb.Foreground = _th.bText;
            tb.CaretBrush = _th.bText;
            tb.SelectionBrush = _th.bAccent;
            tb.BorderThickness = new Thickness(1);
            tb.BorderBrush = _th.bLine;
            // 上下 padding 别太大：以前 5*S 会把 30*S 高的框里文字挤掉一半（2026-10-23 用户反馈）
            tb.Padding = new Thickness(9 * S, 2 * S, 9 * S, 2 * S);
            tb.MinHeight = Math.Max(tb.MinHeight, 32 * S);
            tb.FontFamily = new FontFamily("Microsoft YaHei UI");   // 统一字体（别落到系统默认宋体）
            tb.VerticalContentAlignment = VerticalAlignment.Center;
            var tpl = new ControlTemplate(typeof(TextBox));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "bd";
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(TextBox.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(TextBox.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(TextBox.BorderThicknessProperty));
            var sv = new FrameworkElementFactory(typeof(ScrollViewer));
            sv.Name = "PART_ContentHost";
            sv.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(TextBox.PaddingProperty));
            bd.AppendChild(sv);
            tpl.VisualTree = bd;
            tb.Template = tpl;
        }

        /// <summary>
        /// 给输入框加占位提示。WPF 的 TextBox 没有 Placeholder：
        /// 模板里叠一个 TextBlock，绑到「Text == 空」时显示。
        /// 注意别在按键回调里重建模板——那样光标和焦点都会丢。
        /// </summary>
        void Watermark(TextBox tb, string hint)
        {
            tb.Tag = hint;
            var tpl = new ControlTemplate(typeof(TextBox));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "bd";
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(TextBox.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(TextBox.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(TextBox.BorderThicknessProperty));

            var g = new FrameworkElementFactory(typeof(Grid));

            var sv = new FrameworkElementFactory(typeof(ScrollViewer));
            sv.Name = "PART_ContentHost";
            sv.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(TextBox.PaddingProperty));
            g.AppendChild(sv);

            var ph = new FrameworkElementFactory(typeof(TextBlock));
            ph.Name = "ph";
            ph.SetValue(TextBlock.TextProperty, hint);
            ph.SetValue(TextBlock.ForegroundProperty, _th.bSub);
            ph.SetValue(UIElement.IsHitTestVisibleProperty, false);
            ph.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(TextBox.PaddingProperty));
            ph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            ph.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);   // 默认藏，空的时候再显示
            g.AppendChild(ph);

            bd.AppendChild(g);
            tpl.VisualTree = bd;

            var show = new DataTrigger();
            var b = new System.Windows.Data.Binding("Text");
            b.RelativeSource = new System.Windows.Data.RelativeSource(
                System.Windows.Data.RelativeSourceMode.Self);
            show.Binding = b;
            show.Value = "";
            show.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "ph"));
            tpl.Triggers.Add(show);

            tb.Template = tpl;
        }

        TextBox Input(StackPanel body, string label, string value)
        {
            body.Children.Add(Txt(label, 11.5, false, _th.bSub, 8));
            var t = new TextBox();
            t.Text = value;
            t.FontSize = 12.5 * S;
            t.Padding = new Thickness(8 * S, 5 * S, 8 * S, 5 * S);
            t.Background = _th.bBtn;
            t.Foreground = _th.bText;
            t.BorderBrush = _th.bLine;
            StyleInput(t);
            t.Margin = new Thickness(0, 3 * S, 0, 0);
            body.Children.Add(t);
            return t;
        }

        // ------------------------------------------------ 快捷指令（下拉）
        Button _quickBtn;
        Popup _quickPopup;

        void BuildQuickMenu(StackPanel host)
        {
            var row = new WrapPanel();
            row.Margin = new Thickness(0, 8 * S, 0, 0);
            _quickBtn = Btn("快捷指令  ▾", true, delegate
            {
                if (_quickPopup != null) _quickPopup.IsOpen = !_quickPopup.IsOpen;
            });
            _quickBtn.Padding = new Thickness(18 * S, 8 * S, 18 * S, 8 * S);
            row.Children.Add(_quickBtn);

            // 机器人状态：另一个下拉弹窗（自检 8 条）
            _healthBtn = Btn("机器人状态  ▾", false, delegate { ToggleHealth(); });
            _healthBtn.Padding = new Thickness(18 * S, 8 * S, 18 * S, 8 * S);
            _healthBtn.Margin = new Thickness(8 * S, 0, 8 * S, 8 * S);
            row.Children.Add(_healthBtn);

            host.Children.Add(row);
            host.Children.Add(Txt("「快捷指令」= 启停/日志/扫码；「机器人状态」= 自检每项是否正确", 11, false, _th.bSub, 2));

            _quickPopup = new Popup();
            _quickPopup.PlacementTarget = _quickBtn;
            _quickPopup.Placement = PlacementMode.Bottom;
            _quickPopup.StaysOpen = false;
            _quickPopup.AllowsTransparency = true;
            _quickPopup.VerticalOffset = 6;

            var border = new Border();
            border.Background = _th.bCard;
            border.CornerRadius = new CornerRadius(10);
            border.BorderBrush = _th.bLine;
            border.BorderThickness = new Thickness(1);
            border.Padding = new Thickness(6);
            border.MinWidth = 210 * S;
            border.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 3,
                Opacity = 0.45,
                Color = Colors.Black
            };

            var sp = new StackPanel();
            AddQuick(sp, "刷新状态（重新查询）", delegate { RefreshStatus(true); });
            AddQuick(sp, "─", null);
            AddQuick(sp, "启动机器人", delegate { CloudCmd("start"); });
            AddQuick(sp, "重启机器人", delegate { CloudCmd("restart"); });
            AddQuick(sp, "关闭（先说再见）", delegate { CloudCmd("stop"); });
            AddQuick(sp, "─", null);
            AddQuick(sp, "看运行日志", delegate { TailLog(); });
            AddQuick(sp, "扫码登录（内置二维码）", delegate { ShowPage("qr"); });
            AddQuick(sp, "SnowLuma 网页（5099）", delegate { OpenUrl("http://" + _cfg.Host + ":5099"); });
            AddQuick(sp, "连接诊断（8090 通不通）", delegate { DiagnoseConn(); });
            AddQuick(sp, "─", null);
            AddQuick(sp, "聊天记录", delegate { ShowPage("chat"); });
            AddQuick(sp, "机器人控件", delegate { ShowPage("botctl"); });
            AddQuick(sp, "花名册", delegate { ShowPage("roster"); });
            AddQuick(sp, "─", null);
            AddQuick(sp, _cfg.Dark ? "切到白天" : "切到夜间", delegate
            {
                _cfg.Dark = !_cfg.Dark; _cfg.Save(); RebuildAll();
            });

            border.Child = sp;
            _quickPopup.Child = border;
        }

        // ------------------------------------------------ 机器人状态（自检弹窗）
        Button _healthBtn;
        Popup _healthPopup;
        StackPanel _healthList;
        TextBlock _healthVerdict, _healthHint;

        void ToggleHealth()
        {
            if (_healthPopup == null) BuildHealthPopup();
            _healthPopup.IsOpen = !_healthPopup.IsOpen;
            if (_healthPopup.IsOpen) LoadSelfCheck();
        }

        void BuildHealthPopup()
        {
            _healthPopup = new Popup();
            _healthPopup.PlacementTarget = _healthBtn;
            _healthPopup.Placement = PlacementMode.Bottom;
            _healthPopup.StaysOpen = false;
            _healthPopup.AllowsTransparency = true;
            _healthPopup.VerticalOffset = 6;

            var border = new Border();
            border.Background = _th.bCard;
            border.CornerRadius = new CornerRadius(10);
            border.BorderBrush = _th.bLine;
            border.BorderThickness = new Thickness(1);
            border.Padding = new Thickness(14 * S, 12 * S, 14 * S, 10 * S);
            border.MinWidth = 380 * S;
            border.MaxWidth = 460 * S;
            border.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 3,
                Opacity = 0.45,
                Color = Colors.Black
            };

            var sp = new StackPanel();
            var head = new DockPanel();
            var title = Txt("机器人状态", 13.5, true, _th.bText, 0);
            DockPanel.SetDock(title, Dock.Left);
            head.Children.Add(title);
            var fix = Btn("一键修复", true, delegate { SelfCheckFix(); });   // 手机端也有这个按钮
            fix.Padding = new Thickness(10 * S, 3 * S, 10 * S, 3 * S);
            fix.Margin = new Thickness(8 * S, 0, 6 * S, 0);
            fix.HorizontalAlignment = HorizontalAlignment.Right;
            head.Children.Add(fix);
            var rf = Btn("重新自检", false, delegate { LoadSelfCheck(); });
            rf.Padding = new Thickness(10 * S, 3 * S, 10 * S, 3 * S);
            rf.HorizontalAlignment = HorizontalAlignment.Right;
            head.Children.Add(rf);
            sp.Children.Add(head);

            _healthVerdict = Txt("查询中…", 14, true, _th.bSub, 8);
            sp.Children.Add(_healthVerdict);
            _healthHint = Txt("", 11, false, _th.bSub, 2);
            sp.Children.Add(_healthHint);

            var sep = new Border();
            sep.Height = 1;
            sep.Background = _th.bLine;
            sep.Margin = new Thickness(0, 8 * S, 0, 6 * S);
            sp.Children.Add(sep);

            _healthList = new StackPanel();
            sp.Children.Add(_healthList);

            border.Child = sp;
            _healthPopup.Child = border;
        }

        void LoadSelfCheck()
        {
            if (_healthList == null) return;
            _healthList.Children.Clear();
            _healthVerdict.Text = "查询中…";
            _healthVerdict.Foreground = _th.bSub;
            _healthHint.Text = "";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string json = Ssh.Curl(_cfg, "/api/selfcheck?token=" + Token, 45);
                Dispatcher.BeginInvoke(new Action(delegate { FillSelfCheck(json); }));
            });
        }

        void FillSelfCheck(string json)
        {
            if (_healthList == null) return;
            try
            {
                var ser = new JavaScriptSerializer();
                var o = ser.DeserializeObject(json) as Dictionary<string, object>;
                if (o == null) throw new Exception("空返回");
                string verdict = o.ContainsKey("verdict") ? Convert.ToString(o["verdict"]) : "?";
                string hint = o.ContainsKey("hint") ? Convert.ToString(o["hint"]) : "";
                var items = o["items"] as object[];

                bool allOk = true;
                _healthBad.Clear();
                _healthList.Children.Clear();
                if (items != null)
                {
                    foreach (var it in items)
                    {
                        var d = it as Dictionary<string, object>;
                        bool ok = d.ContainsKey("ok") && Convert.ToBoolean(d["ok"]);
                        if (!ok) allOk = false;
                        string name = Convert.ToString(d["name"]);
                        string detail = d.ContainsKey("detail") ? Convert.ToString(d["detail"]) : "";
                        if (!ok) _healthBad.Add(name);

                        var row = new WrapPanel();
                        row.Margin = new Thickness(0, 3 * S, 0, 3 * S);
                        var mark = Txt(ok ? "✓" : "✗", 12.5, true,
                                       ok ? _th.bOk : _th.bBad, 0);
                        mark.MinWidth = 20 * S;
                        row.Children.Add(mark);
                        var nm = Txt(name, 12, false, _th.bText, 0);
                        nm.MinWidth = 140 * S;
                        row.Children.Add(nm);
                        var dt = Txt(detail, 11.5, false, _th.bSub, 0);
                        dt.MaxWidth = 250 * S;
                        row.Children.Add(dt);
                        _healthList.Children.Add(row);
                    }
                }
                _healthVerdict.Text = verdict;
                // 颜色按「判定文本」来，而不是按每条是否全绿：
                // 只要判定是「在线」就一定是绿字（个别子项红不影响总判定）
                Brush vc;
                if (verdict.IndexOf("不在线") >= 0) vc = _th.bBad;
                else if (verdict.IndexOf("在线") >= 0) vc = _th.bOk;
                else if (verdict.IndexOf("自愈") >= 0) vc = _th.bAccent;
                else vc = allOk ? _th.bOk : _th.bBad;
                _healthVerdict.Foreground = vc;
                _healthHint.Text = hint;
            }
            catch (Exception ex)
            {
                _healthVerdict.Text = "自检失败";
                _healthVerdict.Foreground = _th.bBad;
                _healthHint.Text = ex.Message + "：" + FirstLine(json);
            }
        }

        /// <summary>
        /// 一键修复：和手机端同一套判断 ——
        ///   有「fishbot」相关项挂了 → systemctl start fishbot
        ///   有「容器 / SnowLuma / OneBot」相关项挂了 → docker restart（走 /api/action napcat-restart）
        /// 其余的只能手动处理，把名字列出来。
        /// </summary>
        void SelfCheckFix()
        {
            if (_healthHint == null) return;
            if (_healthBad.Count == 0)
            {
                _healthHint.Text = "都正常，不用修。";
                return;
            }
            bool needStart = false, needNapcat = false;
            var manual = new List<string>();
            foreach (string n in _healthBad)
            {
                if (n.IndexOf("fishbot") >= 0) needStart = true;
                else if (n.IndexOf("容器") >= 0 || n.IndexOf("SnowLuma") >= 0 || n.IndexOf("OneBot") >= 0) needNapcat = true;
                else manual.Add(n);
            }
            if (!needStart && !needNapcat)
            {
                _healthHint.Text = "需要手动处理：" + string.Join("、", manual.ToArray());
                return;
            }
            _healthHint.Text = "修复中…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                var acts = new List<string>();
                if (needStart)
                {
                    string r = Ssh.Run(_cfg, "systemctl start fishbot; sleep 1; systemctl is-active fishbot", 70);
                    acts.Add("启动 fishbot（" + FirstLine(r) + "）");
                }
                if (needNapcat)
                {
                    PostJson("/api/action?token=" + Token, "{\"name\":\"napcat-restart\"}");
                    acts.Add("重启协议端（约 30 秒）");
                }
                Thread.Sleep(2500);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_healthHint != null)
                        _healthHint.Text = "已处理：" + string.Join("、", acts.ToArray()) +
                                          (manual.Count > 0 ? "；仍要手动：" + string.Join("、", manual.ToArray()) : "") +
                                          "　正在重新自检…";
                    LoadSelfCheck();
                }));
            });
        }

        /// <summary>
        /// 连接诊断：8090 通不通 + 看门狗 + 插件服务，一次说清（手机端点状态标题也是干这个）。
        /// 走 SSH，所以本机不需要能访问 8090。
        /// </summary>
        void DiagnoseConn()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string script =
                    "echo SVC=$(systemctl is-active fishbot)\n" +
                    "echo WEB=$(systemctl is-active fishweb)\n" +
                    "echo TIMER=$(systemctl is-active fishdog.timer)\n" +
                    "echo CODE=$(curl -s -o /dev/null -w '%{http_code}' -m 5 http://127.0.0.1:8090/api/health)\n";
                string o = Ssh.Run(_cfg, script, 25);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    string svc = Fld(o, "SVC="), web = Fld(o, "WEB="), timer = Fld(o, "TIMER="), code = Fld(o, "CODE=");
                    var sb = new StringBuilder();
                    sb.AppendLine("服务器：" + _cfg.Host + "（由控制台通过 SSH 代查，本机不用能访问 8090）");
                    sb.AppendLine();
                    sb.AppendLine("接口 8090（/api/health）：" + (code.Trim() == "200" ? "200 通" : (code.Trim() + "（不通）")));
                    sb.AppendLine("控制台后端 fishweb：" + web);
                    sb.AppendLine("插件服务 fishbot：" + svc);
                    sb.AppendLine("看门狗 fishdog.timer：" + timer);
                    sb.AppendLine();
                    if (code.Trim() != "200")
                    {
                        sb.AppendLine("8090 不通时按顺序查这三条：");
                        sb.AppendLine("  ① 腾讯云安全组有没有放行 TCP 8090（0.0.0.0/0）");
                        sb.AppendLine("  ② 手机/PC 的 IP 在不在白名单");
                        sb.AppendLine("  ③ fishweb 是不是没跑：systemctl status fishweb");
                    }
                    else
                    {
                        sb.AppendLine("8090 是通的 —— 连不上就是客户端这一侧的问题（token、IP、网络）。");
                    }
                    MessageBox.Show(sb.ToString(), "连接诊断", MessageBoxButton.OK, MessageBoxImage.Information);
                }));
            });
        }

        void AddQuick(StackPanel sp, string text, Action act)
        {
            if (act == null)
            {
                var sep = new Border();
                sep.Height = 1;
                sep.Background = _th.bLine;
                sep.Margin = new Thickness(8 * S, 5 * S, 8 * S, 5 * S);
                sp.Children.Add(sep);
                return;
            }
            var b = new Button();
            b.Content = text;
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            b.Padding = new Thickness(12 * S, 7 * S, 12 * S, 7 * S);
            b.Margin = new Thickness(0, 1, 0, 1);
            b.Background = Brushes.Transparent;
            b.Foreground = _th.bText;
            b.BorderThickness = new Thickness(0);
            b.FontSize = 12.5 * S;
            b.Cursor = Cursors.Hand;
            b.Template = FlatTemplate();
            b.Click += delegate
            {
                if (_quickPopup != null) _quickPopup.IsOpen = false;
                act();
            };
            sp.Children.Add(b);
        }

        // ------------------------------------------------ 开发信息
        void BuildDevInfo()
        {
            StackPanel b;
            var card = Card("版本与运行环境", out b);
            string[] lines = new string[]
            {
                "控制台：肥鱼控制台 WPF v3.0（C# WPF · .NET Framework 4.8 · csc 直接编译，无 XAML 文件）",
                "机器人插件：肥鱼人设.py",
                "云端：Ubuntu 22.04 · Python 3.10 · nonebot2 2.5.0 · nonebot-adapter-onebot",
                "协议端：SnowLuma（Docker 容器 snowluma）· OneBot v11 · HTTP 3000",
                "进程管理：systemd 服务 fishbot / fishweb（重启走 systemctl）",
                "SSH：plink.exe（PuTTY）· base64 传输 · -hostkey 固定指纹",
                "模型：deepseek-v4-flash-0731（文字）/ deepseek-v4-flash-vision（看图）",
                "存储：无数据库，全部 /opt/qqbot/data/*.json（记忆 / 花名册 / 好感度 / 表情索引）"
            };
            foreach (var s in lines) b.Children.Add(Txt(s, 12, false, _th.bText, 2));
            _page.Children.Add(card);

            StackPanel b2;
            var card2 = Card("当前状态速览", out b2);
            string[][] facts = new string[][]
            {
                new string[] { "机器人 QQ", "1000000001（白米饭大鲸） · 超管 1000000002" },
                new string[] { "服务器", "腾讯云轻量 127.0.0.1 · Ubuntu 22.04 · 2 核 2G" },
                new string[] { "协议端", "SnowLuma（Docker 容器 snowluma，容器内跑 Linux QQ）" },
                new string[] { "框架", "NoneBot2 2.5.0 + adapter-onebot · Python 3.10" },
                new string[] { "语音", "CosyVoice2 克隆音色 → SILK" },
                new string[] { "接口", "OneBot 3000 · WebUI 5099 · 控制台 8090" },
                new string[] { "三个服务", "fishbot（主程序）· fishweb（控制台）· fishdog / fishlogin（定时器）" }
            };
            foreach (var f in facts) b2.Children.Add(KeyRow(f[0], f[1]));
            _page.Children.Add(card2);

            StackPanel b3;
            var card3 = Card("三端怎么分工", out b3);
            string[][] roles = new string[][]
            {
                new string[] { "电脑（本程序）", "状态 / 快捷指令（启停重启、日志、扫码）/ 按群设置 / 聊天记录 / 花名册" },
                new string[] { "手机（肥鱼控制台.apk）", "状态、控件、自检、扫码、日志五页；会话、气泡、本地缓存" },
                new string[] { "网页 API（:8090）", "status / diag / selfcheck / qr / conversations / messages / send / roster" },
                new string[] { "数据来源", "journalctl 的 [接收] [发送] + /opt/qqbot/data/manual_sent.log" }
            };
            foreach (var f in roles) b3.Children.Add(KeyRow(f[0], f[1]));
            _page.Children.Add(card3);

            StackPanel b4;
            var card4 = Card("文档 · 都在 01-文档 文件夹", out b4);
            string[][] docs = new string[][]
            {
                new string[] { "00-从这里开始.md          ← 总入口，先看这个", @"F:\肥鱼机器人\00-从这里开始.md" },
                new string[] { "系统总览.md              架构 / 端口 / 数据流", @"F:\肥鱼机器人\01-文档\系统总览.md" },
                new string[] { "三端控制台.md            电脑 / 手机 / API 功能与构建", @"F:\肥鱼机器人\01-文档\三端控制台.md" },
                new string[] { "运维手册.md              巡检 / 看门狗 / 故障处置", @"F:\肥鱼机器人\01-文档\运维手册.md" },
                new string[] { "更新日志.md              每次改了什么、为什么", @"F:\肥鱼机器人\01-文档\更新日志.md" },
                new string[] { "语音系统.md              克隆音色 / SILK / 情绪 / 排错", @"F:\肥鱼机器人\01-文档\语音系统.md" },
                new string[] { "部署与使用说明.md        v1~v29 历史（大而全）", @"F:\肥鱼机器人\01-文档\部署与使用说明.md" }
            };
            foreach (var d in docs)
            {
                var row = new WrapPanel();
                row.Margin = new Thickness(0, 3 * S, 0, 3 * S);
                var t = Txt(d[0], 12, false, File.Exists(d[1]) ? _th.bText : _th.bSub, 0);
                t.MinWidth = 380 * S;
                t.Margin = new Thickness(0, 0, 10 * S, 0);
                row.Children.Add(t);
                if (File.Exists(d[1]))
                {
                    string p2 = d[1];
                    var bo = Btn("打开", false, delegate { OpenUrl(p2); });
                    bo.Padding = new Thickness(10 * S, 3 * S, 10 * S, 3 * S);
                    var bd = Btn("所在目录", false, delegate
                    {
                        try { Process.Start("explorer.exe", "/select,\"" + p2 + "\""); } catch { }
                    });
                    bd.Padding = new Thickness(10 * S, 3 * S, 10 * S, 3 * S);
                    row.Children.Add(bo);
                    row.Children.Add(bd);
                }
                else row.Children.Add(Txt("（没找到）", 11, false, _th.bBad, 0));
                b4.Children.Add(row);
            }
            _page.Children.Add(card4);

            StackPanel b5;
            var card5 = Card("本地路径", out b5);
            string[][] paths = new string[][]
            {
                new string[] { "工作区", @"F:\肥鱼机器人" },
                new string[] { "插件源码", @"F:\肥鱼机器人\02-程序\插件源码" },
                new string[] { "控制台源码", @"F:\肥鱼机器人\02-程序\控制台WPF" },
                new string[] { "备份包", @"F:\肥鱼机器人\06-备份" },
                new string[] { "配置目录", Cfg.Dir }
            };
            foreach (var p in paths)
            {
                var row = new WrapPanel();
                row.Margin = new Thickness(0, 3 * S, 0, 3 * S);
                var t = Txt(p[0], 12, false, _th.bSub, 0);
                t.MinWidth = 90 * S;
                row.Children.Add(t);
                var t2 = Txt(p[1], 12, false, _th.bText, 0);
                t2.MinWidth = 380 * S;
                t2.Margin = new Thickness(10 * S, 0, 10 * S, 0);
                row.Children.Add(t2);
                string dir = p[1];
                if (Directory.Exists(dir))
                {
                    var bo = Btn("打开", false, delegate { try { Process.Start("explorer.exe", dir); } catch { } });
                    bo.Padding = new Thickness(10 * S, 3 * S, 10 * S, 3 * S);
                    row.Children.Add(bo);
                }
                b5.Children.Add(row);
            }
            b5.Children.Add(Txt("配置文件：" + Cfg.FilePath + "（和旧版控制台共用）", 11, false, _th.bSub, 6));
            _page.Children.Add(card5);
        }

        WrapPanel KeyRow(string key, string val)
        {
            var row = new WrapPanel();
            row.Margin = new Thickness(0, 2 * S, 0, 2 * S);
            var k = Txt(key, 12, false, _th.bSub, 0);
            k.MinWidth = 110 * S;
            row.Children.Add(k);
            var v = Txt(val, 12, false, _th.bText, 0);
            v.MaxWidth = 720 * S;
            row.Children.Add(v);
            return row;
        }

        // ------------------------------------------------ 杂项
        void OpenUrl(string url)
        {
            try { Process.Start(url); }
            catch (Exception ex) { MessageBox.Show("打不开浏览器：" + ex.Message); }
        }

        /// <summary>
        /// 托盘图标。<b>必须用 32bpp 加载</b>：否则 Windows 合成通知区时把透明像素当成黑色，
        /// 图标会出现黑底/黑框。先取 exe 内嵌的 fish.ico，再退到 exe 自身图标，最后才用系统默认。
        /// </summary>
        System.Drawing.Icon LoadTrayIcon()
        {
            int size = WF.SystemInformation.SmallIconSize.Width;   // 150% 缩放时是 24，100% 是 16
            if (size <= 0) size = 16;
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (Stream st = asm.GetManifestResourceStream("fish.ico"))
                {
                    if (st != null)
                    {
                        // w/h 传 0 = 在这个 .ico 里找最接近 size 的那张，比 exe 图标只能取 32x32 清晰得多
                        var ico = new System.Drawing.Icon(st, size, size);
                        return new System.Drawing.Icon(ico, ico.Size);   // 拷贝独立实例，流关掉也不会失效
                    }
                }
            }
            catch { }
            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ico != null) return new System.Drawing.Icon(ico, size, size);
            }
            catch { }
            return null;
        }

        /// <summary>WPF 窗口图标（任务栏 / Alt-Tab），从同一份 fish.ico 里挑指定尺寸。</summary>
        ImageSource LoadAppIcon(int size)
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (Stream st = asm.GetManifestResourceStream("fish.ico"))
                {
                    if (st == null) return null;
                    var ico = new System.Drawing.Icon(st, size, size);
                    using (var bmp = ico.ToBitmap())
                    {
                        var ms = new MemoryStream();
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        ms.Position = 0;
                        var dec = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat,
                                                       BitmapCacheOption.OnLoad);
                        if (dec.Frames.Count > 0) return dec.Frames[0];
                    }
                }
            }
            catch { }
            return null;
        }

        void SetupTray()
        {
            _tray = new WF.NotifyIcon();
            System.Drawing.Icon trayIcon = LoadTrayIcon();
            if (trayIcon != null) _tray.Icon = trayIcon;
            else _tray.Icon = System.Drawing.SystemIcons.Application;
            _tray.Text = "肥鱼控制台 · " + _cfg.Host;
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ShowFromTray(); };
            var menu = new WF.ContextMenuStrip();
            menu.Items.Add("打开", null, delegate { ShowFromTray(); });
            menu.Items.Add("退出", null, delegate
            {
                _exiting = true;
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
                Application.Current.Shutdown();
            });
            _tray.ContextMenuStrip = menu;
        }

        public void HideToTray()
        {
            Hide();
            if (_tray != null)
            {
                _tray.Visible = true;
                _tray.ShowBalloonTip(2000, "肥鱼控制台还在后台", "点托盘图标回来；右键退出。", WF.ToolTipIcon.Info);
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr h, int cmd);

        public void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            // WPF 的 Show() 对「被 Win32 隐藏」的窗口是空操作，再兜一层
            try
            {
                var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (h != IntPtr.Zero) ShowWindow(h, 5);      // SW_SHOW
                ShowWindow(h, 9);                            // SW_RESTORE
            }
            catch { }
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }
    }

    // ============================================================ 入口
    static class Program
    {
        const string MutexName = "Local\\FishConsoleWPF_single";
        const string ShowEventName = "Local\\FishConsoleWPF_show";

        [STAThread]
        public static void Main(string[] args)
        {
            // 自检：-shot <页面> <输出.png>
            if (args.Length >= 3 && args[0].TrimStart('-').ToLower() == "shot")
            {
                MainWindow.ShotMode = true;
                var app0 = new Application();
                var w0 = new MainWindow();
                w0.Width = 1280;
                // 沉默名单那张卡片比较靠下，自检时把窗口拉高，别让 -shot 拍到一半
                bool tallShot = args.Length >= 4 && (args[3] == "blacklist" || args[3] == "qq" || args[3] == "ambient" || args[3] == "persona" || args[3] == "stickers"
                                                      || args[3] == "drawer" || args[3] == "voice" || args[3] == "bots"
                                                      || args[3] == "drawstyles" || args[3] == "fishmem"
                                                      || args[3].StartsWith("tag:"));
                w0.Height = (args.Length >= 4 && args[3] == "persona") ? 2000 : (tallShot ? 1500 : 820);
                w0.Show();
                if (args.Length >= 4) w0.ShotDemo = args[3];   // quote / members：假数据验证界面
                w0.Goto(args[1]);
                // -shot big：拍「双击放大」那个大图窗口
                bool bigShot = args[1] == "big";
                if (bigShot) w0.ShowBigForShot();
                var tmr = new DispatcherTimer();
                // chat:xxx 要等 SSH 拉真数据，给长一点（表情包面板要列表 + 一页缩略图，更慢）
                bool realChat = args.Length >= 4 && (args[3].StartsWith("chat:") || args[3] == "stickers" || args[3] == "persona"
                                                     || args[3] == "voice" || args[3] == "bots"
                                                     || args[3].StartsWith("tag:")
                                                     || args[3] == "photolib2"
                                                     || args[3] == "drawstyles" || args[3] == "fishmem"
                                                     || args[3] == "draw");   // 图库/画图/模板要拉服务器状态
                tmr.Interval = TimeSpan.FromMilliseconds((realChat || args[1] == "qr" || args[1] == "cloud") ? 12000 :
                                                         (args[1] == "chat" ? 9000 : (bigShot ? 3000 : 2600)));   // 聊天页要等图片下载
                tmr.Tick += delegate
                {
                    tmr.Stop();
                    try
                    {
                        // 演示模式：先把页面滚到目标卡片再拍（沉默名单在页面下方）
                        if (args.Length >= 4 && (args[3] == "blacklist" || args[3] == "qq" || args[3] == "drawer"))
                            w0.ScrollDemoToBottom();
                        if (args.Length >= 4 && args[3] == "ambient")
                            w0.ScrollDemoToAmbient();
                        if (args.Length >= 4 && args[3] == "persona")
                            w0.ScrollDemoToPersona();
                        w0.UpdateLayout();
                        // -shot big 拍的是看图窗口，不是主窗口
                        Window shotTarget = w0.ShotExtra != null ? w0.ShotExtra : (Window)w0;
                        shotTarget.UpdateLayout();
                        var rtb = new RenderTargetBitmap((int)shotTarget.ActualWidth, (int)shotTarget.ActualHeight,
                                                          96, 96, PixelFormats.Pbgra32);
                        rtb.Render(shotTarget);
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(rtb));
                        using (var fs = File.Create(args[2])) enc.Save(fs);
                    }
                    catch (Exception ex) { Console.WriteLine("FAIL " + ex.Message); }
                    app0.Shutdown();
                };
                tmr.Start();
                app0.Run();
                return;
            }
            // 单实例：m 必须活到进程退出。
            // 以前写的是局部变量 + GC.KeepAlive(m)，但 KeepAlive 只保证"调用点之前"不被回收，
            // 之后再没人引用它，GC 一跑互斥量就被回收 → 第二个实例照样能抢到锁
            // → 双击几次就有几个进程，托盘里一排一样的图标。
            using (Mutex m = new Mutex(false, MutexName))
            {
                bool got = false;
                try { got = m.WaitOne(0, false); }
                catch (AbandonedMutexException) { got = true; }   // 上一个进程崩了，锁算我拿到
                if (!got)
                {
                    // 已经有实例在跑：让它把自己显示出来，然后本进程立刻退出
                    try
                    {
                        var ev = EventWaitHandle.OpenExisting(ShowEventName);
                        ev.Set();
                        ev.Close();
                    }
                    catch { }
                    return;
                }

                try
                {
                    var app = new Application();
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    var win = new MainWindow();

                    var showEv = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                    var t = new Thread(delegate()
                    {
                        while (true)
                        {
                            showEv.WaitOne();
                            try { win.Dispatcher.BeginInvoke(new Action(delegate { win.ShowFromTray(); })); }
                            catch { }
                        }
                    });
                    t.IsBackground = true;
                    t.Start();

                    app.Run(win);
                }
                finally
                {
                    try { m.ReleaseMutex(); } catch { }
                }
            }
        }
    }
}
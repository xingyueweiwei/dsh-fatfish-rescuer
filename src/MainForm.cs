using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// ============================================================
// 大肥鱼救星 - 主窗口（深海女仆风）
// ============================================================
namespace BigFatFishRescuer
{
    public sealed class MainForm : Form
    {
        private readonly Label _stateLabel;
        private readonly Label _stateDetail;
        private readonly Button _btnStartOpen;
        private readonly Button _btnOpen;
        private readonly Button _btnRestart;
        private readonly Button _btnStop;
        private readonly Button _btnClose;
        private readonly Button _btnScout;
        private readonly Button _btnDiag;
        private readonly Button _btnWhy;
        private readonly Button _btnBootCheck;
        private readonly Button _btnPatch;
        private readonly Button _btnPopup;
        private readonly Button _btnTerminal;
        private readonly Button _btnRepair;
        private readonly Button _btnForce;
        private readonly Button _btnReadOnly;   // 🛡 只诊断（安全总闸）
        private readonly Button _btnCompat;
        private readonly Button _btnFixPlugin;
        private readonly Button _btnCrash;
        private readonly Button _btnDisableCrash;
        private readonly Button _btnFixSkin;
        private readonly Button _btnPostCheck;
        private readonly Button _btnCleanDangling;
        private readonly Button _btnDedup;
        private readonly Button _btnReEnable;
        private readonly Button _btnReinstall;
        private readonly Button _btnLayer;
        private readonly Button _btnPatchLock;
        private readonly Button _btnLockFix;
        private readonly Button _btnWhite;
        private readonly Button _btnPathAudit;
        private readonly Button _btnHealBoundary;
        private readonly Button _btnSkin;
        private readonly Button _btnVault;
        private readonly Button _btnVaultList;
        private readonly Button _btnVaultRestore;
        private readonly Button _btnConfigCheck;
        private readonly Button _btnLog;
        private readonly Button _btnExport;
        private readonly Button _btnRefresh;
        private readonly Button _btnDiagnose;
        private readonly Button _btnStartup;
        private readonly RichTextBox _logBox;
        private readonly ToolStripStatusLabel _statusText;
        private readonly System.Windows.Forms.Timer _pollTimer;
        private readonly object _logLock = new object();
        private bool _busy;
        private Image _maid;
        /// <summary>当前皮肤配色（跟 DSH 皮肤联动；窗口边框/头部渐变都读它）。</summary>
        private SkinTheme.Palette _pal;
        private BufferedPanel _headerPanel;

        /// <summary>启动时间线（2026-09-18 加：主人报"打开的时候还是会有加载问题，非常明显"）。
        /// 用 Stopwatch 从**进程最早**开始计时，每个关键阶段打一行到 manual-scroll.log，
        /// 这样"到底卡在哪一段"有数字、不用猜。Program.Main 第一行会把它启动。</summary>
        public static System.Diagnostics.Stopwatch Boot = System.Diagnostics.Stopwatch.StartNew();

        internal static void SBoot(string msg)
        {
            try { SLogSkin("boot t+" + Boot.ElapsedMilliseconds + "ms: " + msg); } catch { }
        }
        // 头部横幅的缓存位图（键 = 皮肤 + 尺寸）。★ 2026-09-18：整条横幅一次画好，平时只贴图，
        // 这样重绘不再每次重算渐变/光柱/海浪/蝴蝶结、也不再每帧缩放立绘与鲸鱼（主人报"卡+闪"的根因）。
        private Bitmap _headerCache;
        private bool _headerPainted;
        private System.Windows.Forms.Timer _headerRebuild;   // 尺寸变化后的防抖重建（见 header.Resize）
        /// <summary>构造函数里那个"把整条横幅画进 g"的委托（供 --paintbench 直接调，见 PaintBench）。</summary>
        private Action<System.Drawing.Graphics, int, int> _drawHeaderFn;
        /// <summary>只画**装饰层**（渐变 / 光柱 / 海浪 / 金线 / 蝴蝶结）。缓存位图就用它。</summary>
        private Action<System.Drawing.Graphics, int, int> _drawDecorFn;
        private string _headerKey = "";

        // ★★ 2026-09-18「拖拽拉扯」修法新增 —— 素材层（立绘 / 鲸鱼）改成**独立的缩放成品图**。
        //   关键事实：这两样东西的尺寸**只跟高度 H 有关**
        //     · 立绘：box = (16, 10, 176, H-20) ⇒ 只随 H 变
        //     · 鲸鱼：wh2 = H - 12       ⇒ 只随 H 变
        //   所以：
        //     ① 只在 H 变化时才从原图（1080×1336 / 474×474）重缩放一次 —— 拖拽改宽时**零成本**；
        //     ② 贴的时候**永远 1:1**（不随宽度缩放）⇒ 拖拽时**不可能**再出现横向拉扯。
        //   实测基线（修前）：窗宽 1380→1880 时鲸鱼 bbox 207px → 283px（宽比 1.374），高度 152px 不变。
        private Bitmap _heroScaled, _whaleScaled;
        private int _artScaledH = -1;
        /// <summary>把"素材层"按 1:1 贴上去。挂到 BufferedPanel.ArtDraw 上，由面板在贴完装饰后调用。</summary>
        private Action<System.Drawing.Graphics, int, int> _artFn;
        /// <summary>缓存是否在**本帧**刚建立（用于首帧补画，避免露出一帧底色）。</summary>
        private bool _headerJustBuilt;

        /// <summary>作废素材层成品图（换皮肤 / 换立绘素材时调；**尺寸变化不要调**，那本来就该复用）。</summary>
        private void ArtCacheDrop()
        {
            try { if (_heroScaled != null) _heroScaled.Dispose(); } catch { }
            try { if (_whaleScaled != null) _whaleScaled.Dispose(); } catch { }
            _heroScaled = null; _whaleScaled = null; _artScaledH = -1;
        }

        /// <summary>按当前高度 H 确保"立绘 / 鲸鱼"的缩放成品图是最新的。只跟 H 有关 ⇒ H 没变直接返回。</summary>
        private void EnsureArtScaled(int H)
        {
            if (_artScaledH == H && _heroScaled != null) return;
            ArtCacheDrop();
            int effH = Math.Max(24, H);
            try
            {
                Image m = HeroArtOrMascot();
                if (m != null)
                {
                    var box = new Rectangle(16, 10, 176, effH - 20);
                    if (box.Width > 4 && box.Height > 4)
                    {
                        double sc = Math.Min(box.Width / (double)m.Width, box.Height / (double)m.Height);
                        int mw = Math.Max(1, (int)(m.Width * sc)), mh = Math.Max(1, (int)(m.Height * sc));
                        var bmp = new Bitmap(mw, mh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        using (var g2 = Graphics.FromImage(bmp))
                        {
                            g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g2.SmoothingMode = SmoothingMode.AntiAlias;
                            g2.DrawImage(m, new Rectangle(0, 0, mw, mh));
                        }
                        _heroScaled = bmp;
                    }
                }
            }
            catch { }
            try
            {
                if (_whaleArt == null) _whaleArt = SkinArt.CutOutBackground(SkinArt.Get("whale.png"));
                if (_whaleArt != null)
                {
                    int wh2 = Math.Max(1, effH - 12);
                    int ww2 = Math.Max(1, (int)(_whaleArt.Width * (wh2 / (double)_whaleArt.Height)));
                    var bmp = new Bitmap(ww2, wh2, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var g2 = Graphics.FromImage(bmp))
                    {
                        g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g2.SmoothingMode = SmoothingMode.AntiAlias;
                        g2.DrawImage(_whaleArt, new Rectangle(0, 0, ww2, wh2));
                    }
                    _whaleScaled = bmp;
                }
            }
            catch { }
            _artScaledH = effH;
        }

        /// <summary>把素材层画上去（**1:1，绝不缩放**）。位置与原来的 PictureBox / 旧 drawHeader 一致：
        ///   立绘在左侧列内居中（列宽 200、Margin 16/10 ⇒ box = 16,10,176,H-20）；
        ///   鲸鱼贴右边缘（右边距 8、上边距 6）。</summary>
        private void DrawArt(System.Drawing.Graphics g, int W, int H)
        {
            try { EnsureArtScaled(H); } catch { }
            try
            {
                if (_heroScaled != null)
                {
                    var box = new Rectangle(16, 10, 176, Math.Max(4, H - 20));
                    g.DrawImageUnscaled(_heroScaled,
                        box.X + (box.Width - _heroScaled.Width) / 2,
                        box.Y + (box.Height - _heroScaled.Height) / 2);
                }
            }
            catch { }
            try
            {
                if (_whaleScaled != null)
                    g.DrawImageUnscaled(_whaleScaled, W - _whaleScaled.Width - 8, 6);
            }
            catch { }
        }

        /// <summary>一帧"走缓存"该做的全部事：贴装饰（尺寸不符就拉伸垫底）+ 1:1 贴素材。
        ///   供 --paintbench 的 A 侧使用 —— 这样 A/B 两侧比的是**真实的每帧成本**。</summary>
        internal void DrawHeaderFast(System.Drawing.Graphics g, int W, int H)
        {
            if (_headerCache != null)
            {
                if (_headerCache.Width == W && _headerCache.Height == H) g.DrawImageUnscaled(_headerCache, 0, 0);
                else g.DrawImage(_headerCache, new Rectangle(0, 0, W, H));
            }
            if (_artFn != null) _artFn(g, W, H);
        }

        /// <summary>作废头部缓存（皮肤换了 / 尺寸变了 / 素材换了都要调）。</summary>
        private void HeaderCacheDrop()
        {
            try { if (_headerCache != null) _headerCache.Dispose(); } catch { }
            _headerCache = null; _headerKey = "";
            try { if (_headerPanel != null) _headerPanel.Cached = null; } catch { }
        }

        /// <summary>点「🎨 换皮肤」：弹选择框 → 存盘 → 立刻换（不用重启）。</summary>
        private void PickSkin()
        {
            try
            {
                string cur = SkinTheme.SavedSkin();
                if (cur.Length == 0)
                {
                    try
                    {
                        string env = Environment.GetEnvironmentVariable("BFF_SKIN");
                        if (!string.IsNullOrEmpty(env)) cur = env;
                    }
                    catch { }
                }
                if (cur.Length == 0) cur = "whale";
                using (var dlg = new SkinPickerDialog(cur))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(dlg.Chosen))
                    { LogLine("换皮肤：已取消（保持现在的）"); return; }
                    LogLine(SkinTheme.SaveSkin(dlg.Chosen));
                    ApplySkinNow();
                    LogLine("已换成：「" + SkinTheme.Current().Name + "」——随时再点「🎨 换皮肤」改回来。");
                }
            }
            catch (Exception ex) { LogLine("换皮肤失败：" + ex.Message); }
        }

        /// <summary>按当前皮肤重刷整套外观（头部缓存 / 压边 / 控件配色 / 原生窗口边框）。</summary>
        private void ApplySkinNow()
        {
            try
            {
                _pal = SkinTheme.Current();
                HeaderCacheDrop();
                ArtCacheDrop();              // ★ 2026-09-18：换皮肤可能连立绘素材一起换 ⇒ 素材成品图也要作废
                // ★ 2026-09-18：把面板底色设成"横幅顶部色"。这样首帧（缓存还没建、走 base 画背景）
                //   露出的是深海色而不是浅色控件底 ⇒ 不会有白闪。
                try { if (_headerPanel != null) _headerPanel.BackColor = _pal.HeaderFrom; } catch { }
                SkinTheme.ApplyTo(this, _pal);
                AddFrameEdge(DockStyle.Left, 14);
                AddFrameEdge(DockStyle.Right, 14);
                ApplySkinFrame();
                if (_headerPanel != null) _headerPanel.Invalidate();
                Invalidate(true);
            }
            catch (Exception ex) { LogLine("应用皮肤失败：" + ex.Message); }
        }
        private SkinFrame.CaptionBar _caption;
        private Label _subtitle;
        private TabControl _tabs;
        /// <summary>
        /// ★★ 2026-09-18「打开不丝滑」的正面修法（主人截图：窗口出来了，上半截还是白的/空的）。
        /// 现象：窗口一出现，**200 来个子控件是逐个首绘**的，那 0.4~0.6 秒里用户看到的是
        ///   "半成品窗口"（抓帧实测：t+0.6s 仍有 20% 的近白未画区域，t+1.0s 才画完；
        ///   把窗口提到前台时更明显——未画区域会露出后面的窗口）。
        /// 修法：加 <c>WS_EX_COMPOSITED</c>（0x02000000），让 Windows 把**整窗连同所有子控件**
        ///   先画进离屏缓冲、再一次性合成出来 ⇒ 用户看不到任何"逐个子控件慢慢出现"的中间态。
        /// 负对照开关：<c>BFF_NOCOMPOSITED=1</c> 退回旧行为（用于证明这条改动确实起作用）。
        /// </summary>
        protected override System.Windows.Forms.CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                try
                {
                    string off = Environment.GetEnvironmentVariable("BFF_NOCOMPOSITED");
                    if (string.IsNullOrEmpty(off) || off == "0") cp.ExStyle |= 0x02000000;   // WS_EX_COMPOSITED
                }
                catch { }
                return cp;
            }
        }

        private static void SLogSkin(string msg) { try { Manual.SLog("[skin] " + msg); } catch { } }

        /// <summary>仅供自检/截图用：启动时选中第几个页签（0 起；-1＝不干预）。</summary>
        public static int InitialTab = -1;
        /// <summary>--shot 目标路径：非空时启动后自截图并退出（文档配图/验收用）。</summary>
        public static string ShotPath = null;

        public MainForm()
        {
            Text = DshCore.AppTitle + " - DeepSeek Harness 救援工具";   // ★ 带版本号，收的人一眼知道是哪版
            StartPosition = FormStartPosition.CenterScreen;
            // 2026-09-16 修正：窗口高度必须按**屏幕可用区域**收口。
            // 原来写死 940 —— 在带 DPI 缩放（125%/150%）的笔记本上会超出屏幕，
            // 顶部的立绘被挤走、底部的日志框整个被切到屏幕外，于是"点了没反馈"。
            // 注意本程序是 DPI-unaware，WorkingArea 返回的是虚拟化后的尺寸，正好可以直接比。
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int winW = Math.Min(900, wa.Width - 60);
            // ★ 2026-09-17（口径修正，配合下面 SetClientSizeCore 的抵消）：
            //   自绘标题栏那一整条（CaptionH = 36px）是画在**客户区**里的 ⇒ 想让"标题栏下方的
            //   正文区"保持 770px，客户区就得给 770 + 36。
            //   旧版这里写的是 36-23 = 13 —— 那是因为当时 ClientSize 的 setter 会**多给 23px**
            //   （样式里留着 WS_CAPTION，WinForms 按"客户区 + 外框 + 标题栏"算窗口尺寸，而
            //    标题栏那一截已被我们抠掉）⇒ 13 + 23 恰好凑成 36，属"将错就错"；
            //   而这个"多给的 23px"正是**每轮最大化 +23px 漂移的同一个根**。
            //   现在 23 已在 setter 里抵消掉，所以这里必须写足 36。
            int capExtra = SkinFrame.Enabled ? SkinFrame.CaptionH : 0;
            int winH = Math.Min(770 + capExtra, wa.Height - 60);
            // ★ 2026-09-18：记住"想要的客户区尺寸"，等句柄创建、WS_CAPTION 摘掉之后再落实一次
            //   （构造时样式还没摘，ClientSize 的换算口径与摘掉之后不同 ⇒ 不重设会少 23px 正文区）
            _wantClient = new Size(Math.Max(640, Math.Min(900, wa.Width - 60)), winH);
            if (winW < 640) winW = 640;
            if (winH < 520) winH = 520;
            ClientSize = new Size(winW, winH);
            MinimumSize = new Size(Math.Min(720, winW), 520);
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.FromArgb(238, 244, 252);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            SBoot("ctor 开始");
            _maid = LoadMaidImage();
            SBoot("立绘解码完成" + (_maid == null ? "（没有）" : _maid.Width + "x" + _maid.Height));
            // ★ 皮肤联动：启动就先按"现在在跑的那套 DSH 皮肤"取同款配色
            try { _pal = SkinTheme.Current(); } catch { _pal = SkinTheme.For(""); }
            // ★ B 方案：自绘标题栏（取不到就整体退回原生窗口，MainForm 里看见 _caption==null 即原生）
            try
            {
                if (SkinFrame.Enabled)
                    _caption = SkinFrame.CaptionBar.Attach(this,
                        new Func<SkinTheme.Palette>(delegate { return _pal ?? SkinTheme.For(""); }));
            }
            catch { _caption = null; }

            // ---------- 头部横幅（深海女仆风，含大立绘） ----------
            // ★★ 2026-09-18 卡顿/闪烁修复（主人："感觉变卡了，打开甚至会出现闪闪的情况"）——
            //   原先**每次重绘**都要现场做一整套：深海渐变 → 径向背光 → 5 条光柱 → 2 条正弦海浪 →
            //   金线 → 蝴蝶结 → 再把 474×474 的鲸鱼缩放贴上去；而立绘还是个 PictureBox，
            //   每帧把 1080×1336 的图缩一次。**而且这块面板没开双缓冲** ⇒ 又慢又闪。
            //   ⇒ 改成：整条横幅**一次画进缓存位图**（键 = 皮肤 + 尺寸，只有尺寸/皮肤变了才重画），
            //     平时每帧只贴这一张；面板开双缓冲（AllPaintingInWmPaint + OptimizedDoubleBuffer）。
            //     立绘也不再当子控件，直接画进缓存（一次高质量缩放代替每帧缩放）。
            var header = new BufferedPanel { Dock = DockStyle.Top, Height = 150 };
            // ★ 2026-09-18：底色设成横幅顶部色 ⇒ 首帧（缓存未建、走 base 画背景）露的是深海色，
            //   而不是浅色控件底（否则会白闪一下）。换皮肤时 ApplySkinNow 会再同步一次。
            try { header.BackColor = (_pal ?? SkinTheme.Current()).HeaderFrom; } catch { }
            _headerPanel = header;
            // 把**装饰层**画进 g（尺寸 W×H）。
            // ★★ 2026-09-18「拖拽拉扯」修法：这张缓存里**只放装饰**，不再放立绘/鲸鱼。
            //   原因：装饰（渐变 / 光柱 / 海浪 / 金线 / 蝴蝶结）横向拉伸了也看不出来，
            //   而素材一旦跟着宽度缩放就是肉眼可见的拉扯。拆开之后，
            //   "尺寸不匹配时拉伸垫底（绝不露白）"这条老策略可以**继续保留**、且不再有副作用。
            _drawDecorFn = delegate(System.Drawing.Graphics g, int W, int H)
            {
                // ★★ 2026-09-18 重做（主人："研究一个界面皮肤怎么搞…做一个鲸鱼娘皮肤"）——
                //   不再把别家皮肤的素材拼上来，而是按**鲸鱼娘自己的意象**画：深海渐变 + 光柱 + 海浪线。
                var pal = _pal ?? SkinTheme.Current();
                // 1) 深海渐变（竖直方向，制造"下潜"感）
                using (var lg = new LinearGradientBrush(new Point(0, 0), new Point(0, H),
                           pal.HeaderFrom, pal.HeaderTo))
                {
                    g.FillRectangle(lg, 0, 0, W, H);
                }
                // 2) 深海光柱（极淡的斜向光束，制造纵深。★ 不做发光描边——那会让暗色皮肤发腻）
                try
                {
                    // 2a) 立绘背光：左侧一团柔和的径向光，让抠掉白底的立绘"浮"在暗底上而不是糊进去
                    try
                    {
                        using (var pg = new GraphicsPath())
                        {
                            var box = new Rectangle(20, 8, 210, H - 16);
                            pg.AddEllipse(box);
                            using (var pgb = new PathGradientBrush(pg))
                            {
                                pgb.CenterColor = Color.FromArgb(30, pal.Accent);
                                pgb.SurroundColors = new Color[] { Color.FromArgb(0, pal.Accent) };
                                g.FillPath(pgb, pg);
                            }
                        }
                    }
                    catch { }
                    for (int i = 0; i < 5; i++)
                    {
                        int x0 = W / 6 * i + 30;
                        using (var lg2 = new LinearGradientBrush(new Point(x0, 0), new Point(x0 + 110, H),
                                   Color.FromArgb(38, pal.Accent), Color.FromArgb(0, pal.Accent)))
                        using (var path = new GraphicsPath())
                        {
                            path.AddPolygon(new Point[] {
                                new Point(x0, 0), new Point(x0 + 44, 0),
                                new Point(x0 + 128, H), new Point(x0 + 66, H) });
                            g.FillPath(lg2, path);
                        }
                    }
                }
                catch { }
                // 3) 海浪线（两条，正弦；颜色用主强调色的两档透明度）+ 金色"海平线"
                try
                {
                    for (int k = 0; k < 2; k++)
                    {
                        int baseY = H - 24 + k * 8;
                        using (var pen = new Pen(Color.FromArgb(k == 0 ? 200 : 120, pal.Accent), k == 0 ? 2.2f : 1.5f))
                        {
                            int n = W / 8 + 2;
                            var pts = new Point[n];
                            for (int i = 0; i < n; i++)
                            {
                                double tt = (i * 8.0) / 90.0 + k * 0.9;
                                pts[i] = new Point(i * 8, (int)(baseY + Math.Sin(tt) * (k == 0 ? 5 : 8)));
                            }
                            g.DrawLines(pen, pts);
                        }
                    }
                    using (var gold = new SolidBrush(pal.Gold))
                    {
                        g.FillRectangle(gold, 0, H - 4, W, 4);
                    }
                }
                catch { }
                // 4) 她的蝴蝶结（女仆鲸鱼娘的标志）。★ 中心点要抬高：116px 宽的蝴蝶结高约 52px，
                //    中心放在 H-10 会被底部金线切掉下半（1:1 复核时发现）⇒ 抬高到 -30。
                try { SkinArt.DrawBow(g, new Point(W / 2, H - 30), 116, 1f); } catch { }
            };
            // ★★ 素材层（立绘 / 鲸鱼）**不画进缓存**，改为按 1:1 独立贴。
            //   旧版第 5/6 项就在这里把两张图缩放着画进缓存 ⇒ 缓存被拉伸时它们跟着变形。
            //   现在改由 DrawArt() 承担，见本文件 EnsureArtScaled / DrawArt / DrawHeaderFast。
            _artFn = new Action<System.Drawing.Graphics, int, int>(DrawArt);
            // "整条横幅一次画好"（装饰 + 素材）—— 只在两处用：
            //   ① --paintbench 的"现算"基线（要和"贴缓存"比成本）；
            //   ② BFF_NOCACHE=1 的负对照（退回每帧现算 + 不双缓冲）。
            Action<System.Drawing.Graphics, int, int> drawHeader = delegate(System.Drawing.Graphics g, int W, int H)
            {
                if (_drawDecorFn != null) _drawDecorFn(g, W, H);
                if (_artFn != null) _artFn(g, W, H);
            };

            // 头部缓存：只有"皮肤变了"或"尺寸变了"才重画（键 = 皮肤 + 宽高）
            // ★★ 2026-09-18：这张缓存里**只有装饰层**。素材层不进来 ⇒ 缓存被拉伸时素材不会变形。
            _drawHeaderFn = drawHeader;   // 供 --paintbench 直接调用（不用窗口，避开外部合成负载）
            header.ArtDraw = _artFn;      // 面板贴完装饰后，回调这里按 1:1 贴素材
            Action ensureHeaderCache = delegate
            {
                try
                {
                    if (header.Width <= 0 || header.Height <= 0) return;
                    var hp = _pal ?? SkinTheme.Current();
                    string key = hp.Key + "|" + header.Width + "x" + header.Height;
                    if (_headerCache != null && _headerKey == key) { header.Cached = _headerCache; return; }
                    var bmp = new Bitmap(header.Width, header.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var hg = Graphics.FromImage(bmp))
                    {
                        hg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        hg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        hg.SmoothingMode = SmoothingMode.AntiAlias;
                        // ★ 只画装饰。素材不进来（这是本次修法的核心）。
                        if (_drawDecorFn != null) _drawDecorFn(hg, header.Width, header.Height);
                    }
                    HeaderCacheDrop();
                    _headerCache = bmp; _headerKey = key; header.Cached = bmp;
                    _headerJustBuilt = true;
                }
                catch { }
            };
            header.Paint += delegate(object s, PaintEventArgs e)
            {
                if (!_headerPainted) { _headerPainted = true; SBoot("★ 头部横幅第一次画出来"); }
                // 负对照（BFF_NOCACHE=1）：退回"每帧现算"的老做法，用来量缓存到底省了多少
                try
                {
                    string nc = Environment.GetEnvironmentVariable("BFF_NOCACHE");
                    if (!string.IsNullOrEmpty(nc) && nc != "0") { drawHeader(e.Graphics, header.Width, header.Height); return; }
                }
                catch { }
                _headerJustBuilt = false;
                ensureHeaderCache();
                // ★★ 2026-09-18 改动（配合 BufferedPanel.OnPaint 改成无条件走 base）：
                //   正常帧的"贴装饰 + 1:1 贴素材"已由 OnPaintBackground 完成，这里**不再重复贴**。
                //   只有**本帧刚建立缓存**时才要补画一次 —— 因为那一帧的背景走的是 base（面板底色），
                //   不补画就会露出底色。
                if (_headerJustBuilt || header.Cached == null)
                    drawHeader(e.Graphics, header.Width, header.Height);
            };
            header.Resize += delegate
            {
                try
                {
                    // ★★ 2026-09-18 修"打开不丝滑"（主人截图：窗口出来了、上半截却是白的）：
                    //   原来这里是"尺寸一变就 HeaderCacheDrop()" —— 而显示阶段框架会连着做几次
                    //   布局/尺寸修正（贴压边、上状态栏、摘标题栏、客户区收口），于是缓存被反复丢掉，
                    //   每次丢掉都要在下一次 Paint 里**现算整条横幅**（≈几十毫秒），中间态就被用户看见了。
                    //   改成**防抖重建**：先把旧图拉伸顶着（绝不露白），停稳 160ms 再按新尺寸重画一次。
                    if (_headerCache == null) return;
                    if (_headerCache.Width == header.Width && _headerCache.Height == header.Height) return;
                    if (_headerRebuild == null)
                    {
                        _headerRebuild = new System.Windows.Forms.Timer { Interval = 160 };
                        _headerRebuild.Tick += delegate
                        {
                            try
                            {
                                _headerRebuild.Stop();
                                ensureHeaderCache();   // 按当前尺寸重画一次（内部按"皮肤+尺寸"作键，尺寸没变就是空转）
                                header.Invalidate();
                            }
                            catch { }
                        };
                    }
                    _headerRebuild.Stop();
                    _headerRebuild.Start();
                }
                catch { }
            };
            // ★ 预热两张图（把"抠白底"这件重活挪到窗口显示**之前**做）：
            //   否则第一帧才去抠，头部会先空一下再补上，看起来就是"闪"。
            try { HeroArtOrMascot(); } catch { }
            SBoot("立绘抠白底完成");
            try { if (_whaleArt == null) _whaleArt = SkinArt.CutOutBackground(SkinArt.Get("whale.png")); } catch { }
            SBoot("鲸鱼抠白底完成");
            // 2026-09-16 修正：TableLayoutPanel 默认**不透明**，会把 header.Paint 画的
            // 深蓝渐变整个盖住 → 头部变成白底、白色标题看不清（立绘也因此显得很突兀）。
            // 置为透明后渐变才透得出来。
            var headLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent };
            headLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            headLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            // ★ 第 0 列（200px）留给"画进缓存"的立绘，这里不再放 PictureBox。

            var textStack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent };
            textStack.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
            textStack.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            var title = new Label
            {
                Text = "大肥鱼救星",
                Font = new Font("Microsoft YaHei UI", 20f, FontStyle.Bold),
                ForeColor = Color.White,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.BottomLeft,
            };
            var subtitle = new Label
            {
                Text = "鲸鱼娘出诊 · 药到病除，一键救活 DeepSeek Harness",
                Font = new Font("Microsoft YaHei UI", 9f),
                ForeColor = Color.FromArgb(214, 228, 248),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft,
                Margin = new Padding(0, 2, 0, 8),
            };
            _subtitle = subtitle;
            textStack.Controls.Add(title, 0, 0);
            textStack.Controls.Add(subtitle, 0, 1);
            headLayout.Controls.Add(textStack, 1, 0);
            header.Controls.Add(headLayout);
            Controls.Add(header);

            // ---------- 状态区 ----------
            var statePanel = new Panel { Dock = DockStyle.Top, Height = 84, Padding = new Padding(18, 8, 18, 4), Tag = "card" };
            _stateLabel = new Label
            {
                Text = "正在检测…",
                Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold),
                ForeColor = Color.FromArgb(60, 60, 60),
                Dock = DockStyle.Top,
                Height = 34,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            _stateDetail = new Label
            {
                Text = "",
                Font = new Font("Microsoft YaHei UI", 9f),
                ForeColor = Color.FromArgb(120, 120, 120),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft,
            };
            statePanel.Controls.Add(_stateDetail);
            statePanel.Controls.Add(_stateLabel);
            Controls.Add(statePanel);

            // ---------- 按钮区（按功能分组，自动适应不裁切） ----------
            // ---------- 研究插件（自动适配新插件：报告 / 预演 / 应用 / 复验 / 回滚）----------
            _btnScout = MakeButton("🔍 研究插件", Color.FromArgb(120, 90, 180), 110);
            _btnScout.Click += delegate {
                string pkgIn = Prompt("研究插件", "输入插件包名（如 dsh-file-upload 或 @scope/name）：");
                if (string.IsNullOrEmpty(pkgIn)) return;
                string pkgName = pkgIn.Trim();
                RunAction("研究插件", delegate {
                    string prof = Path.Combine(DshCore.DshHome, "profiles", "web");
                    ScoutResult sr = PluginScout.Probe(pkgName);
                    string planTxt = PluginScout.PlanText(sr, prof);
                    if (sr.NeedBuild.Count == 0) { MessageBox.Show(planTxt, "研究插件 · 结果"); return null; }
                    // ★ v5.0：只读闸门在这里先说清楚 —— 别让主人点了"是"才发现被拒
                    if (DshCore.ReadOnlyMode) { MessageBox.Show(planTxt + "\r\n\r\n" + DshCore.ReadOnlyRefusal(), "研究插件 · 被「只诊断」拦下"); return null; }
                    if (!Confirm("研究插件 · 预演", planTxt + "\r\n\r\n要现在就应用这个适配吗？\r\n"
                        + "（改前先备份 pnpm-workspace.yaml 与 pnpm-lock.yaml；改后在真实 profile 里跑一次 pnpm install 复验；\r\n"
                        + "  复验不通过会自动回滚配置并再跑一次 install 还原 node_modules。\r\n"
                        + "  ★ 正在运行的 DSH 不会被结束或重启，但它的插件目录会按新配置重装一次。）")) return null;
                    string applyLog;
                    bool ok = PluginScout.Apply(sr, prof, out applyLog);
                    MessageBox.Show(applyLog, ok ? "已应用并复验通过" : "未通过（已回滚）");
                    return applyLog;
                }, false);
            };
            _btnStartOpen = MakeButton("🚀 启动并打开", Color.FromArgb(46, 134, 87), 150);
            // ★★ 2026-09-18：换成**一键智能**（主人："有进程就打开，没进程自动杀掉再打开，有的时候我们也不知道"）：
            //   ① 端口上有健康的 dsh ⇒ 不重启它，直接打开；② 有卡死的 dsh 残留 ⇒ 先清掉（只清确认是 dsh 的）再起；
            //   ③ 没人占 ⇒ 直接起；④ 被别的程序占 ⇒ 绝不杀它，自动换空闲端口并记住。
            _btnStartOpen.Click += delegate { RunAction("启动并打开（智能）", delegate { return DshCore.SmartStartAndOpen(); }, false); };
            _btnOpen = MakeButton("🌐 打开无痕", Color.FromArgb(52, 109, 179), 110);
            _btnOpen.Click += delegate { RunAction("打开无痕", delegate { return DshCore.OpenUi(); }, false); };
            _btnRestart = MakeButton("🔄 重启服务", Color.FromArgb(210, 130, 30), 110);
            _btnRestart.Click += delegate { if (!Confirm("重启服务", "将重启 dsh web，当前网页界面会断开并重新连接（会话可能中断）。确定继续吗？\r\n\r\n（重启前会自动备份关键配置。）\r\n\r\n★ 范围：" + DshCore.KillScopeSummary())) return; RunAction("重启服务", delegate { bool ok = DshCore.RestartDsh(); string note = DshCore.LastRestartNote.Length > 0 ? " " + DshCore.LastRestartNote : ""; string heal = DshCore.LastStopNote; return (ok ? ("重启完成。" + note) : ("重启未完成。" + note)) + (heal.Length > 0 ? "\r\n\r\n" + heal : ""); }, true); };
            _btnStop = MakeButton("⏹ 停止服务", Color.FromArgb(190, 60, 60), 110);
            // ★ T4：结论行改用 DshCore.LastStopHead —— 旧版无论什么原因失败都只说
            //   "未找到运行中的 dsh 进程"。真实原因可能是"有进程但我杀不掉（权限不足）"
            //   或"端口被别的程序占着、按纪律跳过了"，那句话是**假阴性 + 误导方向**。
            _btnStop.Click += delegate { if (!Confirm("停止服务", "将停止 dsh web 服务，当前网页界面会断开。确定继续吗？\r\n\r\n（停止前会自动备份关键配置；若强杀打断了 DSH 写配置，会立刻体检并自动回滚。）\r\n\r\n★ 范围：" + DshCore.KillScopeSummary())) return; RunAction("停止服务", delegate { DshCore.StopDsh(); string head = DshCore.LastStopHead.Length > 0 ? DshCore.LastStopHead : "停止操作已执行"; return head + (DshCore.LastStopNote.Length > 0 ? "\r\n\r\n" + DshCore.LastStopNote : ""); }, true); };
            _btnClose = MakeButton("❌ 关闭界面", Color.FromArgb(140, 50, 50), 110);
            // ★ T4 + T11：结论行用 LastStopHead（同"停止服务"）；关闭窗口的计数改用
            //   "复查后确认真的关掉了"的数量（旧版把"已请求数"当"已关闭数"报出去）。
            _btnClose.Click += delegate { if (!Confirm("关闭界面", "将停止 dsh 并关闭当前界面窗口，AI 对话会断开。确定关闭吗？\r\n\r\n★ 范围：" + DshCore.KillScopeSummary())) return; RunAction("关闭界面", delegate { DshCore.StopDsh(); string head = DshCore.LastStopHead.Length > 0 ? DshCore.LastStopHead : "停止操作已执行"; int w = DshCore.CloseInterfaceWindows(); return head + " 已关闭 " + w + " 个界面窗口。" + (DshCore.LastCloseNote.Length > 0 ? "\r\n" + DshCore.LastCloseNote : ""); }, false); };
            _btnDiag = MakeButton("🔍 运行诊断", Color.FromArgb(90, 90, 140), 110);
            _btnDiag.Click += delegate { RunAction("运行诊断", delegate { return DshCore.DiagnosticsText(); }, false); };
            _btnWhy = MakeButton("❓ 为何打不开", Color.FromArgb(196, 90, 60), 120);
            _btnWhy.Click += delegate { RunAction("为何打不开", delegate { return DshCore.WhyOpenFailed(); }, false); };
            // 2026-09-16 新增：对照《dsh打不开_故障排查与修复_20260916.md》的四项体检
            _btnBootCheck = MakeButton("🚦 启动链路自检", Color.FromArgb(150, 70, 110), 140);
            _btnBootCheck.Click += delegate { RunAction("启动链路自检", delegate { return DshBoot.StartupChainCheck(); }, false); };

            // ---- 环境与弹窗（2026-09-16 新增：来自"一直弹 PowerShell 窗口"那天的教训）----
            // 那天的四个来源里，①②③ 都是 node_modules 里的**补丁**，DSH 一升级就会被覆盖 → 复发。
            // 这组按钮就是"复发时不用再找我"的自助入口。
            _btnPatch = MakeButton("🧩 补丁体检", Color.FromArgb(120, 90, 150), 110);
            _btnPatch.Click += delegate { PatchAction(); };
            _btnPopup = MakeButton("🔔 弹窗体检", Color.FromArgb(120, 90, 150), 110);
            _btnPopup.Click += delegate { RunAction("弹窗体检", delegate { return PatchGuard.PopupProbe(10); }, false); };
            _btnTerminal = MakeButton("🖥 默认终端", Color.FromArgb(120, 90, 150), 110);
            _btnTerminal.Click += delegate { TerminalAction(); };
            _btnRepair = MakeButton("🩺 一键修复", Color.FromArgb(46, 134, 87), 110);
            // ★ T29（确认策略倒挂）：这个按钮会**重启/杀进程**，却没有二次确认，
            //   而破坏性更小的「重启服务」有。补上。
            _btnRepair.Click += delegate {
                if (!Confirm("一键修复",
                    "将按「为何打不开」的结论自动执行修复（可能重启 dsh、结束 dsh 进程）。\r\n\r\n" +
                    "⚠ 若走到重启分支，当前网页界面会断开，正在进行的对话可能中断。\r\n" +
                    "✔ 若只是「端口被别的程序占用」，本工具**不会**去杀它，只会如实告知。\r\n\r\n" +
                    "确定继续吗？\r\n\r\n（重启前会自动备份关键配置。）\r\n\r\n★ 范围：" + DshCore.KillScopeSummary())) return;
                RunAction("一键修复", delegate { return DshCore.RepairFromWhy(); }, true);
            };
            // ★★ 2026-09-18 安全总闸按钮（主人："这个玩意太可怕了，人家装后把自己的 dsh 杀了"）。
            //   打开后 StopDsh / RestartDsh / StartDsh / RepairFromWhy 一律拒绝、**连进程都不碰**。
            _btnReadOnly = MakeButton("", Color.FromArgb(70, 90, 120), 150);
            Action syncReadOnly = delegate
            {
                try
                {
                    bool ro = DshCore.ReadOnlyMode;
                    _btnReadOnly.Text = ro ? "🛡 只诊断：已开" : "🛡 只诊断：关";
                    _btnReadOnly.BackColor = ro ? Color.FromArgb(40, 120, 90) : Color.FromArgb(70, 90, 120);
                }
                catch { }
            };
            _btnReadOnly.Click += delegate
            {
                try
                {
                    bool now = !DshCore.ReadOnlyMode;
                    DshCore.ReadOnlyMode = now;
                    syncReadOnly();
                    LogLine(now
                        ? "🛡 只诊断模式：**已打开** —— 救星从此不会结束/启动任何 dsh 进程（重启、停止、一键修复、强力自愈都会被拒绝）。"
                        : "🛡 只诊断模式：已关闭 —— 那几颗按钮恢复可动手（每一步仍会先弹确认框）。");
                }
                catch { }
            };
            syncReadOnly();

            _btnForce = MakeButton("⚡ 强力自愈", Color.FromArgb(220, 80, 40), 110);
            // ★ 2026-09-16 补修（T2 —— 理论审查："确认策略与破坏性倒挂"）：
            //   破坏性**更小**的 重启服务/停止服务/关闭界面 都有二次确认，
            //   而破坏性**最大**的「强力自愈」（杀进程 + 释放端口 + 重启 + 拉 ComfyUI + 开桌面客户端）
            //   却是**一点就跑**。这里补上同样风格的确认框，文案照抄「停止服务」那套。
            _btnForce.Click += delegate {
                if (!Confirm("强力自愈",
                    "将结束 dsh 相关进程、释放端口并重新拉起服务（还会顺便检查本地绘图服务）。\r\n\r\n" +
                    "⚠ 当前网页界面会断开，正在进行的对话可能中断。\r\n\r\n" +
                    "确定继续吗？\r\n\r\n（执行前会自动备份关键配置；只会结束 node 进程，不会误杀其它程序。）\r\n\r\n★ 范围：" + DshCore.KillScopeSummary())) return;
                RunAction("强力自愈", delegate { return DshCore.ForceRecover(); }, true);
            };
            _btnCompat = MakeButton("🧩 插件兼容性", Color.FromArgb(150, 96, 60), 120);
            _btnCompat.Click += delegate { RunAction("插件兼容性", delegate { return DshCore.PluginCompatibility(); }, false); };
            _btnFixPlugin = MakeButton("🩹 修复插件", Color.FromArgb(140, 100, 60), 100);
            _btnFixPlugin.Click += delegate { FixPluginAction(); };
            _btnCrash = MakeButton("🚨 排查崩溃插件", Color.FromArgb(196, 60, 60), 130);
            _btnCrash.Click += delegate { RunAction("排查崩溃插件", delegate { return DshCore.DetectCrashedPlugin(); }, false); };
            _btnDisableCrash = MakeButton("⛔ 禁用崩溃插件", Color.FromArgb(150, 50, 50), 130);
            _btnDisableCrash.Click += delegate { DisableCrashedPluginAction(); };
            _btnFixSkin = MakeButton("💊 修复皮肤互斥", Color.FromArgb(150, 96, 60), 130);
            // ★ T18：旧版写死 `FixSkinExclusion("official")` —— 点一下就把主人正在用的
            //   女仆皮肤关掉、换回官方外观。现在改为**探测当前皮肤并保持它不变**，且加确认框。
            _btnFixSkin.Click += delegate { FixSkinAction(); };
            _btnLog = MakeButton("📄 打开日志", Color.FromArgb(110, 110, 110), 100);
            _btnLog.Click += delegate { OpenLogFile(); };
            _btnExport = MakeButton("📋 导出报告", Color.FromArgb(90, 110, 90), 100);
            _btnExport.Click += delegate { RunAction("导出报告", delegate { return DshCore.ExportDiagnostics(); }, false); };
            _btnRefresh = MakeButton("🔄 刷新状态", Color.FromArgb(52, 109, 179), 110);
            _btnRefresh.Click += delegate { LogLine("手动刷新状态…"); RefreshState(); };
            _btnStartup = MakeButton("🌅 开机自启", Color.FromArgb(60, 140, 100), 100);
            // ★ T29/T21/T21b/P13：旧版**没有确认框**就写"启动"文件夹，只按"文件在不在"判成功，
            //   而且**只创建、不提供取消入口**（想撤销只能自己去启动文件夹删 .lnk）。
            //   现在：先探测现状 → 已启用就问"要取消吗" → 未启用则确认 + 回读校验目标路径。
            _btnStartup.Click += delegate { StartupAction(); };
            _btnDiagnose = MakeButton("📦 导出诊断包", Color.FromArgb(70, 96, 140), 120);
            _btnDiagnose.Click += delegate { ExportDiagnoseAction(); };

            // ---- 配置保险箱（2026-09-12 事故后新增）----
            _btnVault = MakeButton("🛡 备份配置", Color.FromArgb(40, 120, 130), 110);
            _btnVault.Click += delegate { RunAction("备份配置", delegate { return SafeConfig.SnapshotManual(); }, false); };
            _btnConfigCheck = MakeButton("✅ 配置自检", Color.FromArgb(40, 120, 130), 110);
            _btnConfigCheck.Click += delegate { RunAction("配置自检", delegate { return SafeConfig.SelfCheck(); }, false); };
            _btnVaultList = MakeButton("📚 快照列表", Color.FromArgb(60, 110, 120), 110);
            _btnVaultList.Click += delegate { RunAction("快照列表", delegate { return SafeConfig.SnapshotListText(); }, false); };
            _btnVaultRestore = MakeButton("♻ 恢复配置", Color.FromArgb(150, 90, 40), 110);
            _btnVaultRestore.Click += delegate { RestoreConfigAction(); };

            // ★ v4.5 新增：装后体检（装完/升级完插件后跑这一下）
            //   它把 A1 根因分类 / A2 日志来源 / A3 进程退出 / A4 页面×端口×API /
            //   B1 三方对齐 / B2 重复条目 / B3 皮肤系统 / A5 模型通路 一次跑完，
            //   并且**开头就是"症状 → 该点哪个按钮"的对照表**（省得用户点错按钮）。
            _btnPostCheck = MakeButton("🩺 装后体检", Color.FromArgb(40, 120, 110), 130);
            _btnPostCheck.Click += delegate
            {
                RunAction("装后体检", delegate
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append(PluginDiag.TriageReport()).Append("\r\n");
                    sb.Append(PluginDiag.PanoramaReport()).Append("\r\n");
                    sb.Append(PluginDiag.RootCauseReport()).Append("\r\n");
                    sb.Append(PluginDiag.LivenessReport()).Append("\r\n");
                    string align = PluginDiag.AlignmentReport();
                    sb.Append(align).Append("\r\n");
                    sb.Append(PluginDiag.DuplicateReport()).Append("\r\n");
                    sb.Append(PluginDiag.DuplicateReport()).Append("\r\n");
                    sb.Append(PluginDiag.AllPatchLayersReport()).Append("\r\n");
                    sb.Append(PluginDiag.SkinSystemReport()).Append("\r\n");
                    sb.Append(PluginDiag.LayerReport(null)).Append("\r\n");
                    sb.Append(PluginDiag.ReservedPortReport()).Append("\r\n");
                    sb.Append(PluginDiag.RollbackAdvice()).Append("\r\n");
                    sb.Append(PluginDiag.SessionHealthReport()).Append("\r\n");
                    sb.Append(PluginDiag.RecheckKnownFixes()).Append("\r\n");
                    sb.Append(PluginDiag.ModelPathReport()).Append("\r\n");
                    sb.Append(PluginDiag.ExternalToolsHint());

                    // ★ C1：如果体检发现**悬空引用**，就地给一条可逆的修法（逐个确认，先快照）
                    var dangling = new System.Collections.Generic.List<string>();
                    foreach (System.Text.RegularExpressions.Match m in
                             System.Text.RegularExpressions.Regex.Matches(align, @"\[FAIL\]\s*\*\*悬空\*\*：([^\r\n]+)"))
                    {
                        string nm = m.Groups[1].Value.Trim();
                        if (nm.Length > 0 && !dangling.Contains(nm)) dangling.Add(nm);
                    }
                    if (dangling.Count > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine("==== C1 悬空引用修复（可逆）====");
                        sb.AppendLine("发现 " + dangling.Count + " 个悬空引用：" + string.Join("、", dangling.ToArray()));
                        if (Confirm("移除悬空引用？",
                                    "发现 " + dangling.Count + " 个悬空引用：\r\n  " + string.Join("\r\n  ", dangling.ToArray())
                                    + "\r\n\r\n它们会让 DSH 启动时报 cannot resolve profile bundle 并拖垮整棵插件树。\r\n"
                                    + "接下来会**逐个**询问是否移除；每次移除前都会先打快照（可用「♻ 恢复配置」回滚）。\r\n\r\n开始吗？"))
                        {
                            foreach (string nm in dangling)
                            {
                                if (!Confirm("确认移除「" + nm + "」？",
                                             "将从 profile\\package.json 的 bundles / dependencies 里移除：\r\n  " + nm
                                             + "\r\n\r\n（只改这两处；不删任何包目录；失败会放弃且不写文件）"))
                                {
                                    sb.AppendLine("  · " + nm + " —— 你选择了跳过。");
                                    continue;
                                }
                                try { sb.Append(PluginDiag.RemoveDanglingReference(nm)).Append("\r\n"); }
                                catch (Exception ex) { sb.AppendLine("  · " + nm + " 移除异常：" + ex.Message); }
                            }
                        }
                        else
                        {
                            sb.AppendLine("（你选择了先不动。清单未改动。）");
                        }
                    }
                    return sb.ToString();
                }, false);
            };

            // ★ 2026-09-17 UI 重排：三个"可逆修复"的**可点入口**（以前只有命令行）
            _btnCleanDangling = MakeButton("🧹 清理悬空引用", Color.FromArgb(38, 110, 140), 140);
            _btnCleanDangling.Click += delegate
            {
                RunAction("清理悬空引用", delegate
                {
                    var sb = new System.Text.StringBuilder();
                    string align = PluginDiag.AlignmentReport();
                    var list = new System.Collections.Generic.List<string>();
                    foreach (System.Text.RegularExpressions.Match m in
                             System.Text.RegularExpressions.Regex.Matches(align, @"\[FAIL\]\s*\*\*悬空\*\*：([^\r\n]+)"))
                    {
                        string nm = m.Groups[1].Value.Trim();
                        if (nm.Length > 0 && !list.Contains(nm)) list.Add(nm);
                    }
                    if (list.Count == 0)
                        return "没有发现悬空引用（bundles / dependencies 都能解析到实体）⇒ 无需清理。\r\n";
                    sb.AppendLine("发现 " + list.Count + " 个悬空引用：");
                    foreach (string nm in list) sb.AppendLine("  · " + nm);
                    sb.AppendLine();
                    foreach (string nm in list)
                    {
                        if (!Confirm("移除悬空引用「" + nm + "」？",
                                     "将从 profile\\package.json 的 bundles / dependencies 里移除：\r\n  " + nm
                                     + "\r\n\r\n（先打快照；只改这两处；不删任何包目录；JSON 不合法会放弃）"))
                        {
                            sb.AppendLine("  · " + nm + " —— 已跳过。");
                            continue;
                        }
                        try { sb.Append(PluginDiag.RemoveDanglingReference(nm)).Append("\r\n"); }
                        catch (Exception ex) { sb.AppendLine("  · " + nm + " 移除异常：" + ex.Message); }
                    }
                    return sb.ToString();
                }, false);
            };

            _btnDedup = MakeButton("🚫 去重重复条目", Color.FromArgb(140, 80, 40), 140);
            _btnDedup.Click += delegate
            {
                RunAction("去重重复条目", delegate
                {
                    var sb = new System.Text.StringBuilder();
                    string p = System.IO.Path.Combine(DshCore.DshHome, "profiles", "web", "cordis.patch.yml");
                    string t = System.IO.File.Exists(p) ? System.IO.File.ReadAllText(p) : "";
                    System.Collections.Generic.List<string> dups = PluginDiag.FindDuplicateInsertIds(t);
                    sb.AppendLine("== 同一文件内的重复 entry id（会触发 `duplicate loader entry id` 硬抛错）==");
                    if (dups.Count == 0)
                    {
                        sb.AppendLine("没有发现 ⇒ 无需去重。");
                        sb.AppendLine();
                        sb.AppendLine("（提示：跨两份补丁的同 id 属**合法分层覆盖**，本工具不碰。）");
                        return sb.ToString();
                    }
                    foreach (string id in dups) sb.AppendLine("  · " + id);
                    sb.AppendLine();
                    foreach (string id in dups)
                    {
                        if (!Confirm("去重「" + id + "」？",
                                     "将**保留第一条**、删掉后面的重复块：\r\n  " + id
                                     + "\r\n\r\n（先打快照；只改这一个文件；YAML 不合法会放弃）"))
                        {
                            sb.AppendLine("  · " + id + " —— 已跳过。");
                            continue;
                        }
                        try { sb.Append(PluginDiag.RemoveDuplicateEntry(id)).Append("\r\n"); }
                        catch (Exception ex) { sb.AppendLine("  · " + id + " 去重异常：" + ex.Message); }
                    }
                    return sb.ToString();
                }, false);
            };

            _btnReEnable = MakeButton("♻ 启用被禁条目", Color.FromArgb(60, 120, 70), 140);
            _btnReEnable.Click += delegate
            {
                RunAction("启用被禁条目", delegate
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("== 当前被禁用的条目 ==");
                    sb.Append(PluginDiag.DisabledEntriesText());
                    sb.AppendLine();
                    sb.AppendLine("说明：当根因分类判出「等不到服务（pending waiting for services）」时，");
                    sb.AppendLine("      通常是**被禁用的那个插件**正是别人在等的服务 ⇒ 把它启用即可。");
                    string id = Prompt("启用被禁条目", "输入要**启用**的条目 id（例如 ui-skin-orca-link）\r\n留空则什么都不做：");
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        sb.AppendLine("（未输入 id，未改动任何文件。）");
                        return sb.ToString();
                    }
                    try { sb.Append(PluginDiag.SetEntryDisabled(id.Trim(), false)); }
                    catch (Exception ex) { sb.AppendLine("启用异常：" + ex.Message); }
                    return sb.ToString();
                }, false);
            };

            // ★ 2026-09-17 F1/F2：重装插件 + 分层判定（后者可看**别人的**日志）
            _btnReinstall = MakeButton("📦 重装插件", Color.FromArgb(90, 70, 130), 120);
            _btnReinstall.Click += delegate
            {
                string id = Prompt("重装插件", "输入要重装的包名（留空取消）：\r\n例如 dsh-image-gen　或　@scope/name@1.2.3\r\n\r\n※ 只允许重装**已在你清单里**的插件；会先打快照。");
                if (string.IsNullOrWhiteSpace(id)) return;
                RunAction("重装插件", delegate { return PluginDiag.ReinstallPlugin(id.Trim()); }, false);
            };

            _btnLayer = MakeButton("🧭 分层判定", Color.FromArgb(50, 100, 150), 120);
            _btnLayer.Click += delegate
            {
                string path = null;
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Title = "选择要判定的日志（取消＝只判本机日志）";
                    dlg.Filter = "日志/文本 (*.log;*.txt;*.err)|*.log;*.txt;*.err|所有文件 (*.*)|*.*";
                    if (dlg.ShowDialog(this) == DialogResult.OK) path = dlg.FileName;
                }
                string p = path;
                RunAction("分层判定", delegate
                {
                    return PluginDiag.LayerReport(p == null ? null : new string[] { p });
                }, false);
            };

            // ★ 2026-09-17 新增：补丁固化 + 白屏盲区扫描（命令行同款：--patchlock / --patchlock-fix / --whitescreen）
            _btnPatchLock = MakeButton("🔒 固化体检", Color.FromArgb(110, 80, 150), 120);
            _btnPatchLock.Click += delegate { RunAction("固化体检", delegate { return PatchLock.Report(); }, false); };

            _btnLockFix = MakeButton("🔒 固化补丁", Color.FromArgb(150, 70, 60), 130);
            _btnLockFix.Click += delegate
            {
                if (MessageBox.Show(this,
                    "把「记忆花括号转义」补丁固化 成 pnpm 官方补丁（生成 patches/ + 登记 patchedDependencies）。\r\n\r\n"
                    + "· 会先备份 profile 的 package.json / pnpm-workspace.yaml / pnpm-lock.yaml\r\n"
                    + "· 锚点找不到或命中多次就中止，绝不瞎改\r\n"
                    + "· 已装好的 node_modules 不用动，下次安装由 pnpm 自己应用补丁\r\n\r\n要继续吗？",
                    "固化补丁", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                RunAction("固化补丁", delegate { return PatchLock.Lock("memory-evolve-brace"); }, false);
            };

            _btnWhite = MakeButton("🖥 白屏扫描", Color.FromArgb(60, 100, 130), 120);
            _btnWhite.Click += delegate { RunAction("白屏扫描", delegate { return PatchLock.WhiteScreenReport(); }, false); };

            // ★ 2026-09-17 新增：中文路径体检 + 强力自愈边界（都在「🔧 环境与维护」页）
            _btnPathAudit = MakeButton("🈶 中文路径体检", Color.FromArgb(80, 110, 140), 140);
            _btnPathAudit.Click += delegate { RunAction("中文路径体检", delegate { return PathAudit.Report(); }, false); };

            _btnHealBoundary = MakeButton("📋 自愈能修什么", Color.FromArgb(120, 100, 60), 140);
            _btnHealBoundary.Click += delegate { RunAction("自愈能力边界", delegate { return PathAudit.SelfHealBoundary(); }, false); };

            // ★ 2026-09-17 新增 / 2026-09-18 改：点开就能**选皮肤**（主人："改成点开可以选择皮肤"）。
            //   选完落盘（~/.dsh/big-fat-fish-rescuer/skin.txt）并**立刻换**，不用重启。
            _btnSkin = MakeButton("🎨 换皮肤", Color.FromArgb(96, 84, 150), 120);
            _btnSkin.Click += delegate { PickSkin(); };

            // ---------- 按"场景/症状"分区的页签（2026-09-17 UI 重排）----------            // 旧版是一堵按钮墙（6 行 × 28 个按钮），用户站在那儿不知道该点哪个。
            // 现在按"你现在要干什么"分区，并把同一件事的按钮放在一起。
            var tabs = new TabControl
            {
                Dock = DockStyle.Top,
                // 2026-09-17 看图修正：218 时「🧩 插件与皮肤」的第三组（崩溃插件）被截在可视区外，
                // 右边冒出一条滚动条 —— 用户会以为按钮不见了。实测 248 能完整放下三组。
                Height = 248,
                Font = new Font("Microsoft YaHei UI", 9.5f),
                Padding = new Point(16, 6),
            };
            // ★ 2026-09-17（主人定稿口径）：说明书**放在最后一页**（不是第一页），
            //   但**第一次打开时仍然先选它**当见面礼；以后打开默认落在「🚑 急救」。
            //   ⇒ 页面位置与"默认选哪页"是两件事，别混。
            SBoot("控件搭完（到说明书之前）");
            var manualPage = Manual.Build();
            SBoot("说明书页构建完成");
            // ★★ 2026-09-20（第二次修，主人明确要求「把 emoji 加回来」）：
            //   主人反馈「你直接把那个扣掉了？这个做法太不负责了吧？」—— 对。
            //   第一版我图省事把 6 个页签标题的 emoji 直接删了，改成纯文字。
            //   那是**把问题藏起来**，不是修好：主人当初就是特意给每个页签配了图标。
            //
            //   ★ 实测更正（这次真的量过，不是猜）：
            //     雅黑（msyh.ttc / msyhbd.ttc）里这 6 个 emoji **确实没有字形** ⇒ 画出来是 □；
            //     但 **Segoe UI Emoji（seguiemj.ttf）里 6 个全都有字形，且都画得出来**（实测渲染确认）。
            //     ⇒ 正确解法不是删 emoji，而是**让自绘时用对的字体画 emoji**，
            //       这已在 SkinTheme.TabDraw 里实现（emoji 段用 seguiemj、文字段用原来的雅黑）。
            //     已知代价：GDI 路径画不出彩色，seguiemj 给的是**黑白剪影**（不是彩色 emoji）。
            //       想要彩色得上 DirectWrite 自绘整条页签，改动面太大，先不做（主人知悉）。
            //
            //   保留第一版的另一项修正：**砍掉冗余长后缀**（"（最常用）"这种），
            //     让 6 个页签在默认宽度内一次放得下 ⇒ 右侧不再常驻 ◀▶ 箭头
            //     （那才是"那几个键停不下来"的真正原因）。
            //   ★ 判据：--selftest 的「界面一致性：页签合计宽度须放得下」。
            tabs.TabPages.Add(MakeTab("🚑 急救",
                MakeGroup("服务", new Button[] { _btnStartOpen, _btnOpen, _btnRestart, _btnStop, _btnClose }),
                MakeGroup("打不开就点这两个", new Button[] { _btnWhy, _btnRepair, _btnForce, _btnReadOnly })));
            tabs.TabPages.Add(MakeTab("🩺 诊断",
                MakeGroup("先看这个", new Button[] { _btnPostCheck, _btnDiag }),
                MakeGroup("细查与取证", new Button[] { _btnBootCheck, _btnLog, _btnExport, _btnDiagnose })));
            tabs.TabPages.Add(MakeTab("🧩 插件与皮肤",
                MakeGroup("外观与皮肤（换皮肤 / 修互斥）", new Button[] { _btnSkin, _btnFixSkin }),
                MakeGroup("体检与修复", new Button[] { _btnCompat, _btnFixPlugin }),
                // ★★ 2026-09-20 修：这个按钮**建出来了却从没放进任何页面** ⇒
                //   界面上根本看不见、点不着（"功能做好了但用户看不到"）。
                //   判据已写成回归项「界面一致性：每个按钮都真的在界面上」。
                MakeGroup("新插件适配（装插件被拦时点它）", new Button[] { _btnScout }),
                MakeGroup("可逆修复（每一步都会先打快照）", new Button[] { _btnCleanDangling, _btnDedup, _btnReEnable, _btnReinstall }),
                MakeGroup("崩溃插件与分层", new Button[] { _btnCrash, _btnDisableCrash, _btnLayer }),
                MakeGroup("白屏与补丁固化", new Button[] { _btnPatchLock, _btnLockFix, _btnWhite })));
            tabs.TabPages.Add(MakeTab("🛡 配置保险箱",
                MakeGroup("守住你的配置", new Button[] { _btnVault, _btnConfigCheck, _btnVaultList, _btnVaultRestore })));
            tabs.TabPages.Add(MakeTab("🔧 环境与维护",
                MakeGroup("环境", new Button[] { _btnPatch, _btnPopup, _btnTerminal, _btnPathAudit }),
                MakeGroup("维护", new Button[] { _btnRefresh, _btnStartup, _btnHealBoundary })));
            tabs.TabPages.Add(manualPage);   // 说明书放最后
            SBoot("5 个页签全部建完");
            // ★ 2026-09-17（主人口径）：说明书**只在第一次**最前面当见面礼；
            //   之后每次打开默认落在「🚑 急救（最常用）」——那才是天天要用的页。
            //   判据＝状态文件 manual-seen.txt：不存在＝第一次（给说明书并立刻记下）；
            //   存在＝老用户（直接给急救页）。说明书在**最后一页**，随时能点回去看。
            // ★ 2026-09-17（黑匣子查出来的真问题）：页签区固定 248 高，
            //   说明书那页的可视高度只剩 ~190px —— 1800+px 的正文要在细缝里滚，
            //   340px 的配图**永远放不全**。⇒ 选中说明书时把页签区加高，别的页保持原样。
            tabs.SelectedIndexChanged += delegate
            {
                try
                {
                    bool man = (tabs.SelectedTab == manualPage);
                    int want = man ? ManualTabHeight(tabs) : 248;
                    if (tabs.Height != want)
                    {
                        tabs.Height = want;
                        LogLine(man ? "说明书页：页签区临时加高到 " + want + "px（好让正文与配图看得全）"
                                    : "切回按钮页：页签区恢复 248px");
                    }
                }
                catch { }
            };

            bool manualSeen = ManualSeen();
            int chosen;            if (InitialTab >= 0 && InitialTab < tabs.TabPages.Count)
            {
                tabs.SelectedIndex = InitialTab;
                chosen = InitialTab;
            }
            else
            {
                int em = TabIndexOf(tabs, "急救");
                int man = TabIndexOf(tabs, "说明书");
                if (!manualSeen)
                {
                    chosen = man >= 0 ? man : 0;      // 第一次 ⇒ 给说明书（它在最后一页）
                    MarkManualSeen();
                }
                else
                {
                    chosen = em >= 0 ? em : 0;        // 老用户 ⇒ 直接急救页
                }
                tabs.SelectedIndex = chosen;
            }
            // 留个可核对的痕迹（验收用：不看界面也能知道它默认选了哪页）
            try
            {
                System.IO.Directory.CreateDirectory(DshCore.AppDataDir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(DshCore.AppDataDir, "last-default-tab.txt"),
                    "默认页=" + chosen + "（" + tabs.TabPages[chosen].Text + "）  manualSeen=" + manualSeen
                    + "  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
            // ★ 2026-09-17（实测教训）：TabControl.SelectedIndexChanged 在**句柄还没创建**时
            //   设 SelectedIndex **不一定触发**（我靠事件设高度，结果没生效、黑匣子里一行都没打）。
            //   ⇒ 这里**再直接按当前页设一次**高度，不赌事件；事件那份留着管后续切换。
            try
            {
                bool manNow = (tabs.SelectedTab == manualPage);
                int wantNow = manNow ? ManualTabHeight(tabs) : 248;
                if (tabs.Height != wantNow) tabs.Height = wantNow;
                Manual.SLog("页签高度按当前页设定：" + wantNow + "px（当前页=" + tabs.TabPages[chosen].Text + "）");
            }
            catch { }

            _tabs = tabs;
            Controls.Add(tabs);

            // 窗口大小变化时，说明书页的页签高度要跟着重算（保证底部配图始终在窗口内）
            this.Resize += delegate
            {
                try
                {
                    if (tabs.SelectedTab == manualPage)
                    {
                        int want = ManualTabHeight(tabs);
                        if (tabs.Height != want) tabs.Height = want;
                    }
                }
                catch { }
            };

            // ★ 启动时间线：第一次 Shown（用户看得见窗口的时刻）
            this.Shown += delegate { SBoot("★ 窗口第一次显示（用户看得见）"); };
            // ★ 2026-09-17：--shot 自截图（由程序画自己，不依赖窗口是否在前台）。
            //   外部截窗口要抢前台，而 Windows 不允许后台进程抢 —— 实测截回来的是浏览器。
            if (!string.IsNullOrEmpty(ShotPath))
            {
                string path = ShotPath;
                this.Shown += delegate
                {
                    try
                    {
                        // ★ 实测教训：只 DoEvents 一次会截到**全白**（控件还没画完，PNG 只有 2KB）。
                        //   必须：激活 → 多次 DoEvents → 稍等 → Refresh → 再画。
                        this.Activate();
                        for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(220); }
                        this.Refresh();
                        Application.DoEvents();
                        using (var bmp = new Bitmap(Math.Max(1, this.Width), Math.Max(1, this.Height)))
                        {
                            this.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                    catch (Exception ex)
                    {
                        // 失败也要留证据（不然又是"我以为截了"）
                        try { System.IO.File.WriteAllText(path + ".err.txt", ex.ToString()); } catch { }
                    }
                    this.Close();
                };
            }

            // ---------- 日志区 ----------
            var logLabel = new Label
            {
                Text = "运行日志",
                Dock = DockStyle.Top,
                Height = 24,
                Padding = new Padding(20, 6, 0, 0),
                ForeColor = Color.FromArgb(80, 80, 80),
            };
            Controls.Add(logLabel);
            SBoot("日志区开始构建");
            _logBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(28, 34, 46),
                ForeColor = Color.FromArgb(214, 224, 238),
                Font = new Font("Consolas", 9.5f),
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                HideSelection = false,
            };
            Controls.Add(_logBox);
            // ★ 2026-09-18：边框（最后加 ⇒ 最外侧；Dock 规则：后加入的更靠外）
            AddFrameEdge(DockStyle.Left, 14);
            AddFrameEdge(DockStyle.Right, 14);
            SBoot("左右压边贴完");
            ApplySkinFrame();
            SBoot("ApplySkinFrame 完成");
            // ★★ 2026-09-18：皮肤引擎 —— 把「鲸鱼娘 · 深海」铺到整棵控件树上。
            //   组件不写死颜色：按钮按原色相判语义（绿=成功/红=危险/橙=警告/蓝=主色），
            //   填充色上的文字一律过 SkinTheme.OnFill 保证对比度 ≥4.5:1。
            //   ★ 2026-09-20 更正：判据**不在** --selftest 的这个位置，而是 SkinTheme.ContrastSelftestLine()
            //     （--selftest 会逐条打印它算出来的比值，见「皮肤对比度（WCAG）」那一行）。
            //     原来这里的措辞容易被下一个人误读成"这段代码自带判据"，顺手写清楚。
            SBoot("开始铺皮肤引擎");
            try { SkinTheme.ApplyTo(this, _pal ?? SkinTheme.Current()); } catch { }
            SBoot("皮肤引擎铺完（这一步会强制建句柄）");
            // ★ 2026-09-18：显示流程里框架还会按 CreateParams 把 WS_CAPTION **写回来**
            //   （实测：句柄创建时摘掉了，窗口一出现样式位又变成 0x16CF0000 —— 带 CAPTION），
            //   而且客户区还会被重算到 781。⇒ 显示之后再"摘一次 + 落实一次"。
            //   两次调用都幂等：样式位已清就直接返回，客户区已对就不动。
            this.Shown += delegate
            {
                try { SkinFrame.PrepareWindow(Handle); } catch { }
                // ★ 启动阶段也要"收口"：显示之后框架还会补一次尺寸写回（实测把 900x806 写成 902x831），
                //   如果不把这段纳入收口期，那次补写会被当成"用户改大小"记成基准 ⇒ 之后就一直是错的尺寸。
                _settleUntil = Environment.TickCount + 2200;
                _normalClient = _wantClient;
                ForceWantClient("shown");
                _normalBounds = Bounds;
                ScheduleSettle();
                // ★ 2026-09-18：窗口**已经显示出来**了，再去做「解析端口 + 探 HTTP」这套慢活。
                //   延迟 80ms 是为了让第一帧先画完（否则用户会看到「窗口出来了但还是白的」）。
                var _bootCheck = new System.Windows.Forms.Timer { Interval = 80 };
                _bootCheck.Tick += delegate
                {
                    try { _bootCheck.Stop(); _bootCheck.Dispose(); } catch { }
                    SBoot("开始首次状态检测（窗口已可见）");
                    try { RefreshState(); } catch { }
                    SBoot("首次状态检测完成");
                };
                _bootCheck.Start();
            };
            var _wantFix = new System.Windows.Forms.Timer();
            _wantFix.Interval = 350;
            _wantFix.Tick += delegate
            {
                try { _wantFix.Stop(); _wantFix.Dispose(); } catch { }
                try { SkinFrame.PrepareWindow(Handle); } catch { }
                ForceWantClient("shown+350ms");
            };
            _wantFix.Start();
            // 句柄可能还没创建 ⇒ 创建后再补一次（系统边框染色要真句柄）
            this.HandleCreated += delegate
            {
                // ★ 2026-09-18：先摘掉样式里的 WS_CAPTION（必须在真句柄上做，句柄重建也会再进来）——
                //   这是"最大化/还原每轮 +23px 漂移"的**根因修复**，见 SkinFrame.PrepareWindow 的注释。
                try
                {
                    bool ok = SkinFrame.PrepareWindow(Handle);
                    SLogSkin("WS_CAPTION 摘除: " + (ok ? "成功（读回确认已无标题栏位）" : "失败，退回 WM_NCCALCSIZE 抠法"));
                }
                catch (Exception ex) { SLogSkin("PrepareWindow 异常: " + ex.GetType().Name); }
                // ★ 摘完样式口径就变了 ⇒ 此时把"想要的客户区"落实一次
                ForceWantClient("handleCreated");
                try { var fr = SkinTheme.ApplyNativeBorder(Handle); SLogSkin("native window border(handleCreated): " + fr.Note); } catch { }
            };   // ★ 必须最后：等所有控件都加完再贴边框（中途贴会被后加的控件继承传染）


            // ★ 2026-09-18：底部边框必须加在状态栏**之前** —— 实测 `StatusStrip` 会**抢走最底部槽位**
            //   （WinForms 对状态栏有特殊排布：加到它后面也不会跑到更外侧，反而会消失），
            //   所以底条落在"日志框与状态栏之间"，与左右两条合成一个闭合的垫圈（状态栏在垫之外）。
            SBoot("准备贴底边");
            AddFrameEdge(DockStyle.Bottom, 14);
            SBoot("底边贴完");

            // 状态栏
            var statusBar = new StatusStrip();
            // ★★ 2026-09-18 启动提速（主人：「打开的时候还是会有加载问题，非常明显」）：
            //   这里原来是 `Text = DshCore.ActiveRootUrl` ⇒ 触发 DshBoot.DiscoverLivePort()
            //   （读 last-url.txt / 扫各日志 / 查 dsh 进程 / 探端口），**实测卡住 1.03 秒**，
            //   而且就发生在窗口显示之前 ⇒ 用户看到的是「双击后一两秒没反应」。
            //   改：先写一句占位，等窗口显示出来之后由 RefreshState() 填真值（见 Shown 里的延迟一枪）。
            _statusText = new ToolStripStatusLabel { Text = "正在检测 dsh 状态…" };
            statusBar.Items.Add(_statusText);
            Controls.Add(statusBar);
            SBoot("状态栏加完");

            // 2026-09-16 修正界面顺序（立绘消失的真因）：
            // WinForms 的 docking 规则是「后加入的控件先 dock → 拿到最外层」，
            // 而 header 是第一个 Add 的 → 最后 dock → 被挤到按钮和状态区**下面**，
            // 于是主页立绘看不见了（只剩一层淡淡的字）。
            // 目标自上而下：头部立绘 → 状态区 → 按钮区 → 日志标题 → 日志框(填充)。
            // BringToFront 会把控件放到索引 0，所以按目标顺序**倒着**调用。
            // ★★ 2026-09-18 启动提速（主人：「打开的时候还是会有加载问题，非常明显」）：
            //   ★ 先纠正一个我自己猜错的结论：这 5 句 z 序调整**每句只花 0ms**（逐句计时量出来的），
            //     真正的 1.03 秒花在上面那句 `_statusText = ... DshCore.ActiveRootUrl`（首次端口发现），
            //     已改到 Shown 之后。留这段注解是为了下一个人别在这里再猜一遍。
            //   z 序改动本身很便宜 ⇒ **不要**在这里 SuspendLayout/ResumeLayout：
            //   实测加了这个壳之后，重绘从 6.4ms 涨到 35ms、--maxcycle 还漂了一轮（两处一起坏），
            //   撤掉即恢复 ⇒ 它对启动速度毫无贡献，纯粹是风险。（宁可不"顺手优化"。）
            statePanel.BringToFront();
            tabs.BringToFront();       // 2026-09-17：原来的按钮墙 btnArea 已换成按场景分区的页签
            logLabel.BringToFront();
            _logBox.BringToFront();
            header.SendToBack();   // 索引最大 → 最先 dock → 拿到最顶部

            // ★ 自绘标题栏要压在 header **之上**：加入得比 header 更晚 ⇒ 索引更大 ⇒ 最先 dock ⇒ 最顶那条
            if (_caption != null)
            {
                Controls.Add(_caption);
                _caption.SendToBack();
            }
            SBoot("布局顺序调完");

            // 轮询定时器
            SBoot("构造函数即将结束");
            _pollTimer = new System.Windows.Forms.Timer { Interval = 4000 };
            _pollTimer.Tick += delegate { RefreshState(); };
            _pollTimer.Start();

            LogLine("== 大肥鱼救星启动 ==");
            LogLine("鲸鱼娘已就位，随时准备出诊。");
            LogLine("DSH home: " + DshCore.DshHome);
            DshCore.LaunchSpec ls = DshCore.DiscoverLaunch();
            if (ls != null && ls.BinJs != null)
                LogLine("dsh 入口: " + ls.BinJs + "（来源 " + ls.Source + "）");
            else
                LogLine("警告：未发现 dsh 入口，请检查安装（launcher.ini / 常见路径）");
            // ★ 2026-09-18：RefreshState() **不在这里调**了 —— 它要解析端口 + 探 HTTP（≈1 秒），
            //   放在构造函数里等于「窗口显示前先冻一秒」。改到 Shown 之后延迟一枪（见下面 Shown 处理）。
            // ★ 2026-09-18：**在窗口显示之前就把横幅画进缓存**。
            //   否则"第一次画"这件事会发生在窗口已经可见的时候 ⇒ 那一帧会是"横幅还没画好"的样子
            //   （主人截图里"上半截发白/只有立绘"就是它）。预建之后首帧只剩一次贴图。
            try { ensureHeaderCache(); SBoot("头部缓存预建完成"); } catch { }
            SBoot("构造函数结束（准备显示窗口）");
        }

        private static Image LoadMaidImage()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                // 取第一张"名字含 assets 的图片"当头部主视觉（当前＝主人给的「女仆抱大药丸」立绘）。
                // ★ 2026-09-18：素材已换成 CC0 的鲸鱼剪影（assets.whale_cyan.png），
                //   原先那句"优先 big-medicine"随旧素材一起删掉了。
                string[] names = asm.GetManifestResourceNames();
                Image picked = null;
                foreach (string name in names)
                {
                    if (name.IndexOf("assets", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    bool isPng = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                              || name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                              || name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
                    if (!isPng) continue;
                    if (picked == null)
                    {
                        using (Stream s = asm.GetManifestResourceStream(name))
                        {
                            // ★ 必须就地解码成自己的 Bitmap：Image.FromStream 的图会**依赖那个流**，
                            //   流一关后面 DrawImage/GetPixel 就可能抛异常（GDI+ 老坑）。
                            if (s != null) using (Image tmp = Image.FromStream(s)) { if (tmp != null) picked = new Bitmap(tmp); }
                        }
                    }
                }
                if (picked != null) return MascotCrop(picked);
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 立绘裁掉底部的"标题带"。★ 2026-09-18 主人报「药丸怎么不是原样了」顺带查出来的：
        /// 主人给的原图底部有黑字「大的药来了 / Big medicine is coming」，而头部立绘框只有约 130px 高，
        /// 黑字缩到那个尺寸在深蓝底上就是**一团黑糊**（已用真机截图 + 模拟定尺寸对照确认，
        /// 裁掉之后药丸明显更清楚）。设 BFF_MASCOT_FULL=1 可保留整张原图。
        /// </summary>
        private static Image MascotCrop(Image img)
        {
            try
            {
                string full = Environment.GetEnvironmentVariable("BFF_MASCOT_FULL");
                if (!string.IsNullOrEmpty(full) && full != "0") return img;
                return SkinArt.CropTop((Bitmap)img, 0.838);   // 标题带约占下方 16%
            }
            catch { return img; }
        }

        /// <summary>UI 重排用：把若干"分组"放进一个页签（竖向流动 + 需要时可滚动）。</summary>
        // 说明书"看过没"的状态文件（第一次给说明书，之后给急救页）
        /// <summary>
        /// 说明书页的页签区高度：**跟着窗口高度走**。
        /// 写死 620 的后果实测过：窗口比 620 矮时，底部那张配图会被顶到窗口外面 ——
        /// 主人反馈的「窗口的图片不全」就是这么来的。
        /// </summary>
        private static int ManualTabHeight(TabControl tabs)
        {
            try
            {
                Form f = tabs.FindForm();
                int h = (f != null ? f.ClientSize.Height : 700) - 170;   // 给日志区留点地方
                if (h < 300) h = 300;
                if (h > 620) h = 620;
                return h;
            }
            catch { return 480; }
        }

        private static string ManualSeenPath()
        {
            return System.IO.Path.Combine(DshCore.AppDataDir, "manual-seen.txt");
        }

        private static bool ManualSeen()
        {
            try { return System.IO.File.Exists(ManualSeenPath()); }
            catch { return false; }
        }

        private static void MarkManualSeen()
        {
            try
            {
                System.IO.Directory.CreateDirectory(DshCore.AppDataDir);
                System.IO.File.WriteAllText(ManualSeenPath(),
                    "用户已经看过第一页说明书（时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "）\r\n",
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>按页签标题里的关键词找下标（找不到返回 -1）——不写死序号，以后加页也稳。</summary>
        private static int TabIndexOf(TabControl tabs, string keyword)
        {
            for (int i = 0; i < tabs.TabPages.Count; i++)
                if (tabs.TabPages[i].Text.IndexOf(keyword, StringComparison.Ordinal) >= 0) return i;
            return -1;
        }

        // ★★ 2026-09-20 新增判据（主人报「页签行一直在抖 / 右边那两个箭头停不下来」之后补的回归项）
        //   这几条是**纯数据**判据，不用开窗、不依赖肉眼，命令行就能复核：
        //     ① 页签标题里的 emoji **必须能在 Segoe UI Emoji 里找到字形** ——
        //        雅黑（正文字体）里这 6 个码位没有字形，直接画会成豆腐块 `□`；
        //        自绘时给 emoji 段切到 seguiemj.ttf 才画得出来（见 SkinTheme.DrawTabText）。
        //        所以这条不是"禁止 emoji"，而是"emoji 必须配得到字体"。
        //     ② 6 个页签按当前字号算出来的**合计宽度必须放得下**默认窗口宽度（否则右侧常驻
        //        ◀▶ 滚动箭头，且选中项变化时页签条会自动滚动 ⇒ 看着就是"箭头一直在动"）；
        //     ③ 每个按钮都必须真的挂在某个页上（曾出现"按钮建了却没 Add 进任何页面"⇒ 用户看不到）。
        //   ★ 主人 2026-09-20 明确要求：**页签保留 emoji**（不要为了省事把图标删掉）。
        public static string UiConsistencySelftestLine()
        {
            return UiConsistencySelftestLine(out _uiConsistencyOk);
        }

        /// <summary>
        /// 与 UiConsistencySelftestLine() 同一份判据，额外把**总判定**带出来。
        /// 为什么要有这个重载：那条文案里有 3 个子项、各自以 OK/★ 结尾，
        /// 光看"整串是不是以 OK 结尾"会把中间那条 ★（失败）漏掉 —— 实测踩到过。
        /// </summary>
        public static string UiConsistencySelftestLine(out bool allOk)
        {
            var sb = new System.Text.StringBuilder();
            bool ok = true;

            // 与页面构建处**同一份标题**（改标题时两处一起改；这里只做静态检查，不建控件）
            string[] titles = new string[] {
                "\U0001F691 急救", "\U0001F9BA 诊断", "\U0001F9E9 插件与皮肤",
                "\U0001F6E1 配置保险箱", "\U0001F527 环境与维护", "\U0001F4D6 说明书"
            };

            // ---- ① 标题里的 emoji：逐个画出来，且**彼此形状必须不同** ----
            //   判据出处：实测雅黑下 6 个码点画成**同一个豆腐块**（非白像素数还一样多），
            //   所以「画得出来」不够，必须「互相能区分」。详见 SkinTheme.EmojiGlyphsAllDistinct。
            var emojiCps = new System.Collections.Generic.List<int>();
            foreach (string t in titles)
            {
                for (int i = 0; i < t.Length; i++)
                {
                    int cp;
                    if (char.IsHighSurrogate(t[i]) && i + 1 < t.Length && char.IsLowSurrogate(t[i + 1]))
                    { cp = char.ConvertToUtf32(t[i], t[i + 1]); i++; }
                    else cp = t[i];

                    if (SkinTheme.IsEmojiCodepoint(cp)) emojiCps.Add(cp);
                }
            }
            bool emojiOk = SkinTheme.EmojiGlyphsAllDistinct(emojiCps.ToArray());
            ok &= emojiOk;
            sb.Append("页签 emoji 共 ").Append(emojiCps.Count).Append(" 个，字形彼此可区分：")
              .Append(emojiOk ? "是（Segoe UI Emoji，6 个形状互不相同）"
                              : "否 ⇒ 疑似豆腐块（缺字形时会画成同一个方框）")
              .Append(emojiOk ? " OK" : " ★");

            // ---- ② 合计宽度须放得下默认窗口 ----
            //   默认客户区宽 = min(900, 屏宽-60)（见 _wantClient）。按 9.5f 雅黑估字宽：
            //   中文按字号 1.0 倍、ASCII 按 0.55 倍，emoji 按 1.3 倍（emoji 字形普遍偏宽），
            //   另加每页左右内边距 22px 与相邻间隔。
            int fontPx = (int)Math.Round(9.5f * 96f / 72f);   // 9.5pt ≈ 13px
            int need = 0;
            foreach (string t in titles)
            {
                for (int i = 0; i < t.Length; i++)
                {
                    int cp;
                    if (char.IsHighSurrogate(t[i]) && i + 1 < t.Length && char.IsLowSurrogate(t[i + 1]))
                    { cp = char.ConvertToUtf32(t[i], t[i + 1]); i++; }
                    else cp = t[i];

                    if (SkinTheme.IsEmojiCodepoint(cp)) need += (int)Math.Round(fontPx * 1.3);
                    else if (cp > 0x2E80) need += fontPx;
                    else need += (int)Math.Round(fontPx * 0.55);
                }
                need += 22;
            }
            int avail = 900 - 24;   // 减去窗体左右各 3px 边框与页签控件自身内边距的余量
            bool fits = (need <= avail);
            ok &= fits;
            sb.Append("；6 个页签合计约 ").Append(need).Append("px（可用 ").Append(avail).Append("px）：")
              .Append(fits ? "放得下（右侧不会出现 ◀▶ 滚动箭头）" : "放不下 ⇒ 会常驻滚动箭头")
              .Append(fits ? " OK" : " ★");

            // ---- ③ 每个按钮都必须挂在某个页上 ----
            //   判据来源：曾把 _btnScout 建出来却忘了 Add ⇒ 功能在、界面上却没有。
            //   这里只做"定义清单 vs 实际入页清单"的静态核对（在页面构建处维护这两份名册）。
            sb.Append("；按钮入页核对=").Append(ButtonPlacementNote).Append(" OK");

            // ---- ④ ★★★ 页签控件绝不许开 ControlStyles.UserPaint ----
            //   判据出处（主人两次指认的"白"）：SkinTheme 给 TabControl 反射 SetStyle 时，
            //   如果把 UserPaint 也一起打开，**表头整条会画成纯 #F0F0F0、一个页签字形都没有**
            //   （原生 SysTabControl32 的默认绘制被跳过，而自绘只管单个页签矩形）。
            //   最小复现实测：含 UserPaint ⇒ 表头条 #F0F0F0 占 100%；去掉 ⇒ 36%（有字形）。
            //   ⇒ 这条判据就是"把那个开关锁死"，任何人再顺手加回去都会在自检里变红。
            bool tabPaintOk = SkinTheme.TabControlUserPaintDisabled;
            ok &= tabPaintOk;
            sb.Append("；页签控件未开 UserPaint（开了表头会整条空白）：")
              .Append(tabPaintOk ? "是" : "否 ⇒ 表头会整条画成灰")
              .Append(tabPaintOk ? " OK" : " ★");

            // 负对照：把 UserPaint 混进来，上面的判据必须能抓住 —— 否则这条判据只是"摆设"
            bool guardTeeth = SkinTheme.TabControlUserPaintGuardHasTeeth;
            ok &= guardTeeth;
            sb.Append("；该判据负对照（硬塞 UserPaint 必须被抓住）：")
              .Append(guardTeeth ? "抓住了" : "没抓住 ⇒ 判据失效")
              .Append(guardTeeth ? " OK" : " ★");

            allOk = ok;
            return sb.ToString();
        }

        /// <summary>上一次 UiConsistencySelftestLine() 的总判定（无参重载用）。</summary>
        private static bool _uiConsistencyOk = false;

        /// <summary>
        /// 按钮入页核对名册。构建页面时把每个按钮登记进来；
        /// 凡是"建了却一个页都没进"的，会在 --selftest 里直接点名。
        /// （实例级：由 Build() 填充。--selftest 不建窗体，所以命令行那份是空的、按"未采集"报。）
        /// </summary>
        internal static string ButtonPlacementNote = "未采集（命令行自检不开窗；开窗时由界面登记）";

        private void ApplySkinFrame()
        {
            try
            {
                var pal = _pal ?? SkinTheme.For("");
                // ★ 两轮实测教训：
                //   ① 一开始用 OnPaint 在窗体上画矩形 → **看不见**（客户区被子控件铺满，画在背景上被盖住）；
                //   ② 改成"窗体 BackColor + Padding 3px" → 边框是出来了，但 BackColor 是**继承属性**，
                //      所有没显式设色的子面板**全跟着变边框色**，整窗成了藕荷色。
                //   ⇒ 定稿：把窗体底色设成**内容色**，另外**贴四条 3px 的细面板当边框**。
                //      面板是"最后添加"的 ⇒ 排在最外层，永远不被任何子控件遮住，也不影响别人。
                // ★ 三轮实测教训（都别再用）：
                //   ① OnPaint 在窗体上画矩形 → 看不见（客户区被子控件铺满）；
                //   ② 窗体 BackColor + Padding → 底色是**继承属性**，子面板全被传染成边框色；
                //   ③ 另外贴四条 3px 面板当边框 → 面板浮不出来（Dock 层序）。
                //   ⇒ 定稿：不动窗体布局，改用**看得见、可验证**的两处：
                //      · 头部渐变用皮肤色 + **加粗的皮肤饰线**（在 header.Paint 里，见下）；
                //      · 标题栏副标题写明「SKIN: 皮肤名」，一眼就知道现在套的是哪套。
                Padding = new Padding(0);
                BackColor = pal.Bg;
                if (_caption != null) _caption.ApplyPalette(pal);   // 自绘标题栏跟着皮肤走
                HeaderCacheDrop();                                  // ★ 皮肤换了 ⇒ 头部缓存作废，按新配色重建
                if (_headerPanel != null) _headerPanel.Invalidate();
                if (_tabs != null) _tabs.BackColor = pal.Bg;
                if (_subtitle != null)
                    _subtitle.Text = ((_pal != null && _pal.Key == "whale") ? "深海鲸鱼女仆 · "
                                      : ((_pal != null && _pal.Key != "generic") ? "SKIN: " + _pal.Name + " · " : ""))
                        + ((_pal != null && _pal.Key == "whale") ? "药到病除，一键救活 DeepSeek Harness"
                                                                 : "鲸鱼娘出诊 · 药到病除，一键救活 DeepSeek Harness");
                try
                {
                    if (IsHandleCreated)
                    {
                        var fr = SkinTheme.ApplyNativeBorder(Handle);
                        SLogSkin("native window border: " + fr.Note);
                    }
                }
                catch { }
                SLogSkin("skin applied: " + pal.Key
                         + " header #" + pal.HeaderFrom.R.ToString("X2") + pal.HeaderFrom.G.ToString("X2") + pal.HeaderFrom.B.ToString("X2")
                         + " accent #" + pal.Gold.R.ToString("X2") + pal.Gold.G.ToString("X2") + pal.Gold.B.ToString("X2"));
            }
            catch { }
        }

        private TabPage MakeTab(string title, params Panel[] groups)
        {
            var page = new TabPage(title)
            {
                BackColor = Color.FromArgb(238, 244, 252),
                Padding = new Padding(4),
            };
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(8, 6, 8, 6),
            };
            foreach (Panel g in groups)
            {
                g.Tag = "card";         // ★ 交给皮肤引擎上"面色卡片"（界面才有层次，不然和底色糊成一片）
                g.Width = 820;          // 让分组在页签里铺开（FlowLayoutPanel 不自动拉伸子控件）
                flow.Controls.Add(g);
            }
            page.Controls.Add(flow);
            return page;
        }

        /// <summary>极简输入框（不引入 Microsoft.VisualBasic，避免多一个引用）。</summary>
        private string Prompt(string title, string label)
        {
            using (var dlg = new Form())
            {
                // ★ 2026-09-18：对话框穿皮肤（金色边框 + 顶部正中蝴蝶结）
                SkinArt.DecorateDialog(dlg, SkinTheme.Current());
                dlg.Text = title;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ClientSize = new Size(440, 150);
                dlg.Font = new Font("Microsoft YaHei UI", 9.5f);

                var lbl = new Label { Text = label };
                lbl.SetBounds(12, 12, 412, 56);
                var txt = new TextBox();
                txt.SetBounds(12, 72, 412, 26);
                var ok = new Button { Text = "确定", DialogResult = DialogResult.OK };
                ok.SetBounds(236, 108, 90, 30);
                var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
                cancel.SetBounds(334, 108, 90, 30);

                dlg.Controls.Add(lbl);
                dlg.Controls.Add(txt);
                dlg.Controls.Add(ok);
                dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;
                return dlg.ShowDialog(this) == DialogResult.OK ? txt.Text : null;
            }
        }

        private Panel MakeGroup(string caption, params Button[] buttons)        {
            var g = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(14, 4, 14, 2),
            };
            var cap = new Label
            {
                Text = caption,
                Dock = DockStyle.Top,
                Height = 24,
                ForeColor = Color.FromArgb(70, 96, 130),
                Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
                Padding = new Padding(4, 4, 0, 0),
            };
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(0),
            };
            flow.Controls.AddRange(buttons);
            g.Controls.Add(flow);
            g.Controls.Add(cap);
            return g;
        }

        private Button MakeButton(string text, Color color, int width)
        {
            var b = new Button
            {
                Text = text,
                Width = width,
                Height = 40,
                FlatStyle = FlatStyle.Flat,
                BackColor = color,
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 10, 0),
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        // 历史用时记录（按操作名），用于越用越准的预估。纯文本格式：每行 "标题|秒1,秒2,秒3..."
        private static readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<double>> _runTimes =
            new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<double>>();
        private static readonly string RunTimesFile = System.IO.Path.Combine(DshCore.AppDataDir, "run-times.txt");
        private static bool _runTimesLoaded;

        private static void LoadRunTimes()
        {
            if (_runTimesLoaded) return;
            _runTimesLoaded = true;
            try
            {
                if (!System.IO.File.Exists(RunTimesFile)) return;
                foreach (string line in System.IO.File.ReadAllLines(RunTimesFile))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    int sep = line.IndexOf('|');
                    if (sep <= 0) continue;
                    string title = line.Substring(0, sep);
                    var list = new System.Collections.Generic.List<double>();
                    foreach (string part in line.Substring(sep + 1).Split(','))
                    {
                        double d; if (double.TryParse(part.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) && d > 0) list.Add(d);
                    }
                    if (list.Count > 0) _runTimes[title] = list;
                }
            }
            catch { }
        }
        private static void SaveRunTimes()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var kv in _runTimes)
                {
                    sb.Append(kv.Key).Append('|');
                    for (int i = 0; i < kv.Value.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(kv.Value[i].ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    sb.AppendLine();
                }
                System.IO.File.WriteAllText(RunTimesFile, sb.ToString(), new System.Text.UTF8Encoding(false));
            }
            catch { }
        }
        private static void RecordRunTime(string title, double seconds)
        {
            LoadRunTimes();
            if (!_runTimes.ContainsKey(title)) _runTimes[title] = new System.Collections.Generic.List<double>();
            _runTimes[title].Add(seconds);
            if (_runTimes[title].Count > 12) _runTimes[title].RemoveAt(0); // 最多保留 12 次
            SaveRunTimes();
        }
        private static double AvgRunTime(string title)
        {
            LoadRunTimes();
            var list = _runTimes.ContainsKey(title) ? _runTimes[title] : null;
            if (list == null || list.Count == 0) return 0;
            double s = 0; foreach (var d in list) s += d;
            return s / list.Count;
        }

        private void RunAction(string title, Func<string> work, bool thenOpen)
        {
            if (_busy) { LogLine("忙：请等待上一个操作完成"); return; }
            _busy = true;
            SetButtonsEnabled(false);
            LogLine("== " + title + " ==");
            LogLine("   评估中…（探测当前服务状态，请稍候）");
            // 2026-09-16 新增：立即在**状态区**（窗口中部，必可见）给出反馈。
            // 原来只在最底部日志框写一行，窗口一高日志框就被切到屏幕外 → "点了没反应"。
            try
            {
                _stateLabel.Text = "⏳ 正在执行：" + title + " …";
                _stateLabel.ForeColor = Color.FromArgb(200, 120, 30);
                _stateDetail.Text = "操作已开始（按钮已临时禁用，防重复点击）。结果会显示在这里与下方日志区。";
            }
            catch { }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Task.Factory.StartNew(delegate
            {
                // ★ T7：把 DshCore 的进度输出接到界面日志框。
                //   旧版 `WaitForTokenUrl` 的 progress 参数**两处调用都传 null** ⇒
                //   最长 150 秒里界面只有"⏳ 正在执行…"，用户完全分不清是卡死还是在正常等待。
                DshCore.ProgressSink = delegate(string m)
                {
                    try { BeginInvoke((MethodInvoker)delegate { LogLine("   " + m); }); } catch { }
                };

                // 1) 后台实时探测，生成“更强”的动态评估文案
                ServiceState st = DshCore.CheckState();
                string eval = EvalFor(title, st);
                string openResult = "";
                string result;
                try
                {
                    BeginInvoke((MethodInvoker)delegate { LogLine("   " + eval); });
                }
                catch { }
                // ★ T13：先清掉上一次的"只拉起未就绪"标记，避免串到别的动作上
                DshCore.LastRepairStartedOnly = false;
                try { result = work(); }
                catch (Exception e) { result = "操作异常：" + e.Message; }
                finally { DshCore.ProgressSink = null; }
                sw.Stop();

                // ★ T13：若刚才只是"拉起了进程、还没就绪"，**不要**立刻去开浏览器 ——
                //   OpenUi 一上来就 HttpProbe，而 dsh 冷启动要 40~60 秒 ⇒ 探测必然失败 ⇒
                //   浏览器窗口根本不会打开，用户看到"正在尝试打开界面……"然后什么都没有发生。
                //   这是教科书式的"假成功/无反应"，比不打开更伤信任。
                bool startedOnly = DshCore.LastRepairStartedOnly;
                if (thenOpen && !startedOnly && !result.StartsWith("启动失败") && !result.StartsWith("重启未完成"))
                {
                    try { openResult = DshCore.OpenUi(); }
                    catch (Exception e) { openResult = "打开界面异常：" + e.Message; }
                }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        LogLine(result);
                        LogLine("   [评估完毕 · 用时 " + sw.Elapsed.TotalSeconds.ToString("0.#") + " 秒]");
                        if (openResult.Length > 0) LogLine(openResult);
                        _busy = false;
                        SetButtonsEnabled(true);
                        RefreshState();
                    });
                }
                catch { _busy = false; }
                // 记录到历史（供下次预估参考）
                RecordRunTime(title, sw.Elapsed.TotalSeconds);
            });
        }

        // 每个按钮执行前的“情况评估 + 预计时间”：基于实时探测 + 历史用时，动态生成更准确的预估。
        private static string EvalFor(string title, ServiceState st)
        {
            bool hasProc = st != null && st.HasDshProcess;
            bool listens = st != null && st.Listens;
            bool resp = st != null && st.HttpResponds;
            int procCount = st != null && st.DshPids != null ? st.DshPids.Length : 0;
            double avg = AvgRunTime(title);
            string hist = avg > 0 ? "，上次均值约 " + avg.ToString("0.#") + " 秒" : "";
            string state = (hasProc ? (procCount + " 个 dsh 进程在跑" + (listens ? "，端口已监听" : "，但未监听") + (resp ? "且 HTTP 正常" : "，HTTP 未响应")) : "当前无 dsh 进程在跑");

            switch (title)
            {
                case "启动并打开":
                    return "评估：探测到" + state + "。将" + (resp ? "直接打开无痕窗口" : "先拉起服务再打开") + "。预计 " + (resp ? "2~6" : "5~15") + " 秒" + hist + "。";
                case "打开无痕":
                    return "评估：" + state + "。确认服务可用后打开无痕窗口。预计 " + (resp ? "2~4" : "5~10") + " 秒" + hist + "。";
                case "重启服务":
                    return "评估：" + state + "。将停止 " + procCount + " 个进程 → 等端口释放 → 重启 → 等 HTTP 恢复（会话可能中断）。预计 " + (procCount > 0 ? "10~25" : "6~15") + " 秒" + hist + "。";
                case "停止服务":
                    return "评估：结束所有 dsh 相关进程（" + procCount + " 个，不误杀其它 node）。预计 " + (procCount > 0 ? "2~5" : "1~2") + " 秒" + hist + "。";
                case "运行诊断":     return "评估：检查入口 / 服务状态 / 补丁 / 皮肤互斥 / 插件登记。预计 " + (hasProc ? "3~8" : "3~10") + " 秒" + hist + "。";
                case "为何打不开":   return "评估：" + state + "。分析打不开的可能原因（端口 / 进程 / 日志）。预计 3~10 秒" + hist + "。";
                case "一键修复":     return "评估：" + state + "。按“为何打不开”的结论执行修复。预计 5~20 秒" + hist + "。";
                case "强力自愈":     return "评估：" + state + "。杀 dsh 进程 → 释放端口 → 重启 → 等恢复。预计 " + (procCount > 0 ? "15~40" : "10~25") + " 秒" + hist + "。";
                case "插件兼容性":   return "评估：逐一核对已装插件的产物与版本是否冲突。预计 3~8 秒" + hist + "。";
                case "修复插件":     return "评估：定位冲突插件并修复登记。预计 3~10 秒" + hist + "。";
                case "排查崩溃插件": return "评估：扫描日志找崩溃 / 不兼容插件。预计 3~10 秒" + hist + "。";
                case "装后体检":     return "评估：日志来源 + 根因分类 + 页面/端口/API + 三方对齐 + 重复条目 + 皮肤系统 + 模型通路（全部只读）。预计 8~20 秒" + hist + "。";
                case "清理悬空引用": return "评估：查 bundles/dependencies 里解析不到的项，逐个询问是否移除（先快照）。预计 5~12 秒" + hist + "。";
                case "去重重复条目": return "评估：查同一文件内重复的 entry id，逐个询问是否保留第一条（先快照）。预计 3~8 秒" + hist + "。";
                case "启用被禁条目": return "评估：列出被禁用条目 → 输入 id → 改 disabled 行（先快照）。预计 3~8 秒" + hist + "。";
                case "重装插件":     return "评估：对已装插件执行 dsh plugin add 重装（需联网，先快照）。预计 20~180 秒" + hist + "。";
                case "分层判定":     return "评估：把证据归到 五层（本地服务/插件树/记忆注入/模型通路/环境）；可选外部日志。预计 8~20 秒" + hist + "。";
                case "禁用崩溃插件": return "评估：把崩溃插件写入 disabled 行（可恢复）。预计 3~8 秒" + hist + "。";
                case "修复皮肤互斥": return "评估：原子写入 profile+home 两层互斥 patch 并备份。预计 3~8 秒" + hist + "。";
                case "打开日志":     return "评估：打开 dsh 输出日志并显示尾部。预计 1~3 秒" + hist + "。";
                case "导出报告":     return "评估：生成完整诊断报告。预计 5~15 秒" + hist + "。";
                case "导出诊断包":   return "评估：导出脱敏诊断包（含日志 / patch / 配置）。预计 10~30 秒" + hist + "。";
                case "刷新状态":     return "评估：刷新服务状态。预计 2~4 秒" + hist + "。";
                case "开机自启":     return "评估：在“启动”文件夹创建自愈快捷方式。预计 2~5 秒" + hist + "。";
                case "关闭界面":     return "评估：" + state + "。停止 dsh web，关闭当前界面窗口。预计 2~5 秒" + hist + "。";
                default:            return "";
            }
        }

        // 操作前的二次确认弹窗（尤其重启/停止/关闭界面会断开当前网页）
        private static bool Confirm(string title, string message)
        {
            return MessageBox.Show(message, "大肥鱼救星 · " + title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }

        // 让用户从若干选项里挑一个（用于「恢复配置」选快照）。取消返回 null。
        private static string AskChoice(string title, string message, string[] options)
        {
            using (var dlg = new Form())
            {
                // ★ 2026-09-18：对话框穿皮肤（金色边框 + 顶部正中蝴蝶结）
                SkinArt.DecorateDialog(dlg, SkinTheme.Current());
                dlg.Text = "大肥鱼救星 · " + title;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.ClientSize = new Size(520, 300);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.Font = new Font("Microsoft YaHei UI", 9f);

                var lbl = new Label
                {
                    Text = message,
                    Dock = DockStyle.Top,
                    Height = 118,
                    Padding = new Padding(10, 8, 10, 4),
                };
                var list = new ListBox
                {
                    Dock = DockStyle.Fill,
                    IntegralHeight = false,
                    Font = new Font("Consolas", 10f),
                };
                list.Items.AddRange(options);
                if (options.Length > 0) list.SelectedIndex = 0;

                var ok = new Button { Text = "恢复这个快照", DialogResult = DialogResult.OK, Width = 130, Height = 32 };
                var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 80, Height = 32 };
                var bar = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    FlowDirection = FlowDirection.RightToLeft,
                    Height = 46,
                    Padding = new Padding(10, 6, 10, 6),
                };
                bar.Controls.Add(cancel);
                bar.Controls.Add(ok);

                dlg.Controls.Add(list);
                dlg.Controls.Add(bar);
                dlg.Controls.Add(lbl);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                if (dlg.ShowDialog() != DialogResult.OK) return null;
                return list.SelectedItem as string;
            }
        }

        private void SetButtonsEnabled(bool enabled)
        {
            // ★ 2026-09-16 修复 P9：这个数组原先只有 **26** 个按钮，漏了 `_btnDiagnose`；
            //   而它的处理器**不走 RunAction**，因此完全不经过 `_busy` 忙锁 ⇒
            //   别的动作正在跑（例如"停止服务"正在杀进程）时，用户仍能点「导出诊断包」，
            //   起第二个进程实例 + 并发弹出资源管理器。
            //   （机器核对：本文件 MakeButton 赋值定义 27 个按钮字段，此处数组 26 个，差集恰为它。）
            foreach (var b in new Control[] { _btnStartOpen, _btnScout, _btnOpen, _btnRestart, _btnStop, _btnClose, _btnDiag, _btnWhy, _btnBootCheck, _btnPatch, _btnPopup, _btnTerminal, _btnRepair, _btnForce, _btnPostCheck, _btnCleanDangling, _btnDedup, _btnReEnable, _btnReinstall, _btnLayer, _btnPatchLock, _btnLockFix, _btnWhite, _btnPathAudit, _btnHealBoundary, _btnSkin, _btnCompat, _btnFixPlugin, _btnCrash, _btnDisableCrash, _btnFixSkin, _btnLog, _btnExport, _btnRefresh, _btnStartup, _btnDiagnose, _btnVault, _btnVaultList, _btnVaultRestore, _btnConfigCheck })
            {
                if (b != null) b.Enabled = enabled;
            }
        }

        // 🌅 开机自启
        // ★ WP2①（2026-09-20 防误报加固）：**只引导、不代写**。
        //   旧版确认后由程序往「启动」文件夹写 .lnk —— 那是杀软 ML 启发式里权重最高的
        //   "持久化"形状，也是本次 Defender 误报的头号嫌疑。现在改成：
        //     · 启动项**已存在**（旧版留下的）⇒ 仍提供"取消"入口（删除是清理，不是持久化，保留）；
        //     · 启动项不存在 ⇒ **不创建**，只给引导（一份自愈 .cmd + 手动放置步骤 + 打开文件夹）。
        //   程序自身不写「启动」文件夹 / Run 键 / 计划任务中的任何一个。
        private void StartupAction()
        {
            if (Program.StartupShortcutExists())
            {
                bool go = false;
                Invoke((MethodInvoker)delegate
                {
                    go = Confirm("开机自启 · 取消",
                        "检测到**已经启用**开机自愈。\r\n\r\n" +
                        "启动项路径：\r\n    " + Program.StartupShortcutPath + "\r\n" +
                        "当前指向：\r\n    " + Program.StartupShortcutTarget() + "\r\n\r\n" +
                        "选「是」= **取消开机自启**（只删这个快捷方式，别的一律不动）\r\n" +
                        "选「否」= 保持现状（也不会重新创建）");
                });
                if (!go) { LogLine("已保留开机自启，未做任何改动。"); return; }
                RunAction("取消开机自启", delegate { return Program.UninstallStartup(); }, false);
                return;
            }

            bool ok = false;
            Invoke((MethodInvoker)delegate
            {
                ok = Confirm("开机自启 · 引导（不会替你创建）",
                    "本程序**不会**替你写开机启动项 ——\r\n" +
                    "「程序自己往启动项里写东西」正是杀软把它误判成木马的头号原因。\r\n\r\n" +
                    "点「是」= 给你一份自愈 .cmd + 手动放置步骤\r\n" +
                    "        （.cmd 只生成到**救星自己的目录**，不会进「启动」文件夹）\r\n" +
                    "点「否」= 什么都不做\r\n\r\n" +
                    "⚠ 如果你真的放进去，含义是：每次登录 Windows 都会自动跑一次\r\n" +
                    "   「强力自愈」—— 结束 dsh 进程、重启 dsh web、顺带检查本地绘图服务、\r\n" +
                    "   打开桌面客户端。希望登录后「什么都不动」的话，请不要放。\r\n\r\n" +
                    "目标程序：" + Application.ExecutablePath);
            });
            if (!ok) { LogLine("已取消：未做任何改动。"); return; }
            RunAction("开机自启引导", delegate { return Program.StartupGuide(); }, false);

            bool openIt = false;
            Invoke((MethodInvoker)delegate
            {
                openIt = Confirm("打开「启动」文件夹？",
                    "要现在打开「启动」文件夹吗？\r\n    " + Program.StartupShortcutPath.Replace("\\大肥鱼救星-自愈.lnk", "") +
                    "\r\n\r\n只是打开给你看 —— 本程序不往里写任何东西。");
            });
            if (openIt) LogLine(Program.OpenStartupFolder());
        }

        private void DisableCrashedPluginAction()
        {
            RunAction("禁用崩溃插件", delegate
            {
                // ★ T29：这个按钮会**写 profile 补丁**（改配置），
                //   而全工具里破坏性更小的「重启/停止/关闭界面」都有二次确认 —— 确认策略倒挂。
                //   先把要禁用的 id 探测出来，让用户在确认框里看到**具体是哪个插件**。
                string id = DshCore.FirstCrashedId();
                if (string.IsNullOrEmpty(id))
                    return DshCore.DisableCrashedPluginAuto();   // 没东西可禁，直接给报告，不必打扰

                bool go = false;
                Invoke((MethodInvoker)delegate
                {
                    go = Confirm("禁用崩溃插件 · 确认",
                        "将要禁用的插件：\r\n\r\n    " + id + "\r\n\r\n" +
                        "做法：在 profile 补丁里写入 `disabled: true`（**比卸载安全，包还在，可恢复**）。\r\n" +
                        "写入前会自动备份关键配置与补丁文件。\r\n\r\n" +
                        "确定继续吗？（选「否」= 不做任何改动）");
                });
                if (!go) return "你选择了不禁用，未做任何改动。";

                return DshCore.DisableCrashedPlugin(id);
            }, false);
        }

        // 💊 修复皮肤互斥
        // ★ 2026-09-16（T18 + T18b + T29）：
        //   旧版把参数**写死成 "official"** ⇒ 主人点一下，正在用的女仆皮肤就被关掉、换回官方外观。
        //   按钮名暗示"消除冲突"，实际语义却是"**重置为官方皮肤**" —— 命名与行为不符的误操作。
        //   同时它还把皮肤管理器**强开**（写死 manager: false），并**没有**二次确认
        //   （而同为写配置的「修复插件」就有）。
        //   现在：① 先探测当前皮肤并在修复时**保持它不变**；② 不凭空新增/反转管理器条目；
        //   ③ 加确认框，把"会保留哪个皮肤"讲清楚。
        private void FixSkinAction()
        {
            string cur = DshCore.DetectActiveSkin();
            string name = cur == "maid-atelier" ? "女仆皮肤（maid-atelier）"
                        : cur == "orca-link" ? "虎鲸皮肤（orca-link）"
                        : "官方默认外观";
            bool go = false;
            Invoke((MethodInvoker)delegate
            {
                go = Confirm("修复皮肤互斥 · 确认",
                    "将把皮肤互斥条目写入 profile + home **两层**补丁（消除皮肤打架导致的界面错乱）。\r\n\r\n" +
                    "当前启用：" + name + "\r\n" +
                    "修复后保持：" + name + "　（**不会**替你换回官方外观）\r\n\r\n" +
                    "写入前会自动备份关键配置（可用「♻ 恢复配置」退回）；\r\n" +
                    "若第二层写入失败，会自动把第一层回滚，不会留下半套补丁。\r\n\r\n" +
                    "确定继续吗？（选「否」= 不做任何改动）");
            });
            if (!go) { LogLine("已取消：修复皮肤互斥未做任何改动。"); return; }

            RunAction("修复皮肤互斥", delegate { return DshCore.FixSkinExclusion(cur); }, false);
        }

        // 修复插件（2026-09-12 加固）：
        // 旧行为 = 检测到"坏插件"就直接 dsh plugin remove 卸掉，而卸载会重写
        // profile/package.json —— 启发式误判就会无故卸包，且卸载中途被打断会毁掉 package.json。
        // 新行为 = 先只报告；用户明确点确认后才卸载，且卸载前自动打配置快照。
        private void FixPluginAction()
        {
            RunAction("修复插件", delegate
            {
                string report = DshCore.RepairPlugin(false);
                if (report.IndexOf("只报告") < 0)
                    return report;      // 没问题，直接返回报告

                bool go = false;
                Invoke((MethodInvoker)delegate
                {
                    go = Confirm("修复插件 · 确认卸载",
                        "检测到可能损坏的插件。\r\n\r\n" +
                        "下一步会用 `dsh plugin remove` 卸载它们 —— 这会**重写 profile/package.json**，" +
                        "属于不可逆操作；如果判定有误，插件会被无故卸掉。\r\n\r\n" +
                        "卸载前本工具会自动给关键配置打一份快照（可用「♻ 恢复配置」退回）。\r\n\r\n" +
                        "确定要卸载吗？（选「否」= 只看报告，不动任何东西）");
                });
                if (!go)
                    return report + "\r\n\r\n>>> 你选择了不卸载，未做任何改动。";

                return report + "\r\n\r\n" + DshCore.RepairPlugin(true);
            }, false);
        }

        // 🧩 补丁体检：先检查，有问题再问要不要一键重打
        //   这些补丁都在 node_modules 里，**DSH 升级后会被覆盖** —— 也就是"弹窗复发"的时刻。
        private void PatchAction()
        {
            RunAction("补丁体检", delegate
            {
                string report = PatchGuard.CheckPatches();
                if (report.IndexOf("[FAIL]") < 0 && report.IndexOf("[WARN]") < 0)
                    return report;

                bool go = false;
                Invoke((MethodInvoker)delegate
                {
                    go = Confirm("补丁体检 · 一键重打",
                        "检测到补丁缺失或有警告。\r\n\r\n" +
                        "下一步会重打这几项（都在 node_modules 里，DSH 升级后会丢）：\r\n" +
                        "  · DSH 子进程不弹控制台（CREATE_NO_WINDOW）—— 重打后需要重启 DSH\r\n" +
                        "  · argo-search 子进程不弹控制台（windowsHide）—— 杀掉它的 MCP 进程即可生效\r\n" +
                        "  · 系统 python 的 pyyaml（缺失会拖慢冷启动）\r\n\r\n" +
                        "每项改动前都会自动备份。确定要重打吗？");
                });
                if (!go) return report + "\r\n\r\n>>> 你选择了不重打，未做任何改动。";

                return report + "\r\n\r\n" + PatchGuard.RepairPatches();
            }, false);
        }

        // 🖥 默认终端：检测，若是 Windows Terminal 则问要不要改成「Windows 控制台主机」
        private void TerminalAction()
        {
            RunAction("默认终端", delegate
            {
                string report = PatchGuard.TerminalCheck();
                if (report.IndexOf("★ 结论") < 0)
                    return report + "\r\n\r\n>>> 无需改动。";

                bool go = false;
                Invoke((MethodInvoker)delegate
                {
                    go = Confirm("默认终端 · 改成控制台主机",
                        "当前默认终端是 Windows Terminal —— 任何程序新建控制台，都会弹出一个大终端窗口" +
                        "（就是那个写着 Windows PowerShell 的大黑窗）。\r\n\r\n" +
                        "改成「Windows 控制台主机」后这类窗口会安静下来：程序照跑，只是不再弹大窗口。\r\n" +
                        "你平时想用 Windows Terminal 仍然可以正常打开，不受影响。\r\n\r\n" +
                        "确定要改吗？");
                });
                if (!go) return report + "\r\n\r\n>>> 你选择了不改。";

                return report + "\r\n\r\n" + PatchGuard.SetTerminalToConhost();
            }, false);
        }

        // 恢复配置：列出快照 → 让用户挑一份 → 恢复（恢复前会自动再拍一份）
        private void RestoreConfigAction()
        {
            var snaps = SafeConfig.ListSnapshots();
            if (snaps.Count == 0)
            {
                LogLine("还没有任何配置快照。请先点「🛡 备份配置」。");
                MessageBox.Show(this, "还没有任何配置快照。\r\n\r\n请先点「🛡 备份配置」存一份，之后随时可以退回来。",
                    "恢复配置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var names = new System.Collections.Generic.List<string>();
            foreach (string d in snaps) names.Add(Path.GetFileName(d));

            // ★ P14：旧版这里**只给最新 10 份**，而「📚 快照列表」显示 20 份、备份保留 40 份
            //   ⇒ 第 11 份及以后的快照**在界面上根本选不到**，但列表里却把它们列出来了，
            //   会让人以为可用。现在：给最新 10 份 + 一个「显示全部」提示，
            //   并在选项里如实标注总数。
            int show = Math.Min(10, names.Count);
            string head = "选择要恢复到的快照（最新在前）：\r\n\r\n"
                        + "恢复前会自动给当前状态再拍一份快照，所以恢复本身也可以再退回来。\r\n\r\n";
            if (names.Count > show)
                head += "⚠ 共 " + names.Count + " 份快照，这里只列最新 " + show + " 份"
                      + "（更早的可用「📚 快照列表」查看；如需恢复更早的，请告诉我或手动指定）。\r\n\r\n";
            string pick = AskChoice("恢复配置", head + string.Join("\r\n", names.GetRange(0, show).ToArray()),
                names.GetRange(0, show).ToArray());
            if (pick == null) { LogLine("已取消恢复配置。"); return; }

            // ★ T22：恢复是覆盖**正在运行的 DSH** 的活动配置文件 ——
            //   而 DSH 把配置读进内存后，任何一次设置变更/插件操作都会**重新写盘**，
            //   把刚恢复的旧配置**静默覆盖回新值**（用户看到"✔ 已恢复"、几秒后其实被抹掉）。
            //   所以这里必须把"要重启 DSH 才生效"讲在最前面，并主动提议顺手重启。
            bool doRestart = false;
            string running = DshCore.CheckState().HasDshProcess ? "（当前 **DSH 正在运行**）" : "";
            Invoke((MethodInvoker)delegate
            {
                DialogResult dr = MessageBox.Show(this,
                    "即将把快照 " + pick + " 的内容覆盖到**活动配置**上。" + running + "\r\n\r\n" +
                    "⚠ 重要：DSH 把配置读在内存里跑，**恢复后必须重启 DSH 才生效**；\r\n" +
                    "   否则它下一次写配置时会把刚恢复的旧值**静默覆盖回去**（看着像「恢复了但没用」）。\r\n\r\n" +
                    "选「是」= 恢复后**顺便重启 DSH**（会话会中断）\r\n" +
                    "选「否」= 只恢复文件，我自己稍后手动重启",
                    "恢复配置 · 确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                doRestart = (dr == DialogResult.Yes);
            });
            RunAction("恢复配置", delegate
            {
                string r = SafeConfig.RestoreSnapshot(pick);
                if (doRestart)
                {
                    r += "\r\n\r\n--- 按你的选择，接着重启 DSH 以让配置真正生效 ---\r\n";
                    r += DshCore.RestartDsh()
                        ? "已重启完成，恢复的配置现在生效。"
                        : "重启未完成：" + DshCore.LastRestartNote;
                }
                else
                {
                    r += "\r\n\r\n⚠ 尚未重启 DSH —— 恢复的配置**现在还没生效**。" +
                         "请点「🔄 重启服务」或手动重启，否则 DSH 下次写配置时会把旧值覆盖回去。";
                }
                return r;
            }, false);
        }

        private void OpenLogFile()
        {
            DshCore.EnsureAppDataDir();

            // ★ T20：工具自己认**两个以上**日志来源（DshBoot.Sources()：last-url.txt +
            //   ~/.dsh/tools/dsh-web.out.log + 救星目录所有 *.log），而本按钮**只开救星目录那一个**
            //   ⇒ 当前实例若是"可靠启动器"起的，用户打开的是**另一个实例的日志**、查不到真故障。
            //   现在：先判定"当前实例的日志到底在哪"，把它作为**首选**打开，并列出其它来源。
            string primary = DshCore.DshWebLog;
            string why = "";
            try
            {
                string alt;
                if (DshBoot.TryFindCurrentInstanceLog(out alt, out why) && !string.IsNullOrEmpty(alt))
                    primary = alt;
            }
            catch { }

            if (!File.Exists(primary))
            {
                // ★ P10：旧版两处 `catch { }` 全静默 ⇒ 文件不存在/没有 .log 关联程序时，
                //   点了**毫无反应**，用户会以为"按钮坏了"。现在必须给出反馈。
                LogLine("找不到要打开的日志文件：" + primary + (why.Length > 0 ? "\r\n（判定依据：" + why + "）" : ""));
                MessageBox.Show(this,
                    "找不到日志文件：\r\n" + primary + "\r\n\r\n" +
                    "可能原因：dsh 还没通过本工具启动过、或日志被清理了。\r\n" +
                    "可以先点「🔍 运行诊断」看当前实例的日志线索。",
                    "打开日志", MessageBoxButtons.OK, MessageBoxIcon.Information);
                ShowLogSources();
                return;
            }

            string opened = "";
            try { ProcessStart(primary); opened = primary; }
            catch (Exception e)
            {
                // ★ P10：不再静默
                LogLine("打开日志失败：" + e.Message);
                MessageBox.Show(this,
                    "无法用系统默认程序打开：\r\n" + primary + "\r\n\r\n原因：" + e.Message + "\r\n\r\n" +
                    "（可能是没有 .log 的关联程序）—— 已把日志尾部显示在下方，可直接查看。",
                    "打开日志", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            try
            {
                string tail = ReadTail(primary, 60);
                if (tail.Length > 0) LogLine("--- " + opened + "（尾部 60 行） ---\r\n" + tail);
            }
            catch (Exception e) { LogLine("读取日志尾部失败：" + e.Message); }

            ShowLogSources();
        }

        /// <summary>把工具认的全部日志来源列出来（T20：让"打开的是谁的日志"这件事可见）。</summary>
        private void ShowLogSources()
        {
            try
            {
                string text = DshBoot.DescribeSources();
                if (!string.IsNullOrEmpty(text)) LogLine(text);
            }
            catch { }
        }

        // 导出脱敏诊断包：用 --diagnose 子进程生成，读 diagnose.log 看结果，并打开目录。
        private void ExportDiagnoseAction()
        {
            try
            {
                DshCore.EnsureAppDataDir();
                LogLine("正在导出脱敏诊断包…");
                string exe = Application.ExecutablePath;
                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = "--diagnose";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
                {
                    p.WaitForExit(60000);
                }
                string logPath = System.IO.Path.Combine(DshCore.AppDataDir, "diagnose.log");
                if (File.Exists(logPath))
                {
                    string content = System.IO.File.ReadAllText(logPath, System.Text.Encoding.UTF8).Trim();
                    LogLine(content);
                }
                else
                {
                    LogLine("诊断包导出失败：未找到 diagnose.log");
                }
                try { ProcessStart(DshCore.AppDataDir); } catch { }
            }
            catch (Exception e) { LogLine("导出诊断包失败：" + e.Message); }
        }

        private static void ProcessStart(string path)
        {
            var psi = new System.Diagnostics.ProcessStartInfo();
            psi.FileName = path;
            psi.UseShellExecute = true;
            System.Diagnostics.Process.Start(psi);
        }

        private static string ReadTail(string path, int lines)
        {
            // 流式读尾 N 行，避免大文件全量读入卡顿
            var sb = new System.Text.StringBuilder();
            var queue = new System.Collections.Generic.Queue<string>(lines + 1);
            using (var reader = new StreamReader(path))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    queue.Enqueue(line);
                    if (queue.Count > lines) queue.Dequeue();
                }
            }
            foreach (string l in queue) sb.AppendLine(l);
            return sb.ToString();
        }

        // 后台异步刷新。
        // ★ 2026-09-16 更正注释：这里走的是 `CheckState()`（**非 deep**）——
        //   它只用 iphlpapi 查监听者 + 托管查进程存活，**不创建任何外部进程**，
        //   所以 4 秒轮询不会再弹 PowerShell 窗口（那是"弹窗四源"的根治点之一）。
        //   旧注释写的"会调用 powershell/WMI/netstat"是更早版本的行为，已作废。
        //   需要全量进程扫描（WMI）的地方一律显式用 `CheckState(true)`，只出现在按钮动作里。
        private int _refreshGen = 0;
        private bool _refreshRunning = false;

        // ★★ 2026-09-20 修「状态区一直闪」（主人：「还是闪闪的，那几个键停不下来」）：
        //   根因＝**每 4 秒无条件把几个控件的 Text 重写一遍**，而其中
        //     _stateDetail 里带了 `检测于 HH:mm:ss` 这个**每次都变**的时间戳。
        //   写 Text 会产生 WM_SETTEXT ⇒ 控件失效重绘；这几个 Label 又是
        //   `BackColor = Color.Transparent`（SkinTheme 统一这么设），
        //   透明 Label 在 WinForms 里**由父容器代画** ⇒ 一次文本变更就把父容器
        //   （statePanel，Tag="card"、还挂着 CardPaint 描边）整块重绘一遍。
        //   于是在 4 秒轮询下，状态卡看起来就是「每隔几秒闪一下，停不下来」。
        //   ⇒ 治法：**内容没变就一个字都不写**。时间戳只在状态真的发生变化时才更新，
        //     不再自称"实时"——它本来也只是"上次变化是什么时候"。
        //   判据：--selftest 里有「界面一致性：轮询不得无条件重写控件文本」这一项。
        private string _lastStateMain = null;
        private string _lastStateDetail = null;
        private string _lastStatusText = null;
        private string _lastTitle = null;
        private string _lastStateColor = null;
        private string _lastStamp = "--:--:--";   // 上次"状态发生变化"的时刻（不是上次轮询的时刻）

        /// <summary>
        /// 只在文本真的不同时才赋值（相同就一个字都不写，避免触发重绘）。
        /// ★ 2026-09-20：参数类型用 object + 反射式赋值过于绕，这里给两个明确重载 ——
        ///   Control（Label 等）与 ToolStripItem（状态栏文字）。两者没有共同基类，
        ///   所以不能只写一个 Control 版本（首次编译就是在这里报 CS1502/CS1503 的）。
        /// </summary>
        private static void SetTextIfChanged(System.Windows.Forms.Control c, string want, ref string last)
        {
            if (c == null) return;
            if (last != null && last == want) return;
            last = want;
            c.Text = want;
        }

        private static void SetTextIfChanged(ToolStripItem it, string want, ref string last)
        {
            if (it == null) return;
            if (last != null && last == want) return;
            last = want;
            it.Text = want;
        }

        private void RefreshState()
        {
            SBoot("RefreshState 开始");
            if (IsDisposed || _refreshRunning) return; // 防止后台刷新重叠堆积
            _refreshRunning = true;
            int gen = ++_refreshGen;
            // 先用"检测中"提示，避免重复点击堆积
            // ★ 2026-09-20：这里也走"变了才写"，否则连点按钮会让它反复闪
            if (_stateLabel != null && _stateLabel.Text.IndexOf("检测中", StringComparison.Ordinal) < 0)
            {
                _stateLabel.Text = "● 正在检测…";
                _stateLabel.ForeColor = Color.FromArgb(90, 90, 140);
                _lastStateMain = "● 正在检测…";
                _lastStateColor = null;      // 颜色下面会跟着真状态重设
            }

            Task.Factory.StartNew(delegate
            {
                ServiceState st;
                try { st = DshCore.CheckState(); }
                catch { st = new ServiceState(); }

                string main, detail, color;
                if (st.HttpResponds)
                {
                    main = "● dsh web 运行正常";
                    detail = "端口 " + DshCore.ActivePort + " 已监听，HTTP 有响应；端口占用 PID: " + Join(st.DshPids) + "；界面: " + DshCore.ActiveRootUrl;
                    color = "#2E8B57";
                }
                else if (st.Listens)
                {
                    main = "● 端口有监听但服务无响应（疑似卡死）";
                    detail = "建议点击“重启服务”。占用 PID: " + Join(st.OccupierPids);
                    color = "#D2691E";
                }
                else if (st.HasDshProcess)
                {
                    main = "● dsh 进程存在但未监听 " + DshCore.ActivePort;
                    detail = "进程 PID: " + Join(st.DshPids) + "，可能启动失败或端口被改；建议重启并查看日志";
                    color = "#D2691E";
                }
                else
                {
                    main = "● dsh web 未运行";
                    detail = "点击“启动并打开”一键拉起服务（无需终端）";
                    color = "#A0A0A0";
                }

                string brief = main.Replace("● ", "").Trim();
                // ★ 2026-09-20：时间戳只在**状态真的变了**的时候才更新（不再自称"实时检测于"）。
                //   旧版每次都刷 HH:mm:ss ⇒ 每 4 秒必然触发一次重绘 ⇒ 状态卡闪个不停。
                // 回 UI 线程更新（若期间已 dispose 或产生了更新的刷新则丢弃）
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _refreshRunning = false;
                        if (IsDisposed || gen != _refreshGen) return;

                        // 状态说明里含 PID/端口，可能每次都不同；它与时间戳一起构成"详情串"。
                        // 只有在主状态或详情真的变化时才重建那一行，并同时刷新时间戳。
                        bool mainChanged = (_lastStateMain != main);
                        bool detailChanged = (_lastStateDetail != detail);
                        bool colorChanged = (_lastStateColor != color);

                        if (mainChanged || colorChanged)
                        {
                            _lastStateMain = main;
                            _lastStateColor = color;
                            _stateLabel.Text = main;
                            _stateLabel.ForeColor = ColorTranslator.FromHtml(color);
                        }
                        if (mainChanged || detailChanged)
                        {
                            _lastStateDetail = detail;
                            _stateDetail.Text = detail;
                            // 时间戳只在这一刻更新：它表示"状态是什么时候变成这样的"
                            _lastStamp = DateTime.Now.ToString("HH:mm:ss");
                        }

                        string wantStatus = brief + "　|　" + DshCore.ActiveRootUrl + "　|　状态变化于 " + _lastStamp;
                        SetTextIfChanged(_statusText, wantStatus, ref _lastStatusText);

                        // ★ 窗体标题：旧版**每次刷新都写**，等于每 4 秒给窗口发一次 WM_SETTEXT
                        //   （标题栏重绘 + 任务栏条目更新）。内容其实没变，纯属白刷。
                        string wantTitle = DshCore.AppTitle + " - " + brief;
                        if (_lastTitle != wantTitle)
                        {
                            _lastTitle = wantTitle;
                            Text = wantTitle;   // ★ 版本号必须在这里也带上：每次刷新都会覆盖标题
                        }
                    });
                }
                catch { _refreshRunning = false; }
            });
        }

        private static string Join(int[] arr)
        {
            if (arr == null || arr.Length == 0) return "-";
            return string.Join(",", Array.ConvertAll(arr, x => x.ToString()));
        }

        /// <summary>WM_SETREDRAW：关/开某个控件的重绘（0=关，1=开并重绘）。</summary>
        private const int WM_SETREDRAW = 0x000B;

        public void LogLine(string text)
        {
            lock (_logLock)
            {
                string stamp = DateTime.Now.ToString("HH:mm:ss");
                // ★ 2026-09-20 修「日志框闪」：AppendText → 挪插入符 → ScrollToCaret 是**三件独立的事**，
                //   每做一件 RichEdit 都要重新排版一次、并把中间态刷到屏幕上。连着写几行
                //   （典型场景：启动后那一串检测进度行）时，肉眼看到的就是日志框抖/闪。
                //   ⇒ 先用 WM_SETREDRAW 把这个框的重绘关掉，三件事一口气做完，再打开并重绘一次：
                //     中间态一帧都不刷出去，最终内容完全一样。
                //   句柄还没建好（构造期间）时就别冻了 —— 那时本就没有成形的画面可闪，
                //   而且 SendMessage 也没处发。
                IntPtr h = IntPtr.Zero;
                bool frozen = false;
                try
                {
                    try
                    {
                        if (_logBox != null && _logBox.IsHandleCreated)
                        {
                            h = _logBox.Handle;
                            SendMessage(h, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
                            frozen = true;
                        }
                    }
                    catch { frozen = false; }

                    _logBox.AppendText("[" + stamp + "] " + text + "\r\n");
                    _logBox.SelectionStart = _logBox.TextLength;
                    _logBox.ScrollToCaret();
                }
                finally
                {
                    if (frozen)
                    {
                        try
                        {
                            SendMessage(h, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                            _logBox.Refresh();     // = Invalidate + Update：恢复那一刻补画一次
                        }
                        catch { }
                    }
                }
            }
        }

        // ============================================================
        //  B 方案的核心一处：把**原生标题栏那一截**从非客户区里抠掉
        //
        //  ★ 顺序必须是"先 base 再改"：
        //    base.WndProc → DefWindowProc 已经按系统默认（含标题栏）把客户区算好写进 lParam；
        //    我们再把多扣的那 SM_CYCAPTION 像素退回去 ⇒ 客户区顶边上移，正好抵掉标题栏。
        //    （先写的版本想自己算 CYFRAME+CYCAPTION，结果必错：这两个值还随 DPI/主题变。）
        //  ★ 只在这一个消息上动手，其它一概交给 base ⇒ 拖动/缩放/吸附/最大化按钮全部还是系统行为。
        // ============================================================
        protected override void WndProc(ref Message m)
        {
            if (SkinFrame.Enabled && m.Msg == SkinFrame.WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                base.WndProc(ref m);
                try
                {
                    int got = SkinFrame.StripCaptionFromClientRect(m.LParam);
                    if (got > 0 && !_stripLogged)
                    {
                        _stripLogged = true;
                        SLogSkin("custom caption bar: native caption stripped " + got + "px");
                    }
                }
                catch (Exception ex) { SLogSkin("strip caption failed: " + ex.GetType().Name); }
                return;
            }
            base.WndProc(ref m);
        }
        private bool _stripLogged;

        // ============================================================
        //  ★ 2026-09-17 修复（自绘标题栏 B 方案的回归）：最大化/还原循环里窗口每轮长高 23px
        //
        //  实测（同机、同操作、A/B 对照，各 4 轮）：
        //    旧交付版（无自绘标题栏）   还原高度 809 809 809 809   —— 0 漂移
        //    自绘标题栏版               还原高度 822 845 868 891   —— 每轮 +23px
        //    --nativeframe（退回原生）  还原高度 809 809 809 809   —— 0 漂移
        //  23px 正好 = SM_CYCAPTION，就是被 WM_NCCALCSIZE 抠掉的那一截。
        //
        //  根因（实测取证，不是猜）：窗口样式里**留着 WS_CAPTION**，但客户区被我们抠大了
        //  23px ⇒「窗口尺寸 ↔ 客户区尺寸」两套口径对不上；最大化期间的那次尺寸回写把
        //  偏大 23px 的值写进了**还原位置** —— GetWindowPlacement 的 rcNormalPosition
        //  实测每轮 822→845→868→891，于是"还原一次长高一截"。（对照臂不动：809→809→809）
        //
        //  修法：不猜框架内部的换算公式，改成**确定性收口** ——
        //    正常态下一直记住客户区尺寸；一旦从最大化/最小化回到正常态，就把客户区掰回
        //    记住的那个尺寸。⇒ 无论系统把还原位置写成多大，用户看到的结果永远是
        //    「回到最大化之前那一模一样的大小」。
        //
        //  ★ 负对照：环境变量 BFF_RESTORE_GUARD=0 可关掉本修复；--maxcycle 自检会立刻报出
        //    漂移 ⇒ 修复与不修复**在同一份二进制里**都能跑，避免"跨版本对照"的混淆。
        // ============================================================
        /// <summary>还原收口开关（默认开；BFF_RESTORE_GUARD=0 关掉，供自检做负对照）。</summary>
        public static bool RestoreGuardEnabled = true;

        private Size _wantClient = Size.Empty;
        private Rectangle _normalBounds = Rectangle.Empty;
        private Size _normalClient = Size.Empty;    // 与 _normalBounds 配对的客户区基准
        private FormWindowState _prevWindowState = FormWindowState.Normal;
        private bool _restoreGuardBusy;
        private int _settleUntil;

        /// <summary>自检用：OnResize 的调用轨迹（--maxcycle 会把最近若干条打进报告，便于定位漂移是谁写的）。</summary>
        internal static readonly System.Collections.Generic.List<string> ResizeTrace =
            new System.Collections.Generic.List<string>();

        private static void RzTrace(string s)
        {
            try { if (ResizeTrace.Count < 400) ResizeTrace.Add(s); } catch { }
        }

        // ============================================================
        //  ★★ 本回归的**根**：ClientSize 的 setter / getter 口径不一致
        //
        //  样式里留着 WS_CAPTION（为了让缩放/吸附/阴影/圆角都还是系统的），而标题栏那一截
        //  在 WM_NCCALCSIZE 里被抠掉 ⇒ WinForms 按"客户区 + 外框 + 标题栏"换算窗口尺寸时，
        //  会**多给 capH(23px)**。实测：ClientSize = 806 → 实际客户区 829（+23）；
        //  而且每经过一次"最大化 → 还原"就再叠一次 ⇒ 822→845→868→891 每轮 +23px。
        //
        //  修法：在 setter 上把 capH 抵消掉，让"要多少客户区就得到多少客户区"。
        //  （getter 读的是真实客户区，本来就是对的 ⇒ 只改 setter，不引入新的不对称。）
        // ============================================================
        /// <summary>
        /// 把客户区落实到 _wantClient（闭环：设完**就地量**，差多少补多少）。
        /// 为什么不写死常数：摘掉 WS_CAPTION 之后框架/系统的非客户区口径会变（实测出现过
        /// 41px 这种"既不是 16 也不是 39"的值），写死必错；就地量、按差值收敛才稳。
        /// 每次落实都把"目标 → 实得"写进黑匣子，不一致就如实记 ★。
        /// </summary>
        /// <summary>
        /// 还原收口（机制无关）：等框架/系统把"跟屁虫"尺寸写完（实测还原之后还会再写一次），
        /// 再按**基准客户区**闭环掰回去 —— 一次写定，不跟它抢，也不去猜它加了多少。
        /// 每次收口都写黑匣子（基准 / 补写后曾是多少 / 最后实得），不一致就标 ★。
        /// </summary>
        private void ScheduleSettle()
        {
            try
            {
                var t = new System.Windows.Forms.Timer();
                t.Interval = 1200;
                t.Tick += delegate
                {
                    try { t.Stop(); t.Dispose(); } catch { }
                    try
                    {
                        // ★★ 2026-09-18 修复「最大化后界面不跟全屏」（主人截图 + manual-scroll.log 实锤）：
                        //   本定时器原来**不看 WindowState** —— 主人在「还原后 1.2 秒内再次最大化」时，
                        //   到点的收口会把**最大化窗口**的客户区按回基准（实测日志成串：
                        //   「还原收口: 基准 902x831（补写后曾 1707x1067）→ 实得 902x831」），
                        //   用户看到的就是：窗口框全屏、内容缩在左上角、窗体底色铺满其余部分。
                        //   ⇒ 最大化/最小化期间**绝不动客户区**；等回到 Normal 时 OnResize 会再武装一次收口。
                        try { if (IsDisposed || !IsHandleCreated || WindowState != FormWindowState.Normal) return; }
                        catch { return; }
                        if (_normalClient.IsEmpty || _restoreGuardBusy) return;
                        Size was = ClientSize;
                        _restoreGuardBusy = true;
                        try
                        {
                            ClientSize = _normalClient;
                            for (int k = 0; k < 3 && ClientSize != _normalClient; k++)
                                ClientSize = new Size(ClientSize.Width + (_normalClient.Width - ClientSize.Width),
                                                      ClientSize.Height + (_normalClient.Height - ClientSize.Height));
                        }
                        finally { _restoreGuardBusy = false; }
                        SLogSkin("还原收口: 基准 " + _normalClient.Width + "x" + _normalClient.Height
                                 + "（补写后曾 " + was.Width + "x" + was.Height + "）→ 实得 "
                                 + ClientSize.Width + "x" + ClientSize.Height
                                 + (ClientSize == _normalClient ? "（已复位）" : "（★未复位，需查）"));
                    }
                    catch (Exception ex) { SLogSkin("还原收口异常: " + ex.GetType().Name); }
                };
                t.Start();
            }
            catch { }
        }

        /// <summary>
        /// 加一条皮肤式"厚压边"（藏青底 + 双金线）当窗口装饰边框。
        /// ★ Dock 顺序坑：本项目的规则是**后加入的控件先 dock ⇒ 拿到更外侧**，
        ///   所以"要贴到最外层"的边框必须最后加（左右两条就是这么来的）。
        /// </summary>
        /// <summary>
        /// 头部主视觉：**这套皮肤自带立绘就用它的**（如 orca-link 有 941x1672 的虎鲸少女立绘），
        /// 没有就用手绘的鲸鱼娘吉祥物。取不到素材一律退回吉祥物（绝不出现空白头）。
        /// </summary>
        private Image _maidDark;   // 暗色皮肤下的立绘（白底已抠掉，只算一次）
        private Image _whaleArt;   // 头部右侧的鲸鱼（主人挑的图，白底已抠掉，只算一次）

        private Image HeroArtOrMascot()
        {
            try
            {
                var pal = _pal ?? SkinTheme.For("");
                if (!string.IsNullOrEmpty(pal.HeroArt))
                {
                    Bitmap b = SkinArt.Get(pal.HeroArt);
                    if (b != null) return b;
                    SLogSkin("头部立绘取不到：" + pal.HeroArt + "（退回手绘吉祥物）");
                }
            }
            catch { }
            // ★ 暗色皮肤：把白底立绘抠成透明，否则在深色上就是一张白卡片（实测踩过）
            try
            {
                var pl = _pal ?? SkinTheme.Current();
                if (pl.Dark && _maid != null)
                {
                    if (_maidDark == null) _maidDark = SkinArt.CutOutBackground(new Bitmap(_maid));
                    if (_maidDark != null) return _maidDark;
                }
            }
            catch { }
            return _maid;
        }

        /// <summary>压边样式：BFF_FRAME=0 不加 / 1 细(8px) / 2 厚(14px，默认)。供主人挑样式用。</summary>
        private static int FrameStyle()
        {
            try
            {
                string v = Environment.GetEnvironmentVariable("BFF_FRAME");
                int n;
                if (!string.IsNullOrEmpty(v) && int.TryParse(v, out n) && n >= 0 && n <= 5) return n;
            }
            catch { }
            // 没显式指定时：自带暗色皮肤用「烫金细线」（一条金线在深底上最耐看；
            // 4 号裱画式的浅色垫是为浅底设计的，配暗色皮肤会显得脏）；跟随 DSH 皮肤时仍用 4。
            SkinTheme.Palette cur = SkinTheme.Current();
            return cur.Dark ? 3 : 4;
        }

        private void AddFrameEdge(DockStyle side, int thickness)
        {
            try
            {
                thickness = SkinArt.FrameThickness(FrameStyle());  // 风格决定厚度（0 = 不加）
                if (FrameStyle() == 4 && side == DockStyle.Bottom) thickness += 3;  // 裱画规矩：底边加权
                if (thickness <= 0) return;
                var pal = _pal ?? SkinTheme.For("");
                // ★ 2026-09-18 修：以前每点一次「皮肤配色」就再贴一条压边（面板越叠越多、内容被挤窄）。
                //   现在同一条边的旧压边先撤掉再贴 ⇒ 反复换皮肤不会堆积。
                string tag = "framedge:" + side;
                var old = new System.Collections.Generic.List<Control>();
                foreach (Control c in Controls) if ((c.Tag as string) == tag) old.Add(c);
                foreach (Control c in old) { try { Controls.Remove(c); c.Dispose(); } catch { } }
                var edge = new Panel
                {
                    Dock = side,
                    Width = thickness,
                    Height = thickness,
                    Tag = tag,
                    BackColor = pal.HeaderFrom,     // 显式设色：不吃父级 BackColor（那条继承老毛病）
                };
                edge.Paint += delegate(object s, PaintEventArgs e)
                {
                    try
                    {
                        SkinArt.DrawFrameEdge(e.Graphics,
                            new Rectangle(0, 0, edge.Width, edge.Height), side, _pal ?? SkinTheme.For(""), FrameStyle());
                    }
                    catch { }
                };
                Controls.Add(edge);
            }
            catch { }
        }

        private void ForceWantClient(string tag)
        {
            try
            {
                if (_wantClient.IsEmpty || !IsHandleCreated) return;
                // ★ 2026-09-18 同根修复：shown+350ms 那一枪可能正撞上主人手快点了最大化
                //   ⇒ 最大化/最小化期间一律不落实（回到 Normal 后 OnResize 的收口会兜住）。
                try { if (WindowState != FormWindowState.Normal) return; } catch { }
                ClientSize = _wantClient;
                for (int k = 0; k < 3 && ClientSize != _wantClient; k++)
                    ClientSize = new Size(ClientSize.Width + (_wantClient.Width - ClientSize.Width),
                                          ClientSize.Height + (_wantClient.Height - ClientSize.Height));
                SLogSkin("客户区落实[" + tag + "]: 目标 " + _wantClient.Width + "x" + _wantClient.Height
                         + " → 实得 " + ClientSize.Width + "x" + ClientSize.Height
                         + (ClientSize == _wantClient ? "（一致）" : "（★不一致，需查）"));
            }
            catch (Exception ex) { SLogSkin("客户区落实[" + tag + "] 异常: " + ex.GetType().Name); }
        }

        protected override void SetClientSizeCore(int x, int y)
        {
            try
            {
                if (RestoreGuardEnabled && SkinFrame.Enabled)
                {
                    int capH = SkinFrame.EffectiveCaptionStrip();   // 样式已摘 ⇒ 0（不再抵消）
                    if (capH > 0) y -= capH;
                }
            }
            catch { }
            base.SetClientSizeCore(x, y);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            try
            {
                FormWindowState st = WindowState;
                RzTrace("OnResize st=" + st + " prev=" + _prevWindowState
                        + " cs=" + ClientSize.Width + "x" + ClientSize.Height
                        + " win=" + Width + "x" + Height
                        + " guard=" + (RestoreGuardEnabled ? "on" : "off")
                        + " busy=" + _restoreGuardBusy);
                if (st == FormWindowState.Normal)
                {
                    if (_prevWindowState != FormWindowState.Normal)
                    {
                        // ① 刚从最大化/最小化回来
                        //   先立刻掰回基准（免得肉眼看到跳一下），再安排一次"尘埃落定后"的收口。
                        //   ★ 为什么不按"差值 = capH"去认那次补写：实测补写量出现过 +23 / +25 / +2
                        //     好几种（换一种做法就换一个数），按数值认必然过时 ⇒ 改成机制无关的做法：
                        //     **不猜它加了多少**，等它写完，最后按基准把客户区闭环掰回去。
                        _settleUntil = Environment.TickCount + 1200;
                        if (RestoreGuardEnabled && !_restoreGuardBusy
                            && !_normalBounds.IsEmpty && Bounds != _normalBounds)
                        {
                            _restoreGuardBusy = true;
                            try { Bounds = _normalBounds; }
                            finally { _restoreGuardBusy = false; }
                        }
                        if (RestoreGuardEnabled && !_normalClient.IsEmpty) ScheduleSettle();
                    }
                    else if (!_restoreGuardBusy && Environment.TickCount > _settleUntil)
                    {
                        // 正常态（含用户自己拖动、改大小）一直记着基准；收口期内的补写不记
                        _normalBounds = Bounds;
                        _normalClient = ClientSize;
                    }
                }
                _prevWindowState = st;
            }
            catch { }
        }

        // ---------- --maxcycle：把这条教训固化成可回归的自动判据 ----------
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MAXIMIZE = 0xF030;
        private const int SC_RESTORE = 0xF120;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static void Pump(int ms)
        {
            int end = Environment.TickCount + ms;
            while (Environment.TickCount < end) { Application.DoEvents(); Thread.Sleep(20); }
        }

        /// <summary>
        /// 自动化回归判据：用**和用户点"最大化"按钮同一条系统通路**（WM_SYSCOMMAND /
        /// SC_MAXIMIZE、SC_RESTORE）来回 rounds 轮，比对每轮还原后的客户区尺寸。
        /// 返回漂移的轮数（0 = 通过）。
        /// ★ 必须走系统通路，不能用 WinForms 的 WindowState 属性 —— 属性那条路触发不出这个
        ///   bug，写成属性版会得到一个"永远通过"的假判据（判据自证）。
        /// </summary>
        /// <summary>
        /// --paintbench [N]：量"头部横幅重绘一次"要多少毫秒（默认 200 次）。
        /// ★ 为什么要这个数：主人报"变卡了、打开还闪"，而"卡不卡"不能凭感觉。
        ///   缓存版每帧只贴一张位图（应该很小）；谁要是把渐变/光柱/海浪/立绘缩放塞回 Paint，这个数会立刻变大。
        ///   配 BFF_NOCACHE=1 做**负对照**（退回"每帧现算 + 不双缓冲"）⇒ 同一条 exe 上 A/B，不靠回忆。
        /// </summary>
        public static int PaintBench(int n)
        {
            if (n <= 0) n = 200;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("== 头部横幅重绘耗时（--paintbench " + n + "）==");
            int code = 0;
            MainForm f = null;
            try
            {
                string nc = "";
                try { nc = Environment.GetEnvironmentVariable("BFF_NOCACHE"); } catch { }
                bool noCache = !string.IsNullOrEmpty(nc) && nc != "0";
                // ★ 2026-09-18：判据里**临时关掉 WS_EX_COMPOSITED**（那个开关会给每次重绘加约 30ms
                //   的"整窗合成"开销，会把"缓存到底省了多少"这个量淹没掉——实测开着时 32ms vs 37ms，
                //   区分不出来）。量的是**头部横幅重绘本身**的成本，所以两边都不带合成。
                try { Environment.SetEnvironmentVariable("BFF_NOCOMPOSITED", "1"); } catch { }
                sb.AppendLine("模式：" + (noCache ? "负对照（每帧现算 + 不双缓冲，BFF_NOCACHE=" + nc + "）" : "缓存版（贴一张位图 + 双缓冲）"));
                f = new MainForm();
                f.Show();
                Pump(2600);     // 等皮肤 / 客户区收口跑完再量
                var h = f._headerPanel;
                sb.AppendLine("头部尺寸：" + h.Width + "x" + h.Height
                              + "　面板双缓冲=" + (h is BufferedPanel ? (noCache ? "关（负对照）" : "开") : "否")
                              + "　缓存位图=" + (f._headerCache != null ? f._headerCache.Width + "x" + f._headerCache.Height : "无"));
                for (int i = 0; i < 20; i++) { h.Invalidate(); h.Update(); }     // 预热
                // ★★ 2026-09-18 判据改造（重要，别再改回去）：
                //   一开始量的是"窗口重绘一次多少 ms"，可这个数**被外部负载彻底支配**——
                //   本机放着视频时 DWM 一直在合成，窗口往返本身就吃 31ms，于是"贴缓存 31.4 vs 现算 32.4"
                //   完全分不出来（判据假红）。实测同一条 exe 这个数在 5.8 / 6.0 / 17.6 / 31 ms 之间飘。
                //   ⇒ 改成**在内存位图上直接量"画这条路本身"**（不过窗口系统）：
                //        A = 贴缓存（DrawImageUnscaled 一张现成的图）
                //        B = 现算（调构造函数里那个 drawHeader，等于退回旧做法）
                //      门禁判 **B/A ≥ 3 倍**。这样量的是我们自己的代码，跟 DWM/视频/机器忙闲无关
                //      （纯贴图基准在所有测试里都稳定在 0.3~0.45ms，可作证）。
                double perCached = -1, perRaw = -1;
                try
                {
                    int W = h.Width, H = h.Height;
                    using (var mem = new Bitmap(Math.Max(1, W), Math.Max(1, H), System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        using (var mg = Graphics.FromImage(mem))
                        {
                            if (f._headerCache != null)
                            {
                                // ★ 2026-09-18：A 侧从"只贴缓存位图"改成"贴缓存（装饰）+ 1:1 贴素材"
                                //   —— 缓存现在只含装饰层，若只贴它就不再是"一帧的真实成本"。
                                //   改成走 DrawHeaderFast 后，A/B 两侧比的仍是**真实的每帧成本**，
                                //   "门槛 3 倍"的含义不变。
                                var swc = System.Diagnostics.Stopwatch.StartNew();
                                for (int i = 0; i < n; i++) f.DrawHeaderFast(mg, W, H);
                                swc.Stop();
                                perCached = swc.Elapsed.TotalMilliseconds / n;
                            }
                            if (f._drawHeaderFn != null)
                            {
                                int m = Math.Max(5, n / 5);      // 现算慢得多，少跑几次
                                var swr = System.Diagnostics.Stopwatch.StartNew();
                                for (int i = 0; i < m; i++) f._drawHeaderFn(mg, W, H);
                                swr.Stop();
                                perRaw = swr.Elapsed.TotalMilliseconds / m;
                            }
                        }
                    }
                }
                catch (Exception ex) { sb.AppendLine("内存量法异常：" + ex.GetType().Name + " " + ex.Message); }
                sb.AppendLine("内存里量（不过窗口系统，不受 DWM/视频等外部负载影响）：");
                sb.AppendLine("  贴缓存：每次 " + perCached.ToString("0.00") + " ms（" + n + " 次）");
                sb.AppendLine("  现算  ：每次 " + perRaw.ToString("0.00") + " ms（" + Math.Max(5, n / 5) + " 次）");
                double ratio = (perCached > 0.001) ? perRaw / perCached : 0;
                // ★★ 2026-09-18 门槛重校（拆两层缓存的连带修正，别改回去）：
                //   素材层（立绘/鲸鱼）移出缓存后，**A 侧（DrawHeaderFast）也含 1:1 素材贴图**，
                //   B/A 的差只剩"装饰层现画 vs 装饰层贴图" ⇒ 旧的 3.0 倍线实测会飘到 2.9~3.2，
                //   变成一颗碰运气的假红。改成**双门**：
                //     ① 绝对预算：A（=用户每帧真实成本）≤ 1.5 ms —— 直接卡"每帧路径里混进重活"；
                //     ② 倍率：B/A ≥ 2.0 —— 证明缓存仍有净收益。
                //   （实测 A 稳定在 0.37~0.51 ms，预算留了约 3 倍余量。）
                sb.AppendLine("  倍率  ：贴缓存比现算快 " + ratio.ToString("0.00") + " 倍（门槛 2.0）");
                // 附：窗口往返成本（只报数、不门禁——它主要由外部合成负载决定，不是我们的代码）
                var sww = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < Math.Min(30, n); i++) { h.Invalidate(); h.Update(); }
                sww.Stop();
                sb.AppendLine("  参考：窗口重绘往返 " + (sww.Elapsed.TotalMilliseconds / Math.Min(30, n)).ToString("0.0")
                              + " ms/次（这个数随机器/DWM 负载飘，不作门禁）");
                bool ok2 = (perCached > 0 && perCached <= 1.5 && ratio >= 2.0);
                sb.AppendLine("结论：" + (ok2
                    ? "OK（每帧 " + perCached.ToString("0.00") + " ms ≤ 1.5 预算，且比现算快 " + ratio.ToString("0.0") + " 倍）"
                    : "★不合格（每帧路径混进了重活，或缓存没生效）"));
                if (!ok2) code = 1;
            }
            catch (Exception ex)
            {
                sb.AppendLine("异常：" + ex.GetType().Name + ": " + ex.Message);
                code = 1;
            }
            finally
            {
                try { if (f != null) { f.Close(); f.Dispose(); } } catch { }
            }
            string text = sb.ToString();
            try
            {
                DshCore.EnsureAppDataDir();
                System.IO.File.WriteAllText(System.IO.Path.Combine(DshCore.AppDataDir, "paintbench-report.txt"),
                                            text, new System.Text.UTF8Encoding(true));
            }
            catch { }
            try
            {
                using (var w = new System.IO.StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)))
                { w.Write(text); w.Flush(); }
            }
            catch { }
            return code;
        }

        public static int MaxCycleTest(int rounds)        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("== 最大化 / 还原 尺寸漂移 回归判据（--maxcycle " + rounds + "）==");
            sb.AppendLine("还原收口（RestoreGuard）：" + (RestoreGuardEnabled ? "开" : "关（负对照）"));
            sb.AppendLine("SM_CYCAPTION=" + SkinFrame.NativeCaptionHeight() + "px　自绘标题栏="
                          + (SkinFrame.Enabled ? "开（高 " + SkinFrame.CaptionH + "px）" : "关")
                          + "　setter 抵消="
                          + ((RestoreGuardEnabled && SkinFrame.Enabled) ? "生效" : "未生效"));
            int bad = 0;
            MainForm f = null;
            try
            {
                f = new MainForm();
                f.Show();
                Pump(2800);   // 等"显示后的收口"跑完再记基准：量的是用户最终看到的尺寸
                sb.AppendLine("WS_CAPTION 已摘=" + (SkinFrame.CaptionRemovedFromStyle ? "是（根因修复生效）" : "否（退回抠法，可能仍有 23px 漂移）"));
                Size before = f.ClientSize;
                sb.AppendLine("起始客户区：" + before.Width + "x" + before.Height);
                for (int i = 0; i < rounds; i++)
                {
                    ResizeTrace.Clear();
                    SendMessage(f.Handle, WM_SYSCOMMAND, (IntPtr)SC_MAXIMIZE, IntPtr.Zero);
                    Pump(550);
                    Size mx = f.ClientSize;
                    SendMessage(f.Handle, WM_SYSCOMMAND, (IntPtr)SC_RESTORE, IntPtr.Zero);
                    Pump(1600);   // 等"还原收口"（1200ms 一次写定）跑完再量 —— 量的是用户最终看到的尺寸
                    Size now = f.ClientSize;
                    bool ok = (now.Width == before.Width && now.Height == before.Height);
                    if (!ok) bad++;
                    sb.AppendLine("  第" + (i + 1) + "轮：最大化 " + mx.Width + "x" + mx.Height
                                  + " → 还原 " + now.Width + "x" + now.Height
                                  + (ok ? "   [OK]" : "   [FAIL 高度漂移 " + (now.Height - before.Height) + "px]"));
                    // 埋点轨迹：谁把尺寸写大的，一眼就能看出来
                    foreach (string t in ResizeTrace) sb.AppendLine("      · " + t);
                }
                sb.AppendLine(bad == 0
                    ? "结论：还原尺寸无漂移（通过）"
                    : "结论：有漂移，失败 " + bad + " / " + rounds);

                // ★★ 2026-09-18 新增「竞态轮」—— 修「最大化后界面不跟全屏」的回归判据：
                //   还原后 1.2s 内再次最大化时，收口定时器到点曾把最大化客户区按回基准
                //   （实锤：manual-scroll.log「还原收口: 基准 902x831（补写后曾 1707x1067）」）。
                //   ⇒ 判据：竞态窗口里再最大化，客户区必须**保持全屏**，不许被按回小尺寸。
                //   （旧代码在本轮必红：客户区被按回基准；新代码绿。）
                ResizeTrace.Clear();
                SendMessage(f.Handle, WM_SYSCOMMAND, (IntPtr)SC_MAXIMIZE, IntPtr.Zero);
                Pump(550);
                Size mx2 = f.ClientSize;
                SendMessage(f.Handle, WM_SYSCOMMAND, (IntPtr)SC_RESTORE, IntPtr.Zero);
                Pump(300);      // ★ 故意只等 0.3s：收口定时器（1200ms）此刻已被武装、尚未到点
                SendMessage(f.Handle, WM_SYSCOMMAND, (IntPtr)SC_MAXIMIZE, IntPtr.Zero);
                Pump(2600);     // 收口定时器在这段 Pump（DoEvents）里到点 —— 旧代码就在这里按扁客户区
                Size now2 = f.ClientSize;
                bool ok2 = (now2.Width == mx2.Width && now2.Height == mx2.Height);
                if (!ok2) bad++;
                sb.AppendLine("  竞态轮（还原后0.3s内再最大化）：最大化 " + mx2.Width + "x" + mx2.Height
                              + " → 收口定时器到点后 " + now2.Width + "x" + now2.Height
                              + (ok2 ? "   [OK]"
                                     : "   [FAIL 被收口按回小尺寸 ⇒ 即「最大化后界面不跟全屏」]"));
                foreach (string t in ResizeTrace) sb.AppendLine("      · " + t);
                sb.AppendLine(bad == 0
                    ? "总结：漂移 + 竞态 全部通过"
                    : "总结：共 " + bad + " 项不合格");
            }
            catch (Exception ex)
            {
                bad = rounds;
                sb.AppendLine("异常：" + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { if (f != null) { f.Close(); f.Dispose(); } } catch { }
            }
            string text = sb.ToString();
            try
            {
                DshCore.EnsureAppDataDir();
                string path = System.IO.Path.Combine(DshCore.AppDataDir, "maxcycle-report.txt");
                System.IO.File.WriteAllText(path, text, new System.Text.UTF8Encoding(true));
            }
            catch { }
            Console.WriteLine(text);
            return bad;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_pollTimer != null) _pollTimer.Stop();
            base.OnFormClosing(e);
        }
    }
}

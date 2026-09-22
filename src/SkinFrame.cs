using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BigFatFishRescuer
{
    // ============================================================
    //  B 方案：自绘标题栏（2026-09-17 实施）
    //
    //  ★ 这里采用的路线：**不丢掉系统原生外框**，只把"原生标题栏那一截"从非客户区里抠掉，
    //    然后在客户区最顶上自己画一条标题栏。
    //
    //    为什么要这么绕 —— 先说结论：直接用 FormBorderStyle.None 全自绘，
    //    会把**系统免费给你的一整套东西**一起弄丢：
    //       · 八向拉缩放：没有 WS_THICKFRAME，就算在 WM_NCHITTEST 里返回 HTLEFT 这类命中码，
    //         也进不了系统那套 sizing 循环（很多"教程"让你再偷偷补 WS_THICKFRAME 回来 ——
    //         绕一圈还是回到本方案：既然要补回来，不如一开始别丢）；
    //       · 贴边吸附（拖到屏幕边缘自动半屏/最大化）；
    //       · 最大化/还原的动画、任务栏缩略图的行为；
    //       · 窗口阴影与 Win11 圆角。
    //    本方案只改一件事：**标题栏归我画，其余还是系统管**。
    //    抠掉原生标题栏用的是 WM_NCCALCSIZE（Chrome / Edge / VS 也是这么干的）。
    //
    //  ★ 退回开关：命令行带 --nativeframe，或环境变量 BFF_NATIVE_FRAME=1，
    //    会整体退回原生窗口（自绘代码一行都不执行），用来排障。
    // ============================================================
    public static class SkinFrame
    {
        public const int WM_NCCALCSIZE = 0x0083;
        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int WM_NCLBUTTONDBLCLK = 0x00A3;
        public const int HTCAPTION = 0x0002;

        private const int SM_CYCAPTION = 4;

        /// <summary>自绘标题栏高度（像素）。本程序是 DPI-unaware，直接用逻辑像素即可。</summary>
        public const int CaptionH = 36;

        private static bool _disabled;
        private static string _disableReason = "";

        public static bool Enabled { get { return !_disabled; } }

        /// <summary>整体退回原生窗口（自绘一行都不跑）。</summary>
        public static void Disable(string why)
        {
            _disabled = true;
            _disableReason = why ?? "";
            Log("disabled: " + _disableReason);
        }

        private static void Log(string msg)
        {
            try { Manual.SLog("[skinframe] " + msg); } catch { }
        }

        // ---------- Win32 ----------
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // ============================================================
        //  ★★ 2026-09-18 根因修复：把 WS_CAPTION 从**窗口样式**里摘掉
        //
        //  为什么必须摘（实测取证）：样式里留着 WS_CAPTION，而客户区又被 WM_NCCALCSIZE
        //  抠大了 23px ⇒「窗口尺寸 ↔ 客户区尺寸」两套口径不一致。后果是**每经过一次
        //  "最大化 → 还原"，系统在还原之后还会补一次尺寸回写**，把窗口写高 23px：
        //      --maxcycle 埋点：win=916x822（还原，正确）→ win=916x845（补写，正好 +SM_CYCAPTION）
        //      还原高度序列：822 → 845 → 868 → 891（每轮 +23px，旧交付版是 809 恒定）
        //  补丁式收口压不住它：补写会再来一次，而且"正常态基准"本身被污染成了偏大值。
        //  摘掉 WS_CAPTION 之后，官方非客户区尺寸 == 实际非客户区尺寸 ⇒ 23px 无处可加。
        //
        //  保留的样式才是"行为"所在：WS_THICKFRAME（八向缩放/阴影/圆角）、WS_SYSMENU、
        //  WS_MINIMIZEBOX、WS_MAXIMIZEBOX（贴边吸附、最大化动画、Win11 贴靠布局）；
        //  WS_CAPTION 只管"由系统画那条标题栏"，而我们自己画。
        // ============================================================
        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;
        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOZORDER = 0x0004;
        private const int SWP_FRAMECHANGED = 0x0020;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, int flags);

        private static bool _captionRemovedFromStyle;

        /// <summary>原生标题栏是否已从窗口样式里摘掉（摘掉后 WM_NCCALCSIZE 不用再抠）。</summary>
        public static bool CaptionRemovedFromStyle { get { return _captionRemovedFromStyle; } }

        /// <summary>
        /// 摘掉窗口样式里的 WS_CAPTION。**必须在句柄创建之后调用**（句柄重建要再调一次）。
        /// ★ 判据自证：摘完要**读回**确认那一位真的没了；读回还在就返回 false，
        ///   调用方自动退回原来的"WM_NCCALCSIZE 抠一截"做法（功能不丢，只是仍有 23px 漂移）。
        /// </summary>
        public static bool PrepareWindow(IntPtr hwnd)
        {
            if (!Enabled || hwnd == IntPtr.Zero) return false;
            try
            {
                long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
                if ((style & WS_CAPTION) == 0)
                {
                    _captionRemovedFromStyle = true;          // 本来就没有
                    return true;
                }
                SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)(style & ~(long)WS_CAPTION));
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER);
                long back = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
                bool ok = (back & WS_CAPTION) == 0;           // ← 读回自证
                _captionRemovedFromStyle = ok;
                Log("PrepareWindow: 摘 WS_CAPTION " + (ok ? "成功" : "失败（读回仍在）")
                    + "，style 0x" + style.ToString("X8") + " → 0x" + back.ToString("X8"));
                return ok;
            }
            catch (Exception ex) { Log("PrepareWindow 异常：" + ex.GetType().Name); return false; }
        }

        /// <summary>
        /// 判据自证（无界面版）：建一个隐藏的 Sizable 窗体，对着它**真摘一次** WS_CAPTION 并读回确认。
        /// 为什么需要它：--selftest 不开主窗口、句柄不建，静态标志自然是 false —— 直接读标志会得到
        /// 一个"永远 FAIL"的假判据；而只看代码又回答不了"这台机器上这手法到底行不行"。
        /// 探针只验证手法，跑完把静态标志复位，不影响真窗口自己的那一轮。
        /// </summary>
        public static bool ProbeStyleStrip(out string note)
        {
            note = "";
            if (!Enabled) { note = "未启用自绘标题栏（--nativeframe），无需摘 OK"; return true; }
            try
            {
                using (var probe = new Form())
                {
                    probe.FormBorderStyle = FormBorderStyle.Sizable;
                    probe.ShowInTaskbar = false;
                    probe.StartPosition = FormStartPosition.Manual;
                    probe.Location = new Point(-4000, -4000);
                    probe.Size = new Size(320, 200);
                    IntPtr h = probe.Handle;                       // 强制建句柄
                    long before = GetWindowLongPtr(h, GWL_STYLE).ToInt64();
                    bool ok = PrepareWindow(h);
                    long after = GetWindowLongPtr(h, GWL_STYLE).ToInt64();
                    note = "实摘一次：" + (ok ? "成功" : "失败") + "（读回 0x" + before.ToString("X8")
                         + " → 0x" + after.ToString("X8") + "，WS_CAPTION 位" + ((after & WS_CAPTION) == 0 ? "已清" : "仍在") + "）"
                         + (ok ? " OK" : " ★");
                    _captionRemovedFromStyle = false;          // 探针不改全局状态
                    return ok;
                }
            }
            catch (Exception ex) { note = "探针异常：" + ex.GetType().Name + " ★"; return false; }
        }

        /// <summary>当前还需要从客户区里抠掉多少：样式已摘 ⇒ 0；摘失败 ⇒ SM_CYCAPTION（旧做法）。</summary>
        public static int EffectiveCaptionStrip()
        {
            return _captionRemovedFromStyle ? 0 : NativeCaptionHeight();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NCCALCSIZE_PARAMS
        {
            public RECT rgrc0, rgrc1, rgrc2;
            public IntPtr lppos;
        }

        /// <summary>系统那条原生标题栏有多高（0 表示取不到 —— 那就不动它，别瞎扣）。</summary>
        public static int NativeCaptionHeight()
        {
            try
            {
                int v = GetSystemMetrics(SM_CYCAPTION);
                return v > 0 ? v : 0;
            }
            catch { return 0; }
        }

        // ============================================================
        //  把"原生标题栏那一截"从非客户区里退回去
        //
        //  调用顺序很关键：**先让 DefWindowProc 算出默认客户区**（这一步已经在 lParam 里填好了），
        //  再把它多挪下去的【标题栏高度】减掉 —— 剩下的偏移量正好是一条可缩放外框，
        //  于是"外框还在、标题栏没了"。
        //  这么做的好处：不用猜 SM_CYFRAME / SM_CXPADDEDBORDER 各是多少（猜必错），
        //  只用 SM_CYCAPTION 这一个系统项。
        //
        //  最大化时也不用特判：最大化窗口的 rc.Top 本来就在屏幕外一条框的位置，
        //  减掉标题栏后客户区顶边正好落在 WorkingArea 顶边 ⇒ 不会压任务栏、不留白条。
        // ============================================================
        public static int StripCaptionFromClientRect(IntPtr lParam)
        {
            int capH = EffectiveCaptionStrip();     // ★ 样式已摘 ⇒ 0（不再抠，也不用抠）
            if (capH <= 0 || lParam == IntPtr.Zero) return 0;
            try
            {
                NCCALCSIZE_PARAMS p = (NCCALCSIZE_PARAMS)Marshal.PtrToStructure(lParam, typeof(NCCALCSIZE_PARAMS));
                p.rgrc0.Top -= capH;
                Marshal.StructureToPtr(p, lParam, false);
                return capH;
            }
            catch (Exception ex)
            {
                Log("strip 异常：" + ex.GetType().Name);
                return 0;
            }
        }

        /// <summary>按住标题栏拖动：交给系统那套标题栏拖动流程（顺带保住拖到屏幕边缘的吸附行为）。</summary>
        public static void BeginDrag(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                ReleaseCapture();
                SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
            catch (Exception ex) { Log("drag 异常：" + ex.GetType().Name); }
        }

        /// <summary>双击标题栏：走系统那条 HTCAPTION 双击通路（最大化/还原由 Windows 负责）。</summary>
        public static void ToggleMaximize(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                ReleaseCapture();
                SendMessage(hwnd, WM_NCLBUTTONDBLCLK, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
            catch (Exception ex) { Log("maximize 异常：" + ex.GetType().Name); }
        }

        // ============================================================
        //  自绘标题栏本体
        //
        //  ★ 2026-09-17 二次返工（记一笔，别再犯）：
        //    最初把三颗按钮做成 `SkinFrameButton : Button` 子控件，用 Dock=Right 排布。
        //    结果屏幕上画出来的是：白色方块 + 黑色文字 + 一条纯红色带 —— 三重错位：
        //      ① 子控件 Button 即使 SetStyle(UserPaint) 也仍会走 ButtonBase 自己那条绘制路径，
        //         覆盖掉 OnPaint 画的东西（实测：OnPaint 里探针确认参数全对，屏上却不是它画的）；
        //      ② Button 不支持透明背景（没开 SupportsTransparentBackColor）⇒
        //         BackColor=Transparent 会**回落到父级 BackColor**，而父级又继承了窗体的内容色
        //         ⇒ 按钮变成一块内容色底板；
        //      ③ Dock=Right 的顺序语义反直觉（后加的索引更大 ⇒ 先 dock ⇒ 拿最外侧）。
        //    ⇒ 定稿：**标题栏不挂任何子控件**，标题与三颗按钮全部在 OnPaint 里画，
        //      点击/悬停用矩形命中判定。少一层控件就少一类坑，行为完全可预期。
        // ============================================================
        public sealed class CaptionBar : Panel
        {
            private const int BtnW = 46;      // 每颗按钮宽度
            private const int BtnCount = 3;

            private readonly Form _owner;
            private readonly Func<SkinTheme.Palette> _palProvider;
            private SkinTheme.Palette _pal;
            private bool _active = true;
            private int _hot = -1;            // 悬停的按钮：0 最小化 / 1 最大化 / 2 关闭 / -1 无
            private Font _titleFont;
            private Font _glyphFont;
            private static int _diagBar;

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    if (_titleFont != null) { _titleFont.Dispose(); _titleFont = null; }
                    if (_glyphFont != null) { _glyphFont.Dispose(); _glyphFont = null; }
                }
                base.Dispose(disposing);
            }

            public static CaptionBar Attach(Form owner, Func<SkinTheme.Palette> paletteProvider)
            {
                if (owner == null) return null;
                try
                {
                    var bar = new CaptionBar(owner, paletteProvider);
                    owner.HandleCreated += delegate { try { bar.Invalidate(); } catch { } };
                    owner.Activated += delegate { try { bar.SetActive(true); } catch { } };
                    owner.Deactivate += delegate { try { bar.SetActive(false); } catch { } };
                    owner.Resize += delegate { try { bar.Invalidate(); } catch { } };
                    return bar;
                }
                catch (Exception ex)
                {
                    Log("attach 异常：" + ex.GetType().Name + "（已整体退回原生窗口）");
                    SkinFrame.Disable("标题栏创建失败：" + ex.GetType().Name);
                    return null;
                }
            }

            private CaptionBar(Form owner, Func<SkinTheme.Palette> paletteProvider)
            {
                _owner = owner;
                _palProvider = paletteProvider;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.UserPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.ResizeRedraw, true);
                Dock = DockStyle.Top;
                Height = CaptionH;
                Margin = Padding.Empty;
                BackColor = Color.FromArgb(20, 51, 95);   // 兜底底色，避免继承窗体内容色
                try { _pal = SkinTheme.For(""); } catch { _pal = null; }
                try { ApplyPalette(paletteProvider()); } catch { }
            }

            // ---------- 三颗按钮的矩形（都在右侧，自右向左：关闭 / 最大化 / 最小化） ----------
            private Rectangle ButtonRect(int index)
            {
                // index: 0=最小化 1=最大化 2=关闭
                int right = Width - (BtnCount - 1 - index) * BtnW;
                int left = right - BtnW;
                return new Rectangle(left, 0, BtnW, Height);
            }

            /// <summary>命中判定：返回 0/1/2，未命中返回 -1。留 2px 间隔，避免误触。</summary>
            private int HitButton(Point p)
            {
                for (int i = 0; i < BtnCount; i++)
                {
                    Rectangle r = ButtonRect(i);
                    r.Inflate(-1, 0);
                    if (r.Contains(p)) return i;
                }
                return -1;
            }

            private bool IsMaximized()
            {
                try { return _owner.WindowState == FormWindowState.Maximized; } catch { return false; }
            }

            private void SetActive(bool v)
            {
                if (_active == v) return;
                _active = v;
                Invalidate();
            }

            public void ApplyPalette(SkinTheme.Palette pal)
            {
                try
                {
                    _pal = pal ?? SkinTheme.For("");
                    Invalidate();
                }
                catch { }
            }

            private static string GlyphFor(int index, bool maximized)
            {
                if (index == 0) return "—";          // 最小化
                if (index == 1) return maximized ? "❐" : "☐";   // 还原 / 最大化
                return "✕";                          // 关闭
            }

            private void DoButton(int index)
            {
                try
                {
                    if (index == 0) _owner.WindowState = FormWindowState.Minimized;
                    else if (index == 1) SkinFrame.ToggleMaximize(_owner.Handle);
                    else _owner.Close();
                }
                catch (Exception ex) { Log("按钮动作异常：" + ex.GetType().Name); }
            }

            // ---------- 鼠标：拖动 / 双击 / 三颗按钮 ----------
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                int h = HitButton(e.Location);
                if (h != _hot)
                {
                    _hot = h;
                    Cursor = (h >= 0) ? Cursors.Hand : Cursors.Default;
                    Invalidate();
                }
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (_hot != -1) { _hot = -1; Cursor = Cursors.Default; Invalidate(); }
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                int h = HitButton(e.Location);
                if (h >= 0) { _hot = h; Invalidate(); return; }   // 按钮上：等 MouseUp 再执行
                SkinFrame.BeginDrag(_owner.Handle);               // 空白处：交给系统拖动
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button != MouseButtons.Left) return;
                int h = HitButton(e.Location);
                if (h >= 0 && h == _hot) DoButton(h);
                _hot = -1;
                Invalidate();
            }

            protected override void OnMouseDoubleClick(MouseEventArgs e)
            {
                base.OnMouseDoubleClick(e);
                if (e.Button != MouseButtons.Left) return;
                if (HitButton(e.Location) >= 0) return;           // 点的是按钮，不做最大化
                SkinFrame.ToggleMaximize(_owner.Handle);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var pal = _pal ?? SkinTheme.For("");
                try
                {
                    Color from = pal.HeaderFrom, to = pal.HeaderTo;
                    if (!_active) { from = Dim(from); to = Dim(to); }
                    using (var lg = new LinearGradientBrush(new Point(0, 0), new Point(Width, 0), from, to))
                    {
                        e.Graphics.FillRectangle(lg, 0, 0, Width, Height);
                    }

                    // 三颗按钮（先画底，再画底边饰线，最后画字形）
                    for (int i = 0; i < BtnCount; i++)
                    {
                        if (i != _hot) continue;
                        Color hb = (i == 2) ? Color.FromArgb(232, 90, 96) : pal.Gold;
                        bool down = (Control.MouseButtons & MouseButtons.Left) != 0;
                        using (var sb = new SolidBrush(Color.FromArgb(down ? 210 : 120, hb)))
                            e.Graphics.FillRectangle(sb, ButtonRect(i));
                    }

                    // 底边饰线：金色平色。
                    // ★ 2026-09-18：原先是横铺 maid-atelier 的金线花边素材 ⇒ 那套素材是 CC BY-NC-SA，
                    //   交付要带整条署名链；主人不要那份署名 ⇒ 素材全部退役，饰线回到自绘平色。
                    using (var br = new SolidBrush(_active ? pal.Gold : Dim(pal.Gold)))
                        e.Graphics.FillRectangle(br, 0, Height - 3, Width, 3);

                    // 标题
                    string title = BuildTitle(_owner, pal);
                    Color fg = _active ? Color.White : Color.FromArgb(215, 220, 230);
                    if (_titleFont == null) _titleFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
                    int titleW = Math.Max(10, Width - BtnW * BtnCount - 30);
                    TextRenderer.DrawText(e.Graphics, title, _titleFont,
                        new Rectangle(14, 0, titleW, Height),
                        fg, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                    // 三颗按钮的字形
                    if (_glyphFont == null) _glyphFont = new Font("Segoe UI Symbol", 10f);
                    for (int i = 0; i < BtnCount; i++)
                    {
                        Rectangle r = ButtonRect(i);
                        TextRenderer.DrawText(e.Graphics, GlyphFor(i, IsMaximized()), _glyphFont, r,
                            Color.White,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    }

                    if (_diagBar < 1)
                    {
                        _diagBar++;
                        Log("caption bar: size=" + Width + "x" + Height
                            + " 子控件=" + Controls.Count + "（应为 0） 按钮矩形="
                            + ButtonRect(0) + ButtonRect(1) + ButtonRect(2));
                    }
                }
                catch (Exception ex) { Log("captionbar paint 异常：" + ex.GetType().Name); }
            }

            private static string BuildTitle(Form owner, SkinTheme.Palette pal)
            {
                string t = "";
                try { t = owner.Text ?? ""; } catch { }
                if (pal != null && pal.Key != "generic") t = t + "   ｜ SKIN: " + pal.Name;
                return t;
            }

            private static Color Dim(Color c)
            {
                int r = (int)(c.R * 0.72), g = (int)(c.G * 0.72), b = (int)(c.B * 0.72);
                return Color.FromArgb(Math.Min(255, r), Math.Min(255, g), Math.Min(255, b));
            }
        }

        // ============================================================
        //  判据（给 --skin / --selftest 用）
        // ============================================================
        public static string Report()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("-- 自绘标题栏（B 方案）--");
            sb.AppendLine(Enabled
                ? "[OK]   已启用：标题栏由程序自绘（高 " + CaptionH + "px），原生外框保留（缩放/吸附/最大化仍由系统负责）"
                : "[?]    未启用（退回原生窗口）：" + _disableReason);
            sb.AppendLine("       抠掉原生标题栏的方式：WM_NCCALCSIZE 回退 SM_CYCAPTION = " + NativeCaptionHeight() + "px");
            sb.AppendLine("       拖动/双击最大化：ReleaseCapture + WM_NCLBUTTONDOWN(DBLCLK)/HTCAPTION");
            sb.AppendLine("       三颗按钮：标题栏内自绘（无子控件）+ 矩形命中判定");
            sb.AppendLine("       退回原生窗口的办法：命令行 --nativeframe 或环境变量 BFF_NATIVE_FRAME=1");
            return sb.ToString();
        }

        /// <summary>
        /// 自检判据（无窗口也能跑）。
        /// ① SM_CYCAPTION 必须 > 0（否则不该去扣标题栏）；
        /// ② 真在内存里摆一个 NCCALCSIZE_PARAMS，扣完必须正好差标题栏那么高（纯算法，可复现）；
        /// ③ CaptionBar 挂到一个隐藏 Form 上不得抛异常，且**不得挂任何子控件**（返工教训的判据）。
        /// </summary>
        public static string SelftestLine()
        {
            try
            {
                int capH = NativeCaptionHeight();
                bool ok = capH > 0;

                // ② 纯算法：模拟 DefWindowProc 已经填好默认客户区 rc.Top = 130
                int removed = 0;
                IntPtr mem = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NCCALCSIZE_PARAMS)));
                try
                {
                    var p = new NCCALCSIZE_PARAMS();
                    p.rgrc0.Left = 100; p.rgrc0.Top = 130; p.rgrc0.Right = 900; p.rgrc0.Bottom = 700;
                    Marshal.StructureToPtr(p, mem, false);
                    removed = StripCaptionFromClientRect(mem);
                    NCCALCSIZE_PARAMS after = (NCCALCSIZE_PARAMS)Marshal.PtrToStructure(mem, typeof(NCCALCSIZE_PARAMS));
                    ok = ok && removed == capH && after.rgrc0.Top == (130 - capH);
                }
                finally { Marshal.FreeHGlobal(mem); }

                // ③ 挂到一个隐藏窗口上不能抛异常，且不能有子控件
                int bars = 0;
                int kids = -1;
                try
                {
                    using (var probe = new Form())
                    {
                        probe.ShowInTaskbar = false;
                        probe.Opacity = 0;
                        var bar = CaptionBar.Attach(probe, delegate { return SkinTheme.For(""); });
                        if (bar != null)
                        {
                            probe.Controls.Add(bar);
                            IntPtr h = probe.Handle;      // 逼出句柄，走一遍真实创建
                            bars = 1;
                            kids = bar.Controls.Count;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("selftest attach 异常：" + ex.GetType().Name);
                    ok = false;
                }
                ok = ok && kids == 0;

                return "自绘标题栏（SM_CYCAPTION=" + capH + "px、扣减=" + removed + "px、样例挂载=" + bars
                     + "、子控件=" + kids + "）: " + (ok ? "OK" : "FAIL");
            }
            catch (Exception ex)
            {
                return "自绘标题栏: FAIL " + ex.GetType().Name;
            }
        }
    }
}

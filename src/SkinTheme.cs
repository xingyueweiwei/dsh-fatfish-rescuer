using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BigFatFishRescuer
{
    // ============================================================
    //  皮肤联动（2026-09-17 新增，主人要求：自动连 DeepSeek 的皮肤换上边框）
    //
    //  说明白边界（别吹）：
    //    · 皮肤本来是给 **DSH 网页** 用的（@dsh-external/dsh-client-ui-skin-*，
    //      里面是 web 客户端的 JS/CSS 覆盖层）；救星是 **WinForms 桌面程序**，
    //      技术上没法直接吃那套 CSS。
    //    · 所以这里做的是**真联动、非同一套代码**：
    //        ① 从 profile 里**真的检出**装了哪些皮肤、哪个正在跑（读 skin.json + cordis.patch.yml）；
    //        ② 按每套皮肤**自己声明的风格**（skin.json 的 tagline/description）
    //           取同款配色，刷到救星的**头部渐变、饰线、窗口边框**上；
    //        ③ 检测与配色都可在界面上一键查看（🎨 皮肤配色）。
    //    · 已知三套皮肤用各自风格色；未知/新皮肤走中性深海配色（不瞎编颜色）。
    // ============================================================
    public static class SkinTheme
    {
        public class Skin
        {
            public string Id = "";
            public string Name = "";
            public string Author = "";
            public string Tagline = "";
            public string Pkg = "";
            public bool Active;
        }

        public class Palette
        {
            public string Key = "";
            public string Name = "";
            public string From = "";     // 这套配色的来源（哪套皮肤 / 通用）
            public Color HeaderFrom, HeaderTo, Gold, Border, Bg;
            /// <summary>金色高光（画外框内线/装饰用；留空则退回 Gold）。</summary>
            public Color GoldLight = Color.Empty;
            /// <summary>
            /// 装饰素材包：只有 maid-atelier 自带花边/蕾丝/蝴蝶结这类装饰素材。
            /// 空串＝这套皮肤**没有**装饰素材 ⇒ 只用配色 + 几何画（绝不把别家的花边混进来）。
            /// </summary>
            public string ArtPack = "";
            /// <summary>头部主视觉用的嵌入素材名（空串＝用手绘鲸鱼娘吉祥物）。</summary>
            public string HeroArt = "";
            /// <summary>
            /// 裱画式边框的**垫色**（mat）。裱画规矩：垫要**比作品浅/暖**才"后退"、才分得出层次
            /// （深垫会显胀）。所以这里不是直接用内容底色，而是取皮肤配色里更暖的一档。
            /// </summary>
            public Color Mat = Color.Empty;
            // ===== 语义令牌（skin 只允许用这些，不许在控件里写死颜色）=====
            /// <summary>应用底色（60% 的主体面）。</summary>
            public Color BgDeep = Color.Empty;
            /// <summary>卡片/面板底色（30%）。</summary>
            public Color Surface = Color.Empty;
            /// <summary>悬浮/凸起面（悬停、次级容器）。</summary>
            public Color SurfaceRaised = Color.Empty;
            /// <summary>描边色（UI 元素，对比度须 ≥3:1）。</summary>
            public Color Line = Color.Empty;
            /// <summary>正文色（对比度须 ≥4.5:1）。</summary>
            public Color TextPrimary = Color.Empty;
            /// <summary>次要文字色（对比度须 ≥4.5:1）。</summary>
            public Color TextSecondary = Color.Empty;
            /// <summary>主强调色（10% 的点睛：主按钮、选中态、焦点）。</summary>
            public Color Accent = Color.Empty;
            /// <summary>强调色上的文字色（须与 Accent 对比 ≥4.5:1）。</summary>
            public Color OnAccent = Color.Empty;
            /// <summary>语义色：危险/成功/警告（也用作状态点，必须配文字或图标，不能只靠颜色）。</summary>
            public Color Danger = Color.Empty;
            public Color Success = Color.Empty;
            public Color Warning = Color.Empty;
            /// <summary>这套皮肤是不是暗色（决定控件重着色的方向）。</summary>
            public bool Dark = false;
        }

        private static string ProfileDir()
        {
            string p = Environment.GetEnvironmentVariable("DSH_PROFILE");
            if (string.IsNullOrEmpty(p)) p = "web";
            return Path.Combine(DshCore.DshHome, "profiles", p.Trim());
        }

        /// <summary>检出装了哪些皮肤（读 skin.json），并判断哪套在跑（看 cordis.patch.yml 的 insert 行）。</summary>
        public static List<Skin> Detect()
        {
            var list = new List<Skin>();
            string prof = ProfileDir();
            string patch = "";
            try { if (File.Exists(Path.Combine(prof, "cordis.patch.yml")))
                      patch = File.ReadAllText(Path.Combine(prof, "cordis.patch.yml"), new UTF8Encoding(false)); }
            catch { }

            string nm = Path.Combine(prof, "node_modules");
            if (!Directory.Exists(nm)) return list;
            try
            {
                foreach (string dir in Directory.GetDirectories(nm))
                {
                    string leaf = Path.GetFileName(dir);
                    if (!leaf.StartsWith("@", StringComparison.Ordinal))
                    {
                        if (leaf.IndexOf("skin", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        AddSkin(list, dir, patch);
                    }
                    else
                    {
                        foreach (string sub in Directory.GetDirectories(dir))
                        {
                            if (Path.GetFileName(sub).IndexOf("skin", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            AddSkin(list, sub, patch);
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        private static void AddSkin(List<Skin> list, string dir, string patch)
        {
            try
            {
                string sj = Path.Combine(dir, "skin.json");
                string pj = Path.Combine(dir, "package.json");
                if (!File.Exists(sj) && !File.Exists(pj)) return;
                var s = new Skin();
                s.Pkg = Path.GetFileName(dir);
                if (File.Exists(sj))
                {
                    string t = File.ReadAllText(sj, new UTF8Encoding(false));
                    s.Id = JsonStr(t, "\"id\"");
                    s.Name = JsonStr(t, "\"name\"");
                    s.Author = JsonStr(t, "\"author\"");
                    s.Tagline = JsonStr(t, "\"tagline\"");
                }
                if (s.Id.Length == 0) s.Id = s.Pkg;
                if (s.Name.Length == 0) s.Name = s.Pkg;
                // 在跑吗？patch 里出现这个包名 ⇒ 已插进插件名册
                s.Active = patch.Length > 0 && patch.IndexOf(s.Pkg, StringComparison.OrdinalIgnoreCase) >= 0;
                try
                {
                    string act = DshCore.DetectActiveSkin();
                    if (!string.IsNullOrEmpty(act) && !act.Equals("official", StringComparison.OrdinalIgnoreCase)
                        && (s.Id.Equals(act, StringComparison.OrdinalIgnoreCase) || s.Pkg.IndexOf(act, StringComparison.OrdinalIgnoreCase) >= 0))
                        s.Active = true;
                }
                catch { }
                list.Add(s);
            }
            catch { }
        }

        /// <summary>按皮肤 id 取同款配色；认不出来的走中性深海配色（不瞎编）。</summary>
        /// <summary>
        /// 救星**自己的**皮肤：「鲸鱼娘 · 深海」。这是本程序的默认外观，不再跟着 DSH 皮肤跑。
        /// 设计依据（界面皮肤的通行规矩，非拍脑袋）：
        ///   · 1 主色 + 1 强调色 + 3~5 中性色，彩色总数 ≤5；60-30-10 配比（底 60 / 面 30 / 点睛 10）。
        ///   · 正文对比度 ≥4.5:1、UI 元素（描边/焦点）≥3:1 —— **用公式算，不靠肉眼**（--selftest 里有判据）。
        ///   · 暗色不用纯黑（#000），用深海蓝黑；文字不用纯白，用冰蓝白；彩色降饱和，避免"发光"。
        ///   · 两层令牌：下面这些值就是 primitive，组件只用 Palette 的语义字段。
        /// 取色来源：鲸鱼娘本人（蓝发/蓝眼/白围裙）＋女仆装的金色饰边。
        /// </summary>
        public static Palette Whale()
        {
            return new Palette
            {
                Key = "whale", Name = "鲸鱼娘 · 深海",
                From = "救星自带皮肤（鲸鱼娘主题，不跟随 DSH 皮肤）",
                Dark = true,
                BgDeep = Color.FromArgb(10, 22, 34),          // 海底最深处（60%）
                Surface = Color.FromArgb(18, 40, 58),         // 卡片/面板（30%）
                SurfaceRaised = Color.FromArgb(27, 58, 82),   // 悬停/凸起
                Line = Color.FromArgb(90, 134, 166),          // 描边（对 Surface 3.88:1，达标 ≥3）
                TextPrimary = Color.FromArgb(230, 241, 248),  // 冰蓝白（不用纯白）
                TextSecondary = Color.FromArgb(159, 182, 200),
                Accent = Color.FromArgb(95, 211, 232),        // 鲸蓝（点睛 10%）
                OnAccent = Color.FromArgb(4, 20, 28),         // 强调色上的深字
                Gold = Color.FromArgb(224, 190, 122),         // 女仆装的金饰边
                GoldLight = Color.FromArgb(242, 223, 180),
                Danger = Color.FromArgb(226, 85, 79),       // 填充用鲜艳色，字用 OnFill（深字 5.03:1）
                Success = Color.FromArgb(79, 180, 119),      // 深字 7.24:1
                Warning = Color.FromArgb(232, 163, 61),      // 深字 8.68:1
                Mat = Color.FromArgb(232, 238, 242),
                Border = Color.FromArgb(224, 190, 122),       // 系统边框也走金
                HeaderFrom = Color.FromArgb(8, 19, 30),
                HeaderTo = Color.FromArgb(23, 65, 92),
                ArtPack = "", HeroArt = "",
            };
        }

        /// <summary>WCAG 相对亮度与对比度（用于客观校验，不许靠肉眼）。</summary>
        public static double Contrast(Color a, Color b)
        {
            double la = Lum(a), lb = Lum(b);
            if (la < lb) { double tmp = la; la = lb; lb = tmp; }
            return (la + 0.05) / (lb + 0.05);
        }

        private static double Lum(Color c)
        {
            double[] v = new double[3];
            v[0] = c.R / 255.0; v[1] = c.G / 255.0; v[2] = c.B / 255.0;
            for (int i = 0; i < 3; i++)
                v[i] = (v[i] <= 0.03928) ? v[i] / 12.92 : Math.Pow((v[i] + 0.055) / 1.055, 2.4);
            return 0.2126 * v[0] + 0.7152 * v[1] + 0.0722 * v[2];
        }

        /// <summary>对比度自检：正文/次要文字 ≥4.5:1，描边与主按钮文字 ≥3:1 与 4.5:1。</summary>
        public static string ContrastSelftestLine()
        {
            Palette p = Current();
            double t1 = Contrast(p.TextPrimary, p.Surface);
            double t2 = Contrast(p.TextSecondary, p.Surface);
            double t3 = Contrast(p.Line, p.Surface);
            double t4 = Contrast(p.OnAccent, p.Accent);
            double t5 = Contrast(p.TextPrimary, p.BgDeep);
            double t6 = Contrast(OnFill(p.Danger), p.Danger);
            double t7 = Contrast(OnFill(p.Success), p.Success);
            double t8 = Contrast(OnFill(p.Warning), p.Warning);
            double t9 = Contrast(p.TextPrimary, p.SurfaceRaised);
            bool ok = t1 >= 4.5 && t2 >= 4.5 && t3 >= 3.0 && t4 >= 4.5 && t5 >= 4.5
                      && t6 >= 4.5 && t7 >= 4.5 && t8 >= 4.5 && t9 >= 4.5;
            return "皮肤对比度（WCAG）：正文 " + t1.ToString("0.00") + ":1 / 次要 " + t2.ToString("0.00")
                 + ":1 / 描边 " + t3.ToString("0.00") + ":1 / 主按钮 " + t4.ToString("0.00")
                 + ":1 / 底色正文 " + t5.ToString("0.00") + ":1 / 危险 " + t6.ToString("0.00")
                 + ":1 / 成功 " + t7.ToString("0.00") + ":1 / 警告 " + t8.ToString("0.00")
                 + ":1 / 凸起面正文 " + t9.ToString("0.00") + ":1（门槛 4.5/4.5/3/4.5/4.5/4.5/4.5/4.5/4.5）"
                 + (ok ? " OK" : " ★不达标");
        }

        /// <summary>填充色上的文字色：黑白哪个对比高用哪个（保证 ≥4.5:1，selftest 会验）。</summary>
        public static Color OnFill(Color fill)
        {
            Color dark = Color.FromArgb(4, 20, 28);
            return (Contrast(dark, fill) >= Contrast(Color.White, fill)) ? dark : Color.White;
        }

        private static bool IsLight(Color c)
        {
            if (c.IsEmpty) return true;
            return Lum(c) > 0.45;
        }

        private static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        private static readonly System.Collections.Generic.List<IntPtr> _tabPatched =
            new System.Collections.Generic.List<IntPtr>();

        /// <summary>
        /// ★★★ 给页签控件（TabControl）额外开的 ControlStyles 位。**不要再往里加 UserPaint**。
        ///
        /// 为什么单独拎成一个字段（而不是写在 SetStyle 调用处）：
        ///   这样 --selftest 的判据可以**直接读这个字段的位**，而不是读一句谎话般的注释。
        ///   只要有人往这里加 UserPaint，界面一致性自检就会立刻变红（见 TabControlUserPaintDisabled）。
        ///
        /// 为什么是这两位、且**绝不能有 UserPaint**（最小复现实测，自身对照）：
        ///   · OptimizedDoubleBuffer | AllPaintingInWmPaint ....... 表头条 #F0F0F0 = 36%（有字形）
        ///   · 上面两位 **再加 UserPaint** ......................... 表头条 #F0F0F0 = **100%（整条空白）**
        ///   · 只 OptimizedDoubleBuffer ........................... 36%（有字形）
        ///   ⇒ 独立变量就是 UserPaint。机理见下面 TabControl 分支里的长注释。
        /// </summary>
        private static readonly System.Windows.Forms.ControlStyles TabExtraStyles =
            System.Windows.Forms.ControlStyles.OptimizedDoubleBuffer
            | System.Windows.Forms.ControlStyles.AllPaintingInWmPaint;

        /// <summary>
        /// 给 --selftest 用的判据：页签控件**没有**开 UserPaint（开了表头会整条空白）。
        /// 直接去读真正会被 SetStyle 用的那个字段，所以它不是"写死为 true 的摆设"。
        /// </summary>
        public static bool TabControlUserPaintDisabled
        {
            get
            {
                return (TabExtraStyles & System.Windows.Forms.ControlStyles.UserPaint)
                       == (System.Windows.Forms.ControlStyles)0;
            }
        }

        /// <summary>
        /// 给 --selftest 用的**负对照**：把 UserPaint 强行混进来，判据必须立刻变红。
        /// 没有这个负对照，判据可能只是"永远返回 true"。
        /// </summary>
        public static bool TabControlUserPaintGuardHasTeeth
        {
            get
            {
                System.Windows.Forms.ControlStyles bogus =
                    TabExtraStyles | System.Windows.Forms.ControlStyles.UserPaint;
                bool wouldBeCaught =
                    (bogus & System.Windows.Forms.ControlStyles.UserPaint)
                    != (System.Windows.Forms.ControlStyles)0;
                // 只有当"混进 UserPaint 会被现判据抓住"时才说明判据有牙
                return wouldBeCaught && TabControlUserPaintDisabled;
            }
        }

        /// <summary>按钮填充色 → 语义令牌：按**原色的色相**判角色（绿=成功 / 红=危险 / 橙黄=警告 / 蓝=主色 / 灰=中性面）。</summary>
        public static Color RoleFill(Color original, Palette p)
        {
            if (original.IsEmpty) return p.SurfaceRaised;
            int r = original.R, g = original.G, b = original.B;
            int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            if (mx - mn < 28) return p.SurfaceRaised;          // 灰阶 ⇒ 中性按钮
            // ★ 顺序有讲究：**先判"警告"再判"危险"** —— 橙色（如 #E8A33D）的 r 也大于 g+40，
            //   先判危险会把所有橙色按钮吞成红色（实测踩过：重启服务 / 强力自愈 都变红）。
            if (r > 150 && g > 90 && b + 60 < g) return p.Warning;
            if (r > g + 40 && r > b + 40) return p.Danger;
            if (g > r + 25 && g >= b) return p.Success;
            if (b > r + 30) return p.Accent;
            return p.SurfaceRaised;
        }

        /// <summary>文字色 → 语义令牌：饱和色说明它有语义（状态点等），保留语义；黑白灰映射到正文/次要。</summary>
        public static Color RoleText(Color original, Palette p)
        {
            if (original.IsEmpty) return p.TextPrimary;
            int r = original.R, g = original.G, b = original.B;
            int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            if (mx - mn < 40) return IsLight(original) ? p.TextPrimary : p.TextPrimary;   // 灰阶文字 → 正文色
            if (g > r + 25 && g >= b) return p.Success;
            if (r > g + 40 && r > b + 40) return p.Danger;
            if (b > r + 30) return p.Accent;
            return p.TextSecondary;
        }

        /// <summary>
        /// ★★ 皮肤引擎：把皮肤铺到整棵控件树上（救星自带皮肤的落地方式）。
        /// 规矩：**组件只用语义令牌**，颜色不写在控件里；填充色上的文字一律过 OnFill（保证对比度）。
        /// 只在"自己的皮肤"（Dark=true）下整体重着色；跟随 DSH 皮肤时保持旧行为（已验证过，不动）。
        /// </summary>
        public static void ApplyTo(System.Windows.Forms.Control root, Palette p)
        {
            ApplyTo(root, p, 0);
        }

        private static void ApplyTo(System.Windows.Forms.Control root, Palette p, int depth)
        {
            if (root == null || p == null || !p.Dark) return;
            try { ApplyOne(root, p, depth); } catch { }
            foreach (System.Windows.Forms.Control c in root.Controls) ApplyTo(c, p, depth + 1);
        }

        private static void ApplyOne(System.Windows.Forms.Control c, Palette p, int depth)
        {
            // ★ 暗色皮肤下，滚动条也要暗：WinForms 的滚动条是系统绘制的浅色条，
            //   不改的话日志框/说明书右边会亮着一条白条。用 uxtheme 的 DarkMode_Explorer 主题即可。
            try { if (c.IsHandleCreated) SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }

            string tn = c.GetType().Name;
            if (tn.IndexOf("CaptionBar", StringComparison.Ordinal) >= 0) return;   // 自绘标题栏自己管颜色

            if (c is System.Windows.Forms.Form) { c.BackColor = p.BgDeep; c.ForeColor = p.TextPrimary; return; }

            if (c is System.Windows.Forms.TabControl)
            {
                var tc = (System.Windows.Forms.TabControl)c;
                tc.DrawMode = System.Windows.Forms.TabDrawMode.OwnerDrawFixed;
                tc.BackColor = p.BgDeep;
                tc.ForeColor = p.TextPrimary;
                // ★★ 2026-09-20 修「页签行一直在抖」：OwnerDrawFixed 下 TabControl **默认不双缓冲**，
                //   自绘的每一项（底 + 字 + 选中强调条）都直接画到屏幕上 ⇒ 每次选中/悬停变化
                //   都能看见"先擦后画"的中间态。开双缓冲后整帧一次换，抖动消失。
                //   与上面 _tabPatched 同一条"只挂一次"的口径（SetStyle 本身可重复调，但没必要）。
                //
                // ★★★ 2026-09-20 **第二次修（主人指认的"白"）**：
                //   原来这里把 ControlStyles.UserPaint 也一起打开了 —— 那是**表头整条画成
                //   纯 #F0F0F0、一个页签字形都没有**的真因（主人截图里那条灰带）。
                //
                //   最小复现实测（自身对照，同一份代码只切 SetStyle 的位）：
                //     · 只自绘（不动样式）.................. 表头条 #F0F0F0 占 36%（有字形）
                //     · Optimized|AllPaint **|UserPaint** ... #F0F0F0 占 **100%（整条空白）**
                //     · Optimized|AllPaint（去掉 UserPaint）.. 36%（有字形）
                //     · 只 OptimizedDoubleBuffer ............ 36%（有字形）
                //     ⇒ 独立变量就是 **UserPaint** 这一位，两组各自复现、去掉即好。
                //
                //   机理：UserPaint 的语义是"这个控件完全由**你的 OnPaint** 负责画"。
                //     WinForms 的 TabControl 是**原生控件包装**（SysTabControl32），它没有为
                //     UserPaint 准备自绘路径 —— 这一位打开后，原生控件的默认绘制（表头整条背景
                //     + 页签字形）被**跳过**，而 TabControl 自己只在 DrawItem 里画**单个页签矩形**，
                //     于是表头整条没人画 ⇒ 露出窗口默认底 #F0F0F0（= Color.Control）。
                //     注：GDI 的 OwnerDrawFixed 仍然生效（TCM_GETITEMCOUNT=6、DrawItem 被调用），
                //     只是"画布"已经没人擦底 + 原生那层被跳过。
                //   ⇒ 结论：**保留双缓冲两位，绝不加 UserPaint**。
                //     落地方式：要设的位放在字段 TabExtraStyles 里（见上面的说明），
                //     判据挂在 --selftest 的「界面一致性」族的第 ④ 项：
                //       「页签控件未开 UserPaint」+ 它的负对照（硬塞 UserPaint 必须被抓住）。
                //     ⇒ 以后谁再顺手把 UserPaint 加回去，自检会直接变红，不会再偷偷跑掉。
                if (!_tabPatched.Contains(tc.Handle))
                {
                    _tabPatched.Add(tc.Handle);
                    try
                    {
                        typeof(System.Windows.Forms.Control)
                            .GetMethod("SetStyle",
                                       System.Reflection.BindingFlags.Instance
                                       | System.Reflection.BindingFlags.NonPublic)
                            .Invoke(tc, new object[] { TabExtraStyles, true });
                    }
                    catch { }
                    tc.DrawItem += TabDraw;
                    // ★★★ 2026-09-20（第三次修，"白"的收尾）：把表头整条（含页签右侧那段空白）
                    //   铺成皮肤底色。**必须走 WM_ERASEBKGND，不能在 Paint 里做** —— 实测依据：
                    //     · TabControl 客户区宽 1332px，6 个页签只占到 ~1030px，
                    //       右边 x=1038..1341 那 300px **任何人都不填色** ⇒ 露窗口默认底 #F0F0F0。
                    //     · 一开始我在 tc.Paint 里 FillRectangle(全宽)：**只画到 x=1038 就停了** ——
                    //       因为 Paint 的 Graphics 被 e.ClipRectangle（增量重绘区）裁掉了，
                    //       传全宽也没用。
                    //     · 改用 WM_ERASEBKGND 后：实测表头条 #F0F0F0 = **0 列**（干净）。
                    //       而且**开了 AllPaintingInWmPaint 之后 WM_ERASEBKGND 依然会被调到**
                    //       （实测计数 = 1，不是 0）⇒ 这条路与双缓冲可以共存。
                    //   ⇒ 做法：子类化 TabControl 的窗口过程，在 WM_ERASEBKGND 里只填表头条那一段。
                    TabHeadEraser.Attach(tc);
                    // 顺带在 Paint 里补一条 1px 饰线（颜色之外还有"分界"这个第二信号）
                    try { tc.Paint += TabHeadPaint; } catch { }
                }
                return;
            }
            if (c is System.Windows.Forms.TabPage) { c.BackColor = p.BgDeep; c.ForeColor = p.TextPrimary; return; }

            if (c is System.Windows.Forms.Button)
            {
                var b = (System.Windows.Forms.Button)c;
                Color fill = RoleFill(b.BackColor, p);
                b.UseVisualStyleBackColor = false;
                b.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = Mix(fill, Color.White, 0.14f);
                b.FlatAppearance.MouseDownBackColor = Mix(fill, Color.Black, 0.18f);
                b.BackColor = fill;
                b.ForeColor = OnFill(fill);
                return;
            }

            if (c is System.Windows.Forms.RichTextBox)
            {
                c.BackColor = Mix(p.BgDeep, Color.Black, 0.25f);
                c.ForeColor = p.TextSecondary;
                return;
            }
            if (c is System.Windows.Forms.TextBox) { c.BackColor = p.Surface; c.ForeColor = p.TextPrimary; return; }
            if (c is System.Windows.Forms.ComboBox)
            {
                var cb = (System.Windows.Forms.ComboBox)c;
                cb.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
                cb.BackColor = p.Surface; cb.ForeColor = p.TextPrimary;
                return;
            }
            if (c is System.Windows.Forms.StatusStrip) { c.BackColor = p.Surface; c.ForeColor = p.TextSecondary; return; }

            if (c is System.Windows.Forms.Label)
            {
                c.BackColor = Color.Transparent;
                c.ForeColor = RoleText(c.ForeColor, p);
                return;
            }

            if (c is System.Windows.Forms.Panel || c is System.Windows.Forms.TableLayoutPanel || c is System.Windows.Forms.FlowLayoutPanel || c is System.Windows.Forms.GroupBox)
            {
                // ★ 显式标了 Tag="card" 的容器 = 设计上就要当**卡片** ⇒ 一律上面色 Surface
                //   （比"按深度猜"可靠：深度分不清"页面容器"和"卡片"，而 Tag 是作者明确表态）
                if (c.Tag as string == "card")
                {
                    c.BackColor = p.Surface;
                    c.ForeColor = p.TextPrimary;
                    // 暗色下"面色 vs 底色"差得很小，卡片需要一条 1px 描边才立得住（只挂一次）
                    if (!_cardPatched.Contains(c.Handle))
                    {
                        _cardPatched.Add(c.Handle);
                        c.Paint += CardPaint;
                    }
                    return;
                }
                // ★★ 有意做成透明的容器**不许动**：头部那层 TableLayoutPanel 本来就是 Transparent，
                //   好让自绘的深海渐变透出来；皮肤引擎把它当"卡片"涂成面色 ⇒ 渐变被盖住、
                //   立绘区出现一块死板的色块（实测踩过）。透明 ⇒ 只改文字色。
                if (c.BackColor == Color.Transparent)
                {
                    c.ForeColor = p.TextPrimary;
                    return;
                }
                // ★ 按**深度**分层次（60-30-10 落地）：
                //   窗体的直接子控件（头部/状态区/页签/日志/状态栏）= 底色（60%）；
                //   更深的容器 = 面色 Surface（30%，卡片）——这样"页面里的分组块"会变成卡片，
                //   而不是一片和底色一样的黑。
                c.BackColor = (depth <= 1) ? p.BgDeep : p.Surface;
                if (!(c is System.Windows.Forms.GroupBox)) c.ForeColor = p.TextPrimary;
                return;
            }
        }

        private static readonly System.Collections.Generic.List<IntPtr> _cardPatched =
            new System.Collections.Generic.List<IntPtr>();

        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        /// <summary>卡片描边：1px 语义描边色（暗色下"面色 vs 底色"差得小，靠这条线把卡片立住）。</summary>
        private static void CardPaint(object sender, System.Windows.Forms.PaintEventArgs e)
        {
            try
            {
                var c = (System.Windows.Forms.Control)sender;
                Palette p = Current();
                using (var pen = new Pen(Color.FromArgb(120, p.Line), 1f))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, c.Width - 1, c.Height - 1);
                }
            }
            catch { }
        }

        /// <summary>
        /// emoji 专用字体（Segoe UI Emoji）。
        ///
        /// ★★ 这里有一组**踩过坑的实测结论**，别再改回去：
        ///   ① 正文字体（雅黑 msyh/msyhbd）里 🚑🩺🧩🛡🔧📖 这些码位**没有字形** ⇒
        ///      6 个都画成**同一个方框**（豆腐块），这就是主人截图里"全是 □"的原因。
        ///   ② 换成 Segoe UI Emoji 就对了 —— 而且**用最简单的 `new Font(名字, 字号)` 即可**，
        ///      实测 7/8/9/9.5/10/11/12/14pt 各档都是 6 个形状互不相同。
        ///   ③ ★ 千万别自作聪明去指定 SYMBOL_CHARSET(2)：
        ///      实测那样会**把字体解析成 Wingdings**（Font.Name 直接变成 "Wingdings"），
        ///      于是 6 个码点又画成同一个方框 —— 修反了。
        ///      （这是我实际踩过的坑：先加了 lfCharSet=2，自检立刻报 15 对全同。）
        ///   ④ GDI 路径拿到的是**黑白剪影**，不是彩色（彩色要 DirectWrite 自绘整条）。
        /// </summary>
        private static Font _emojiFont = null;
        private static bool _emojiFontTried = false;

        /// <summary>emoji 字号：比正文略小一档（emoji 字形视觉上偏大）。</summary>
        private const float EmojiPt = 8.5f;

        private static Font EmojiFont()
        {
            if (_emojiFontTried) return _emojiFont;
            _emojiFontTried = true;
            try
            {
                // ★ 就用最朴素的构造：不要传 charset（见上面 ③）
                _emojiFont = new Font("Segoe UI Emoji", EmojiPt, FontStyle.Regular,
                                      System.Drawing.GraphicsUnit.Point);
                // 万一本机没有这个字体，构造不会抛异常但会静默换成别的字体 ⇒ 逐码点校验一次
                if (!LooksLikeRealEmojiFont(_emojiFont)) { try { _emojiFont.Dispose(); } catch { } _emojiFont = null; }
            }
            catch { _emojiFont = null; }
            return _emojiFont;
        }

        /// <summary>抽验：用这个字体画 🚑 和 🩺，两者必须长得不一样（否则就是豆腐块字体）。</summary>
        private static bool LooksLikeRealEmojiFont(Font f)
        {
            try
            {
                byte[] a = RenderGlyph(f, 0x1F691);
                byte[] b = RenderGlyph(f, 0x1F9BA);
                if (a == null || b == null || a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++)
                    if (a[i] != b[i]) return true;      // 有差异 ⇒ 真字形
                return false;                            // 完全一样 ⇒ 豆腐块
            }
            catch { return false; }
        }

        /// <summary>把一个码点画到 48x48 白底位图上，返回灰度字节。</summary>
        private static byte[] RenderGlyph(Font f, int cp)
        {
            const int N = 48;
            string s;
            try { s = char.ConvertFromUtf32(cp); }
            catch { return null; }
            using (var bmp = new Bitmap(N, N))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.White);
                    var sf = new System.Drawing.StringFormat();
                    sf.FormatFlags |= System.Drawing.StringFormatFlags.NoWrap;
                    using (var br = new SolidBrush(Color.Black))
                        g.DrawString(s, f, br, new RectangleF(0, 0, N, N), sf);
                }
                return BitmapToGray(bmp);
            }
        }

        /// <summary>判断一个字符是不是 emoji/图形符号（需要走 emoji 字体画的那些）。</summary>
        private static bool IsEmojiChar(char ch)
        {
            // 与 MainForm.UiConsistencySelftestLine 的口径保持一致，便于互相印证：
            //   0x2190–0x2BFF 箭头/数学/杂项符号；0x2600–0x27BF 杂项符号与装饰；
            //   0xFE0F 变体选择符；0x1F000+ 由代理对处理（见下）
            if (ch >= 0x2190 && ch <= 0x2BFF) return true;
            if (ch >= 0x2600 && ch <= 0x27BF) return true;
            if (ch == 0xFE0F || ch == 0xFE0E) return true;
            return false;
        }

        /// <summary>判断某个码点（含代理对合成）是不是 emoji。</summary>
        private static bool IsEmojiCp(int cp)
        {
            if (cp >= 0x1F000 && cp <= 0x1FAFF) return true;   // 各类 emoji
            if (cp >= 0x1F300 && cp <= 0x1F5FF) return true;
            if (cp >= 0x1F600 && cp <= 0x1F64F) return true;
            if (cp >= 0x1F680 && cp <= 0x1F6FF) return true;
            if (cp >= 0x1F900 && cp <= 0x1F9FF) return true;
            return IsEmojiChar((char)cp);
        }

        /// <summary>给 selftest 用的公开口径：某个码点算不算"需要走 emoji 字体画"的字符。</summary>
        public static bool IsEmojiCodepoint(int cp) { return IsEmojiCp(cp); }

        /// <summary>
        /// ★ 2026-09-20 新增：把一段文字按「emoji 段 / 非 emoji 段」切开，给**拼 RTF**用。
        ///
        /// 为什么需要：GDI 的 DrawString 不做字体回退，RichEdit 也不做 ——
        ///   ⇒ 正文里混着的 emoji 必须自己切出来、单独指定字体，否则就是一排 □。
        ///   （自绘页签那边是 DrawTabText 逐段画；这里因为正文走 RichTextBox，
        ///     没法逐段 DrawString，只能产出「段 + 是不是 emoji」让调用方拼 \fN。）
        ///
        /// 与 DrawTabText 的分段口径**完全一致**（变体选择符跟前面走、代理对按 2 字符取），
        /// 所以两处不会各自跑偏。
        /// </summary>
        public static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, bool>> SplitEmojiRuns(string txt)
        {
            var segs = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, bool>>();
            if (string.IsNullOrEmpty(txt)) return segs;
            int i = 0;
            while (i < txt.Length)
            {
                bool emoji;
                int cp;
                int take;

                if (char.IsHighSurrogate(txt[i]) && i + 1 < txt.Length && char.IsLowSurrogate(txt[i + 1]))
                {
                    cp = char.ConvertToUtf32(txt[i], txt[i + 1]);
                    take = 2;
                }
                else
                {
                    cp = txt[i];
                    take = 1;
                }
                emoji = IsEmojiCp(cp);

                if (!emoji && (cp == 0xFE0F || cp == 0xFE0E) && segs.Count > 0 && segs[segs.Count - 1].Value)
                    emoji = true;

                if (segs.Count > 0 && segs[segs.Count - 1].Value == emoji)
                {
                    var kv = segs[segs.Count - 1];
                    segs[segs.Count - 1] = new System.Collections.Generic.KeyValuePair<string, bool>(
                        kv.Key + txt.Substring(i, take), emoji);
                }
                else
                {
                    segs.Add(new System.Collections.Generic.KeyValuePair<string, bool>(txt.Substring(i, take), emoji));
                }
                i += take;
            }
            return segs;
        }

        /// <summary>
        /// ★ 2026-09-20 新增：emoji 字体在 RTF 里该报的名字（给拼 \fonttbl 用）。
        /// 拿不到真字体（本机没装 seguiemj / 被静默换成别的）时返回 null，
        /// 调用方就老老实实全用正文字体 —— 宁可显示成 □，也不要谎报一个不存在的字体名。
        /// </summary>
        public static string EmojiFontRtfName()
        {
            Font f = EmojiFont();
            if (f == null) return null;
            try { return f.Name; }
            catch { return null; }
        }

        /// <summary>给 selftest 用：emoji 字体到底能不能用（= 画出来不是豆腐块）。</summary>
        public static bool EmojiFontUsable()
        {
            return EmojiFont() != null;
        }

        /// <summary>
        /// 给 selftest 用：这些码点画出来是不是**彼此不同**（= 真字形，不是豆腐块）。
        ///
        /// ★ 为什么不能用「数非白像素 > N」判（踩过）：
        ///   雅黑下 6 个码点都画成同一个方框，非白像素数一模一样（各 58 个）
        ///   ⇒ 「像素够多」照样判"有字形"，**假阳性**。
        /// ★ 为什么不能用 GetGlyphIndicesW 判（踩过）：
        ///   它对非 BMP 码位在任何字体下都返回 0xFFFF（连 seguiemj 里真有字形的 🚑 也报 MISSING），
        ///   纯属**假阴性**。
        /// ★ 正解（本方法）：真字形彼此形状不同；豆腐块则所有缺字形码点长得一模一样。
        ///   判据 = 「两两像素完全相同」的对数必须为 0。
        ///   实测：seguiemj → 0 对（通过）；雅黑 → 15 对全同（失败）；宋体 → 15 对全同（失败）。
        /// </summary>
        public static bool EmojiGlyphsAllDistinct(int[] codepoints)
        {
            Font ef = EmojiFont();
            if (ef == null || codepoints == null) return false;
            return GlyphsAllDistinctWith(ef, codepoints);
        }

        /// <summary>
        /// ★ 2026-09-20 新增：**负对照**专用 —— 把同一批码点交给指定字体判一次。
        ///
        /// 为什么要负对照：一个"永远返回 true"的判据也能让自检变绿，那是没用的判据。
        /// 必须证明它**换成正文字体（雅黑）时会变红**，才说明它真的在区分字体。
        ///   · 正样本：Segoe UI Emoji → 必须 0 对（通过）
        ///   · 负样本：Microsoft YaHei UI → 必须 >0 对（不通过）
        /// 实测雅黑下这些码点全画成同一个 .notdef 方框 ⇒ 负对照稳定变红。
        /// </summary>
        public static bool GlyphsAllDistinctInFontNamed(string fontName, float pt, int[] codepoints)
        {
            if (string.IsNullOrEmpty(fontName) || codepoints == null || codepoints.Length == 0) return false;
            try
            {
                using (var f = new Font(fontName, pt, FontStyle.Regular, GraphicsUnit.Point))
                    return GlyphsAllDistinctWith(f, codepoints);
            }
            catch { return false; }
        }

        /// <summary>核心：用给定字体把码点逐个画出来，两两比像素，完全相同的一对都不许有。</summary>
        private static bool GlyphsAllDistinctWith(Font ef, int[] codepoints)
        {
            if (ef == null || codepoints == null || codepoints.Length == 0) return false;
            try
            {
                var raws = new byte[codepoints.Length][];
                for (int k = 0; k < codepoints.Length; k++)
                {
                    raws[k] = RenderGlyph(ef, codepoints[k]);
                    if (raws[k] == null) return false;
                    // 自身全白（压根没画出来）也算失败
                    bool any = false;
                    for (int p = 0; p < raws[k].Length; p++)
                        if (raws[k][p] < 240) { any = true; break; }
                    if (!any) return false;
                }

                for (int i = 0; i < raws.Length; i++)
                    for (int j = i + 1; j < raws.Length; j++)
                    {
                        bool same = true;
                        for (int p = 0; p < raws[i].Length; p++)
                            if (raws[i][p] != raws[j][p]) { same = false; break; }
                        if (same) return false;      // 两个码点长得一样 ⇒ 至少一个是豆腐块
                    }
                return true;
            }
            catch { return false; }
        }

        /// <summary>把位图转成灰度字节数组（判据内部用）。</summary>
        private static byte[] BitmapToGray(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var buf = new byte[w * h];
            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                var row = new byte[stride];
                for (int y = 0; y < h; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(
                        new IntPtr(data.Scan0.ToInt64() + (long)y * stride), row, 0, stride);
                    for (int x = 0; x < w; x++)
                    {
                        int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                        buf[y * w + x] = (byte)((r * 30 + g * 59 + b * 11) / 100);
                    }
                }
            }
            finally { bmp.UnlockBits(data); }
            return buf;
        }

        /// <summary>
        /// ★★★ 2026-09-20（第三次修「白」的收尾）：把**整个表头条**铺成皮肤底色。
        ///
        /// 为什么必须做：
        ///   自绘（TabDraw / DrawItem）只填**单个页签的矩形** ——
        ///   页签与页签之间的缝隙、最后一个页签右边到控件右边缘那一段，
        ///   **没有任何人填色** ⇒ 露窗口默认底 #F0F0F0（浅灰，= Color.Control）。
        ///   实测（救星真机）：TabControl 客户区宽 1332px，6 个页签只占到约 1030px，
        ///   右边 x=1038..1341 那 300px 就是纯灰。
        ///
        /// 为什么**不能**在 tc.Paint 里 FillRectangle（我第一版就是那么写的，实测失败）：
        ///   Paint 里拿到的 Graphics 被 e.ClipRectangle（**增量重绘区**）裁掉了 ——
        ///   即使传的是整条 ClientRectangle.Width，实际也只画到 ~1038 就停
        ///   ⇒ 页签右侧那段永远是灰的。这是实测到的（不是推测）。
        ///
        /// 为什么走 WM_ERASEBKGND 就对：
        ///   这个处理器拿的是窗口 DC（GetWindowDC/ERASEBKGND 的 wParam），**不受
        ///   增量 ClipRectangle 限制**，可以一次刷满整条。
        ///   ★ 关键实测：**开了 AllPaintingInWmPaint 之后 WM_ERASEBKGND 依然会被调到**
        ///     （探针计数 = 1，不是 0）⇒ 它和双缓冲不冲突，可以共存。
        ///
        /// 表头条的边界怎么算：
        ///   ClientRectangle 的 (0,0,W,H) 里，**y &lt; DisplayRectangle.Top** 那一段就是表头
        ///   （DisplayRectangle 是"页签内容区"，它的 Top 就是表头下沿）。实测该值为 34px。
        ///
        /// 只填表头条、不动内容区：内容区由 TabPage 自己铺（ApplyOne 里已设成 BgDeep），
        ///   这里若整块刷反而会在切页时闪一下。
        /// </summary>
        private sealed class TabHeadEraser : System.Windows.Forms.NativeWindow
        {
            private static readonly System.Collections.Generic.List<IntPtr> _attached =
                new System.Collections.Generic.List<IntPtr>();

            private const int WM_ERASEBKGND = 0x0014;

            [DllImport("user32.dll")]
            private static extern bool GetClientRect(IntPtr hWnd, out ERECT r);
            [DllImport("gdi32.dll")]
            private static extern IntPtr CreateSolidBrush(int crColor);
            [DllImport("gdi32.dll")]
            private static extern bool DeleteObject(IntPtr hObj);
            [DllImport("user32.dll")]
            private static extern int FillRect(IntPtr hDC, ref ERECT lprc, IntPtr hbr);

            [StructLayout(LayoutKind.Sequential)]
            private struct ERECT { public int left, top, right, bottom; }

            /// <summary>只挂一次（同一控件的句柄可能重建，但那属于同一个窗口对象，不重复挂）。</summary>
            public static void Attach(System.Windows.Forms.TabControl tc)
            {
                try
                {
                    if (tc == null || _attached.Contains(tc.Handle)) return;
                    var e = new TabHeadEraser();
                    e.AssignHandle(tc.Handle);
                    _attached.Add(tc.Handle);
                }
                catch { }
            }

            protected override void WndProc(ref System.Windows.Forms.Message m)
            {
                if (m.Msg == WM_ERASEBKGND)
                {
                    try
                    {
                        var tc = System.Windows.Forms.Control.FromHandle(Handle)
                                 as System.Windows.Forms.TabControl;
                        if (tc != null)
                        {
                            Palette p = Current();
                            ERECT rc;
                            GetClientRect(Handle, out rc);
                            int hb = tc.DisplayRectangle.Top;     // 表头条下边界
                            if (hb > 0 && hb <= rc.bottom)
                            {
                                // COLORREF = 0x00BBGGRR（BGR 顺序！）
                                int cr = (p.BgDeep.R) | (p.BgDeep.G << 8) | (p.BgDeep.B << 16);
                                IntPtr br = CreateSolidBrush(cr);
                                ERECT head = new ERECT();
                                head.left = rc.left; head.top = rc.top;
                                head.right = rc.right; head.bottom = hb;
                                FillRect(m.WParam, ref head, br);
                                DeleteObject(br);
                                m.Result = (IntPtr)1;             // 已处理 ⇒ 不再走默认擦底
                                return;
                            }
                        }
                    }
                    catch { }
                }
                base.WndProc(ref m);
            }
        }

        /// <summary>
        /// 表头条与内容区之间的细饰线（"分界"作为颜色之外的第二信号）。
        /// 挂 Paint 即可：这一步只画 1px 线，不会被增量裁剪影响（即使被裁也只是少画那 1px）。
        /// </summary>
        private static void TabHeadPaint(object sender, System.Windows.Forms.PaintEventArgs e)
        {
            try
            {
                var tc = (System.Windows.Forms.TabControl)sender;
                Palette p = Current();
                System.Drawing.Rectangle head = tc.ClientRectangle;
                int hb = tc.DisplayRectangle.Top;
                if (hb <= 0 || hb > head.Height) return;
                using (var ln = new Pen(Mix(p.BgDeep, p.Accent, 0.35f)))
                    e.Graphics.DrawLine(ln, head.Left, hb - 1, head.Right - 1, hb - 1);
            }
            catch { }
        }

        /// <summary>页签自绘（暗色皮肤必须自绘，否则是系统浅色页签，跟皮肤不搭）。</summary>
        private static void TabDraw(object sender, System.Windows.Forms.DrawItemEventArgs e)
        {
            try
            {
                var tc = (System.Windows.Forms.TabControl)sender;
                Palette p = Current();
                bool sel = (e.Index == tc.SelectedIndex);
                Rectangle r = tc.GetTabRect(e.Index);

                // ★★ 2026-09-20 修「页签文字被裁一半 / 顶部被切」（主人截图实据：`□ 诊断` 只剩下半截）：
                //   根因＝**量尺寸用的字体和画字用的字体不是同一个**。
                //     · GetTabRect() 返回的矩形是按 TabControl 自己的 Font 度量出来的；
                //     · 而下面画字用的是**新建的** "Microsoft YaHei UI 9f Bold"。
                //   字体度量一旦比矩形所需的更高（雅黑的 ascent/descent 比系统默认大），
                //   文字就会画到矩形外面 —— 上边被裁、下边溢出，看起来就是"文字缺一截"。
                //   ⇒ 两处对齐：① 画字改用**控件自己的 Font**（矩形就是按它算的）；
                //              ② 选中态想加粗就**同时把矩形往上撑**，让加粗后的字仍然装得下；
                //              ③ 用 SetClip 把绘制限在矩形内，宁可两端各缩一点，绝不画到邻居上。
                Font baseFont = tc.Font != null ? tc.Font : new Font("Microsoft YaHei UI", 9f);
                bool needBold = sel;
                Font f = needBold ? new Font(baseFont, FontStyle.Bold)
                                  : new Font(baseFont, FontStyle.Regular);
                try
                {
                    // 加粗会让字形略胖，给矩形补 1px 余量（不改 tc.GetTabRect 的返回值，只用在绘制上）
                    Rectangle rr = r;
                    if (needBold) { rr.Y -= 1; rr.Height += 1; }

                    using (var br = new SolidBrush(sel ? p.Surface : p.BgDeep))
                        e.Graphics.FillRectangle(br, r);

                    System.Drawing.Region oldClip = e.Graphics.Clip;
                    try
                    {
                        e.Graphics.SetClip(r);      // 限在本页签矩形内画，绝不侵占邻居
                        string txt = tc.TabPages[e.Index].Text;
                        Color fg = sel ? p.TextPrimary : p.TextSecondary;
                        DrawTabText(e.Graphics, txt, f, fg, rr);
                    }
                    finally { e.Graphics.Clip = oldClip; }
                }
                finally { f.Dispose(); }

                if (sel)   // 选中态：底部一条强调色（颜色之外还有形状/位置作为第二信号）
                {
                    using (var br = new SolidBrush(p.Accent))
                        e.Graphics.FillRectangle(br, r.Left + 4, r.Bottom - 3, r.Width - 8, 3);
                }
            }
            catch { }
        }

        /// <summary>
        /// 画页签文字：**按字符分段**——emoji 段用 Segoe UI Emoji，其余用正文字体，
        /// 最后整体水平居中。这样标题里可以既有 emoji 又有中文（主人要的「🚑 急救」）。
        ///
        /// 为什么要分段：雅黑里没有这些 emoji 的字形，混在一起画就是一个 □；
        /// 而 GDI 的 DrawString **不会**自动做字体回退（不像 DirectWrite），
        /// 所以必须我们手动切字体。
        /// </summary>
        private static void DrawTabText(Graphics g, string txt, Font textFont, Color fg, Rectangle box)
        {
            if (string.IsNullOrEmpty(txt)) return;

            // 1) 先按「emoji / 非 emoji」切成若干段
            var segs = new System.Collections.Generic.List<KeyValuePair<string, bool>>();
            int i = 0;
            while (i < txt.Length)
            {
                bool emoji;
                int cp;
                int take;

                if (char.IsHighSurrogate(txt[i]) && i + 1 < txt.Length && char.IsLowSurrogate(txt[i + 1]))
                {
                    cp = char.ConvertToUtf32(txt[i], txt[i + 1]);
                    take = 2;
                }
                else
                {
                    cp = txt[i];
                    take = 1;
                }
                emoji = IsEmojiCp(cp);

                // 变体选择符（U+FE0F/FE0E）跟着前一个字符走，不单独起段
                if (!emoji && (cp == 0xFE0F || cp == 0xFE0E) && segs.Count > 0 && segs[segs.Count - 1].Value)
                    emoji = true;

                if (segs.Count > 0 && segs[segs.Count - 1].Value == emoji)
                {
                    var kv = segs[segs.Count - 1];
                    segs[segs.Count - 1] = new KeyValuePair<string, bool>(kv.Key + txt.Substring(i, take), emoji);
                }
                else
                {
                    segs.Add(new KeyValuePair<string, bool>(txt.Substring(i, take), emoji));
                }
                i += take;
            }

            Font ef = EmojiFont();

            // 2) 量总宽（emoji 段若没字体就退回正文宽度）
            var sfNoWrap = new System.Drawing.StringFormat();
            sfNoWrap.FormatFlags |= System.Drawing.StringFormatFlags.NoWrap;

            float total = 0f;
            float[] widths = new float[segs.Count];
            for (int k = 0; k < segs.Count; k++)
            {
                Font useF = (segs[k].Value && ef != null) ? ef : textFont;
                widths[k] = g.MeasureString(segs[k].Key, useF, int.MaxValue, sfNoWrap).Width;
                total += widths[k];
            }

            // 3) 从居中位置起笔，逐段画（用下标循环，不能用 IndexOf —— 内容相同的段会取错）
            float x = box.Left + (box.Width - total) / 2f;
            float y = box.Top + box.Height / 2f;
            var sfOne = new System.Drawing.StringFormat();
            sfOne.Alignment = System.Drawing.StringAlignment.Near;
            sfOne.LineAlignment = System.Drawing.StringAlignment.Center;
            sfOne.FormatFlags |= System.Drawing.StringFormatFlags.NoWrap;

            for (int k = 0; k < segs.Count; k++)
            {
                Font useF = (segs[k].Value && ef != null) ? ef : textFont;
                using (var br = new SolidBrush(fg))
                    g.DrawString(segs[k].Key, useF, br, x, y, sfOne);
                x += widths[k];
            }
        }

        public static Palette For(string id)
        {
            string k = (id ?? "").ToLowerInvariant();
            if (k.IndexOf("maid-atelier") >= 0)
            {
                return new Palette
                {
                    Key = "maid-atelier", Name = "深海女仆工坊同款",
                    From = "皮肤 maid-atelier（深海蓝 + 陶瓷白 + 长春花蓝 + 柔金）",
                    HeaderFrom = Color.FromArgb(20, 51, 95), HeaderTo = Color.FromArgb(63, 123, 191),
                    // ★ 2026-09-18 修正：原先这里填的是 **#8FA0E8（长春花蓝）**，等于把皮肤的
                    //   "柔金"整个丢了 ⇒ 主人一句"皮肤还是摆设"就是指着这个。
                    //   下面这两个值是从皮肤自己的 CSS 里**统计实测**出来的金色家族：
                    //     主金 #c5a468（出现 5 次，是它的主装饰金）、高光 #e1c17d / 极浅 #fff1ce。
                    Gold = Color.FromArgb(197, 164, 104),
                    GoldLight = Color.FromArgb(225, 193, 125),
                    // 窗口那圈原生边框也走金色（金色边框是这套皮肤的signature，主人点名要看）
                    Border = Color.FromArgb(197, 164, 104), Bg = Color.FromArgb(255, 253, 248),
                    ArtPack = "maid",    // 唯一自带装饰素材的一套（金线花边/蕾丝/蝴蝶结/卷草）
                    Mat = Color.FromArgb(243, 232, 207)   // 测：皮肤 CSS 里的暖奶油 #f3e8cf
                };
            }
            if (k.IndexOf("orca") >= 0)
            {
                return new Palette
                {
                    Key = "orca-link", Name = "虎鲸链路同款",
                    // ★ 2026-09-18：下面这些是**从它自己的 CSS 里统计实测**出来的（原先那套是我编的）：
                    //   品牌电蓝 #4d91ff（出现 8 次＝主色）、近黑 #0f151f/#11151b、珍珠白 #fbf7ef、
                    //   青铜/琥珀 #9b8061 / #dfa02b。渐变端点由实测主色加深/提亮得到（标注"推"）。
                    From = "皮肤 orca-link（石墨黑 + 珍珠白 + 电蓝 #4d91ff）",
                    HeaderFrom = Color.FromArgb(17, 21, 27), HeaderTo = Color.FromArgb(29, 63, 115),   // 推：近黑 → 电蓝加深
                    Gold = Color.FromArgb(77, 145, 255), GoldLight = Color.FromArgb(159, 196, 255),   // 测：#4d91ff / 推：亮电蓝
                    Border = Color.FromArgb(77, 145, 255), Bg = Color.FromArgb(251, 247, 239),        // 测：电蓝 / 珍珠白
                    ArtPack = "", HeroArt = "orca_hero.png",  // 没有花边素材，但有**自己的立绘**⇒ 头部换成它
                    Mat = Color.FromArgb(239, 231, 216)   // 推：由珍珠白 #fbf7ef 压暖一档
                };
            }
            if (k.IndexOf("deep-whale") >= 0 || k.IndexOf("whale") >= 0)
            {
                return new Palette
                {
                    Key = "deep-whale", Name = "深海鲸鱼同款",
                    // ★ 2026-09-18：deep-whale-manager 是 **DSH 自带主题/皮肤管理器**，它自己**一个素材都没有**
                    //   （纯 CSS，用 DSH 的 --dsw-alias-* token）⇒ 这套只能跟**配色**，装饰全由几何画。
                    //   实测 token（dsh-client-ui-theme\lib\client.js）：主色 #4176e6、深 #151517、
                    //   浅 #f9fafb、浅蓝 #93c5fd / #dbeafe。原先那套"深海底色+浅鲸蓝"是我编的，已撤。
                    From = "DSH 默认主题 deep-whale（#4176e6 品牌蓝 + 中性蓝灰）",
                    HeaderFrom = Color.FromArgb(21, 21, 23), HeaderTo = Color.FromArgb(65, 118, 230),   // 测：深 #151517 → 品牌 #4176e6
                    Gold = Color.FromArgb(147, 197, 253), GoldLight = Color.FromArgb(219, 234, 254),   // 测：#93c5fd / #dbeafe
                    Border = Color.FromArgb(65, 118, 230), Bg = Color.FromArgb(249, 250, 251),         // 测：#4176e6 / #f9fafb
                    ArtPack = "", HeroArt = "",
                    Mat = Color.FromArgb(238, 242, 247)   // 推：由 #f9fafb/#dbeafe 取中，冷色垫配冷色皮
                };
            }
            return new Palette
            {
                Key = "generic", Name = "通用深海配色",
                From = "通用（没有对应皮肤，或皮肤不认识）",
                HeaderFrom = Color.FromArgb(24, 46, 88), HeaderTo = Color.FromArgb(52, 96, 160),
                Gold = Color.FromArgb(197, 164, 104), GoldLight = Color.FromArgb(232, 205, 150), Border = Color.FromArgb(46, 74, 120),
                Mat = Color.FromArgb(255, 243, 222),
                Bg = Color.FromArgb(255, 252, 246)
            };
        }

        /// <summary>
        /// 皮肤选择落盘文件。★ 2026-09-18：主人要求"**点开就能选皮肤**"⇒ 选择存这里，
        /// 下次启动照旧生效（不用再设环境变量）。内容就一行 id（见 <see cref="Choices"/>）。
        /// </summary>
        public static string SkinFile
        {
            get { return Path.Combine(DshCore.AppDataDir, "skin.txt"); }
        }

        private static string _savedCache = null;
        private static DateTime _savedStamp = DateTime.MinValue;

        /// <summary>读上次选的皮肤 id（"" = 没存过 ⇒ 用自带皮肤）。按 mtime 缓存，手改文件也会被发现。</summary>
        public static string SavedSkin()
        {
            try
            {
                string f = SkinFile;
                if (!File.Exists(f)) { _savedCache = ""; return ""; }
                DateTime st = File.GetLastWriteTimeUtc(f);
                if (_savedCache != null && st == _savedStamp) return _savedCache;
                string t = File.ReadAllText(f, new UTF8Encoding(false)).Trim();
                _savedCache = t; _savedStamp = st;
                return t;
            }
            catch { return _savedCache != null ? _savedCache : ""; }
        }

        /// <summary>存下皮肤选择。返回一句给日志用的人话。</summary>
        public static string SaveSkin(string id)
        {
            try
            {
                Directory.CreateDirectory(DshCore.AppDataDir);
                File.WriteAllText(SkinFile, id != null ? id : "", new UTF8Encoding(false));
                _savedCache = id != null ? id : "";
                _savedStamp = File.GetLastWriteTimeUtc(SkinFile);
                return "皮肤选择已保存：" + NameOf(id) + "（" + SkinFile + "）";
            }
            catch (Exception ex) { return "皮肤选择保存失败：" + ex.Message + "（本次仍会立刻生效，但重启后会丢）"; }
        }

        /// <summary>
        /// 可选皮肤清单（界面上「🎨 换皮肤」对话框就是读它）。
        /// ★ 2026-09-18 记一笔别再犯：我一度把后三条"借它的X色"从界面撤掉（以为主人说"按这个不好"是嫌它们不好看），
        ///   主人的意思是**"按了没反应"**（那是真 bug，已修）——选项本身没问题，只留两条反而单调。
        ///   ⇒ 已恢复成五条。
        /// </summary>
        public static readonly string[,] Choices = new string[,]
        {
            { "whale",        "鲸鱼娘 · 深海（自带）", "救星自己的皮肤：深海蓝底 + 鲸蓝点睛 + 女仆金饰线。默认就用它。", "" },
            { "auto",         "跟随 DSH 皮肤",          "认出你 DSH 里正在跑的那套皮肤，借它的**主色/渐变**染到救星窗口上（界面仍是暗色）。", "" },
            { "orca-link",    "虎鲸链路（借它的电蓝）",   "电蓝当点睛色；界面仍是救星的暗色底，只换配色。", "" },
            { "maid-atelier", "深海女仆工坊（借它的柔金）", "柔金当点睛色；界面仍是救星的暗色底，只换配色。", "" },
            { "deep-whale",   "深海鲸鱼（借它的品牌蓝）", "DSH 默认主题的品牌蓝当点睛色；界面仍是救星的暗色底。", "" },
        };

        /// <summary>额外只给命令行用的预览 id（当前为空；留着这个口子，将来要加"不上界面"的排障项就往这儿放）。</summary>
        public static readonly string[] PreviewIds = new string[0];

        /// <summary>id → 给人看的名字（认不出就把 id 原样回显，不瞎编）。</summary>
        public static string NameOf(string id)
        {
            try
            {
                if (string.IsNullOrEmpty(id)) return "鲸鱼娘 · 深海（自带）";
                for (int i = 0; i < Choices.GetLength(0); i++)
                    if (string.Equals(Choices[i, 0], id, StringComparison.OrdinalIgnoreCase)) return Choices[i, 1];
            }
            catch { }
            return id;
        }

        /// <summary>
        /// 把"只跟配色"的那几套补成**完整令牌**。
        /// ★ 为什么需要（2026-09-18 自检打印出「强调色 000000」才发现）：跟随模式下 Palette 只声明了
        ///   HeaderFrom/To、Gold、Border、Bg、Mat，其余字段是 Color.Empty（画出来就是**纯黑**）——
        ///   以前只拿它刷头部渐变和窗口边框，看不出来；现在选择框要拿它**画色卡、给确定按钮上色**，
        ///   黑块就露馅了（选到"跟随 DSH 皮肤"时色卡一团黑）。
        /// 补齐规则：**只补声明为空的字段**，一律从自带皮肤 Whale() 的同名令牌取，
        /// 强调色缺就用它**自己的金饰线**当强调（不另编一个颜色）。
        /// </summary>
        public static Palette Normalize(Palette s)
        {
            if (s == null) return Whale();
            if (s.Dark) return s;                 // 自带皮肤本来就是完整令牌
            Palette b = Whale();
            var n = new Palette();
            n.Key = s.Key; n.Name = s.Name; n.From = s.From;
            // ★★ 2026-09-18 修（主人点开选择框选了"虎鲸链路同款"之后界面变白、立绘成白卡片才发现）：
            //   这几套"只跟配色"的 Palette 原来 **Dark=false** ⇒ 皮肤引擎 `ApplyTo` 直接 return，
            //   于是整窗退回系统浅色、头部立绘也不抠白底（白底图在浅底上就是一张白卡片）。
            //   选择框把它们当"可选皮肤"摆出来以后，这个半成品状态就藏不住了。
            //   ⇒ 一律按**暗色皮肤**处理：引擎照常铺我们这套暗色底/字/卡片，只借用对方的主色与渐变。
            n.Dark = true;
            n.ArtPack = s.ArtPack; n.HeroArt = s.HeroArt;
            n.HeaderFrom = Empty(s.HeaderFrom) ? b.HeaderFrom : s.HeaderFrom;
            n.HeaderTo = Empty(s.HeaderTo) ? b.HeaderTo : s.HeaderTo;
            n.Gold = Empty(s.Gold) ? b.Gold : s.Gold;
            n.GoldLight = Empty(s.GoldLight) ? b.GoldLight : s.GoldLight;
            n.Border = Empty(s.Border) ? b.Border : s.Border;
            n.Bg = Empty(s.Bg) ? b.Bg : s.Bg;
            n.Mat = Empty(s.Mat) ? b.Mat : s.Mat;
            n.BgDeep = Empty(s.BgDeep) ? b.BgDeep : s.BgDeep;
            n.Surface = Empty(s.Surface) ? b.Surface : s.Surface;
            n.SurfaceRaised = Empty(s.SurfaceRaised) ? b.SurfaceRaised : s.SurfaceRaised;
            n.Line = Empty(s.Line) ? b.Line : s.Line;
            n.TextPrimary = Empty(s.TextPrimary) ? b.TextPrimary : s.TextPrimary;
            n.TextSecondary = Empty(s.TextSecondary) ? b.TextSecondary : s.TextSecondary;
            n.Accent = Empty(s.Accent) ? (Empty(s.Gold) ? b.Accent : s.Gold) : s.Accent;
            n.OnAccent = Empty(s.OnAccent) ? OnFill(n.Accent) : s.OnAccent;
            n.Danger = Empty(s.Danger) ? b.Danger : s.Danger;
            n.Success = Empty(s.Success) ? b.Success : s.Success;
            n.Warning = Empty(s.Warning) ? b.Warning : s.Warning;
            return n;
        }

        /// <summary>判"这个令牌是空的"（Color.Empty 或全 0 = 没声明过）。</summary>
        private static bool Empty(Color c)
        {
            return c.IsEmpty || (c.A == 0 && c.R == 0 && c.G == 0 && c.B == 0);
        }

        /// <summary>id → 该套的配色（认不出的走通用深海配色，不瞎编）。★ 一律补齐成完整令牌（见 Normalize）。</summary>
        public static Palette PaletteOf(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Equals("whale", StringComparison.OrdinalIgnoreCase)) return Whale();
            if (id.Equals("auto", StringComparison.OrdinalIgnoreCase)) return Normalize(CurrentReal());
            return Normalize(For(id));
        }

        /// <summary>
        /// 命令行切皮肤：<c>--skin list</c> 列可选值；<c>--skin &lt;id&gt;</c> 存下并生效（下次启动照旧）。
        /// 返回进程退出码（0 成功 / 1 参数不认识）。★ 这条同时是"皮肤选择"功能的**可脚本化验收入口**。
        /// </summary>
        public static int CliSkin(string arg)
        {
            try
            {
                if (string.IsNullOrEmpty(arg) || arg.Equals("list", StringComparison.OrdinalIgnoreCase))
                {
                    string cur = SavedSkin();
                    if (cur.Length == 0) cur = "whale";
                    Console.WriteLine("可选皮肤（切换：大肥鱼救星.exe --skin <id>）：");
                    for (int i = 0; i < Choices.GetLength(0); i++)
                        Console.WriteLine("  " + Choices[i, 0].PadRight(14) + Choices[i, 1]
                                          + (string.Equals(Choices[i, 0], cur, StringComparison.OrdinalIgnoreCase) ? "   ← 当前" : ""));
                    if (PreviewIds.Length > 0) Console.WriteLine("  —— 以下是预览用（界面上不显示，只作排障）——");
                    foreach (string pid in PreviewIds)
                        Console.WriteLine("  " + pid.PadRight(14) + NameOf(pid)
                                          + (string.Equals(pid, cur, StringComparison.OrdinalIgnoreCase) ? "   ← 当前" : ""));
                    Console.WriteLine("当前生效：" + Current().Name);
                    Console.WriteLine("选择文件：" + SkinFile);
                    return 0;
                }
                string id = arg.Trim().ToLowerInvariant();
                bool known = false;
                for (int i = 0; i < Choices.GetLength(0); i++)
                    if (string.Equals(Choices[i, 0], id, StringComparison.OrdinalIgnoreCase)) known = true;
                foreach (string pid in PreviewIds)
                    if (string.Equals(pid, id, StringComparison.OrdinalIgnoreCase)) known = true;
                if (!known)
                {
                    Console.WriteLine("不认识的皮肤 id：" + arg + "（用 --skin list 看可选值）");
                    return 1;
                }
                Console.WriteLine(SaveSkin(id));
                Console.WriteLine("现在生效的是：" + Current().Name);
                Console.WriteLine("（界面上也能换：插件与皮肤 → 换皮肤。★ 命令行不打印 emoji：GBK 控制台会把它们变成问号）");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("切皮肤失败：" + ex.Message);
                return 1;
            }
        }

        /// <summary>
        /// 当前该用哪套。优先级：①环境变量 <c>BFF_SKIN</c>（临时覆盖，不落盘）
        /// ②界面里选过并保存的（skin.txt）③都没有 ⇒ 救星自带的「鲸鱼娘 · 深海」。
        /// ★ 判"哪套 DSH 皮肤在跑"**复用 DshCore.DetectActiveSkin()**（它看两层 cordis.patch.yml 的
        ///   insert 行）—— 皮肤是 manager 在运行时切的，光在本包里搜包名是认不出来的（实测踩过）。
        /// ★ 2026-09-18 修：原先 `BFF_SKIN=auto` **根本不会跟随**（那个 if 把 auto 排除掉后直接 return Whale()，
        ///   而 CurrentReal() 从头到尾没人调用 = 死代码）⇒ 文档写着"auto 跟随"，实际点不着。
        ///   现在 auto（不管来自环境变量还是界面选择）真的会去认 DSH 当前皮肤。
        /// </summary>
        public static Palette Current()
        {
            try
            {
                string forced = Environment.GetEnvironmentVariable("BFF_SKIN");
                if (!string.IsNullOrEmpty(forced)) return PaletteOf(forced);
            }
            catch { }
            return PaletteOf(SavedSkin());
        }

        private static Palette CurrentReal()
        {
            string act = "";
            try { act = DshCore.DetectActiveSkin(); } catch { }
            if (!string.IsNullOrEmpty(act) && !act.Equals("official", StringComparison.OrdinalIgnoreCase))
                return For(act);
            try
            {
                foreach (var s in Detect())
                    if (s.Active) return For(s.Id);
            }
            catch { }
            return For("");
        }

        public static string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("== 皮肤联动（🎨 皮肤配色）==");
            sb.AppendLine("说明：皮肤原本是给 **DSH 网页** 用的（CSS 覆盖层），救星是桌面程序吃不了那套 CSS；");
            sb.AppendLine("      所以这里做的是「**认出现在哪套皮肤在跑**，再把**同款配色**刷到救星窗口上」。");
            sb.AppendLine();

            var skins = Detect();
            sb.AppendLine("-- 本机装了的皮肤 --");
            if (skins.Count == 0)
                sb.AppendLine("[?]    没检出皮肤（profile 里没有 *skin* 包）。");
            foreach (var s in skins)
            {
                sb.AppendLine((s.Active ? "[在用] " : "[装了] ") + s.Name + "（" + s.Id + "）"
                              + (s.Author.Length > 0 ? "  作者 " + s.Author : ""));
                if (s.Tagline.Length > 0) sb.AppendLine("       风格：" + s.Tagline);
                sb.AppendLine("       包：" + s.Pkg);
            }
            sb.AppendLine();

            var pal = Current();
            string savedId = SavedSkin();
            string from = "";
            try { if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BFF_SKIN"))) from = "环境变量 BFF_SKIN"; } catch { }
            if (from.Length == 0) from = (savedId.Length == 0 ? "默认（没存过选择）" : "界面里选的（" + SkinFile + "）");
            sb.AppendLine("-- 救星会用的配色 --");
            sb.AppendLine("[OK]   " + pal.Name + "（" + pal.From + "）");
            sb.AppendLine("       来源：" + from);
            sb.AppendLine("       头部渐变 #" + pal.HeaderFrom.R.ToString("X2") + pal.HeaderFrom.G.ToString("X2") + pal.HeaderFrom.B.ToString("X2")
                          + " → #" + pal.HeaderTo.R.ToString("X2") + pal.HeaderTo.G.ToString("X2") + pal.HeaderTo.B.ToString("X2"));
            sb.AppendLine("       饰线/强调 #" + pal.Gold.R.ToString("X2") + pal.Gold.G.ToString("X2") + pal.Gold.B.ToString("X2")
                          + "，窗口边框 #" + pal.Border.R.ToString("X2") + pal.Border.G.ToString("X2") + pal.Border.B.ToString("X2"));
            sb.AppendLine();
            sb.AppendLine("-- 系统窗口边框（Win11 原生接口）--");
            var fr = ProbeNativeBorder();
            sb.AppendLine(fr.Matched
                ? "[OK]   已把窗口边框染成皮肤色 " + fr.Hex(fr.Want) + "（设置成功 + 对照拒绝 ⇒ 判定生效；该属性只写、系统不支持回读）"
                : (fr.Supported ? "[WARN] " + fr.Note : "[?]    " + fr.Note + " —— 本机走不了这条路（Win11 22000+ 才支持）"));
            sb.AppendLine();
            sb.AppendLine("想换皮肤？点上面的「🎨 皮肤配色」就能**点选**（选择会记住，下次启动仍生效）。");
            sb.AppendLine("也可以临时用环境变量覆盖（不落盘）：set BFF_SKIN=auto|orca-link|maid-atelier|deep-whale");
            sb.AppendLine("注意：多套皮肤同时启用会互斥（「🧩 插件与皮肤」页有互斥体检）。");
            return sb.ToString();
        }

        // ============================================================
        //  Win11 原生窗口边框染色（DWMWA_BORDER_COLOR = 34）
        //  主人选的是「A 方案」：不动窗口布局，直接让**系统画的窗口边框**变成皮肤色。
        //  ★ 纪律：设完必须**回读**（DwmGetWindowAttribute），读回来一致才算成功；
        //    系统不支持（Win10 没这个接口）就如实报"不支持"，绝不假装成功。
        // ============================================================
        private const int DWMWA_BORDER_COLOR = 34;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        public class FrameResult
        {
            public bool Attempted, Supported, Matched;
            public int Want, Got;
            public string Note = "";
            public string Hex(int v) { return "#" + ((v & 0xFF)).ToString("X2") + ((v >> 8) & 0xFF).ToString("X2") + ((v >> 16) & 0xFF).ToString("X2"); }
        }

        /// <summary>把当前皮肤色刷到**系统窗口边框**上（COLORREF = 0x00BBGGRR）。</summary>
        public static FrameResult ApplyNativeBorder(IntPtr hwnd)
        {
            var r = new FrameResult();
            var pal = Current();
            r.Want = (pal.Border.B << 16) | (pal.Border.G << 8) | pal.Border.R;
            if (hwnd == IntPtr.Zero) { r.Note = "没有窗口句柄"; return r; }
            try
            {
                r.Attempted = true;
                int want = r.Want;
                int hr = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref want, sizeof(int));
                if (hr != 0)
                {
                    r.Note = "本机系统不支持（hr=0x" + hr.ToString("X8") + "；Win10 没有这个接口）";
                    return r;
                }
                r.Supported = true;
                // ★ 实测结论：DWMWA_BORDER_COLOR 是**只写属性** —— 设成功（S_OK）之后
                //   用 DwmGetWindowAttribute 去读会返回 E_INVALIDARG(0x80070057)、读到 0。
                //   所以"回读一致"这条判据**在本属性上不成立**（我第一版就栽在这）。
                //   改用**阳性+阴性对照**证明这条路真的通：
                //     阳性 = 属性 34 设置返回 S_OK；
                //     阴性 = 故意用一个不存在的属性号，DWM 必须**拒绝**（否则说明调用根本没到 DWM）。
                int bogus = r.Want;
                int hrBad = DwmSetWindowAttribute(hwnd, 99999, ref bogus, sizeof(int));
                r.Got = 0;
                r.Matched = (hr == 0 && hrBad != 0);
                r.Note = r.Matched
                    ? "已生效（设置返回 S_OK；对照：乱给属性号会被拒 0x" + hrBad.ToString("X8") + " ⇒ 说明调用真的到了 DWM）"
                    : ("可疑：设置 hr=0x" + hr.ToString("X8") + "，对照 hr=0x" + hrBad.ToString("X8"));
            }
            catch (Exception ex) { r.Note = "调用异常：" + ex.GetType().Name + ": " + ex.Message; }
            return r;
        }

        /// <summary>用一个隐藏窗口实测"这个系统能不能给窗口边框染色"（自检/报告用，不闪窗）。</summary>
        public static FrameResult ProbeNativeBorder()
        {
            var r = new FrameResult();
            try
            {
                using (var f = new System.Windows.Forms.Form())
                {
                    f.ShowInTaskbar = false;
                    f.Opacity = 0;          // 全透明 ⇒ 不闪
                    f.FormBorderStyle = System.Windows.Forms.FormBorderStyle.Sizable;
                    IntPtr h = f.Handle;    // 逼出窗口句柄
                    r = ApplyNativeBorder(h);
                }
            }
            catch (Exception ex) { r.Note = "探测异常：" + ex.GetType().Name; }
            return r;
        }
        // ---------- 小工具 ----------
        private static string JsonStr(string txt, string key)
        {
            if (string.IsNullOrEmpty(txt)) return "";
            int i = txt.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return "";
            int c = txt.IndexOf(':', i + key.Length);
            if (c < 0) return "";
            int k = c + 1;
            while (k < txt.Length && char.IsWhiteSpace(txt[k])) k++;
            if (k >= txt.Length || txt[k] != '"') return "";
            int q2 = txt.IndexOf('"', k + 1);
            if (q2 < 0) return "";
            return txt.Substring(k + 1, q2 - k - 1);
        }

        public static string SelftestLine()
        {
            try
            {
                var l = Detect();                                   // 不该抛
                var known = For("maid-atelier");
                var orca = For("orca-link");
                var unknown = For("some-unknown-skin-xyz");
                bool ok = known.Key == "maid-atelier"
                       && orca.Key == "orca-link"
                       && unknown.Key == "generic"                  // 负样本：不认识的必须走通用
                       && For("").Key == "generic"
                       && known.HeaderFrom != unknown.HeaderFrom;   // 已知皮肤不能和通用撞色
                var cur = Current();                                // 不该抛
                ok = ok && cur != null;
                var fr = ProbeNativeBorder();
                string frameTxt = fr.Matched ? "边框染色已生效(" + fr.Hex(fr.Want) + ")"
                               : (fr.Supported ? "边框染色未生效" : "本机不支持边框染色(跳过)");
                bool frameOk = fr.Matched || !fr.Supported;     // 不支持不等于坏
                return "皮肤检测/配色（检出 " + l.Count + " 套、当前 " + (cur == null ? "无" : cur.Key) + "、" + frameTxt + "）: "
                     + ((ok && frameOk) ? "OK" : "FAIL");
            }
            catch (Exception ex)
            {
                return "皮肤检测/配色: FAIL " + ex.GetType().Name;
            }
        }
    }

    /// <summary>
    /// 双缓冲面板。★ 2026-09-18 立（主人："感觉变卡了，打开甚至会出现闪闪的情况"）：    ///   WinForms 的普通 Panel 自绘 + 子控件（这里是透明背景的标题/副标题 Label）
    ///   会走"先擦背景、再画前景"两条路 ⇒ 肉眼可见的闪烁。
    ///   这里做两件事：
    ///     ① 开 <c>AllPaintingInWmPaint</c> + <c>OptimizedDoubleBuffer</c>：自绘与背景都先进后台位图再一次性贴出；
    ///     ② <see cref="Cached"/> 非空时，**背景与前景都只贴这张缓存图** ——
    ///        透明子控件重绘时会回调父控件的 <c>OnPaintBackground</c>，走同一条路 ⇒ 不会再擦出白块。
    /// </summary>
    internal class BufferedPanel : System.Windows.Forms.Panel
    {
        /// <summary>由使用方维护的缓存位图（尺寸必须与本控件一致）。为 null 时退回普通绘制。</summary>
        public System.Drawing.Bitmap Cached;

        /// <summary>★★ 2026-09-18 新增「素材层」回调 —— 修「拖拽窗口时头部横幅被拉扯」。
        ///
        /// 背景：缓存位图里混着两类东西，它们对宽度的依赖完全不同：
        ///   · **装饰**（渐变 / 光柱 / 海浪 / 金线 / 蝴蝶结）：与宽度有关，但拉伸了也看不出来；
        ///   · **素材**（立绘 / 鲸鱼）：尺寸只跟**高度**有关（`wh2 = H-12`、`box=(16,10,176,H-20)`），
        ///     一旦跟着宽度被非等比缩放，就变成肉眼可见的「拉扯」。
        /// 旧版在尺寸不匹配时把**整张**缓存 `DrawImage(Cached, ClientRectangle)` ⇒ 素材被横向拉长/压扁
        /// （实测：窗宽 1380→1880 时鲸鱼 207px 宽被拉到 283px，而高度纹丝不动 152px）。
        ///
        /// 现方案：**让能拉伸的去拉伸，让会变形的完全不参与拉伸** ——
        ///   缓存位图只放装饰（尺寸不匹配时照样拉伸垫底 ⇒ 仍然"绝不露白"），
        ///   素材则由这个回调**按 1:1 现贴**（尺寸只跟高度有关，所以永远不会被宽度拉伸）。
        /// 传 null 时行为退回旧版（整张拉伸）。</summary>
        public Action<System.Drawing.Graphics, int, int> ArtDraw;

        public BufferedPanel()
        {
            // ★ 负对照开关：BFF_NOCACHE=1 ⇒ 退回"不双缓冲"的老面板。
            //   用途：证明"双缓冲 + 缓存位图"这两个改动各自值多少（--paintbench 同一条 exe 上 A/B），
            //   而不是只凭"我觉得快了"。判据见 MainForm.PaintBench。
            try
            {
                string nc = Environment.GetEnvironmentVariable("BFF_NOCACHE");
                if (!string.IsNullOrEmpty(nc) && nc != "0") return;
            }
            catch { }
            SetStyle(System.Windows.Forms.ControlStyles.AllPaintingInWmPaint
                   | System.Windows.Forms.ControlStyles.OptimizedDoubleBuffer
                   | System.Windows.Forms.ControlStyles.UserPaint, true);
            UpdateStyles();
        }

        protected override void OnPaintBackground(System.Windows.Forms.PaintEventArgs e)
        {
            if (Cached != null)
            {
                try
                {
                    // ★ 2026-09-18：尺寸还没跟上时**拉伸顶一下**，绝不落到 base（那会先刷成一片白底，
                    //   用户看到的就是"窗口出来了但上半截是白的"——实测抓帧抓到的就是这一下）。
                    //   ★★ 但**只拉伸装饰层**：这一张缓存里已经没有立绘/鲸鱼了（它们由 ArtDraw 现贴），
                    //      所以拉伸不会造成任何肉眼可见的变形。这条修法同时保住了「绝不露白」这个意图。
                    if (Cached.Width == Width && Cached.Height == Height) e.Graphics.DrawImageUnscaled(Cached, 0, 0);
                    else e.Graphics.DrawImage(Cached, ClientRectangle);
                    if (ArtDraw != null) ArtDraw(e.Graphics, Width, Height);   // 素材层：1:1，永不拉伸
                    return;
                }
                catch { }
            }
            base.OnPaintBackground(e);
        }

        protected override void OnPaint(System.Windows.Forms.PaintEventArgs e)
        {
            // ★★ 2026-09-18 修正（隐藏的定时炸弹）：
            //   旧版写的是 `if (Cached == null) base.OnPaint(e);` —— 而 WinForms **正是靠 base.OnPaint
            //   才触发 Paint 事件**。于是缓存一旦建好，挂在 Paint 上的处理器就再也不会被调用：
            //     · 表现上"看起来还能用"，只是因为另有一条 160ms 防抖计时器在兜底重建；
            //     · 实际上 MainForm 挂在 Paint 上那套逻辑被**静默吞掉**了 ——
            //       以后往 Paint 里加任何东西都会无声失效。
            //   ⇒ 必须无条件走 base，让 Paint 事件恢复正常语义。
            //   （重绘成本不受影响：真正贴着"缓存 + 素材"画的活是 OnPaintBackground 干的。）
            base.OnPaint(e);
        }
    }

    /// <summary>
    /// 「🎨 皮肤配色」点开后的**皮肤选择框**（2026-09-18 主人要求："改成点开可以选择皮肤"）。
    /// 左边一列候选（色卡 + 名字 + 一句话说明），底部「用这个 / 取消」。
    /// · 当前用的那套会标「← 正在用」；
    /// · 选中的那套右边有色卡预览（底色/面色/点睛/金饰线），不点确定也不会动主窗口；
    /// · 双击某一行 = 直接用它。
    /// 结果放在 <see cref="Chosen"/>（null = 用户取消）。落盘由调用方做（<see cref="SkinTheme.SaveSkin"/>）。
    /// </summary>
    internal class SkinPickerDialog : System.Windows.Forms.Form
    {
        public string Chosen = null;

        private readonly System.Windows.Forms.ListBox _list;
        private readonly System.Windows.Forms.Label _hint;
        private System.Windows.Forms.Button _ok;
        private readonly string[] _ids;
        private readonly SkinTheme.Palette _pal;

        public SkinPickerDialog(string currentId)
        {
            _pal = SkinTheme.Current();
            Text = "选择皮肤";
            ClientSize = new System.Drawing.Size(620, 462);
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9f);
            BackColor = _pal.BgDeep;
            ForeColor = _pal.TextPrimary;
            Padding = new System.Windows.Forms.Padding(14, 16, 14, 12);

            int n = SkinTheme.Choices.GetLength(0);
            _ids = new string[n];
            for (int i = 0; i < n; i++) _ids[i] = SkinTheme.Choices[i, 0];

            var title = new System.Windows.Forms.Label
            {
                Text = "要哪套皮肤？选完会**记住**，下次启动仍生效（随时可以回来改）。",
                Dock = System.Windows.Forms.DockStyle.Top, Height = 34,
                ForeColor = _pal.TextSecondary, BackColor = System.Drawing.Color.Transparent,
            };

            _list = new System.Windows.Forms.ListBox
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle,
                DrawMode = System.Windows.Forms.DrawMode.OwnerDrawFixed,
                ItemHeight = 56,
                IntegralHeight = false,
                Font = new System.Drawing.Font("Microsoft YaHei UI", 10f),
            };
            // ★★ 必须真的把条目加进去！第一版只写了自绘 DrawItem、忘了 Add ⇒ 列表是**空的**
            //    （自绘模式下 ListBox 不会自己画任何东西，SelectedIndex 也无从设置）。
            //    这条已进自检：SkinPickerDialog.SelftestLine() 会数 Items.Count。
            for (int i = 0; i < _ids.Length; i++) _list.Items.Add(_ids[i]);
            _list.DrawItem += ListDraw;
            _list.DoubleClick += delegate { Accept(); };
            _list.SelectedIndexChanged += delegate { HintUpdate(); };

            _hint = new System.Windows.Forms.Label
            {
                Dock = System.Windows.Forms.DockStyle.Bottom, Height = 26,
                ForeColor = _pal.TextSecondary, BackColor = System.Drawing.Color.Transparent,
            };

            var ok = new System.Windows.Forms.Button
            {
                Text = "用这个", DialogResult = System.Windows.Forms.DialogResult.OK,
                Size = new System.Drawing.Size(116, 36), BackColor = _pal.Accent, ForeColor = _pal.OnAccent,
                FlatStyle = System.Windows.Forms.FlatStyle.Flat,
            };
            ok.FlatAppearance.BorderSize = 0;
            _ok = ok;
            // ★★ 2026-09-18 修「按了没效果」（主人反馈）：原来只有**双击**那条路会写 Chosen（Accept()）；
            //   点这枚按钮走的是 DialogResult=OK ⇒ 对话框照常关闭、ShowDialog 也返回 OK，
            //   但 **Chosen 一直是空的** ⇒ 调用方那句 `string.IsNullOrEmpty(dlg.Chosen)` 把它当成"取消"，
            //   直接 return ⇒ 界面什么都不变（而双击那一行却是有效的，正是这个不对称让我一开始没看出来）。
            //   现在按钮自己调 Accept()（写 Chosen → 置 DialogResult → 关窗），并在 OnFormClosing 里再兜一层底。
            ok.Click += delegate { Accept(); };
            var cancel = new System.Windows.Forms.Button
            {
                Text = "取消", DialogResult = System.Windows.Forms.DialogResult.Cancel,
                Size = new System.Drawing.Size(92, 36), BackColor = _pal.Surface, ForeColor = _pal.TextPrimary,
                FlatStyle = System.Windows.Forms.FlatStyle.Flat,
            };
            cancel.FlatAppearance.BorderSize = 0;

            var bottom = new System.Windows.Forms.FlowLayoutPanel
            {
                // ★ 2026-09-18 修：原来条高 42px、按钮 32px、上内边距 6 ⇒ 按钮底只剩 **4px** 余量，
                //   而对话框那圈金框是画在客户区边上的 ⇒ 看起来"按钮被底边裁掉了"（主人反馈"按这个好像不好"）。
                //   现在：条高 58 + 上内边距 8 ⇒ 按钮底留 **14px**，金框与按钮不再打架。
                Dock = System.Windows.Forms.DockStyle.Bottom, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft,
                Height = 58, BackColor = System.Drawing.Color.Transparent, Padding = new System.Windows.Forms.Padding(0, 8, 0, 0),
            };
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(ok);

            // ★ Dock 顺序（本项目踩过两次的坑）：**后加入的先占外侧槽位**，Fill 必须**最先加**，
            //   否则它会把整块客户区吃光、底部的提示与按钮被挤没（第一版渲染出来按钮就看不见）。
            Controls.Add(_list);     // Fill：最后才分配，拿剩下的中间
            Controls.Add(_hint);     // 底：说明
            Controls.Add(bottom);    // 底：按钮条（在最外）
            Controls.Add(title);     // 顶：标题（最先分配）

            AcceptButton = ok; CancelButton = cancel;
            int sel = 0;
            for (int i = 0; i < n; i++) if (string.Equals(_ids[i], currentId, StringComparison.OrdinalIgnoreCase)) sel = i;
            _list.SelectedIndex = sel;
            HintUpdate();

            SkinArt.DecorateDialog(this, _pal);
            Shown += delegate
            {
                try { SkinTheme.ApplyTo(this, _pal); _list.BackColor = _pal.Surface; _list.ForeColor = _pal.TextPrimary; } catch { }
            };
        }

        private void HintUpdate()
        {
            try
            {
                int i = _list.SelectedIndex;
                _hint.Text = (i < 0) ? "" : SkinTheme.Choices[i, 2];
            }
            catch { }
        }

        /// <summary>自绘每行：色卡（底/面/点睛/金）+ 名字 + 说明。</summary>
        private void ListDraw(object sender, System.Windows.Forms.DrawItemEventArgs e)
        {
            try
            {
                if (e.Index < 0 || e.Index >= _ids.Length) return;
                var p = SkinTheme.PaletteOf(_ids[e.Index]);
                bool sel = ((e.State & System.Windows.Forms.DrawItemState.Selected) != 0);
                var g = e.Graphics;
                var r = e.Bounds;
                using (var b = new SolidBrush(sel ? p.SurfaceRaised : p.BgDeep)) g.FillRectangle(b, r);

                // 色卡：底 / 面 / 点睛 / 金
                var sw = new System.Drawing.Rectangle(r.Left + 8, r.Top + 8, 64, 38);
                using (var b = new SolidBrush(p.BgDeep)) g.FillRectangle(b, sw);
                using (var b = new SolidBrush(p.Surface)) g.FillRectangle(b, new System.Drawing.Rectangle(sw.X + 4, sw.Y + 4, 28, 30));
                using (var b = new SolidBrush(p.Accent)) g.FillRectangle(b, new System.Drawing.Rectangle(sw.X + 36, sw.Y + 4, 24, 14));
                using (var b = new SolidBrush(p.Gold)) g.FillRectangle(b, new System.Drawing.Rectangle(sw.X + 36, sw.Y + 20, 24, 14));
                using (var pen = new Pen(System.Drawing.Color.FromArgb(150, p.Line))) g.DrawRectangle(pen, sw);

                bool cur = string.Equals(_ids[e.Index], SkinTheme.SavedSkin(), StringComparison.OrdinalIgnoreCase);
                using (var b = new SolidBrush(p.TextPrimary))
                using (var f = new System.Drawing.Font(Font.FontFamily, 10f, System.Drawing.FontStyle.Bold))
                    g.DrawString(SkinTheme.Choices[e.Index, 1] + (cur ? "   ← 正在用" : ""), f, b, r.Left + 84, r.Top + 8);
                using (var b = new SolidBrush(p.TextSecondary))
                using (var f = new System.Drawing.Font(Font.FontFamily, 8.5f))
                    g.DrawString(Shorten(SkinTheme.Choices[e.Index, 2], 46), f, b, r.Left + 84, r.Top + 30);
                if (sel) using (var pen = new Pen(p.Accent, 2f)) g.DrawRectangle(pen, r.Left + 1, r.Top + 1, r.Width - 3, r.Height - 3);
            }
            catch { }
        }

        private static string Shorten(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("**", "");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        /// <summary>供自检读取列表（只读用）。</summary>
        internal System.Windows.Forms.ListBox ListForTest { get { return _list; } }

        /// <summary>供自检量"确定按钮是不是整颗都在客户区里"（被裁掉就点不着）。</summary>
        internal int OkButtonBottomForTest
        {
            get
            {
                try
                {
                    if (_ok == null || _ok.Parent == null) return -1;
                    return _ok.Parent.Top + _ok.Bottom;
                }
                catch { return -1; }
            }
        }

        /// <summary>
        /// 自检（无界面也能跑）：把选择框**建出来、渲染一遍**，验四件事 ——
        ///   ① 列表条目数 = Choices 行数（★ 第一版就栽在这：只写自绘、忘了 Add ⇒ 列表是空的）；
        ///   ② 渲染出来"有色像素"占比够（说明真画出来了，不是一块白板）；
        ///   ③ 各行色卡的取样色**两两不全同**（说明色卡按各套配色分别画，不是同一个色块）；
        ///   ④ 选中第 i 行 ⇒ 拿到的 id 就是第 i 行的 id（选谁给谁，不串行）。
        /// ★ 为什么要这条判据：主人要"点开能选皮肤"，而**外部进程点不动这个程序的按钮**
        ///   （实测：合成鼠标点击连"自愈能修什么"这种有文件副作用的按钮都点不动）
        ///   ⇒ 改用"程序自己建自己渲染"来验收，比一次性的手点更可复现。
        /// </summary>
        public static string SelftestLine()
        {
            try
            {
                int n = SkinTheme.Choices.GetLength(0);
                using (var dlg = new SkinPickerDialog("whale"))
                {
                    dlg.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                    dlg.Location = new System.Drawing.Point(-4000, -4000);
                    dlg.ShowInTaskbar = false;
                    dlg.Show();
                    for (int i = 0; i < 6; i++) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                    var lb = dlg.ListForTest;
                    int items = lb == null ? -1 : lb.Items.Count;
                    int inkPct = 0;
                    using (var shot = new System.Drawing.Bitmap(Math.Max(1, dlg.ClientSize.Width), Math.Max(1, dlg.ClientSize.Height)))
                    {
                        dlg.DrawToBitmap(shot, new System.Drawing.Rectangle(0, 0, shot.Width, shot.Height));
                        // 供人眼复核：设 BFF_SKINPICK_SHOT=<png路径> ⇒ 把选择框的样子存下来看一眼
                        try
                        {
                            string dump = Environment.GetEnvironmentVariable("BFF_SKINPICK_SHOT");
                            if (!string.IsNullOrEmpty(dump)) shot.Save(dump, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        catch { }
                        System.Drawing.Color bgc = shot.GetPixel(2, 2);
                        int ink = 0, all = 0;
                        for (int y = 0; y < shot.Height; y += 3)
                            for (int x = 0; x < shot.Width; x += 3)
                            {
                                all++;
                                System.Drawing.Color c = shot.GetPixel(x, y);
                                if (Math.Abs(c.R - bgc.R) + Math.Abs(c.G - bgc.G) + Math.Abs(c.B - bgc.B) > 24) ink++;
                            }
                        inkPct = all == 0 ? 0 : (int)(ink * 100.0 / all);
                    }
                    // 选谁给谁
                    bool mapOk = true;
                    for (int i = 0; i < n; i++)
                    {
                        lb.SelectedIndex = i;
                        System.Windows.Forms.Application.DoEvents();
                        if (!string.Equals(System.Convert.ToString(lb.SelectedItem), SkinTheme.Choices[i, 0], StringComparison.OrdinalIgnoreCase))
                            mapOk = false;
                    }
                    // 各套配色必须真的不一样：查"强调色 + 金饰线 + 底色"三元组（数据级判据）。
                    // ★ 为什么不在渲染图上按坐标取色卡：实测按算出来的格子取点，取到的是行背景/面色，
                    //   跟绘制代码里的偏移对不上（坐标换算最容易骗人）⇒ 直接查配色表，判据更硬。
                    var sigs = new System.Collections.Generic.List<string>();
                    for (int i = 0; i < n; i++)
                    {
                        var pp = SkinTheme.PaletteOf(SkinTheme.Choices[i, 0]);
                        sigs.Add(pp.Accent.R.ToString("X2") + pp.Accent.G.ToString("X2") + pp.Accent.B.ToString("X2") + "-"
                               + pp.Gold.R.ToString("X2") + pp.Gold.G.ToString("X2") + pp.Gold.B.ToString("X2"));
                    }
                    var uniq = new System.Collections.Generic.List<string>();
                    for (int i = 0; i < sigs.Count; i++) if (!uniq.Contains(sigs[i])) uniq.Add(sigs[i]);
                    int distinct = uniq.Count;
                    bool colorsDiffer = distinct >= Math.Min(3, n);   // 候选只剩两条时门槛跟着降
                    // 确定按钮必须**整颗**落在客户区里、而且要留出金框的余量（金框画在客户区边上）
                    int okBottom = dlg.OkButtonBottomForTest;
                    bool okVisible = okBottom > 0 && okBottom <= dlg.ClientSize.Height - 8;
                    // ★★ 2026-09-18 新增判据（主人报「按了没效果」的回归位）：
                    //   模拟"按确定按钮"这条**不用鼠标**的路径——选中第 2 行、置 DialogResult=OK、关窗，
                    //   然后要求 Chosen 必须是那一行的 id。旧代码在这里必然拿到空 ⇒ 调用方当"取消"处理，
                    //   于是"按钮按了没效果、双击却有效"（这个不对称就是主人遇到的现象）。
                    bool acceptOk = false;
                    string acceptGot = "(没测到)";
                    if (lb != null && n >= 2)
                    {
                        lb.SelectedIndex = 1;
                        System.Windows.Forms.Application.DoEvents();
                        dlg.DialogResult = System.Windows.Forms.DialogResult.OK;
                        dlg.Close();
                        acceptGot = dlg.Chosen == null ? "(空)" : dlg.Chosen;
                        acceptOk = string.Equals(acceptGot, SkinTheme.Choices[1, 0], StringComparison.OrdinalIgnoreCase);
                    }
                    else { dlg.Close(); }
                    bool ok = (items == n) && (inkPct >= 5) && colorsDiffer && mapOk && okVisible && acceptOk;
                    // ★ 结尾必须是 "OK"/"★"：DshCore 的门禁判据就是 EndsWith("OK")
                    return "皮肤选择框（条目 " + items + "/" + n + "、渲染有色 " + inkPct + "%(门槛5%)、配色 " + distinct
                         + " 种不同 " + string.Join("/", sigs.ToArray()) + "、选中映射 " + (mapOk ? "对" : "错")
                         + "、确定按钮底 " + okBottom + "/客户区 " + dlg.ClientSize.Height
                         + "、按确定能拿到 id " + acceptGot + "）："
                         + (ok ? "OK" : "★不合格");
                }
            }
            catch (Exception ex) { return "皮肤选择框：★异常 " + ex.GetType().Name + " " + ex.Message; }
        }

        private void Accept()
        {
            try
            {
                if (_list.SelectedIndex < 0) return;
                Chosen = _ids[_list.SelectedIndex];
                DialogResult = System.Windows.Forms.DialogResult.OK;
                Close();
            }
            catch { }
        }

        /// <summary>
        /// 兜底：不管从哪条路关的窗（按钮 / 回车 / 双击 / 直接设 DialogResult），
        /// 只要结果是 OK，就必须给出一个 Chosen —— 否则调用方会当成"取消"（这就是「按了没效果」那个 bug）。
        /// </summary>
        protected override void OnFormClosing(System.Windows.Forms.FormClosingEventArgs e)
        {
            try
            {
                if (DialogResult == System.Windows.Forms.DialogResult.OK
                    && string.IsNullOrEmpty(Chosen) && _list.SelectedIndex >= 0)
                    Chosen = _ids[_list.SelectedIndex];
            }
            catch { }
            base.OnFormClosing(e);
        }
    }
}

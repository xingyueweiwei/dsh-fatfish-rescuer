using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using DockStyle = System.Windows.Forms.DockStyle;   // 只用别名，避免 Point/Size 与 System.Drawing 撞名

// ============================================================
//  大肥鱼救星 · 鲸鱼娘皮肤的装饰与素材
//
//  ★ 素材来源（2026-09-18 定稿，取代早先"搬 maid-atelier 素材"的做法）：
//     · 头部两张图（女仆抱大药丸的立绘、蓝色鲸鱼）＝ **主人本人提供**，随程序内嵌，无第三方署名义务。
//     · 蝴蝶结、海浪线、金色海平线、边框压边、对话框金框 —— 全部**本文件里自绘**，不依赖任何外部素材。
//     早先那批 CC BY-NC-SA 的素材（金线花边/蕾丝/卷草纹边角/虎鲸立绘）已整体退役，
//     连"仅限非商业 + 相同方式共享"的署名链一起撤掉 ⇒ 交付物可以放心转发（主人要求）。
//
//  另外：立绘资源名故意**含 "assets"**（MainForm.LoadMaidImage() 就是按"名字含 assets 且
//  以 .png 结尾"挑立绘的）；勾线稿/表情包一类素材要用别的名字前缀，否则会把立绘挑错。
// ============================================================
namespace BigFatFishRescuer
{
    public static class SkinArt
    {
        /// <summary>
        /// 素材名 → 预期尺寸 + "内容占比"下限（%）。自检三条：取得到、尺寸对、**不是空白图**。
        /// ★ 内容占比下限是**按素材各自标定**的，不是拍脑袋一个数：
        ///   实测 bow 52.7% / trim_gold 100% / lace 93.6% / corner 2.7% ——
        ///   corner 是细线稿，天生只占 2.7%；我最初统一用 3% 就把它误杀成"素材缺失"
        ///   （尺子没校准 ⇒ 假失败，本项目的老毛病）。空白图的占比是 ~0%，所以门槛仍有效。
        /// </summary>
        public static readonly string[,] Expected = new string[,]
        {
            // ★ 2026-09-18：旧素材（maid-atelier 的 bow/trim/lace/corner）全部退役 ⇒ 换成 CC0 鲸鱼染色版。
            //   两个都是同一张「Big whale」（Openclipart，CC0 公有领域）按皮肤令牌染色后的结果。
                        { "whale.png", "474x474", "5" },   // 主人挑的鲸鱼（白底由运行时抠掉）
        };

        private static readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>();

        /// <summary>取素材（带缓存）。取不到返回 null —— 调用方一律要能"没有素材也照常跑"。</summary>
        public static Bitmap Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (_cache)
            {
                Bitmap hit;
                if (_cache.TryGetValue(name, out hit)) return hit;
                Bitmap bmp = null;
                try
                {
                    Assembly asm = Assembly.GetExecutingAssembly();
                    string want = "skinart." + name;         // 例：skinart.bow.png
                    foreach (string rn in asm.GetManifestResourceNames())
                    {
                        if (!rn.EndsWith(want, StringComparison.OrdinalIgnoreCase)) continue;
                        using (Stream s = asm.GetManifestResourceStream(rn))
                        {
                            if (s == null) continue;
                            using (var tmp = new Bitmap(s)) bmp = new Bitmap(tmp);  // ★ 必须拷一份再关流
                        }
                        break;
                    }
                }
                catch { bmp = null; }
                _cache[name] = bmp;
                return bmp;
            }
        }

        // ---------- 绘制助手（画不了就什么都不画，绝不抛） ----------

        /// <summary>把素材横向平铺到 row 这一行（按 row.Height 等比缩放；超出部分裁剪）。</summary>
        public static void DrawTiled(Graphics g, string name, Rectangle row, float alpha)
        {
            try
            {
                Bitmap b = Get(name);
                if (b == null || row.Width <= 1 || row.Height <= 1) return;
                int tileW = Math.Max(2, (int)Math.Round(b.Width * (row.Height / (double)b.Height)));
                GraphicsState st = g.Save();
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SetClip(row);
                using (var ia = new ImageAttributes())
                {
                    var cm = new ColorMatrix();
                    cm.Matrix33 = Math.Max(0f, Math.Min(1f, alpha));
                    ia.SetColorMatrix(cm);
                    for (int x = row.Left; x < row.Right; x += tileW)
                        g.DrawImage(b, new Rectangle(x, row.Top, tileW, row.Height),
                            0, 0, b.Width, b.Height, GraphicsUnit.Pixel, ia);
                }
                g.Restore(st);
            }
            catch { }
        }

        /// <summary>画边角卷草纹（mirrorX/mirrorY 用来拼另外三个角）。</summary>
        public static void DrawCorner(Graphics g, Rectangle box, bool mirrorX, bool mirrorY, float alpha)
        {
            try
            {
                Bitmap b = Get("corner.png");
                if (b == null || box.Width <= 1 || box.Height <= 1) return;
                GraphicsState st = g.Save();
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TranslateTransform(mirrorX ? box.Right : box.Left, mirrorY ? box.Bottom : box.Top);
                if (mirrorX || mirrorY) g.ScaleTransform(mirrorX ? -1f : 1f, mirrorY ? -1f : 1f);
                using (var ia = new ImageAttributes())
                {
                    var cm = new ColorMatrix();
                    cm.Matrix33 = Math.Max(0f, Math.Min(1f, alpha));
                    ia.SetColorMatrix(cm);
                    g.DrawImage(b, new Rectangle(0, 0, box.Width, box.Height),
                        0, 0, b.Width, b.Height, GraphicsUnit.Pixel, ia);
                }
                g.Restore(st);
            }
            catch { }
        }

        /// <summary>画蝴蝶结（居中放在 anchor 的中点上）。w&lt;=0 时用素材原始尺寸的 scale 倍。</summary>
        /// <summary>
        /// 画一枚蝴蝶结 —— **完全自己画的，不依赖任何外部素材**。
        /// 为什么要自绘：原先用的是第三方皮肤包里的 bow.png（CC BY-NC-SA），
        /// 那意味着交付时必须带一整条署名链；主人不要那份署名 ⇒ 换成自绘，
        /// 顺带好处是它会**跟着皮肤令牌变色**（别的配色也自动协调）。
        /// 结构：左右两个环（深藏青渐变、金色描边）+ 中间打结 + 一颗金心宝石。
        /// </summary>
        public static void DrawBow(Graphics g, Point center, int w, float alpha)
        {
            try
            {
                SkinTheme.Palette p = SkinTheme.Current();
                if (w <= 0) w = 120;
                int h = Math.Max(8, (int)(w * 0.46));
                float a = Math.Max(0f, Math.Min(1f, alpha));
                Color dark = Color.FromArgb((int)(255 * a), 26, 48, 92);
                Color mid = Color.FromArgb((int)(255 * a), 44, 78, 138);
                Color hi = Color.FromArgb((int)(255 * a), 96, 140, 200);
                Color gold = Color.FromArgb((int)(255 * a), p.Gold);
                Color goldHi = Color.FromArgb((int)(255 * a), p.GoldLight.IsEmpty ? p.Gold : p.GoldLight);

                int lobeW = (int)(w * 0.46), lobeH = (int)(h * 0.70), knotW = (int)(w * 0.18);
                GraphicsState st = g.Save();
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // 两个环：以中心为轴各旋转 ±24°，形成"蝴蝶结"的八字
                for (int k = 0; k < 2; k++)
                {
                    int sign = (k == 0) ? -1 : 1;
                    GraphicsState st2 = g.Save();
                    g.TranslateTransform(center.X + sign * (int)(w * 0.20), center.Y);
                    g.RotateTransform(sign * 24f);
                    var loop = new Rectangle(-lobeW / 2, -lobeH / 2, lobeW, lobeH);
                    using (var lg = new LinearGradientBrush(loop, mid, dark, 60f))
                        g.FillEllipse(lg, loop);
                    using (var pen = new Pen(gold, Math.Max(1.2f, w / 90f)))
                        g.DrawEllipse(pen, loop);
                    var inner = new Rectangle(-lobeW / 2 + lobeW / 5, -lobeH / 2 + lobeH / 5, lobeW * 3 / 5, lobeH * 3 / 5);
                    using (var br = new SolidBrush(Color.FromArgb((int)(90 * a), hi)))
                        g.FillEllipse(br, inner);
                    g.Restore(st2);
                }
                // 打结 + 金心宝石
                var knot = new Rectangle(center.X - knotW / 2, center.Y - (int)(h * 0.30), knotW, (int)(h * 0.60));
                using (var lg = new LinearGradientBrush(knot, hi, mid, 90f))
                    g.FillEllipse(lg, knot);
                using (var pen = new Pen(gold, Math.Max(1.2f, w / 90f)))
                    g.DrawEllipse(pen, knot);
                int gem = Math.Max(4, (int)(h * 0.17));
                var gemR = new Rectangle(center.X - gem / 2, center.Y - gem / 2, gem, gem);
                using (var br = new SolidBrush(gold))
                    g.FillEllipse(br, gemR);
                using (var br = new SolidBrush(Color.FromArgb((int)(200 * a), goldHi)))
                    g.FillEllipse(br, new Rectangle(gemR.X + gem / 4, gemR.Y + gem / 4, gem / 3, gem / 3));
                g.Restore(st);
            }
            catch { }
        }

        /// <summary>金色双线边框（面板/对话框用；内线更亮，形成"金属压边"的层次）。</summary>
        public static void DrawGoldFrame(Graphics g, Rectangle r, Color outer, Color inner)
        {
            try
            {
                if (r.Width < 6 || r.Height < 6) return;
                using (var p1 = new Pen(outer, 2f))
                using (var p2 = new Pen(Color.FromArgb(150, inner), 1f))
                {
                    g.DrawRectangle(p1, r.X + 1, r.Y + 1, r.Width - 3, r.Height - 3);
                    g.DrawRectangle(p2, r.X + 4, r.Y + 4, r.Width - 9, r.Height - 9);
                }
            }
            catch { }
        }

        /// <summary>
        /// 给对话框穿上皮肤：金色边框 + 顶部正中一个蝴蝶结。
        /// （对应主人说的"那个对话框的蝴蝶结" —— 皮肤里输入框/对话框就是这个构图：
        ///   金色卷边外框 + 正中一枚藏青金心蝴蝶结。）
        /// </summary>
        public static void DecorateDialog(System.Windows.Forms.Form dlg, SkinTheme.Palette pal)
        {
            try
            {
                if (dlg == null) return;
                SkinTheme.Palette p = pal ?? SkinTheme.For("");
                dlg.Paint += delegate(object s, System.Windows.Forms.PaintEventArgs e)
                {
                    try
                    {
                        Graphics g = e.Graphics;
                        var r = new Rectangle(0, 0, dlg.ClientSize.Width, dlg.ClientSize.Height);
                        DrawGoldFrame(g, r, p.Gold, p.GoldLight.IsEmpty ? p.Gold : p.GoldLight);
                        // 蝴蝶结是**自绘**的（不再依赖任何外部素材）⇒ 任何皮肤都画
                        DrawBow(g, new Point(r.Width / 2, 2), 86, 1f);
                    }
                    catch { }
                };
                // ★ 运行时弹出的对话框：皮肤引擎只在启动时跑过一遍，所以对话框要**显示时自己再铺一次**
                //   （此处在 new Form() 之后立刻调用，控件还没加完，所以不能马上 ApplyTo）
                dlg.Shown += delegate
                {
                    try { SkinTheme.ApplyTo(dlg, p); } catch { }
                };
            }
            catch { }
        }

        /// <summary>
        /// 判据：对话框装饰**真的画上去了**吗 —— 建个窗体、装饰、DrawToBitmap，
        /// 量顶部正中那一小块"非背景像素"比**不装饰**时多了多少（正/负对照在同一次调用里做完）。
        /// 为什么不能只判素材取得到：装饰代码可能压根没接到窗体上，那种情况下素材好好的、界面没变化。
        /// </summary>
        public static bool DialogDecorInk(out string note)
        {
            note = "";
            try
            {
                using (Bitmap plain = RenderDialog(false))
                using (Bitmap deco = RenderDialog(true))
                {
                    // ★ 判据必须与背景色无关：窗体底色是 #F0F0F0，我第一版按"白=背景"去数，
                    //   结果两边都数出满屏 5280（把底色全算成内容）⇒ 改成正/负对照**逐像素比差**。
                    int diff = 0, x0 = plain.Width / 2 - 70;
                    for (int y = 0; y < Math.Min(50, plain.Height); y++)
                        for (int x = Math.Max(0, x0); x < Math.Min(plain.Width, x0 + 140); x++)
                        {
                            Color a = plain.GetPixel(x, y), b = deco.GetPixel(x, y);
                            if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 24) diff++;
                        }
                    bool ok = diff > 200;
                    note = "装饰带来的改变像素 " + diff + "（未装饰对照逐像素比差，>200 判通过）"
                         + (ok ? " OK" : " ★没画上");
                    return ok;
                }
            }
            catch (Exception ex) { note = "异常：" + ex.GetType().Name + " ★"; return false; }
        }

        /// <summary>渲染一个探针对话框（decorate=true 时穿上皮肤），供正/负对照比差用。</summary>
        private static Bitmap RenderDialog(bool decorate)
        {
            Bitmap shot = null;
            using (var f = new System.Windows.Forms.Form())
            {
                f.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
                f.ClientSize = new Size(420, 200);
                f.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                f.Location = new Point(-4000, -4000);
                f.ShowInTaskbar = false;
                if (decorate) DecorateDialog(f, SkinTheme.For("maid-atelier"));
                f.Show();
                for (int i = 0; i < 6; i++) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                shot = new Bitmap(f.ClientSize.Width, f.ClientSize.Height);
                f.DrawToBitmap(shot, new Rectangle(0, 0, shot.Width, shot.Height));
                f.Close();
            }
            return shot;
        }
        /// <summary>
        /// 画一条"金属压边"：深海军蓝底 + 双金线 + 一条高光细线。
        /// 为什么需要它：这套皮肤的边框是**厚压边**（藏青 + 双金线 + 蕾丝），不是一根细线；
        /// 只画一根线会显得"边框太小、跟皮肤不搭"（主人原话：边框太小了这个皮肤）。
        /// </summary>
        private static float EdgeX(Rectangle r, DockStyle side, float d)
        {
            return (side == DockStyle.Left) ? r.Left + d : r.Right - d;
        }

        private static float EdgeY(Rectangle r, DockStyle side, float d)
        {
            return (side == DockStyle.Top) ? r.Top + d : r.Bottom - d;
        }

        /// <summary>边框厚度（按风格）：0 不画 / 1 细压边 8 / 2 厚压边 14 / 3 烫金细线 6 / 4 裱画 12 / 5 素材花边 14。</summary>
        public static int FrameThickness(int style)
        {
            switch (style)
            {
                case 0: return 0;    // 不画框
                case 1: return 8;    // 细金属压边
                case 3: return 6;    // 烫金细线
                case 4: return 13;   // 裱画式（3 框 + 1.5 金线 + 7 垫 + 1 内金线；底边另加 3）
                case 5: return 14;   // 皮肤素材花边
            }
            return 14;               // 2 = 厚金属压边（默认）
        }

        /// <summary>
        /// 画一条边框。style 见 FrameThickness 的注释；各风格是**不同的做法**，不是同一做法的粗细：
        ///   2 金属压边（深色轨道 + 双金线 + 内侧浅衬线）
        ///   3 烫金细线（不留深色轨道，只在边缘描金）
        ///   4 裱画式（内容不贴边：浅色衬垫 + 极细深边 + 金线）
        ///   5 素材花边（直接平铺皮肤自带的金线花边素材；没有素材的皮肤退回 3）
        /// ★ 所有风格都**按边方向（side）显式算偏移**，左右/上下天然镜像。
        /// </summary>
        public static void DrawFrameEdge(Graphics g, Rectangle r, DockStyle side, SkinTheme.Palette pal, int style)
        {
            try
            {
                SkinTheme.Palette p = pal ?? SkinTheme.For("");
                if (r.Width <= 2 || r.Height <= 2 || style == 0) return;
                Color navy = Color.FromArgb(23, 35, 71);
                Color navyMid = Color.FromArgb(45, 68, 118);
                Color gold = p.Gold;
                Color goldHi = p.GoldLight.IsEmpty ? p.Gold : p.GoldLight;
                Color mat = p.Bg;
                bool vertical = (side == DockStyle.Left || side == DockStyle.Right);
                bool lowSide = (side == DockStyle.Left || side == DockStyle.Top);
                int dir = lowSide ? 1 : -1;
                // 由外往内的第 d 像素（左/上为正方向，右/下为负方向）
                // ★ 本项目的编译器是 .NET Framework 的 csc（只到 C# 5）⇒ **不能用局部函数**（C# 7 特性），
                //   第一版写成 `float X(float d) {...}` 直接语法崩，改成类级私有方法。

                if (style == 3)   // 烫金细线：只描金线，不铺深色轨道
                {
                    // ★ 这条带的底色必须跟**底色**一致：暗色皮肤下若用浅色垫，窗口边上会出现一圈白线（实测踩过）
                    using (var br = new SolidBrush(p.Dark ? p.BgDeep : mat)) g.FillRectangle(br, r);
                    using (var penG = new Pen(gold, 2f))
                    using (var penH = new Pen(Color.FromArgb(150, goldHi), 1f))
                    {
                        if (vertical)
                        {
                            g.DrawLine(penG, EdgeX(r, side, 1.5f), r.Top, EdgeX(r, side, 1.5f), r.Bottom);
                            g.DrawLine(penH, EdgeX(r, side, 4.5f), r.Top + 2, EdgeX(r, side, 4.5f), r.Bottom - 2);
                        }
                        else
                        {
                            g.DrawLine(penG, r.Left, EdgeY(r, side, 1.5f), r.Right, EdgeY(r, side, 1.5f));
                            g.DrawLine(penH, r.Left + 2, EdgeY(r, side, 4.5f), r.Right - 2, EdgeY(r, side, 4.5f));
                        }
                    }
                    return;
                }

                if (style == 4)   // 裱画式（按画框裱画的行业规矩做，见注释）
                {
                    // ★★ 2026-09-18 重做（主人选了 4，并要求"上网查人家怎么做"）。查到的规矩与实际落地：
                    //   ① **分层**：真实画框由外往内是「外框 → 金线(fillet) → 垫(mat) → 内金线 → 作品」，
                    //      不是一条线糊一圈 ⇒ 这里就按这个层次画（3px 深框 / 1.5px 金线 / 7px 垫 / 1px 内金线）。
                    //   ② **底边加权**：传统裱画底边比上边和侧边宽 ½~1 吋（视觉重心更稳；等宽反而显"底重"）
                    //      ⇒ 缩放到窗口尺度就是**底边垫多 3px**。
                    //   ③ **深垫显胀、浅垫后退**：垫要比内容**更暖一档**才分得开（直接用内容底色会糊在一起）
                    //      ⇒ 用皮肤 CSS 实测的暖奶油 #f3e8cf，而不是内容白。
                    //   ④ **只描细线、不做发光**（UI 侧通行做法：hairline 1px，不用霓虹色）⇒ 金线 1~1.5px。
                    //   ✗ **不照搬**"垫边 ≥ 作品短边 1/3"：换算到本窗口要 ~250px 垫边，窗口上荒谬 ——
                    //      只把**比例关系**缩到 7px。
                    Color matCol = p.Mat.IsEmpty ? p.Bg : p.Mat;
                    const float frameW = 3f, filletW = 1.5f;
                    float matW = 7f + ((side == DockStyle.Bottom) ? 3f : 0f);   // ★ 底边加权
                    float fOut = frameW;                    // 金线中线（框外→内）
                    float matA = frameW + filletW;          // 垫起点
                    float matB = matA + matW;               // 垫终点（= 内金线位置）
                    using (var brF = new SolidBrush(navy))
                    using (var brM = new SolidBrush(matCol))
                    using (var penF = new Pen(gold, filletW))
                    using (var penI = new Pen(gold, 1f))
                    using (var penB = new Pen(Color.FromArgb(130, goldHi), 1f))
                    {
                        if (vertical)
                        {
                            float xe = (side == DockStyle.Left) ? r.Left : r.Right;
                            int sx = (side == DockStyle.Left) ? r.Left : (int)(r.Right - frameW);
                            g.FillRectangle(brF, sx, r.Top, (int)Math.Ceiling(frameW), r.Height);
                            g.DrawLine(penF, EdgeX(r, side, fOut), r.Top, EdgeX(r, side, fOut), r.Bottom);
                            int mx = (side == DockStyle.Left) ? (int)(r.Left + matA) : (int)(r.Right - matB);
                            g.FillRectangle(brM, mx, r.Top, (int)Math.Ceiling(matW), r.Height);
                            g.DrawLine(penI, EdgeX(r, side, matB), r.Top, EdgeX(r, side, matB), r.Bottom);
                            g.DrawLine(penB, EdgeX(r, side, matB + 1f), r.Top + 2, EdgeX(r, side, matB + 1f), r.Bottom - 2);
                        }
                        else
                        {
                            int sy = (side == DockStyle.Top) ? r.Top : (int)(r.Bottom - frameW);
                            g.FillRectangle(brF, r.Left, sy, r.Width, (int)Math.Ceiling(frameW));
                            g.DrawLine(penF, r.Left, EdgeY(r, side, fOut), r.Right, EdgeY(r, side, fOut));
                            int my = (side == DockStyle.Top) ? (int)(r.Top + matA) : (int)(r.Bottom - matB);
                            g.FillRectangle(brM, r.Left, my, r.Width, (int)Math.Ceiling(matW));
                            g.DrawLine(penI, r.Left, EdgeY(r, side, matB), r.Right, EdgeY(r, side, matB));
                            g.DrawLine(penB, r.Left + 2, EdgeY(r, side, matB + 1f), r.Right - 2, EdgeY(r, side, matB + 1f));
                        }
                    }
                    return;
                }
                if (style == 5)   // 素材花边：平铺皮肤自带的金线花边（竖边把素材旋转 90°）
                {
                    if (Get("trim_gold.png") == null)   // 素材已退役 ⇒ 永远回退到 3 号烫金细线
                    {
                        DrawFrameEdge(g, r, side, p, 3);      // 没素材的皮肤退回烫金细线
                        return;
                    }
                    if (!vertical)
                    {
                        DrawTiled(g, "trim_gold.png", r, 1f);
                    }
                    else
                    {
                        GraphicsState st = g.Save();
                        if (side == DockStyle.Left) { g.TranslateTransform(r.Left, r.Bottom); g.RotateTransform(-90f); }
                        else { g.TranslateTransform(r.Right, r.Top); g.RotateTransform(90f); }
                        DrawTiled(g, "trim_gold.png", new Rectangle(0, 0, r.Height, r.Width), 1f);
                        g.Restore(st);
                    }
                    return;
                }

                // style 1/2：金属压边（深色轨道 + 双金线 + 内侧浅衬线）
                var blend = new ColorBlend(3);
                blend.Colors = new Color[] { navy, navyMid, navy };
                blend.Positions = new float[] { 0f, 0.5f, 1f };
                using (var lg = vertical
                    ? new LinearGradientBrush(new Point(0, 0), new Point(r.Width, 0), navy, navy)
                    : new LinearGradientBrush(new Point(0, 0), new Point(0, r.Height), navy, navy))
                {
                    lg.InterpolationColors = blend;
                    g.FillRectangle(lg, r);
                }
                using (var penG = new Pen(gold, 2f))
                using (var penH = new Pen(Color.FromArgb(170, goldHi), 1f))
                {
                    if (vertical)
                    {
                        g.DrawLine(penG, EdgeX(r, side, 3f), r.Top, EdgeX(r, side, 3f), r.Bottom);
                        g.DrawLine(penG, EdgeX(r, side, 8f), r.Top, EdgeX(r, side, 8f), r.Bottom);
                        g.DrawLine(penH, EdgeX(r, side, 10f), r.Top + 2, EdgeX(r, side, 10f), r.Bottom - 2);
                    }
                    else
                    {
                        g.DrawLine(penG, r.Left, EdgeY(r, side, 3f), r.Right, EdgeY(r, side, 3f));
                        g.DrawLine(penG, r.Left, EdgeY(r, side, 8f), r.Right, EdgeY(r, side, 8f));
                        g.DrawLine(penH, r.Left + 2, EdgeY(r, side, 10f), r.Right - 2, EdgeY(r, side, 10f));
                    }
                }
                using (var br = new SolidBrush(mat))
                {
                    if (vertical) g.FillRectangle(br, lowSide ? r.Right - 4 : r.Left, r.Top, 4, r.Height);
                    else g.FillRectangle(br, r.Left, lowSide ? r.Bottom - 4 : r.Top, r.Width, 4);
                }
            }
            catch { }
        }
        /// <summary>画布上"有内容"的像素占比（非全透明、且不是纯白的算有内容）。</summary>
        /// <summary>
        /// 抠掉**白底背景**（只抠"与画布四边连通的近白区域"），保留画面**内部**的白色。
        ///
        /// ★ 为什么不能按"近白就全透"一刀切（2026-09-18 主人报「药丸怎么不是原样了」的根因）：
        ///   主人的立绘是「女仆抱着大药丸」——药丸的**上半截本来就是白的**、女仆的围裙和头顶
        ///   蕾丝也是白的。按"全通道 ≥236 就全透"处理，这些**内部白**会被一起抠掉，
        ///   药丸的上半截变成空洞（深蓝头部透出来），女仆变镂空 —— 主人一眼就看出"不是原样了"。
        ///   正解＝**泛洪法**：从四边出发，只把"从外边能走到的近白像素"当背景（4 连通），
        ///   被黑色描边封在里头的白（药丸白半、眼睛高光、鲸鱼肚皮）全部原样留下。
        ///
        /// 参数：全通道 ≥236 判为纯背景；紧贴背景的 205~236 浅灰像素按比例给 alpha（消白边毛刺）。
        /// ★ 用 LockBits 走字节缓冲：百万像素级图片逐像素 GetPixel 要好几秒，会拖慢启动。
        /// </summary>
        public static Bitmap CutOutBackground(Bitmap src)
        {
            if (src == null) return null;
            try
            {
                var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0, src.Width, src.Height);
                int W = bmp.Width, H = bmp.Height;
                var rect = new Rectangle(0, 0, W, H);
                var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                try
                {
                    int stride = data.Stride;
                    int bytes = Math.Abs(stride) * H;
                    var buf = new byte[bytes];
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, bytes);

                    // 1) 每个像素的"白度"＝三通道最小值（内存序 B,G,R,A）
                    var mn = new byte[W * H];
                    for (int y = 0; y < H; y++)
                    {
                        int row = y * stride, p = y * W;
                        for (int x = 0; x < W; x++)
                        {
                            int i = row + x * 4;
                            int m = buf[i];
                            if (buf[i + 1] < m) m = buf[i + 1];
                            if (buf[i + 2] < m) m = buf[i + 2];
                            mn[p + x] = (byte)m;
                        }
                    }

                    // 2) 从四边泛洪（显式栈，避免递归爆栈；本图 1080x1336 有 144 万像素）
                    var bg = new bool[W * H];
                    var stack = new int[W * H];
                    int sp = 0;
                    for (int x = 0; x < W; x++)
                    {
                        if (mn[x] >= 236) { bg[x] = true; stack[sp++] = x; }
                        int b = (H - 1) * W + x;
                        if (mn[b] >= 236 && !bg[b]) { bg[b] = true; stack[sp++] = b; }
                    }
                    for (int y = 0; y < H; y++)
                    {
                        int a = y * W, c = y * W + W - 1;
                        if (mn[a] >= 236 && !bg[a]) { bg[a] = true; stack[sp++] = a; }
                        if (mn[c] >= 236 && !bg[c]) { bg[c] = true; stack[sp++] = c; }
                    }
                    while (sp > 0)
                    {
                        int p = stack[--sp];
                        int px = p % W, py = p / W;
                        if (px > 0) { int n = p - 1; if (!bg[n] && mn[n] >= 236) { bg[n] = true; stack[sp++] = n; } }
                        if (px < W - 1) { int n = p + 1; if (!bg[n] && mn[n] >= 236) { bg[n] = true; stack[sp++] = n; } }
                        if (py > 0) { int n = p - W; if (!bg[n] && mn[n] >= 236) { bg[n] = true; stack[sp++] = n; } }
                        if (py < H - 1) { int n = p + W; if (!bg[n] && mn[n] >= 236) { bg[n] = true; stack[sp++] = n; } }
                    }

                    // 3) 落地：背景全透；只有"紧贴背景"的浅灰像素才按白度给 alpha
                    for (int y = 0; y < H; y++)
                    {
                        int row = y * stride, p = y * W;
                        for (int x = 0; x < W; x++)
                        {
                            int idx = p + x, i = row + x * 4;
                            if (bg[idx]) { buf[i + 3] = 0; continue; }
                            int m = mn[idx];
                            if (m < 205) continue;
                            bool near = (x > 0 && bg[idx - 1]) || (x < W - 1 && bg[idx + 1])
                                     || (y > 0 && bg[idx - W]) || (y < H - 1 && bg[idx + W]);
                            if (!near) continue;
                            int na = 255 * (236 - m) / 31;
                            if (na < 0) na = 0;
                            if (na > 255) na = 255;
                            if (na < buf[i + 3]) buf[i + 3] = (byte)na;
                        }
                    }
                    System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, bytes);
                }
                finally { bmp.UnlockBits(data); }
                return bmp;
            }
            catch { return src; }
        }

        /// <summary>只取上面 keepRatio 那一截（用来裁掉立绘原图底部的黑字标题带）。</summary>
        public static Bitmap CropTop(Bitmap src, double keepRatio)
        {
            if (src == null) return null;
            try
            {
                int h = (int)(src.Height * keepRatio);
                if (h < 8) h = src.Height;
                if (h >= src.Height) return src;
                var bmp = new Bitmap(src.Width, h, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.DrawImage(src, new Rectangle(0, 0, src.Width, h),
                                new Rectangle(0, 0, src.Width, h), GraphicsUnit.Pixel);
                }
                return bmp;
            }
            catch { return src; }
        }

        // ---------- 自检 ----------
        /// <summary>
        /// 判据：四个素材都要能取到、尺寸要对、而且**不能是空白图**（内容占比 ≥ 各素材各自标定的门槛）。
        /// 门槛按素材分别标定（细线稿的 corner 天生只占 ~2.7%，统一用一个数会误杀）。
        /// </summary>
        public static string SelftestLine()
        {
            int okCount = 0, total = Expected.GetLength(0);
            string dnote;
            bool dok = DialogDecorInk(out dnote);
            string detail = "";
            for (int i = 0; i < total; i++)
            {
                string name = Expected[i, 0], want = Expected[i, 1];
                double pct = 1;
                double.TryParse(Expected[i, 2], out pct);
                double minInk = pct / 100.0;
                Bitmap b = Get(name);
                bool ok = false;
                string got = "取不到";
                if (b != null)
                {
                    got = b.Width + "x" + b.Height;
                    double ink = InkRatio(b);
                    ok = (got == want) && (ink >= minInk);
                    got += " 内容" + (ink * 100).ToString("0.0") + "%(门槛" + (minInk * 100).ToString("0.#") + "%)";
                }
                if (ok) okCount++;
                detail += name + "=" + got + (ok ? " " : " ★ ");
            }
            string cut = CutOutSelftestLine();
            bool cutOk = cut.StartsWith("OK", StringComparison.Ordinal);
            return "皮肤素材（鲸鱼剪影）：" + okCount + "/" + total
                   + "（" + detail.Trim() + "）"
                   + "　对话框装饰：" + dnote
                   + "　抠底：" + cut
                   + ((okCount == total && dok && cutOk) ? " OK" : " ★素材/装饰/抠底有问题");
        }

        /// <summary>
        /// 抠底判据（阳性 + 阴性双对照）：造一张"白底 + 深色方框 + 框内留白"的合成图，
        ///   ① **阴性对照**：画布四角的纯白（通到边框的背景）必须被抠成全透明；
        ///   ② **阳性对照**：方框**里面**那块白必须**原样留着**（alpha=255）。
        /// ★ 为什么必须要 ②：2026-09-18 主人报「药丸怎么不是原样了」——旧实现是"近白就全透"，
        ///   把药丸的白色上半截、女仆的白围裙一起抠掉了。这条判据就是为那次事故立的，
        ///   它一旦变红就说明"内部白又被吃了"，不许放行。
        /// </summary>
        public static string CutOutSelftestLine()
        {
            try
            {
                using (var b = new Bitmap(48, 48, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(b))
                    {
                        g.Clear(Color.White);
                        using (var dark = new SolidBrush(Color.FromArgb(20, 20, 20)))
                        {
                            g.FillRectangle(dark, 12, 12, 24, 24);
                            g.FillRectangle(Brushes.White, 18, 18, 12, 12);
                        }
                    }
                    using (Bitmap cut = CutOutBackground(b))
                    {
                        int aBg = cut.GetPixel(2, 2).A;        // 阴性：背景白 → 应全透
                        int aIn = cut.GetPixel(24, 24).A;      // 阳性：框内白 → 应保留
                        int aInk = cut.GetPixel(24, 14).A;      // 深色方框本身不许被动
                        bool ok = (aBg == 0) && (aIn == 255) && (aInk == 255);
                        return (ok ? "OK" : "★内部白被误抠")
                             + "（背景白 alpha=" + aBg + " 应0 / 框内白 alpha=" + aIn + " 应255 / 深色 alpha=" + aInk + " 应255）";
                    }
                }
            }
            catch (Exception ex) { return "★异常 " + ex.Message; }
        }

        private static double InkRatio(Bitmap b)
        {
            try
            {
                int step = Math.Max(1, Math.Min(b.Width, b.Height) / 60);
                int ink = 0, all = 0;
                for (int y = 0; y < b.Height; y += step)
                    for (int x = 0; x < b.Width; x += step)
                    {
                        all++;
                        Color c = b.GetPixel(x, y);
                        if (c.A > 24 && !(c.R > 246 && c.G > 246 && c.B > 246)) ink++;
                    }
                return all == 0 ? 0 : ink / (double)all;
            }
            catch { return 0; }
        }
    }
}

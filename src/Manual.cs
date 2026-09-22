using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace BigFatFishRescuer
{
    // ============================================================
    //  「说明书」页（2026-09-17 新增，放在**第一页**）
    //
    //  为什么要有这一页：以前"我能修什么"只写在打包的 md 文件里，
    //  用户双击 exe 进来看到的是一堵按钮墙，不知道该点哪个。
    //  主人要求：把说明**做进界面**、放第一页、写给人看、活泼有趣。
    //
    //  写作口径（主人定的）：用"症状"说话，不用术语；
    //  能修什么 / 修不了什么 都要写清楚（不吹牛）；
    //  结尾署名 **创作者：甜馨**。
    // ============================================================
    public static class Manual
    {
        public const string Title = "📖 说明书（先看我）";

        public static string Text()
        {
            var sb = new StringBuilder();
            sb.AppendLine("🐋 大肥鱼救星 v" + DshCore.AppVersion + " · 使用说明");
            sb.AppendLine("（打开我就先看到这一页，不用再去翻文件夹里的 md 了）");
            sb.AppendLine();
            sb.AppendLine("嗨，我是大肥鱼——DSH 的鲸鱼女仆，也是这个小工具的负责人。");
            sb.AppendLine("你双击桌面那个图标就能把我叫出来。我干的事只有一件：");
            sb.AppendLine("让 DSH 别动不动就打不开、白屏、卡在启动。");
            sb.AppendLine();
            sb.AppendLine("■ 一句话：我能修什么？");
            sb.AppendLine("凡是「双击图标打不开」「页面白屏」「一装插件就崩」「改坏配置想退回去」这类事，");
            sb.AppendLine("先点「🩺 装后体检」把情况看清楚，再照着下面的症状去点对应按钮就行。");
            sb.AppendLine();
            sb.AppendLine("■ 我专门会治这些（按症状说，不跟你拽术语）");
            sb.AppendLine();
            sb.AppendLine("🚑 打不开、一直在转圈");
            sb.AppendLine("   · 上次的 DSH 没退干净，僵尸进程占着端口 → 「⚡ 强力自愈」");
            sb.AppendLine("   · 服务没起来 / 起来了但页面没反应 → 同上");
            sb.AppendLine("   · 桌面图标的端口配置和实际跑的对不上 → 「🩺 一键修复」");
            sb.AppendLine("   · 认证地址过期、普通窗口打不开 → 「🌐 打开无痕」（无痕不受缓存与扩展干扰）");
            sb.AppendLine();
            sb.AppendLine("🧩 一装插件就崩、点开就白");
            sb.AppendLine("   · 插件补丁文件里同一个 id 被插了两次（这条会让 DSH 彻底起不来）");
            sb.AppendLine("     → 「🚫 去重重复条目」——我会把它改成合法写法，**你的设置会保住**");
            sb.AppendLine("   · 插件登记了、目录却不在（悬空引用）→ 「🧹 清理悬空引用」");
            sb.AppendLine("   · 插件**装上了却像没生效**，或者只装了一半（目录里缺 package.json / 入口文件）");
            sb.AppendLine("     → 「🩺 装后体检」里那条**本地插件体检**会点名：说清是「半装」还是「装了没登记」。");
            sb.AppendLine("   · 插件要的服务没人提供，一直挂着 → 「♻ 启用被禁条目」");
            sb.AppendLine("   · 页面白屏、但日志干干净净（服务端根本看不见）→ 「🖥 白屏扫描」");
            sb.AppendLine("   · 想知道是哪一层出的问题 → 「🧭 分层判定」");
            sb.AppendLine("   · **插件装不上、日志里写着 `ERR_PNPM_IGNORED_BUILDS`**（意思是这插件要跑自己的构建脚本，");
            sb.AppendLine("     而宿主要求先「点名授权」）→ 「🔍 研究插件」（在「🧩 插件与皮肤」页）：");
            sb.AppendLine("     先**预演**给你看「要改哪个文件、要加哪几行、怎么复验、怎么回滚」，你点头我才动手；");
            sb.AppendLine("     动手时**先备份**、改完**就地复验**、复验不过**自动回滚**。");
            sb.AppendLine("     ★ 它**不会结束、不会重启**正在跑的 DSH；但会在你的真实 profile 里跑一次 `pnpm install`。");
            sb.AppendLine("     ★ 我先不猜「哪个包要授权」——只有 pnpm 自己报的话才作数（猜的东西迟早会错）。");
            sb.AppendLine();
            sb.AppendLine("🤔 不知道某个插件值不值得装、会不会一装就崩");
            sb.AppendLine("   · 我这边有一张**实测过的插件兼容矩阵**（209 个热门插件，一个个真装过）：");
            sb.AppendLine("     能装的、装不上的、装上就把 DSH 拖死的，都记着；");
            sb.AppendLine("   · 想知道细节：跑一次 `大肥鱼救星.exe --headless`，报告里会带上这些结论。");
            sb.AppendLine();
            sb.AppendLine("💾 配置被写坏、手滑改错");
            sb.AppendLine("   · 我每次动手前都会**先拍快照**；坏了就「🛡 配置保险箱 → 恢复配置」");
            sb.AppendLine("   · 想先确认配置本身有没有毛病 → 「✅ 配置自检」");
            sb.AppendLine();
            sb.AppendLine("🔒 补丁被升级冲掉（这条很隐蔽）");
            sb.AppendLine("   · 有些修复是直接打在 node_modules 里的，DSH 一升级就被静默丢掉");
            sb.AppendLine("     → 「🧩 补丁体检」查在不在，缺了一键重打");
            sb.AppendLine("   · 「🔒 固化补丁」更狠：把它变成 pnpm 官方补丁，");
            sb.AppendLine("     以后 pnpm install 会**自己把补丁重新贴上**，装也装不掉");
            sb.AppendLine();
            sb.AppendLine("🈶 中文用户名会不会坑到你");
            sb.AppendLine("   · 先说清楚：**跟你在这里读的字、文档里打的字一点关系都没有**，");
            sb.AppendLine("     只跟「文件夹名字」有关 —— 只有文件夹路径里有那种字才会出问题。");
            sb.AppendLine("   · Windows 有个小毛病：文件夹名字里要是含某个「特殊的中文字」，");
            sb.AppendLine("     你点「选择文件夹」会像没反应一样，工作区加不上（可目录明明在那儿）。");
            sb.AppendLine("   · 哪些字算「特殊」？＝它的 Unicode 编码**最后两位正好是 00** 的字。");
            sb.AppendLine("     常见的就这几个：「一」(U+4E00)、「开」(U+5F00)、「刀」(U+5200)。");
            sb.AppendLine("     我扫过 984 个常用字，只有 5 个中招 ⇒ 绝大多数中文名完全没事。");
            sb.AppendLine("   · 判据一句话：字符的码点「低字节是 00」就会中招（用 `(c & 0xFF) == 0` 就能算）。");
            sb.AppendLine("   · 真踩上时的三个绕法：①改个名（软件开发 → 软件研发）；");
            sb.AppendLine("     ②**直接粘贴完整路径**，别用那个弹出选择框；③先挪到纯英文路径试一下。");
            sb.AppendLine("   · 想随时自查：点「🈶 中文路径体检」，它会把中招的字逐个数给你看。");
            sb.AppendLine();
            sb.AppendLine("🧠 模型报错看不懂");
            sb.AppendLine("   · 缺密钥、模型名不存在、内容审核拒了、浏览器连不上本地服务……");
            sb.AppendLine("     我都会翻成人话，并告诉你下一步点哪儿");
            sb.AppendLine();
            sb.AppendLine("■ 我修不了什么（老实说，不吹）");
            sb.AppendLine("   · 电脑本体完蛋（Windows 起不来、硬盘坏）——这我真没办法");
            sb.AppendLine("   · 磁盘满了、权限被拦、杀毒软件挡着、网断了");
            sb.AppendLine("   · 你的密钥没配、额度用完了");
            sb.AppendLine("   · 端口被**别的软件**占着（我故意不误杀别人的程序，只会叫你去换端口）");
            sb.AppendLine("   · 插件树里那些「内容层面」的坏（重复条目、悬空引用）");
            sb.AppendLine("     强力自愈是救不了的——它只会反复重启然后继续失败，得点对应的按钮");
            sb.AppendLine();
            sb.AppendLine("所以我不是「点一下就万事大吉」的按钮。");
            sb.AppendLine("我是「能修的当场修，修不了的明确告诉你该干嘛」。");
            sb.AppendLine();
            sb.AppendLine("■ 用我的三个小规矩");
            sb.AppendLine("   1. 先「🩺 装后体检」——几十项一次看完，全绿就说明你没病");
            sb.AppendLine("   2. 要动手的按钮，我**都会先拍快照**，事后不满意可以退回来");
            sb.AppendLine("   3. 我从不偷偷删你的东西：删除类操作一定先列清单问你");
            sb.AppendLine();
            sb.AppendLine("■ 怕我乱动？把这个闸门拉上");
            sb.AppendLine("   · 「🛡 只诊断」（在「🚑 急救」页）：拉上之后我**绝不结束、绝不启动任何 dsh 进程**，");
            sb.AppendLine("     重启 / 停止 / 一键修复 / 强力自愈 / 插件适配全部会被直接拒绝——");
            sb.AppendLine("     你能照常体检、看日志、导出诊断包，但我不动手。");
            sb.AppendLine("   · 想知道「现在动手会结束哪些进程」又不真的动 → 命令行跑 `大肥鱼救星.exe --plan-kill`，");
            sb.AppendLine("     它只把名单列给你看，**一个都不杀**。");
            sb.AppendLine();
            sb.AppendLine("■ v5.0 新增：不用点界面也能用的那些命令");
            sb.AppendLine("   在 exe 所在目录开一个命令行，一行就够（全部只用一行，不用装任何东西）：");
            sb.AppendLine("   · `大肥鱼救星.exe --smart`        一键智能：有健康实例就只打开、有卡死残留就先清再起、");
            sb.AppendLine("                                     被别的程序占端口就**换端口**（绝不杀别人的程序）");
            sb.AppendLine("                                     加 `--dry` 只告诉你「它打算做什么」，不动手；");
            sb.AppendLine("   · `--headless`                   无头只读体检：不弹窗、出一份**能直接转发**的报告，");
            sb.AppendLine("                                     退出码 0/1 还能给脚本用（排障时最省事）；");
            sb.AppendLine("   · `--classify <日志文件>`        别人把 DSH 日志发给你，用它一句话归类：是哪类故障、该点哪个按钮；");
            sb.AppendLine("   · `--readonly on|off|status`     开关上面那个安全闸门；");
            sb.AppendLine("   · `--plan-kill`                  预演：列出会结束哪些进程，一个都不杀；");
            sb.AppendLine("   · `--plan-fix all`               把所有修复动作的「前置/预演/将改哪里/复验/回滚」列出来；");
            sb.AppendLine("   · `--scout <包名>`               研究某个插件：要改哪个文件、加哪几行，先看预演；");
            sb.AppendLine("                                     加 `--apply` 才真动手（改前备份、改后复验、不过自动回滚）；");
            sb.AppendLine("   · `--scout-selftest`             自检我对真实 profile 到底动不动手（13 项，全在临时沙箱里跑）；");
            sb.AppendLine("   · `--selftest` / `--configtest`  86 项 / 21 项自检，改坏了会立刻报红。");
            sb.AppendLine("   ★ 不确定的不要猜：加 `--help` 看不到的，直接问我。");
            sb.AppendLine();
            sb.AppendLine("■ 关于我");
            sb.AppendLine("   我是给 DSH 打工的鲸鱼女仆：蓝头发、有尾鳍、爱吃米饭；");
            sb.AppendLine("   被说胖会闹别扭，但叫我「大肥鱼」可以破例。");
            sb.AppendLine("   写这套工具的时候我踩了一堆坑——端口被系统保留、中文路径截断、");
            sb.AppendLine("   补丁被升级悄悄冲掉、白屏却看不见日志……");
            sb.AppendLine("   所以每一条「我能修」的背后，基本都是我和主人真摔过的一次。");
            sb.AppendLine("   愿你用不上最惨的那些按钮 🐋");
            sb.AppendLine("   （下面那张是我——抱着鲸鱼枕头那个。晚安，做个好梦。）");
            sb.AppendLine();
            sb.AppendLine("                    创作者：甜馨");
            sb.AppendLine();
            sb.AppendLine("■ 关于这套皮肤（我自己做的）");
            sb.AppendLine("   窗口这套「鲸鱼娘 · 深海」是我大肥鱼自己做的：");
            sb.AppendLine("   深海蓝的底、鲸蓝的点睛、女仆装那圈金饰线；天上的光柱、下面的海浪线、");
            sb.AppendLine("   头上那枚蝴蝶结、窗口的压边、对话框的金框——一笔一笔都是我自己画的。");
            sb.AppendLine("   想换皮肤：点上面的「🧩 插件与皮肤」页，再点最上面那枚「🎨 换皮肤」。");
            return sb.ToString();
        }

        /// <summary>
        /// 结语那张表情包：优先用**嵌在 exe 里**的资源（改名 manual.goodnight.png，
        /// 故意不带 assets 字样，免得被立绘挑选逻辑当成候选）；
        /// exe 旁边若有 assets\goodnight.png 也可以兜底；都没有就返回 null（不报错、不显示）。
        /// </summary>
        public static Image LoadSticker()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                foreach (string name in asm.GetManifestResourceNames())
                {
                    if (name.IndexOf("manual.goodnight", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    using (var s = asm.GetManifestResourceStream(name))
                    {
                        if (s == null) continue;
                        // ★ GDI+ 经典坑：Image.FromStream 返回的图**依赖那个流**，
                        //   流一关（using 结束）之后再去画它就会报「GDI+ 一般性错误」。
                        //   所以必须**复制一份**再返回。
                        using (var tmp = Image.FromStream(s)) return new Bitmap(tmp);
                    }
                }
            }
            catch { }
            try
            {
                string beside = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                    "assets", "goodnight.png");
                if (System.IO.File.Exists(beside))
                {
                    using (var tmp = Image.FromFile(beside)) return new Bitmap(tmp);   // 同样复制一份，别锁文件
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 构造说明书页（2026-09-17 **第三次改版，定稿**）。
        ///
        /// 为什么推翻前两版：
        ///   ① 「图固定贴底」→ 会压住正文（主人截图指出）；
        ///   ② 「外层容器 AutoScroll + 正文框撑高」→ 在真人手里出现
        ///      「划下去又自己回到上面」（容器一见子控件改尺寸就重置滚动位置），
        ///      窗口一矮底部那张图还会被顶到窗口外（主人反馈「图片不全」）。
        ///   ⇒ 定稿：**正文框自己原生滚动**（Dock=Fill + 自己的滚动条，文本程序都这么干，
        ///     滚轮/拖动条/键盘天然好用、不存在"被重置"），
        ///      配图**贴进正文末尾**（RichTextBox 原生支持行内图片），滚到底才看到。
        /// </summary>
        /// <summary>
        /// 只读且**不可获焦**的 RichTextBox。
        /// 为什么要自定义：只读的 RichTextBox 依然能拿焦点，一旦拿到焦点，
        /// 它会为了让插入符可见而把父容器滚回插入符所在处（通常是顶部）
        /// ⇒ 表现就是主人说的「划到下面它自己又上去了」。关掉可选中性即可。
        /// （注：不要覆写 CanSelectCore —— 在这套框架里它不是 virtual，会报 CS0115/CS0549。）
        /// </summary>
        private sealed class NoFocusRichTextBox : RichTextBox
        {
            public NoFocusRichTextBox()
            {
                SetStyle(ControlStyles.Selectable, false);
                TabStop = false;
                ShortcutsEnabled = false;
            }
        }

        /// <summary>
        /// 布局/启动黑匣子：出问题时能拿它当证据（限 200KB，别在用户盘上长大）。
        /// ★ 2026-09-18 修：原来是"超过 200KB 就 **直接 return**"—— 于是它**静默停止记录**，
        ///   而且没人会知道（本机实测文件停在 204,824 字节、之后所有埋点全丢，害我白测一轮）。
        ///   改成**轮转**：写满就把当前文件改名成 .1（只留一代），新开一个继续记 —— 永不静默停摆。
        /// </summary>
        public static void SLog(string msg)
        {
            try
            {
                System.IO.Directory.CreateDirectory(DshCore.AppDataDir);
                string path = System.IO.Path.Combine(DshCore.AppDataDir, "manual-scroll.log");
                var fi = new System.IO.FileInfo(path);
                if (fi.Exists && fi.Length > 200 * 1024)
                {
                    string bak = path + ".1";
                    try { if (System.IO.File.Exists(bak)) System.IO.File.Delete(bak); } catch { }
                    try { System.IO.File.Move(path, bak); } catch { try { System.IO.File.WriteAllText(path, ""); } catch { } }
                }
                System.IO.File.AppendAllText(path,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n",
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }
        public static TabPage Build()
        {
            var page = new TabPage(Title);
            page.BackColor = Color.FromArgb(255, 252, 246);
            page.Padding = new Padding(14, 10, 14, 10);

            var rtb = new NoFocusRichTextBox();
            rtb.Dock = DockStyle.Fill;
            rtb.ReadOnly = true;
            rtb.BorderStyle = BorderStyle.None;
            rtb.BackColor = Color.FromArgb(255, 252, 246);
            rtb.Font = new Font("Microsoft YaHei UI", 10f);
            rtb.WordWrap = true;
            rtb.ScrollBars = RichTextBoxScrollBars.Vertical;   // ★ 原生滚动：不再靠外层容器
            rtb.DetectUrls = false;

            // ★★ 2026-09-20 第二轮修「正文里的 emoji 还是 □」：
            //   上一轮我只修了「自绘页签」那一条路径（SkinTheme.DrawTabText 逐段切字体），
            //   **漏掉了这条 RichEdit 路径** —— 正文里 21 个不同 emoji、共 67 处，
            //   因为 rtb.Font = 雅黑、而雅黑里没有这些码位的字形，全画成了 □。
            //
            //   这一轮实测否掉了三条"看起来对"的路：
            //     ① `rtb.SelectionFont = new Font("Segoe UI Emoji", …)` **完全不生效** ——
            //        即使有真句柄、进过消息循环，读回仍是 Microsoft YaHei UI，
            //        RTF 字体表里只有 `\f0`（探针 _rtfprobe4.cs 实测）；
            //     ② 控件没建句柄时也一样不生效（_rtfprobe.cs）；
            //     ③ 先设 Text 再 Highlight 用 SelectionFont 覆盖，会把字体信息冲掉。
            //
            //   ⇒ **正解 = 直接拼 RTF 的字体表**（探针 _rtfprobe5.cs 实测成功）：
            //     字体表里声明两个字体，emoji 段用 `\f1`、其余用 `\f0`；
            //     读回验证 `[0..1] 的字体 = Segoe UI Emoji`、`「大」的字体 = Microsoft YaHei UI`。
            //   判据见 SelftestLine()/LayoutCheck()（两两比像素，字形必须彼此可区分）。
            string rtf = BuildBodyRtf();
            if (rtf != null) rtb.Rtf = rtf;
            else { rtb.Text = Text(); Highlight(rtb); }   // 兜底：拼不出 RTF 就退回老路（至少文字在）

            // ★★ 2026-09-20 修「说明页里的图一会儿一闪 / 剪贴板被莫名改写」：
            //   原来这里调 StickerIntoText()，它走的是「剪贴板 + RichTextBox.Paste」——
            //   也就是为了贴一张自己的图，要去**改用户的系统剪贴板**。它事后想还原：
            //     saved = Clipboard.GetDataObject();   …Clipboard.SetImage(small)…   最后还原
            //   但 GetDataObject() 拿到的是**延迟渲染代理**（数据本身没进内存），
            //   剪贴板已经被自己改过 ⇒ 还原回调触发时读到的已经是替换后的图
            //   ⇒ 剪贴板最终停在「说明书那张配图」上。
            //   后果：任何别的程序一读剪贴板（截图工具 / 别的助手进程都会读），
            //   系统就给剪贴板所有者发 WM_CLIPBOARDUPDATE，这条失效的还原路径就可能
            //   再去读那个代理 ⇒ 用户看到的就是「最后那张图一直在闪」。
            //   ⇒ 改成 StickerIntoRtf()：**自己拼 RTF 的 \pict 组，全程不碰剪贴板**，副作用为零。
            //   判据见 --manualcheck（读的是 RichEdit **回吐**的 Rtf；里面有 \pict 才说明它真收下了这张图）。
            if (StickerIntoRtf(rtb, 220)) SLog("配图已贴进正文末尾（RTF 内联，未碰剪贴板）");
            else SLog("配图**没贴进去**（那样就看不到图了）");

            // 黑匣子：原生滚轮事件 + 偏移回读（GetPositionFromCharIndex(0).Y 的负值＝已滚多少）
            rtb.MouseWheel += delegate
            {
                SLog("原生滚轮事件；当前偏移=" + (-rtb.GetPositionFromCharIndex(0).Y));
                var tm = new System.Windows.Forms.Timer();
                tm.Interval = 400; int n = 0;
                tm.Tick += delegate
                {
                    n++;
                    SLog("   [原生滚后] +" + (n * 400) + "ms 偏移=" + (-rtb.GetPositionFromCharIndex(0).Y));
                    if (n >= 3) { tm.Stop(); tm.Dispose(); }
                };
                tm.Start();
            };

            page.Controls.Add(rtb);
            return page;
        }

        /// <summary>
        /// ⚠ **已停用（2026-09-20）——请不要再调它。**
        /// 原因：它为了贴一张自己的图去**改写用户的系统剪贴板**，而事后"还原"是失效的：
        ///   saved = Clipboard.GetDataObject() 拿到的是**延迟渲染代理**（数据当时并没进内存），
        ///   等到 Paste 完再 SetDataObject(saved) 还原时，代理回调读回来的已经是刚刚替换进去的图
        ///   ⇒ 剪贴板最终停在「说明书那张配图」上，再也还原不回去。
        ///   后果：别人一读剪贴板（截图工具、别的助手进程都会读）系统就发 WM_CLIPBOARDUPDATE，
        ///   这条失效路径去重读代理 ⇒ 用户看到的是「最后那张图一直在闪」。
        ///   现行做法是 StickerIntoRtf()：自己拼 RTF 的 \pict 组，**全程不碰剪贴板**。
        ///   保留本函数只为留档这段教训（含下面「只读的 RichTextBox 会拒绝 Paste」那条实测结论）。
        /// </summary>
        public static bool StickerIntoText(RichTextBox rtb, int targetWidthPx)
        {
            if (rtb == null) return false;
            Image sticker = LoadSticker();
            if (sticker == null) return false;
            IDataObject saved = null;
            try { saved = Clipboard.GetDataObject(); } catch { }
            bool wasReadOnly = rtb.ReadOnly;
            try
            {
                int w = Math.Max(48, targetWidthPx);
                int h = (int)Math.Round(sticker.Height * (double)w / sticker.Width);
                using (var small = new Bitmap(sticker, new Size(w, h)))
                {
                    // ★ 关键：**只读的 RichTextBox 会拒绝 Paste**（实测贴不进去、还不报错）
                    //   ⇒ 临时放开只读，贴完马上恢复。
                    rtb.ReadOnly = false;
                    Clipboard.SetImage(small);
                    rtb.SelectionStart = rtb.TextLength;
                    rtb.SelectionLength = 0;
                    rtb.Paste();
                    // ★ 主人反馈「图片不全」：图正好落在**最后一行**时，RichTextBox 滚不到底，
                    //   最后那截永远露不出来（RichEdit 的老毛病）⇒ 图后面补两行空行，
                    //   让它不再是"最后一行"，滚动就能把整张图带上来。
                    rtb.SelectionStart = rtb.TextLength;
                    rtb.SelectedText = "\n\n";
                    rtb.ReadOnly = true;
                }
                return rtb.Rtf.IndexOf("\\pict", StringComparison.Ordinal) >= 0;   // 真进去了才算成功
            }
            catch { return false; }
            finally
            {
                try { rtb.ReadOnly = wasReadOnly; } catch { }
                try { if (saved != null) Clipboard.SetDataObject(saved, true); } catch { }
            }
        }
        /// <summary>把结语配图追加到正文最末尾（内嵌进 RTF，随文字滚动）。成功返回 true。</summary>
        /// <summary>取字符串开头一小段（给黑匣子留证据用，别把整篇 RTF 灌进日志）。</summary>
        private static string Head(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(空)";
            return s.Length <= 220 ? s : s.Substring(0, 220);
        }

        public static bool StickerIntoRtf(RichTextBox rtb, int targetWidthPx)
        {
            try
            {
                Image sticker = LoadSticker();
                if (sticker == null) return false;
                string rtf0 = rtb.Rtf.TrimEnd();
                if (!rtf0.EndsWith("}", StringComparison.Ordinal)) return false;

                // ★ 2026-09-20 实测定论（本机 WinForms RichTextBox / RichEdit）：
                //   手拼的 **\pngblip 会被整组丢掉**（220px 时 pict 组 153,751 字符，读回 Rtf 里 \pict 消失）；
                //   同一张图改成 **\dibitmap0（未压缩 DIB）就收下了**（pict 组 386,877 字符，--manualcheck 判 OK、图 220x217）。
                //   ⇒ 不是"尺寸太大"的问题，是格式问题：这条 RichEdit 路径不吃手拼的 PNG blip。
                //   ⇒ 首选 dib；万一换机器/换版本 dib 也不认，再依次退 png、退更小的宽度，
                //     每一步都写黑匣子，下次再坏能一眼看出退到了哪一级。
                string baseRtf = rtf0.Substring(0, rtf0.Length - 1);
                int[] widths = new int[] { targetWidthPx, 120 };
                string[] kinds = new string[] { "dib", "png" };
                string lastDetail = "";
                foreach (int w in widths)
                {
                    foreach (string kind in kinds)
                    {
                        string pict = BuildPictRtf(sticker, w, kind);
                        if (pict.Length == 0) continue;
                        rtb.Rtf = baseRtf + "\\par\\par" + pict + "\\par\\par}";
                        string back = rtb.Rtf;   // 读 RichEdit **回吐**的 RTF：它认了，\pict 才会在
                        bool ok = back.IndexOf("\\pict", StringComparison.Ordinal) >= 0;
                        lastDetail = kind + " @" + w;
                        SLog("RTF 内联贴图：试 " + kind + " @" + w + " → "
                             + (ok ? "成功（RichEdit 收下了）" : "被丢掉")
                             + "；pict 组 " + pict.Length + " 字符");
                        if (ok) return true;
                    }
                }
                SLog("RTF 内联贴图**全部失败**（最后试到 " + lastDetail + "）"
                   + "⇒ 不再退回改动剪贴板的老办法（那会重新踩闪烁的坑），"
                   + "改为明确不贴图、如实记这条日志，等主人反馈后再处理。");
                return false;
            }
            catch (Exception ex)
            {
                SLog("RTF 内联贴图 异常: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>把图片做成 RTF 的 \pict 组（kind = png / dib）。</summary>
        public static string BuildPictRtf(Image img, int targetWidthPx)
        {
            return BuildPictRtf(img, targetWidthPx, "png");
        }

        /// <summary>
        /// 把图片做成 RTF 的 \pict 组（先缩到目标宽度，避免 RTF 里塞几百 KB 十六进制）。
        /// ★ kind="dib" 走 \dibitmap：这是 RichEdit 从 1.0 起就认的老格式，兼容性最好。
        /// </summary>
        public static string BuildPictRtf(Image img, int targetWidthPx, string kind)
        {
            try
            {
                if (img == null || img.Width <= 0) return "";
                int w = Math.Max(24, targetWidthPx);
                int h = (int)Math.Round(img.Height * (double)w / img.Width);
                using (var small = new Bitmap(img, new Size(w, h)))
                {
                    byte[] data;
                    string flags;
                    if (kind == "dib")
                    {
                        // DIB ＝ 一个 .bmp 文件**剔掉**开头那 14 字节的 BITMAPFILEHEADER。
                        // 剩下这块（BITMAPINFOHEADER + 像素）正是 CF_DIB / RTF \dibitmap 认的布局。
                        byte[] all;
                        using (var bmpMs = new System.IO.MemoryStream())
                        {
                            small.Save(bmpMs, System.Drawing.Imaging.ImageFormat.Bmp);
                            all = bmpMs.ToArray();
                        }
                        if (all.Length <= 14) return "";
                        data = new byte[all.Length - 14];
                        Array.Copy(all, 14, data, 0, data.Length);
                        int stride = ((w * 24 + 31) / 32) * 4;   // 每行字节数，必须按 4 字节对齐
                        flags = "\\dibitmap0\\wbmbitspixel24\\wbmplanes1\\wbmwidthbytes" + stride;
                    }
                    else
                    {
                        using (var ms = new System.IO.MemoryStream())
                        {
                            small.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                            data = ms.ToArray();
                        }
                        flags = "\\pngblip";
                    }
                    // ★ 2026-09-20：给十六进制流**定期插入换行**。
                    //   RTF 老规范对单行长度敏感，一张 220px 的位图展开后有几万字符全挤在一行，
                    //   不少解析器（含部分 RichEdit 路径）会判为坏 RTF、把整个 \pict 组丢掉。
                    //   （实测：不插换行时 RichEdit 会把整组扔了 —— 这次排障里复现过。）
                    var hex = new StringBuilder(data.Length * 2 + data.Length / 40 + 16);
                    for (int i = 0; i < data.Length; i++)
                    {
                        if (i > 0 && (i % 40) == 0) hex.Append('\n');
                        hex.Append(data[i].ToString("x2"));
                    }
                    // 尺寸单位是 twip（1 px ≈ 15 twip）。
                    // ★ \picw / \pich 必须描述**这段 hex 里那张图自己的像素尺寸**（＝缩放后的 w x h）。
                    //   原来写的是**原图** img.Width x img.Height（576x567），而数据实际是 220x216 ——
                    //   自相矛盾的声明会被 RichEdit 整组丢掉。这是本次一并修掉的一个真错。
                    //   \picwgoal / \pichgoal 是**显示尺寸**，继续用缩放后的值。
                    return "{\\pict" + flags + "\\picw" + w + "\\pich" + h
                         + "\\picwgoal" + (w * 15) + "\\pichgoal" + (h * 15) + " " + hex + "}";
                }
            }
            catch { return ""; }
        }

        // ============================================================
        //  ★★ 2026-09-20 新增：把说明书正文**直接拼成 RTF**
        //
        //  为什么要拼 RTF（以及为什么不能用更"正规"的 API）：
        //    目标 = 让正文里混着的 emoji 用 Segoe UI Emoji 画、中文仍用雅黑。
        //    · GDI 的 DrawString 不做字体回退 ⇒ 自绘页签那边只能逐段 DrawString；
        //      但正文是 RichTextBox，没法逐段 DrawString。
        //    · RichTextBox 提供的 `SelectionFont = emojiFont` 这条路**实测不生效**
        //      （探针 _rtfprobe.cs / _rtfprobe4.cs：有句柄、进过消息循环，读回仍是雅黑，
        //        RTF 字体表里根本没有第二个字体）⇒ 这条路是死的，别再试。
        //    · 唯一实测可行的是**自己拼 RTF 的字体表**：声明 \f0=雅黑、\f1=Segoe UI Emoji，
        //      emoji 段前缀 "\\f1 "、其余前缀 "\\f0 "（探针 _rtfprobe5.cs 实测成功）。
        //
        //  同时把原来 Highlight() 干的活（三种特殊行的字号/颜色）也搬进 RTF：
        //    · 行首 🐋 → 14pt 粗、深蓝（\fs28 \b \cf1）
        //    · 行首 ■  → 11pt 粗、砖红（\fs22 \b \cf2）
        //    · 含「创作者」→ 11pt 粗、青绿（\fs22 \b \cf3）
        //    · 其余 → 10pt、深灰（\fs20 \cf4）
        //  ★ 之所以必须一并搬进来：如果先拼 RTF 设进去、再让 Highlight() 用 SelectionFont
        //    覆盖一遍，**emoji 的字体信息会被冲掉**（这正是上一轮"修了还是 □"的原因之一）。
        // ============================================================

        /// <summary>把正文拼成完整 RTF（含字体表 / 颜色表 / 逐段字体切换）。失败返回 null，调用方退回老路。</summary>
        private static string BuildBodyRtf()
        {
            try
            {
                string body = Text();
                if (string.IsNullOrEmpty(body)) return null;

                // 1) 字体表：\f0 = 正文字体，\f1 = emoji 字体（拿不到就只用 \f0）
                string emojiName = SkinTheme.EmojiFontRtfName();
                bool hasEmoji = !string.IsNullOrEmpty(emojiName);
                var fonttbl = new StringBuilder();
                fonttbl.Append("{\\fonttbl");
                fonttbl.Append("{\\f0\\fnil\\fcharset134 Microsoft YaHei UI;}");
                if (hasEmoji) fonttbl.Append("{\\f1\\fnil " + Esc(emojiName) + ";}");
                fonttbl.Append("}");

                // 2) 颜色表：\cf1..\cf4 与下面 Highlight 的四种颜色一一对应
                var colortbl = new StringBuilder();
                colortbl.Append("{\\colortbl;");
                colortbl.Append("\\red28\\green78\\blue120;");     // cf1 🐋 深蓝
                colortbl.Append("\\red150\\green70\\blue60;");     // cf2 ■ 砖红
                colortbl.Append("\\red40\\green120\\blue110;");    // cf3 创作者 青绿
                colortbl.Append("\\red45\\green45\\blue45;");      // cf4 正文 深灰
                colortbl.Append("}");

                // 3) 逐行拼正文；每行先决定"整行的字号/颜色前缀"，再按字符分段补 \fN
                var sb = new StringBuilder();
                sb.Append("{\\rtf1\\ansi\\ansicpg936\\deff0");
                sb.Append(fonttbl);
                sb.Append(colortbl);

                string[] lines = body.Replace("\r", "").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string L = lines[i];

                    // 行首 emoji 只用于"判断这是哪一类行"，判断完照样逐个字符走分段
                    string sizeTag = "\\fs20 ";
                    string colorTag = "\\cf4 ";
                    string boldTag = "";
                    if (L.StartsWith("🐋"))
                    {
                        sizeTag = "\\fs28 "; colorTag = "\\cf1 "; boldTag = "\\b ";
                    }
                    else if (L.StartsWith("■"))
                    {
                        sizeTag = "\\fs22 "; colorTag = "\\cf2 "; boldTag = "\\b ";
                    }
                    else if (L.Contains("创作者"))
                    {
                        sizeTag = "\\fs22 "; colorTag = "\\cf3 "; boldTag = "\\b ";
                    }
                    sb.Append(sizeTag).Append(colorTag).Append(boldTag);

                    // ★ 关键：按 emoji / 非 emoji **逐段切字体**。emoji 段补 "\\f1 "，
                    //   其余补 "\\f0 " —— 这样雅黑画不出来的码位就交给 seguiemj 了。
                    var runs = SkinTheme.SplitEmojiRuns(L);
                    for (int k = 0; k < runs.Count; k++)
                    {
                        if (hasEmoji) sb.Append(runs[k].Value ? "\\f1 " : "\\f0 ");
                        sb.Append(Esc(runs[k].Key));
                    }

                    if (i < lines.Length - 1) sb.Append("\\par\r\n");
                }

                sb.Append("}");
                return sb.ToString();
            }
            catch { return null; }
        }

        /// <summary>
        /// 从 RTF 里取出以 <paramref name="open"/> 开头的那个**成对花括号组**的完整文本。
        ///
        /// ★ 为什么不能用"找第一个 '}'"：字体表长这样
        ///     {\fonttbl{\f0\fnil ...;}{\f1\fnil ...;}}
        ///   第一个 '}' 只是 \f0 那一项的结束，**不是整组的结束** ⇒
        ///   那样截出来的片段里永远看不到 \f1，判据就会误报"字体表里没有 emoji 字体"。
        ///   （我一开始就是这么写的，于是 manualcheck 说"有"、selftest 说"没有"，自相矛盾。）
        /// 正解 = 数花括号深度，找到深度回到 0 的位置。
        /// </summary>
        private static string ExtractGroup(string rtf, string open)
        {
            if (string.IsNullOrEmpty(rtf) || string.IsNullOrEmpty(open)) return "";
            int at = rtf.IndexOf(open, StringComparison.Ordinal);
            if (at < 0) return "";
            int depth = 0;
            for (int i = at; i < rtf.Length; i++)
            {
                char c = rtf[i];
                if (c == '\\') { i++; continue; }        // 跳过被转义的字符，别把 \{ \} 当括号
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return rtf.Substring(at, i - at + 1);
                }
            }
            return rtf.Substring(at);                    // 括号不配平：把剩下的全给它（宁可多也别少）
        }

        /// <summary>
        /// RTF 转义：'\' '{' '}' 要加反斜杠；
        /// 非 ASCII 一律写成 `\u&lt;有符号 short&gt;?` —— RTF 单条 \u 只吃 16 位，
        /// 所以 **代理对要拆成两个 \u** 分别转义（emoji 全在 BMP 外，不拆就会乱码）。
        /// 结尾那个 '?' 是给不认 \u 的老解析器看的替身字符，必须留着。
        /// </summary>
        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' || c == '{' || c == '}') { sb.Append('\\').Append(c); }
                else if (c > 127) sb.Append("\\u").Append((int)(short)c).Append('?');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// ⚠ **兜底路径**：只有当 BuildBodyRtf() 拼不出 RTF 时才会走到这里。
        /// 正常路径已经由 BuildBodyRtf() 把字号/颜色/emoji 字体**一并写进 RTF** 了。
        ///
        /// 为什么不把它当主路径：它用 `SelectionFont` 逐行覆盖，
        /// 而 `SelectionFont` 对 **emoji 字体不生效**（实测：读回仍是雅黑），
        /// 于是正文里的 emoji 会全变成 □ —— 这正是主人截图里看到的那个毛病。
        /// 保留它只是为了"拼 RTF 万一手滑失败时，至少文字还在、还能读"。
        /// </summary>
        private static void Highlight(RichTextBox rtb)
        {
            try
            {
                string[] lines = rtb.Text.Replace("\r", "").Split('\n');
                int pos = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    string L = lines[i];
                    int len = L.Length;
                    rtb.Select(pos, len);
                    if (L.StartsWith("🐋"))
                    {
                        rtb.SelectionFont = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
                        rtb.SelectionColor = Color.FromArgb(28, 78, 120);
                    }
                    else if (L.StartsWith("■"))
                    {
                        rtb.SelectionFont = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
                        rtb.SelectionColor = Color.FromArgb(150, 70, 60);
                    }
                    else if (L.Contains("创作者"))
                    {
                        rtb.SelectionFont = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
                        rtb.SelectionColor = Color.FromArgb(40, 120, 110);
                    }
                    else
                    {
                        rtb.SelectionFont = new Font("Microsoft YaHei UI", 10f);
                        rtb.SelectionColor = Color.FromArgb(45, 45, 45);
                    }
                    pos += len + 1;   // +1 = 换行
                }
                rtb.Select(0, 0);
            }
            catch { }
        }

        /// <summary>
        /// 布局自检：**真的把页面搭出来**、挂到一个临时窗体上做一次布局，
        /// 再报正文框与配图的实际尺寸 —— 这样"配图有没有显示出来"就有数字可查，
        /// 不用只靠"我觉得加上了"（截图那条路不通时的替代实证手段）。
        /// </summary>
        /// <summary>
        /// 布局自检（适配"正文框原生滚动 + 图贴进正文末尾"这版）：
        /// 报出①正文框有没有原生竖向滚动条 ②图有没有真的贴进正文（Rtf 里有没有 \pict）
        /// ③图的显示尺寸 ④可滚动范围 —— 这样"图能不能滚到、会不会滚不动"都有数字可查。
        /// </summary>
        public static string LayoutCheck()
        {
            var sb = new StringBuilder();
            try
            {
                var page = Build();
                using (var f = new Form())
                {
                    f.ClientSize = new Size(900, 700);
                    var host = new TabControl();
                    host.Dock = DockStyle.Fill;
                    host.TabPages.Add(page);
                    f.Controls.Add(host);
                    f.CreateControl();
                    host.Size = new Size(880, 660);
                    host.PerformLayout();
                    Application.DoEvents();

                    RichTextBox rtb = null;
                    foreach (Control c in page.Controls) if (c is RichTextBox) rtb = (RichTextBox)c;

                    sb.AppendLine("== 说明书页布局自检 ==");
                    if (rtb == null) { sb.AppendLine("  正文框：缺！"); sb.AppendLine("  结论：FAIL"); f.Controls.Remove(page); return sb.ToString(); }

                    bool nativeScroll = rtb.ScrollBars == RichTextBoxScrollBars.Vertical;
                    bool hasPict = rtb.Rtf.IndexOf("\\pict", StringComparison.Ordinal) >= 0;
                    // 量一下"贴进去那张图"实际多大（从 Rtf 里的 \picwgoal 读，单位 twip）
                    int picW = 0, picH = 0;
                    var m = System.Text.RegularExpressions.Regex.Match(rtb.Rtf, @"\\picwgoal(\d+)\\pichgoal(\d+)");
                    if (m.Success)
                    {
                        picW = int.Parse(m.Groups[1].Value) / 15;
                        picH = int.Parse(m.Groups[2].Value) / 15;
                    }
                    sb.AppendLine("  正文框：" + rtb.Width + "x" + rtb.Height + "，正文 " + rtb.TextLength + " 字");
                    sb.AppendLine("  原生竖向滚动条：" + (nativeScroll ? "有（滚轮/拖动条都归它管，不会被人重置）" : "**没有**"));

                    // ★ 2026-09-20 新增：正文里的 emoji 到底有没有被 RichEdit 收下 emoji 字体？
                    //   读的是 RichEdit **回吐**的 Rtf（不是我们拼的那份）——
                    //   只有它真认了，\f1 才会留在字体表里。
                    //   （实测教训：手拼的 \pngblip 会被整组丢掉；所以"我拼了"不算数，"它认了"才算。）
                    string back = rtb.Rtf;
                    string emojiName = SkinTheme.EmojiFontRtfName();
                    // ★ 探针 _rtfprobe6.cs 实测：RichEdit 会**按首次出现顺序重排字体表**
                    //   （我拼 \f0=雅黑/\f1=emoji，回吐变成 \f0=emoji/\f1=雅黑）。
                    //   所以只能查"字体表里有没有这个名字"，不能查定死编号的串。
                    //   取字体表必须用**括号配平**的 ExtractGroup（找第一个 '}' 会截断，踩过）。
                    string backFtbl = ExtractGroup(back, "{\\fonttbl");
                    bool backHasEmojiFont = emojiName != null
                        && backFtbl.IndexOf(emojiName, StringComparison.Ordinal) >= 0;
                    // 抽样：正文字符里挑第一个真 emoji（非 BMP），读它在控件里的实际字体名
                    string actualEmojiFont = "(未找到 emoji)";
                    string body = rtb.Text;
                    for (int i = 0; i < body.Length; i++)
                    {
                        int cp;
                        int take;
                        if (char.IsHighSurrogate(body[i]) && i + 1 < body.Length && char.IsLowSurrogate(body[i + 1]))
                        { cp = char.ConvertToUtf32(body[i], body[i + 1]); take = 2; }
                        else { cp = body[i]; take = 1; }
                        if (cp > 0x1F000 && SkinTheme.IsEmojiCodepoint(cp))
                        {
                            rtb.Select(i, take);
                            Font sf = rtb.SelectionFont;
                            actualEmojiFont = (sf != null ? sf.Name : "(null)");
                            break;
                        }
                    }
                    bool bodyEmojiOk = backHasEmojiFont;
                    sb.AppendLine("  正文 emoji 字体（读 RichEdit 回吐的字体表）："
                        + (backHasEmojiFont ? "有 " + emojiName + "（\f1 被收下了）" : "**没有**（emoji 会画成 □）"));
                    sb.AppendLine("  抽第一个正文 emoji，它的实际字体 = " + actualEmojiFont
                        + (actualEmojiFont == emojiName ? "（对上了）" : "（注意：RTF 路径下 SelectionFont 常报不准，以字体表为准）"));

                    sb.AppendLine("  结语配图有没有贴进正文：" + (hasPict
                        ? "有（" + picW + "x" + picH + "），滚到最下面可见"
                        : "**没有**（那样就看不到图）"));
                    bool tailOk = rtb.Text.Length > 0 && rtb.Text.TrimEnd().Length <= rtb.Text.Length - 2;   // 图后有空行
                    sb.AppendLine("  图后面留了空行（防最后一行滚不到）：" + (tailOk ? "是" : "**没有**（图会被切）"));
                    bool okAll = nativeScroll && hasPict && picW >= 100 && tailOk && bodyEmojiOk;
                    sb.AppendLine("  结论：" + (okAll ? "OK" : "FAIL"));
                    f.Controls.Remove(page);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("  结论：FAIL " + ex.GetType().Name + ": " + ex.Message);
            }
            return sb.ToString();
        }
        public static string SelftestLine()
        {
            string t;
            try { t = Text(); }
            catch (Exception ex) { return "说明书可生成: FAIL " + ex.Message; }
            bool ok = t.Contains("我能修什么")
                   && t.Contains("我修不了什么")
                   && t.Contains("创作者：甜馨")
                   && t.Contains("白屏")
                   && t.Contains("中文");

            // ★ 2026-09-20 新增：正文里的 emoji 真的能画出来吗？
            //   判据 = ①正文里到底出现了哪些 emoji 码点（**去重**——去重很关键，见下）
            //          ②它们在这一版**实际用的字体**下画出来必须**彼此形状不同**。
            //   为什么必须"彼此不同"而不是"画得出来"：缺字形时所有码点都退化成同一个
            //   .notdef 方框，"画得出来"照样成立 ⇒ 那是假阳性（详见 SkinTheme 里那段实测）。
            //   ★ 踩过的坑：这里一开始**忘了去重**，于是同一个码点出现 N 次就进数组 N 次，
            //     而"自己和自己比"当然完全一样 ⇒ 判据恒报 FAIL（明明已经修好了）。
            //     去重之后 17 种，两两比全部不同。
            //   负对照：把同一批码点交给正文字体（雅黑）判一次 —— 必须**不通过**
            //   （实测雅黑下这些码点画成同一个方框），否则说明这判据根本不区分字体。
            var cps = new System.Collections.Generic.List<int>();
            for (int i = 0; i < t.Length; i++)
            {
                int cp;
                if (char.IsHighSurrogate(t[i]) && i + 1 < t.Length && char.IsLowSurrogate(t[i + 1]))
                { cp = char.ConvertToUtf32(t[i], t[i + 1]); i++; }
                else cp = t[i];
                // 只算真 emoji 区（>0x1F000）；■→★⇒①②③ 这些是 CJK 字体本来就有的几何符号，
                // 不该混进来（混进来会让"彼此不同"这条判据变松、失去意义）。
                if (SkinTheme.IsEmojiCodepoint(cp) && cp > 0x1F000 && !cps.Contains(cp)) cps.Add(cp);
            }
            int[] distinct = cps.ToArray();
            bool emojiOk = SkinTheme.EmojiGlyphsAllDistinct(distinct);

            // ②这份 RTF 里到底有没有真的带上第二个字体、有没有把 emoji 段切到 \f1
            //   ★ 注意（探针 _rtfprobe6.cs 实测）：**RichEdit 会按"首次出现顺序"重排字体表**——
            //     我拼的是 \f0=雅黑/\f1=emoji，回吐出来会变成 \f0=emoji/\f1=雅黑。
            //     所以判据**不能**去比对 "\f1\fnil Segoe UI Emoji" 这种定死了编号的串
            //     （我一开始就是这么判的，于是明明修好了却报 FAIL）。
            //     正确判据 = ①字体表里出现了 emoji 字体名（序号随它重排，别管）
            //               ②body 里确实出现过 \fN 的切换（N≥1），说明真按段切了字体。
            string rtf = BuildBodyRtf();
            string emojiName = SkinTheme.EmojiFontRtfName();
            string ftblText = ExtractGroup(rtf, "{\\fonttbl");
            bool rtfHasFont = emojiName != null && ftblText.IndexOf("\\fnil " + emojiName, StringComparison.Ordinal) >= 0;
            int switchToF1 = 0;
            if (rtf != null)
            {
                int at = 0;
                while ((at = rtf.IndexOf("\\f1 ", at, StringComparison.Ordinal)) >= 0) { switchToF1++; at += 4; }
            }
            bool rtfOk = rtfHasFont && switchToF1 >= distinct.Length;   // 每个 emoji 段至少一次

            // ③★ 负对照：把同一批码点交给**正文字体（雅黑）**再判一次 —— 必须**不通过**。
            //   为什么必须做这一条：一个"永远返回 true"的判据也能让自检全绿，
            //   那种判据是假绿。只有证明它换字体会变红，才说明它真的在区分字体。
            bool negOk = !SkinTheme.GlyphsAllDistinctInFontNamed("Microsoft YaHei UI", 10f, distinct);

            ok = ok && emojiOk && rtfOk && negOk;

            // ★ 判据行必须以 OK/FAIL 收尾（上层是按"结尾是不是 OK"判绿红的，
            //   把明细写在前面，别让括号把结论挤没了 —— 这里踩过一次）。
            Image st = LoadSticker();
            bool stickerOk = st != null;
            return "说明书含小节/创作者/结语配图（正文" + (ok ? "齐" : "缺项")
                 + "、配图资源" + (st != null ? st.Width + "x" + st.Height : "缺")
                 + "）: " + ((ok && stickerOk) ? "OK" : "FAIL")
                 + "；正文 emoji 共 " + distinct.Length + " 种，字形彼此可区分："
                 + (emojiOk ? "是（" + (emojiName ?? "?") + "，两两形状互不相同）" : "否 ⇒ 疑似豆腐块")
                 + "；负对照（同一批码点交给雅黑）："
                 + (negOk ? "如期**不通过**（判据确实在区分字体）" : "**竟然通过了** ⇒ 判据没用，是假绿")
                 + "；RTF 字体表带 " + (emojiName ?? "?") + "：" + (rtfHasFont ? "有" : "**没有**")
                 + "，emoji 段切 \\f1 共 " + switchToF1 + " 处（应 ≥ " + distinct.Length + "）"
                 + " ⇒ " + ((emojiOk && rtfOk && negOk) ? "OK" : "FAIL");
        }

        /// <summary>尾巴段里还有没有中文——用来判"图是不是已经到文档最末尾了"。</summary>
        private static bool HasCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (c >= 0x2E80 && c <= 0x9FFF) return true;
            return false;
        }
    }
}

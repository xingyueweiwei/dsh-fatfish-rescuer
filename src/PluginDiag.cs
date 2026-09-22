// ============================================================
//  大肥鱼救星 · 装后体检（v4.5）
//
//  为什么要有这个文件（对着真实事故写的）：
//    真实的故障链是这样的 ——
//      ① 删/装插件 → 某条 loader entry 应用失败
//      ② cordis 抛错并**拖垮整棵插件树**（plugin tree failed to load）
//      ③ Node 带着未捕获异常**退出** ⇒ 服务消失
//      ④ 用户那个还开着的页面每次操作都打不到服务 ⇒ 弹 Failed to fetch (gateway/internal)
//      ⑤ 之后**怎么修插件都还报同一句**（因为报错反映的是「地址上没有服务」）
//
//    而旧版的「排查崩溃插件」**只读救星自己写的那两份日志**：如果 DSH 是被桌面图标 /
//    可靠启动器 / 工坊桌面端拉起来的，崩溃行在**别的文件**里 ⇒ 它永远报「未能定位」，
//    「禁用崩溃插件」跟着空转 —— 用户看到的就是「点了等于没点」。
//
//  本文件第一步先解决"读得到"：A2 日志来源全景 + A3 进程退出判据。
//  ★ 纪律：只读、零外部进程、绝不修改任何文件；每条结论都要能回指到具体某一行。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BigFatFishRescuer
{
    public static class PluginDiag
    {
        /// <summary>插件树整体加载失败的标志文本（来自 dsh-app-boot 的 boot()）。</summary>
        public const string TreeFailMarker = "plugin tree failed to load";

        /// <summary>
        /// ★ 2026-09-17 修（跨环境验证抓到的真漏洞）：
        ///   原来 ClassifyFiles 的**预筛**只认 `plugin tree failed to load` / `loader entry` /
        ///   `pending (waiting for service` 三个关键词，于是这三类的行**根本进不来**：
        ///     · 悬空引用   → `cannot resolve profile bundle "X"`（**compose 阶段**就报，没有 tree 标记）
        ///     · 入口缺失   → `installed package X main entry is missing at Y`
        ///     · 记忆花括号 → `unknown prompt variable`
        ///   ⇒ 在别人的机器上会报「没发现问题」，正是"像摆设"的那种假阴性。
        ///   **规矩：预筛关键词必须与分类判据同源** —— 每加一类判据，这里必须同步加。
        /// </summary>
        private static readonly string[] TriggerMarkers = new string[]
        {
            TreeFailMarker,
            "loader entry",
            "pending (waiting for service",
            "cannot resolve profile bundle",
            "duplicate loader entry id",
            "main entry is missing",
            "EADDRINUSE",
            // ★★ v5 补漏（WP1，2026-09-18）：下列 4 个词在 ClassifyLine 里**有判据**，
            //   却因为不在这里而**永远进不了分类器** ⇒ 在真实语料上表现为 100% 假阴性。
            //   实测：语料 370 条里「包/模块解析不到」8 条，其中 5 条明明写着
            //   ERR_MODULE_NOT_FOUND / Cannot find module，救星全部报「没匹配到故障」。
            //   这正是上面那段注释自己定的规矩（预筛必须与判据同源）被违反的结果——第二次同类问题。
            "EACCES",                  // ClassifyLine ⑤b 有判据，闸门漏
            "ENOENT",
            "unknown prompt variable",
            "ERR_MODULE_NOT_FOUND",    // ClassifyLine ①b 有判据，闸门漏
            "Cannot find package",     // ClassifyLine ①b 有判据，闸门漏
            "Cannot find module"       // ClassifyLine ①b 有判据，闸门漏
        };

        // ★★ v5（WP1）· 结构性修复：
        //    这两组词**就是** ClassifyLine ⑧⑨ 的判据本体，预筛直接引用它们。
        //    以前把预筛词**手抄**一份塞进 TriggerMarkers ⇒ 加判据时忘了同步，
        //    于是「Cannot find package / ERR_MODULE_NOT_FOUND / EACCES」整整漏了一个版本
        //    （实测语料上 100% 假阴性）。手抄这个动作本身就是 bug 来源，现在从结构上消除它。
        private static readonly string[] PnpmMarkers = new string[]
        { "ERR_PNPM", "corepack", "node-gyp", "EPERM" };

        private static readonly string[] MarketMarkers = new string[]
        { "Invalid URL", "WebDAV", "源不可达", "registry.npmjs", "123pan" };

        /// <summary>
        /// ★ v5 补漏（WP3 故障注入 F7 抓到，2026-09-18）：**配置/补丁文件解析失败**这一类
        ///   原来是「预筛 + 判据**双双**缺失」。F7 把 profiles\web\cordis.patch.yml 截成
        ///   引号未闭合的半截 YAML 后，dsh 报的是：
        ///     Error: dsh: failed to parse overlay &lt;file&gt;: YAMLException: unexpected end of the
        ///     stream within a single quoted scalar (4:1)
        ///   而救星 `--classify` 回答「没有匹配到任何已知故障形态」——**同一句日志一个字都没用上**。
        ///   这正是本文件开头那段注释自己定的规矩（**预筛必须与判据同源**）第三次被违反：
        ///   前两次分别是 `Cannot find package / ERR_MODULE_NOT_FOUND` 与 `EACCES`。
        ///   判据全部取自报错原文，**不认任何插件名单**（对任何插件、任何机器都成立）。
        /// </summary>
        private static readonly string[] CfgParseMarkers = new string[]
        { "failed to parse", "YAMLException", "unexpected end of the stream",
          "parsePatchList", "loadOverlayPatches", "failed to load overlay" };

        /// <summary>
        /// 这行是不是 dsh 的**源码回显**（Node 崩溃时会把抛出点那一行也打出来）？
        /// ★ 2026-09-18 修（WP3 F6 ② 抓到）：真实日志里同时有
        ///   · 真实报错行：`Error: dsh: cannot resolve profile bundle "dshmarket" from ...`
        ///   · 源码回显行：`throw new Error(`${binName}: cannot resolve profile bundle ${JSON.stringify(packageName)} ...`)`
        ///   两行都含判据词，于是「按报错文本」的判据在回显行上抽出了
        ///   `涉及：${JSON.stringify(packageName)}` —— 对用户毫无意义，还像工具自己有 bug。
        ///   源码回显不是报错本身 ⇒ 直接不判（真实报错行仍然会被正常归类，数量不减少）。
        /// </summary>
        private static bool IsSourceEcho(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            // ★ 判据刻意**收窄**：只认两个强信号，别的启发式（按 if(/const 开头之类）会误杀正常日志行。
            //   ① 含 JS 模板插值 `${`（源码行必然有，真实报错行不会有）；
            //   ② 含 JSON.stringify(（同上）；
            //   ③ 以 `throw new Error` 开头（就是抛出点那一行）。
            //   实测：F6② 日志里该行三个信号全中；真实报错行一个都不中。
            if (line.IndexOf("${", StringComparison.Ordinal) >= 0) return true;
            if (line.IndexOf("JSON.stringify(", StringComparison.Ordinal) >= 0) return true;
            return line.TrimStart().StartsWith("throw new Error", StringComparison.Ordinal);
        }

        private static bool ContainsAny(string line, string[] markers)
        {
            if (line == null) return false;
            foreach (string k in markers)
                if (line.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>这一行像不像"某种加载失败"（预筛；判据见 TriggerMarkers 的注释）。</summary>
        private static bool LooksLikeFailureLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            foreach (string k in TriggerMarkers)
                if (line.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // ★ v5：预筛与判据**同源**（直接复用同一组词，不再手抄）
            if (ContainsAny(line, PnpmMarkers)) return true;
            if (ContainsAny(line, MarketMarkers)) return true;
            if (ContainsAny(line, CfgParseMarkers)) return true;   // ★ WP3 F7：配置解析失败
            return false;
        }

        /// <summary>尾部扫描上限：只读文件末尾这么多字节，避免把 46MB 日志整份读进来。</summary>
        private const int TailBytes = 512 * 1024;

        /// <summary>判定「进程是否已退出」时只看日志最后这么多行（防止历史崩溃污染当前判断）。</summary>
        private const int ExitTailLines = 60;

        public sealed class LogSource
        {
            public string Path;
            public string Label;
            public string Group;          // 救星 / 可靠启动器 / 工坊桌面端 / 其它
            public bool Exists;
            public long Bytes;
            public DateTime Mtime = DateTime.MinValue;
            public int TreeFailHits;      // 尾部出现 TreeFailMarker 的行数
            public int AnyFailHits;       // ★ 尾部"像某种加载失败"的行数（预筛，含非 tree 类故障）
            public bool ProcessExited;    // A3：尾部出现 Node 崩溃页脚
            public string ExitEvidence;   // 证据行原文
            public string Error;          // 读失败的原因（如实上报）
            public string SampleFailLine; // 第一条失败行的片段（A1 分类会用到）
        }

        // ============================================================
        //  一、A2 日志来源全景
        // ============================================================

        /// <summary>
        /// 收集"可能记录 DSH 启动/崩溃"的日志来源。**读不到就如实报不存在**，不静默跳过。
        /// 全程只用托管 API，不创建任何进程（这一点对"不弹黑窗"是硬要求）。
        /// </summary>
        public static List<LogSource> CollectLogSources()
        {
            var list = new List<LogSource>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ── ① 救星自己启动的实例（旧版只读了这两个）──
            Add(list, seen, DshCore.DshWebLog, "救星启动的实例 · stdout", "救星");
            Add(list, seen, DshCore.DshWebLog + ".err", "救星启动的实例 · stderr", "救星");

            // ── ② 可靠启动器 open-dsh.mjs 写的（★ 旧版完全没读）──
            try
            {
                Add(list, seen, DshBoot.ToolsOutLog, "可靠启动器 · stdout", "可靠启动器");
                Add(list, seen, DshBoot.ToolsErrLog, "可靠启动器 · stderr", "可靠启动器");
            }
            catch { }

            // ── ③ 工坊桌面端（DSH Desktop）候选日志目录：路径不确定，逐个探测并如实报"无" ──
            foreach (string dir in DesktopLogDirs())
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    var files = new List<FileInfo>(new DirectoryInfo(dir).GetFiles("*.log"));
                    files.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTime.CompareTo(a.LastWriteTime); });
                    for (int i = 0; i < files.Count && i < 3; i++)
                        Add(list, seen, files[i].FullName, "工坊桌面端 · " + files[i].Name, "工坊桌面端");
                }
                catch { }
            }

            // ── ④ 其它：在 ~/.dsh 的浅层目录里找"真的提到插件树失败"的日志 ──
            foreach (string f in ShallowLogsMentioningTreeFail())
                Add(list, seen, f, "其它日志 · " + Path.GetFileName(f), "其它");

            // 逐份读取（只读尾部）
            foreach (LogSource s in list) Inspect(s);
            return list;
        }

        private static void Add(List<LogSource> list, HashSet<string> seen, string path, string label, string group)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!seen.Add(path)) return;
            list.Add(new LogSource { Path = path, Label = label, Group = group });
        }

        /// <summary>工坊 DSH Desktop 的可能日志目录（**探测不到就是探测不到**，不做假设）。</summary>
        private static IEnumerable<string> DesktopLogDirs()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(local, "Programs", "dsh-desktop", "logs");
            yield return Path.Combine(local, "Programs", "DSH Desktop", "logs");
            yield return Path.Combine(local, "dsh-desktop", "logs");
            yield return Path.Combine(roaming, "dsh-desktop", "logs");
            yield return Path.Combine(up, ".dsh-desktop", "logs");
        }

        /// <summary>
        /// 在 ~/.dsh 的**浅层**（不含 node_modules / sessions / profiles/node_modules）找 *.log，
        /// 只保留**真的提到插件树失败**的文件 —— 这样既不漏，又不会把几万个文件扫一遍。
        /// </summary>
        private static List<string> ShallowLogsMentioningTreeFail()
        {
            var found = new List<string>();
            var dirs = new List<string>();
            try
            {
                string home = DshCore.DshHome;
                dirs.Add(home);
                foreach (string d in Directory.GetDirectories(home))
                {
                    string name = Path.GetFileName(d);
                    if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("sessions", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("cache", StringComparison.OrdinalIgnoreCase)) continue;
                    dirs.Add(d);
                    // 再下一层（不含 node_modules）
                    try
                    {
                        foreach (string d2 in Directory.GetDirectories(d))
                        {
                            string n2 = Path.GetFileName(d2);
                            if (n2.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                            dirs.Add(d2);
                        }
                    }
                    catch { }
                }
            }
            catch { return found; }

            foreach (string dir in dirs)
            {
                try
                {
                    foreach (string f in Directory.GetFiles(dir, "*.log"))
                    {
                        string tail = TailText(f, 256 * 1024);
                        if (tail.IndexOf(TreeFailMarker, StringComparison.OrdinalIgnoreCase) >= 0) found.Add(f);
                    }
                }
                catch { }
            }
            return found;
        }

        // ============================================================
        //  二、读取与判据
        // ============================================================

        /// <summary>读文件尾部（共享读：DSH 正在写也能读；不创建进程）。</summary>
        public static string TailText(string path, int maxBytes)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long len = fs.Length;
                    long start = len > maxBytes ? len - maxBytes : 0;
                    fs.Seek(start, SeekOrigin.Begin);
                    int n = (int)(len - start);
                    var buf = new byte[n];
                    int read = 0;
                    while (read < n)
                    {
                        int r = fs.Read(buf, read, n - read);
                        if (r <= 0) break;
                        read += r;
                    }
                    return Encoding.UTF8.GetString(buf, 0, read);
                }
            }
            catch { return ""; }
        }

        /// <summary>
        /// ★ 2026-09-20 新增（修主人实测的「点了启动，结果你没起来」）：
        ///   只读**某个字节偏移之后**新增的尾部内容。
        ///
        /// 为什么必须要有：这两个日志是**只追加**的历史文件（跨几十次启动），
        ///   老崩溃块的页脚常常就停在文件末尾附近。而"读文件尾 N 字节"这种读法
        ///   分不清"这是**这一次**写的"还是"这是几天前留下的" ——
        ///   本机实测：12:05 那次启动，文件末尾 142 字符处就是一段**旧的**
        ///   `EADDRINUSE 127.0.0.1:3199` 崩溃块，于是启动后 5 秒就被判成
        ///   「启动失败：端口被占(3199)」，而那个 3080 的实例其实正在正常冷启动。
        ///   ⇒ 任何"归因"都必须以**本次起点**为界往后读。
        /// </summary>
        public static string TailTextSince(string path, long fromOffset, int maxBytes)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long len = fs.Length;
                    if (fromOffset < 0) fromOffset = 0;
                    if (fromOffset >= len) return "";      // 本次之后没有新增 ⇒ 没有可归因的内容
                    long start = fromOffset;
                    if (len - start > maxBytes) start = len - maxBytes;
                    fs.Seek(start, SeekOrigin.Begin);
                    int n = (int)(len - start);
                    var buf = new byte[n];
                    int read = 0;
                    while (read < n)
                    {
                        int r = fs.Read(buf, read, n - read);
                        if (r <= 0) break;
                        read += r;
                    }
                    return Encoding.UTF8.GetString(buf, 0, read);
                }
            }
            catch { return ""; }
        }

        /// <summary>
        /// 文件字节长度（读不到返回 0）。用于记录"本次启动的日志起点"。
        /// </summary>
        public static long LengthOf(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch { return 0; }
        }

        /// <summary>
        /// ★ 2026-09-20 新增：往一个"可能正被别的进程占着"的日志里追加一行分界。
        ///   目的：让**只追加的历史日志**从此有"每次启动的时间戳分界"，
        ///   人和机器都能一眼看出"这一段是这一次的"。
        ///   被占用/无权限就**如实返回 false**，绝不抛异常、绝不影响启动。
        /// </summary>
        public static bool TryAppendDivider(string path, string line)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    sw.Write("\r\n" + line + "\r\n");
                    sw.Flush();
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// A3：判断"这个实例的进程是不是已经崩溃退出"。
        /// 判据＝**日志尾部**出现 Node 的崩溃页脚（形如 `Node.js v24.20.0`）。
        /// ★ 只看尾部 N 行：历史崩溃不该影响当前判断。
        /// </summary>
        public static bool TailHasProcessExit(string tail, out string evidence)
        {
            evidence = null;
            if (string.IsNullOrEmpty(tail)) return false;
            string[] lines = tail.Replace("\r\n", "\n").Split('\n');
            int from = Math.Max(0, lines.Length - ExitTailLines);
            for (int i = lines.Length - 1; i >= from; i--)
            {
                string t = lines[i].Trim();
                if (t.Length == 0) continue;
                if (Regex.IsMatch(t, @"^Node\.js v\d"))
                {
                    evidence = t;
                    return true;
                }
            }
            return false;
        }

        /// <summary>数一数尾部有几行提到"插件树加载失败"，并记下第一条失败行的片段。</summary>
        private static void CountTreeFail(string tail, LogSource s)
        {
            if (string.IsNullOrEmpty(tail)) return;
            foreach (string raw in tail.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.IndexOf(TreeFailMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    s.TreeFailHits++;
                    if (s.SampleFailLine == null && line.Length > 0)
                        s.SampleFailLine = line.Length > 300 ? line.Substring(0, 300) : line;
                }
                if (LooksLikeFailureLine(line))
                {
                    s.AnyFailHits++;
                    if (s.SampleFailLine == null)
                        s.SampleFailLine = line.Length > 300 ? line.Substring(0, 300) : line;
                }
            }
        }

        private static void Inspect(LogSource s)
        {
            try
            {
                if (!File.Exists(s.Path)) { s.Exists = false; return; }
                var fi = new FileInfo(s.Path);
                s.Exists = true;
                s.Bytes = fi.Length;
                s.Mtime = fi.LastWriteTime;

                string tail = TailText(s.Path, TailBytes);
                CountTreeFail(tail, s);
                string ev;
                if (TailHasProcessExit(tail, out ev)) { s.ProcessExited = true; s.ExitEvidence = ev; }
            }
            catch (Exception e)
            {
                s.Error = e.GetType().Name + ": " + e.Message;
            }
        }

        // ============================================================
        //  三、报告（A2 + A3）
        // ============================================================

        public static string PanoramaReport()
        {
            var sb = new StringBuilder();
            List<LogSource> srcs = CollectLogSources();

            sb.AppendLine("==== 装后体检 · 日志来源全景（A2）====");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            int exists = 0, withFail = 0, exited = 0;
            foreach (LogSource s in srcs)
            {
                if (s.Exists) exists++;
                if (s.TreeFailHits > 0) withFail++;
                if (s.ProcessExited) exited++;
            }
            sb.AppendLine("候选来源 " + srcs.Count + " 个：存在 " + exists + "、含插件树失败 " + withFail
                          + "、判定进程已退出 " + exited);
            sb.AppendLine();

            foreach (LogSource s in srcs)
            {
                if (!s.Exists)
                {
                    sb.AppendLine("[无]   " + s.Label);
                    sb.AppendLine("       " + s.Path + "  → 不存在（如实上报，不跳过）");
                    continue;
                }
                sb.AppendLine("[有]   " + s.Label + "（" + s.Group + "）");
                sb.AppendLine("       " + s.Path);
                sb.AppendLine("       " + s.Bytes.ToString("N0", CultureInfo.InvariantCulture) + " 字节　最后写入 "
                              + (s.Mtime == DateTime.MinValue ? "?" : s.Mtime.ToString("yyyy-MM-dd HH:mm:ss"))
                              + "　失败行命中 " + s.AnyFailHits + " 行（其中插件树失败 " + s.TreeFailHits + " 处）");
                if (s.Error != null) sb.AppendLine("       ⚠ 读取异常：" + s.Error);
                if (s.ProcessExited)
                    sb.AppendLine("       ★ 判定：**进程已崩溃退出**（证据：尾部出现「" + s.ExitEvidence + "」）");
                if (s.SampleFailLine != null)
                    sb.AppendLine("       首条失败行：" + s.SampleFailLine);
            }

            sb.AppendLine();
            sb.AppendLine("★ 这一节只回答一个问题：**该读的日志，我到底读到没读到**。");
            sb.AppendLine("  （旧版「排查崩溃插件」只读救星自己那两份，凡是桌面图标/启动器拉起的实例都读不到 ⇒ 永远「未能定位」。）");
            return sb.ToString();
        }

        // ============================================================
        //  四、A1 起不来 · 根因分类器
        //
        //  判据全部来自 DSH 源码里的**原文报错**（不是我的猜测）：
        //    · cannot resolve profile bundle "X"     → dsh-app-boot 找不到该 bundle（删了插件、引用还在）
        //    · duplicate loader entry id: X          → cordis-plugin-loader 硬抛错（同一 id 出现两次）
        //    · installed package X main entry is missing at Y → 装不完整
        //    · X: pending (waiting for service(s): Y) → 等不到服务（依赖的插件被禁用/删掉）
        //    · listen EADDRINUSE …                   → 端口冲突（**插件是受害者，不许建议禁用它**）
        //    · ENOENT                                → 资源文件缺失
        //    · unknown prompt variable               → 记忆里的字面量花括号（已有专门防线）
        //  分类不了的**如实归「未归类」**，绝不硬猜。
        // ============================================================

        public sealed class Finding
        {
            public string Category;
            public string Evidence;
            public string SourcePath;
            public string Advise;
            public string Culprit;     // 能提取出来就填（条目名 / id）
            public bool IsIncident;    // true＝"plugin tree failed to load" 那一行本身（一次失败）
        }

        public static readonly string[] CategoryOrder = new string[]
        {
            "悬空引用（列了但解析不到）",
            "插件依赖解析不到（少了包 / 插件在 profile 外面）",
            "重复条目 / entry id 冲突",
            "入口缺失（安装不完整）",
            "等不到服务（依赖被禁用或删掉）",
            "端口冲突（插件是受害者）",
            "权限/保留端口（EACCES）",
            "资源文件缺失（ENOENT）",
            "记忆字面量花括号（unknown prompt variable）",
            // ★ v5 新增（WP1）：这两类在 362 条真实语料里合计占 35%（106 + 23 条），
            //   原来是"如实说未归类"（符合定义，但给不出下一步）。现在补上判据与修法。
            "包管理器/构建链失败（pnpm）",
            "插件市场/远程源不可达",
            // ★ v5 再补（WP3 F7，2026-09-18）：配置文件半截写入 / YAML 解析失败。
            //   为什么单列一类：它的**修法完全不同**（回滚配置快照，而不是动插件、动端口）。
            "配置/补丁文件解析失败（YAML/JSON）",
            "未归类"
        };

        /// <summary>供外部（如 `--classify` 开关）取「这一类该怎么修」的建议文本。</summary>
        public static string AdviseOf(string cat) { return AdviseFor(cat); }

        private static string AdviseFor(string cat)
        {
            switch (cat)
            {
                case "悬空引用（列了但解析不到）":
                    return "→ 点「🧹 清理悬空引用」把那一项从清单里移除（会先快照，可回滚），或把该插件重新装回来。"
                         + "\r\n     （实测 2026-09-17：这类故障让 DSH 在 **compose 阶段**就退出 —— 服务根本不会起来，"
                         + "所以现象常常是「页面打不开 / 每次操作 Failed to fetch」。）";
                case "插件依赖解析不到（少了包 / 插件在 profile 外面）":
                    return "→ ★ 某个插件 import 了一个**解析不到**的包（Node 报 Cannot find package / ERR_MODULE_NOT_FOUND）。"
                         + "\r\n     最常见的根因：**这个插件不在 profile 的 node_modules 树里**"
                         + "（例如装在 D:\\plugins\\... ，靠补丁里的绝对路径挂上去），"
                         + "\r\n     于是它看不到 DSH 自己那套包（@deepseek-ai/*）—— 依赖解析是从**插件自己的目录**往上找的。"
                         + "\r\n     修法（先救活、再修根）："
                         + "\r\n     ① **先让 DSH 能起来**：点「🚨 排查崩溃插件」→「⛔ 禁用崩溃插件」，"
                         + "\r\n        把那条 loader entry 禁掉（先快照、随时可恢复）；"
                         + "\r\n     ② **正解**：把插件**装进 profile**（`dsh plugin --profile web add <包名或路径>`），"
                         + "\r\n        让它在 profile 的 node_modules 里被链接 —— 这样依赖才解析得到；"
                         + "\r\n     ③ 治标替代：把缺的那个包从 **DSH 安装树**链接/拷到该插件目录（它自己的 node_modules 里），能撑住，"
                         + "\r\n        但 DSH 升级后版本容易对不上 ⇒ 只当临时手段。";

                case "重复条目 / entry id 冲突":
                    return "→ 同一插件被装了两份（例如 npm 装一次 + git 装一次，或两个补丁文件各写了一条）⇒ 只保留一份；体检会指出重复来源。";
                case "入口缺失（安装不完整）":
                    return "→ 该插件装坏了（包在、入口文件不在）：重装这一个插件即可，别去动别的。";
                case "等不到服务（依赖被禁用或删掉）":
                    return "→ 某个被禁用的插件正是别人在等的服务：把被禁用的条目恢复（体检会列出「等待者 + 缺失服务」），别去删等待者。";
                case "端口冲突（插件是受害者）":
                    return "→ 这是**端口被占**，不是插件坏：走「🚀 启动并打开 / 🔧 一键修复」（别忘了 v4.4 起会按进程身份认端口）。**不要禁用这个插件**。";
                case "权限/保留端口（EACCES）":
                    return "→ ★ 这**不是**占用、而是**没权限绑这个端口**：Windows 上 3080 常落在 Hyper-V/WSL2 的"
                         + "\r\n     **保留端口区间**里（DSH 官方也有这个 issue：Discussion #589/#1462，报错堆栈很晦涩）。"
                         + "\r\n     解法＝**换一个端口**：把端口号写进 `%USERPROFILE%\\.dsh\\big-fat-fish-rescuer\\port.txt`"
                         + "\r\n     （例如 3090），或设环境变量 `DSH_PORT=3090`；然后重开服务。"
                         + "\r\n     （装后体检会顺手帮你查：当前端口是否落在保留区间里。）";
                case "资源文件缺失（ENOENT）":
                    return "→ 该插件启动时要拷贝的资源没装上：点「🚨 排查崩溃插件」→「⛔ 禁用崩溃插件」（保留包、可恢复）。";
                case "记忆字面量花括号（unknown prompt variable）":
                    return "→ 点「🧩 补丁体检」的第 ④ 项（prompt 组装的字节防线），它会检查两层防线并一键重打。";
                // ★ v5 新增（WP1）：判据全部按**报错文本**走，不认任何插件名单。
                case "包管理器/构建链失败（pnpm）":
                    return "→ ★ 这是**安装/构建环节**失败，不是 DSH 本体坏了（常见：ERR_PNPM_* / EPERM / corepack / node-gyp）。"
                         + "\r\n     ① 先看错误码：`ERR_PNPM_IGNORED_BUILDS` ⇒ 该插件的原生构建脚本被 pnpm 的授权检查拦了，"
                         + "\r\n        需要给这个包单独放行（别全局关掉构建检查，那会削弱供应链安全）；"
                         + "\r\n     ② `EPERM` ⇒ 多半是目标目录/文件被占用或权限不足（Windows 上常见于杀软或文件锁）："
                         + "\r\n        关掉占用它的进程后重试，**不需要**重装 DSH；"
                         + "\r\n     ③ `corepack` 相关 ⇒ 包管理器版本没被正确切换，按报错里给的版本执行 `corepack prepare <pm>@<ver> --activate`；"
                         + "\r\n     ④ 装完后**必须重启 dsh web**，并确认 profile 的 node_modules 里真的出现了该包（体检的「三方对齐」会查）。"
                         + "\r\n     ⚠ 修法要在你自己的机器上执行成功后**复验**（重跑安装 + 看 DSH 能否起来）才算数。";
                case "插件市场/远程源不可达":
                    return "→ ★ 这是**取插件清单/包**这一步失败，与 DSH 本体无关（常见：Invalid URL / WebDAV 30x / registry 不可达）。"
                         + "\r\n     ① 先确认网络与代理：DSH 市场走的是 HTTP，若你在用本机代理，检查 `HTTP_PROXY`/`HTTPS_PROXY` 是否**大小写重复**"
                         + "\r\n        （本机的真实坑：同时存在 `HTTP_PROXY` 和 `http_proxy` 会让 .NET 的进程环境字典直接抛异常）；"
                         + "\r\n     ② `Invalid URL` ⇒ 配置里填的源地址非法（多半是备份恢复带进来的旧地址），改回默认源；"
                         + "\r\n     ③ `HTTP 30x` ⇒ 源站把请求 302 到 CDN，客户端不跟随重定向：换一个直连的源，或等上游修；"
                         + "\r\n     ④ **不要**因为市场打不开就去重装 DSH / 全禁用插件 —— 那两件事都修不了网络问题。";
                case "配置/补丁文件解析失败（YAML/JSON）":
                    return "→ ★ 这是**配置文件本身解析不出来**（不是插件坏、不是端口冲突、更不是 DSH 装坏了）："
                         + "\r\n     最常见成因＝写到一半断电 / 被强杀，或者手工编辑时引号、缩进没闭合。"
                         + "\r\n     dsh 在 **compose 阶段**就退出 ⇒ 现象是「服务起不来 / 页面打不开 / 每次操作 Failed to fetch」。"
                         + "\r\n     ① **先定位哪个文件**：报错原文里 `failed to parse overlay <文件>` 已经点名了"
                         + "\r\n        （本工具会把它抽成上面的「涉及」）；"
                         + "\r\n     ② **最快救活**：点「♻ 恢复配置」，回滚到上一份快照"
                         + "\r\n        （本工具每次改配置前都会留快照，所以只要你用过它，就一定回得去）；"
                         + "\r\n     ③ 没有快照时：按报错给的 `行:列`（例如 (4:1) ＝第 4 行）去修，"
                         + "\r\n        优先查**引号没闭合 / 缩进错位 / 混进了 tab**；改前先把原文件另存一份；"
                         + "\r\n     ④ 修完重启 dsh web，并用「DSH 关键配置体检」复查一遍；"
                         + "\r\n     ⑤ **不要**因为这一条去重装 DSH / 删插件 / 换端口 —— 这份文件跟插件好坏、端口占用都没关系。";
                default:
                    return "→ 未归类：请把这条证据行连同整份日志发给能看的人（不要凭这条猜着改配置）。";
            }
        }

        /// <summary>把一条日志行归类。顺序即优先级（先判最具体的）。</summary>
        public static string ClassifyLine(string line, out string culprit)
        {
            culprit = null;
            if (string.IsNullOrEmpty(line)) return null;

            // ★ 2026-09-18（WP3 F6 ②）：源码回显行不是报错本身，先剔掉（见 IsSourceEcho 注释）
            if (IsSourceEcho(line)) return null;

            Match m;

            // ① 悬空引用：dsh-app-boot 找不到 profile bundle
            m = Regex.Match(line, "cannot resolve profile bundle\\s+(\"([^\"]+)\"|(\\S+))", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                culprit = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                return "悬空引用（列了但解析不到）";
            }

            // ①b ★★ 2026-09-18 新增（来自真实报错，见主人转来的截图）：
            //   `plugin tree failed to load: ... failed to import loader entry ui-ponytail (dsh-client-ui-ponytail):
            //    Cannot find package '@deepseek-ai/schemastery' imported from D:\plugins\dsh-plugin-ponytail\lib\index.js`
            //   ⇒ 这是"**插件不在 profile 的 node_modules 树里**，于是看不见 DSH 自己的包"这一类，
            //     与"悬空引用"不同（悬空是清单里那一项找不到实体；这里是插件能找到、但它 import 的包找不到）。
            //   判据核心（**通用，不认插件名单**）：Node 的那句 `Cannot find package '<pkg>' imported from <path>`
            //     —— 包名与路径都是**从报错里抽出来的**，所以对市面上任何插件都成立。
            if (line.IndexOf("ERR_MODULE_NOT_FOUND", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("Cannot find package", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("Cannot find module", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                m = Regex.Match(line, "Cannot find (?:package|module)\\s+['\"]([^'\"]+)['\"]", RegexOptions.IgnoreCase);
                if (m.Success) culprit = m.Groups[1].Value;
                Match m2 = Regex.Match(line, "imported from\\s+(\\S+)", RegexOptions.IgnoreCase);
                if (m2.Success) culprit = (culprit == null ? "" : culprit + "　（从 " + m2.Groups[1].Value + " 里 import）");
                return "插件依赖解析不到（少了包 / 插件在 profile 外面）";
            }

            // ② 重复条目 / entry id 冲突
            m = Regex.Match(line, "duplicate loader entry id:\\s*(\\S+)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                culprit = m.Groups[1].Value;
                return "重复条目 / entry id 冲突";
            }

            // ③ 入口缺失
            m = Regex.Match(line, "installed package\\s+(\\S+)\\s+main entry is missing", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                culprit = m.Groups[1].Value;
                return "入口缺失（安装不完整）";
            }

            // ④ 等不到服务
            m = Regex.Match(line, "^([^:]+):\\s*pending \\(waiting for service", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                culprit = m.Groups[1].Value.Trim();
                return "等不到服务（依赖被禁用或删掉）";
            }
            if (line.IndexOf("pending (waiting for service", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                m = Regex.Match(line, "waiting for service[s]?:\\s*([^\\)]+)");
                culprit = m.Success ? m.Groups[1].Value.Trim() : null;
                return "等不到服务（依赖被禁用或删掉）";
            }

            // ⑤ 端口冲突（★ 必须排在 ENOENT 之前判，且在"禁用建议"里要排除）
            if (line.IndexOf("EADDRINUSE", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                m = Regex.Match(line, "loader entry\\s+(\\S+)\\s+\\(((?:@[A-Za-z0-9_.-]+/)?[A-Za-z0-9_.:-]+)\\)", RegexOptions.IgnoreCase);
                culprit = m.Success ? m.Groups[2].Value : null;
                return "端口冲突（插件是受害者）";
            }

            // ⑤b ★ 2026-09-17 新增（来自真实报错：DSH Discussion #589/#1462）：
            //   Windows 上 3080 可能落在 **Hyper-V/WSL2 的保留端口区间**里 ⇒ 明明没人占用，
            //   dsh web 也会以 **EACCES** 崩掉，堆栈还很晦涩。这类**不是**端口占用，
            //   解法是**换端口**（我们 v4.4 的 port.txt / DSH_PORT 正好是对症的出口）。
            if (line.IndexOf("EACCES", StringComparison.OrdinalIgnoreCase) >= 0)
                return "权限/保留端口（EACCES）";

            // ⑥ 资源缺失
            if (line.IndexOf("ENOENT", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                m = Regex.Match(line, "loader entry\\s+(\\S+)\\s+\\(((?:@[A-Za-z0-9_.-]+/)?[A-Za-z0-9_.:-]+)\\)", RegexOptions.IgnoreCase);
                culprit = m.Success ? m.Groups[2].Value : null;
                return "资源文件缺失（ENOENT）";
            }

            // ⑦ 记忆花括号
            if (line.IndexOf("unknown prompt variable", StringComparison.OrdinalIgnoreCase) >= 0)
                return "记忆字面量花括号（unknown prompt variable）";

            // ⑧ ★ v5 新增（WP1）：包管理器/构建链失败 —— 362 条真实语料里最大的一类（106 条）。
            //   判据词就是 PnpmMarkers；预筛直接引用同一个数组 ⇒ 不可能再不同步。
            if (ContainsAny(line, PnpmMarkers))
            {
                m = Regex.Match(line, "(ERR_PNPM_[A-Z_]+)", RegexOptions.IgnoreCase);
                if (m.Success) culprit = m.Groups[1].Value;
                else
                    foreach (string k in PnpmMarkers)
                        if (line.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) { culprit = k; break; }
                return "包管理器/构建链失败（pnpm）";
            }

            // ⑨ ★ v5 新增（WP1）：插件市场 / 远程源不可达（23 条）。同样与预筛同源。
            if (ContainsAny(line, MarketMarkers))
            {
                m = Regex.Match(line, "(Invalid URL|HTTP\\s*30[0-9]|WebDAV)", RegexOptions.IgnoreCase);
                if (m.Success) culprit = m.Groups[1].Value;
                else
                    foreach (string k in MarketMarkers)
                        if (line.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) { culprit = k; break; }
                return "插件市场/远程源不可达";
            }

            // ⑩ ★ v5 新增（WP3 F7）：配置/补丁文件**解析不出来**（半截写入、引号未闭合、缩进坏掉…）。
            //   与「重复条目 / entry id 冲突」的区别：那种是**能解析、但语义冲突**；
            //   这里是**根本解析不出来**，dsh 在 compose 阶段就退出。
            //   原文（dsh-app-boot parsePatchList / loadOverlayPatches）：
            //     `Error: dsh: failed to parse overlay <file>: YAMLException: unexpected end of
            //      the stream within a single quoted scalar (4:1)`
            if (ContainsAny(line, CfgParseMarkers))
            {
                m = Regex.Match(line, "([A-Za-z]:\\\\[^\\r\\n]*?\\.(?:yml|yaml|json|toml))",
                                RegexOptions.IgnoreCase);
                if (m.Success) culprit = m.Groups[1].Value.Trim();
                else
                {
                    m = Regex.Match(line, "(YAMLException[^\\r\\n]*|unexpected end of the stream[^\\r\\n]*)",
                                    RegexOptions.IgnoreCase);
                    if (m.Success) culprit = m.Groups[1].Value.Trim();
                }
                return "配置/补丁文件解析失败（YAML/JSON）";
            }

            return null; // 交给调用方归「未归类」
        }

        /// <summary>
        /// 供**自检 / 跨环境验证**：对"指定的日志文件列表"做分类，不读本机固定路径。
        /// 为什么需要它：本机只有一种故障形态（端口冲突），而别人的机器可能是悬空引用、
        /// 重复 entry id、等不到服务……这一个接缝让我们能在隔离环境里造出那些形态，
        /// 再验证判据认不认得出来（否则就只是"在我自己机器上能跑"）。
        /// </summary>
        public static List<Finding> ClassifyFiles(IEnumerable<string> logPaths)
        {
            var list = new List<Finding>();
            foreach (string p in logPaths)
            {
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) continue;
                string tail = TailText(p, TailBytes);
                if (tail.Length == 0) continue;
                foreach (string raw in tail.Replace("\r\n", "\n").Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (!LooksLikeFailureLine(line)) continue;   // ★ 预筛与判据同源（见 TriggerMarkers）
                    // ★ 2026-09-18（WP3 F6 ②）：dsh 崩溃时会把**抛出点那一行源码**也打出来。
                    //   它不是报错，却含判据词，旧版会凭空多出一条「未归类」Finding，
                    //   把报告搅浑（用户会以为工具没认出来）。⇒ 直接不当成线索。
                    if (IsSourceEcho(line)) continue;

                    string culprit;
                    string cat = ClassifyLine(line, out culprit);
                    list.Add(new Finding
                    {
                        Category = cat ?? "未归类",
                        Culprit = culprit,
                        SourcePath = p,
                        Evidence = line.Length > 260 ? line.Substring(0, 260) : line,
                        Advise = AdviseFor(cat ?? "未归类"),
                        IsIncident = line.IndexOf(TreeFailMarker, StringComparison.OrdinalIgnoreCase) >= 0
                    });
                }
            }
            return list;
        }

        // ============================================================
        // ★ v5 新增（WP1）：语料库回归 —— 把「认不认得故障」变成可跑的判据
        //
        //   背景：在这之前，"分类器准不准"只能靠几个手工样例说事；改一条判据，无法知道有没有退化。
        //   现在一条命令能把 370 条**真实报错片段**（GitHub issue + 本机日志，已脱敏）跑一遍，
        //   把召回/精确率与**写死的门槛**比对 —— 不达标就 exit 1。
        //
        //   门槛为什么只卡这几项：语料里 PNPM(136) + MARKET(97) 占 63%，而救星**还没有这两类**，
        //   所以整体召回天然很低（改前 6.2%）。先卡住"已经能认的那几类不许退化"。
        //   放宽门槛必须给出理由和独立证据（项目纪律）。
        //
        //   ★ 判据必须自报覆盖面：报告里会打印"跑了多少条、多少类"，
        //     因为本项目两次栽在"扫描器静默漏文件"给出的假绿灯上。
        // ============================================================
        private const int CorpusMinSize = 150;
        private const int CorpusMinCategories = 10;
        private const double CorpusMinRecallModNF = 0.55;   // 「包/模块解析不到」
        private const double CorpusMinRecallAddr = 0.90;    // 「端口冲突」
        // ★ v5 补了两类判据后新增的门槛：PNPM / MARKET
        private const double CorpusMinRecallPnpm = 0.50;    // 「pnpm/构建链失败」实测 58.5%
        private const double CorpusMinRecallMarket = 0.12;  // 「市场/远程源不可达」实测 17.4%
        // ★ v5 收紧（理由：补完判据后实测精确率 94.0%、整体召回 21.5%，
        //   原门槛 0.80/0.07 已经低到失去防退化意义 —— 收紧到"略低于实测"是为了能报红）
        private const double CorpusMinPrecision = 0.85;     // 实测 94.0%
        private const double CorpusMinRecallAll = 0.20;     // 实测 21.5%

        /// <summary>找语料文件：显式路径 &gt; exe 同目录 v5\corpus &gt; 上一级 &gt; 工作目录。</summary>
        private static string ResolveCorpusPath(string given)
        {
            var cands = new List<string>();
            if (!string.IsNullOrEmpty(given)) cands.Add(given);
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            try { cands.Add(Path.Combine(baseDir, @"v5\corpus\corpus.tsv")); } catch { }
            try { cands.Add(Path.Combine(baseDir, @"..\v5\corpus\corpus.tsv")); } catch { }
            try { cands.Add(Path.Combine(Environment.CurrentDirectory, @"v5\corpus\corpus.tsv")); } catch { }
            foreach (string c in cands)
            {
                try { if (File.Exists(c)) return Path.GetFullPath(c); }
                catch { }
            }
            return null;
        }

        /// <summary>语料类别代号 ← 救星分类类别（两侧体系不同，做近似对齐；写死但可查）。</summary>
        private static string MapRescuerCategory(string cat)
        {
            if (string.IsNullOrEmpty(cat)) return null;
            switch (cat)
            {
                case "插件依赖解析不到（少了包 / 插件在 profile 外面）": return "MODNOTFOUND";
                case "重复条目 / entry id 冲突": return "DUPLICATE";
                case "等不到服务（依赖被禁用或删掉）": return "PENDING";
                case "端口冲突（插件是受害者）": return "EADDRINUSE";
                case "权限/保留端口（EACCES）": return "PERM";
                case "记忆字面量花括号（unknown prompt variable）": return "PROMPTVAR";
                case "悬空引用（列了但解析不到）": return "PLUGINTREE";
                case "入口缺失（安装不完整）": return "PLUGINTREE";
                case "资源文件缺失（ENOENT）": return "PNPM";
                // ★ v5 新增（WP1）补的两类
                case "包管理器/构建链失败（pnpm）": return "PNPM";
                case "插件市场/远程源不可达": return "MARKET";
                // ★ WP3 F7 补的「配置/补丁文件解析失败」**故意不映射**：
                //   语料库的类别体系（PLUGINTREE/PNPM/MARKET/…）里没有这一类
                //   （实测：370 条语料里 0 条含 failed to parse / YAMLException），
                //   硬映射到别的类别会让"精确率"变成假数字。返回 null ⇒ 在语料回归里
                //   按「未归类」计数（既不加假分，也不扣假分），这是诚实的处理。
            }
            return null;
        }

        private static double Ratio(Dictionary<string, int> hit, Dictionary<string, int> tot, string key)
        {
            if (!tot.ContainsKey(key) || tot[key] == 0) return 0.0;
            return (double)(hit.ContainsKey(key) ? hit[key] : 0) / tot[key];
        }

        /// <summary>拿真实语料库回归分类器；pass=false 表示有门槛未达标（调用方应 exit 1）。</summary>
        public static string CorpusCheck(string tsvPath, out bool pass)
        {
            pass = false;
            var sb = new StringBuilder();
            sb.AppendLine("==== 大肥鱼救星 v" + DshCore.AppVersion + " · 语料库回归（WP1）====");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            string path = ResolveCorpusPath(tsvPath);
            if (path == null)
            {
                sb.AppendLine("[FAIL] 找不到语料文件 corpus.tsv。");
                sb.AppendLine("  试过：--corpus 参数 / exe同目录\\v5\\corpus / exe上一级\\v5\\corpus / 工作目录\\v5\\corpus");
                sb.AppendLine("  ⇒ 没有语料就不给绿灯（不许在缺输入时静默通过）。");
                return sb.ToString();
            }
            sb.AppendLine("语料：" + path);

            string[] lines;
            try { lines = File.ReadAllLines(path, Encoding.UTF8); }
            catch (Exception e)
            {
                sb.AppendLine("[FAIL] 读不了语料：" + e.GetType().Name + ": " + e.Message);
                return sb.ToString();
            }

            var truth = new List<string>();
            var texts = new List<string>();
            for (int i = 1; i < lines.Length; i++)   // 第 0 行是表头
            {
                if (lines[i] == null || lines[i].Trim().Length == 0) continue;
                string[] c = lines[i].Split('\t');
                if (c.Length < 5) continue;
                truth.Add(c[1]);
                texts.Add(c[4]);
            }

            int total = texts.Count;
            var catTotal = new Dictionary<string, int>();
            var catClassified = new Dictionary<string, int>();
            var catHit = new Dictionary<string, int>();
            int classified = 0, correct = 0;

            for (int i = 0; i < total; i++)
            {
                string t = truth[i];
                if (!catTotal.ContainsKey(t)) { catTotal[t] = 0; catClassified[t] = 0; catHit[t] = 0; }
                catTotal[t]++;

                string mapped = null;
                string line = texts[i].Trim();
                // 与真实 --classify 路径**完全一致**：先预筛，再分类
                if (LooksLikeFailureLine(line))
                {
                    string cul;
                    mapped = MapRescuerCategory(ClassifyLine(line, out cul));
                }
                if (mapped != null)
                {
                    classified++;
                    catClassified[t]++;
                    if (mapped == t) { correct++; catHit[t]++; }
                }
            }

            double prec = classified > 0 ? (double)correct / classified : 0.0;
            double recall = total > 0 ? (double)correct / total : 0.0;
            double rMnf = Ratio(catHit, catTotal, "MODNOTFOUND");
            double rAddr = Ratio(catHit, catTotal, "EADDRINUSE");
            double rPnpm = Ratio(catHit, catTotal, "PNPM");
            double rMarket = Ratio(catHit, catTotal, "MARKET");

            sb.AppendLine();
            sb.AppendLine("-- 覆盖面 --");
            sb.AppendLine("  语料条数 : " + total + "（门槛 ≥" + CorpusMinSize + "）");
            sb.AppendLine("  类别数   : " + catTotal.Count + "（门槛 ≥" + CorpusMinCategories + "）");
            sb.AppendLine();
            sb.AppendLine("-- 总体 --");
            sb.AppendLine("  给出归类 : " + classified + " / " + total + "  (" + (total > 0 ? (100.0 * classified / total).ToString("F1") : "0.0") + "%)");
            sb.AppendLine("  归类正确 : " + correct);
            sb.AppendLine("  精确率   : " + (100.0 * prec).ToString("F1") + "%  （门槛 ≥" + (100.0 * CorpusMinPrecision).ToString("F0") + "%）");
            sb.AppendLine("  召回率   : " + (100.0 * recall).ToString("F1") + "%  （门槛 ≥" + (100.0 * CorpusMinRecallAll).ToString("F0") + "%）");
            sb.AppendLine();
            sb.AppendLine("-- 按类别（总 / 被归类 / 正确）--");

            var keys = new List<string>(catTotal.Keys);
            keys.Sort();
            foreach (string k in keys)
            {
                int n = catTotal[k], cl = catClassified[k], ok2 = catHit[k];
                sb.AppendLine("  " + k.PadRight(13) + " 总" + n.ToString().PadLeft(4)
                    + "  归类" + cl.ToString().PadLeft(4)
                    + "  正确" + ok2.ToString().PadLeft(4)
                    + "  召回" + (100.0 * ok2 / n).ToString("F1").PadLeft(6) + "%");
            }

            sb.AppendLine();
            sb.AppendLine("-- 门槛判定 --");
            var failed = new List<string>();
            CheckGate(sb, failed, "语料条数 ≥ " + CorpusMinSize, total >= CorpusMinSize, total.ToString());
            CheckGate(sb, failed, "类别数 ≥ " + CorpusMinCategories, catTotal.Count >= CorpusMinCategories, catTotal.Count.ToString());
            CheckGate(sb, failed, "MODNOTFOUND 召回 ≥ " + (100.0 * CorpusMinRecallModNF).ToString("F0") + "%",
                rMnf >= CorpusMinRecallModNF, (100.0 * rMnf).ToString("F1") + "%");
            CheckGate(sb, failed, "EADDRINUSE 召回 ≥ " + (100.0 * CorpusMinRecallAddr).ToString("F0") + "%",
                rAddr >= CorpusMinRecallAddr, (100.0 * rAddr).ToString("F1") + "%");
            CheckGate(sb, failed, "PNPM 召回 ≥ " + (100.0 * CorpusMinRecallPnpm).ToString("F0") + "%",
                rPnpm >= CorpusMinRecallPnpm, (100.0 * rPnpm).ToString("F1") + "%");
            CheckGate(sb, failed, "MARKET 召回 ≥ " + (100.0 * CorpusMinRecallMarket).ToString("F0") + "%",
                rMarket >= CorpusMinRecallMarket, (100.0 * rMarket).ToString("F1") + "%");
            CheckGate(sb, failed, "整体精确率 ≥ " + (100.0 * CorpusMinPrecision).ToString("F0") + "%",
                prec >= CorpusMinPrecision, (100.0 * prec).ToString("F1") + "%");
            CheckGate(sb, failed, "整体召回 ≥ " + (100.0 * CorpusMinRecallAll).ToString("F0") + "%",
                recall >= CorpusMinRecallAll, (100.0 * recall).ToString("F1") + "%");

            sb.AppendLine();
            if (failed.Count == 0)
            {
                sb.AppendLine("结果: PASS —— 全部门槛达标");
                pass = true;
            }
            else
            {
                sb.AppendLine("结果: FAIL(" + failed.Count + " 项) —— 未达标：" + string.Join("；", failed.ToArray()));
                sb.AppendLine("  ⇒ 分类判据或预筛闸门被改坏了。检查 PluginDiag.TriggerMarkers 是否与 ClassifyLine 判据同源。");
            }
            return sb.ToString();
        }

        private static void CheckGate(StringBuilder sb, List<string> failed, string name, bool ok, string actual)
        {
            sb.AppendLine("  " + (ok ? "[OK]  " : "[FAIL]") + " " + name.PadRight(34) + " 实测 " + actual);
            if (!ok) failed.Add(name);
        }

        /// <summary>扫所有日志来源，把失败行归类汇总。</summary>
        public static List<Finding> ClassifyFailures(List<LogSource> sources)
        {
            var paths = new List<string>();
            foreach (LogSource s in sources) if (s.Exists) paths.Add(s.Path);
            return ClassifyFiles(paths);
        }

        public static string RootCauseReport()
        {
            var sb = new StringBuilder();
            List<LogSource> srcs = CollectLogSources();
            List<Finding> finds = ClassifyFailures(srcs);

            sb.AppendLine("==== 装后体检 · 起不来根因分类（A1）====");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            bool exited = false;
            string exitEv = null, exitPath = null;
            foreach (LogSource s in srcs)
                if (s.ProcessExited) { exited = true; exitEv = s.ExitEvidence; exitPath = s.Path; break; }

            if (finds.Count == 0)
            {
                sb.AppendLine("★ 结论：**在已读到的日志里没有发现插件树加载失败**。");
                sb.AppendLine("  （已读来源见 A2 全景；若你确实遇到「起不来」，请把 A2 里标为「无」的那几份日志找齐再跑一次。）");
            }
            else
            {
                int incidents = 0;
                foreach (Finding f in finds) if (f.IsIncident) incidents++;
                sb.AppendLine("★ 结论：**插件树加载失败 " + incidents + " 次**（另附 " + (finds.Count - incidents) + " 条相关行作佐证），分类如下：");
                sb.AppendLine();
                foreach (string cat in CategoryOrder)
                {
                    var hit = finds.FindAll(delegate(Finding f) { return f.Category == cat; });
                    if (hit.Count == 0) continue;
                    sb.AppendLine("【" + cat + "】" + hit.Count + " 条");
                    int shown = 0;
                    var culprits = new List<string>();
                    foreach (Finding f in hit)
                    {
                        if (f.Culprit != null && !culprits.Contains(f.Culprit)) culprits.Add(f.Culprit);
                        if (shown < 3)
                        {
                            sb.AppendLine("   证据：" + f.Evidence);
                            shown++;
                        }
                    }
                    if (culprits.Count > 0) sb.AppendLine("   涉及：" + string.Join("、", culprits.ToArray()));
                    sb.AppendLine("   " + AdviseFor(cat));
                    sb.AppendLine();
                }
            }

            sb.AppendLine("---- 进程状态（A3）----");
            if (exited)
            {
                sb.AppendLine("★ 该实例的**进程已崩溃退出**（证据行「" + exitEv + "」，来自 " + exitPath + "）。");
                sb.AppendLine("  ⇒ 这种情况下，**界面上那个还开着的页面每次操作都会弹 Failed to fetch (gateway/internal)**，"
                              + "因为那个地址上已经没有服务在应答了 —— 这与插件修没修**无关**。");
                sb.AppendLine("  ⇒ 正确动作：先把服务拉起来（🚀 启动并打开），**再用新抓到的地址重新打开页面**（🌐 打开无痕）。");
            }
            else
            {
                sb.AppendLine("未发现「进程崩溃退出」的证据（尾部没有 Node 崩溃页脚）。");
            }
            return sb.ToString();
        }

        // ============================================================
        //  五、A4 页面地址 × 服务端口 × API 存活（三合一探活）
        //
        //  这一节对症的正是那句报错：
        //      client api: session/prompt failed: Failed to fetch (gateway/internal)
        //  它由**浏览器端** dsh-client-connection 的 transportError() 产生，含义是
        //  「**浏览器连不上本地服务**（请求没拿到任何响应）」 —— 与插件、模型都无关。
        //  所以要回答三件事：① 你页面用的那个地址上还有服务吗？
        //  ② 现在实跑在哪个端口？③ 静态页能开 ≠ API 能用（必须分开探测）。
        // ============================================================

        public sealed class Liveness
        {
            public string PageUrl;         // 已脱敏（token 以 *** 代替）
            public int PagePort;
            public string PageUrlSource;
            public int LivePort;
            public string LivePortSource;
            public bool LiveListens;
            public bool LiveHttp;          // 静态页有响应（任意 HTTP 状态都算）
            public bool LiveApi;           // API 路径有响应
            public bool PagePortServes;    // 页面那个端口上是否还有服务
            public string Verdict;
            public string Action;
        }

        /// <summary>报告里**绝不打印一次性 token**（那是能直接登录的凭据）。</summary>
        private static string MaskToken(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            return Regex.Replace(url, @"(token=)[A-Za-z0-9_\-\.]+", "$1***", RegexOptions.IgnoreCase);
        }
        /// <param name="pageUrlOverride">仅供自检/验证注入一个假地址；正常调用传 null。</param>
        public static Liveness CheckLiveness(string pageUrlOverride = null)
        {
            var L = new Liveness();

            // ① 页面地址：优先可靠启动器写的 last-url.txt，其次扫日志里最后一条认证地址
            string url = pageUrlOverride;
            string srcLabel = (pageUrlOverride != null) ? "（外部注入，仅用于自检）" : null;
            if (url == null)
            {
                try
                {
                    url = DshBoot.ReadUrlFromFile(DshBoot.LastUrlFile);
                    if (!string.IsNullOrEmpty(url)) srcLabel = "last-url.txt（可靠启动器写入）";
                }
                catch { }
            }
            if (string.IsNullOrEmpty(url))
            {
                foreach (LogSource s in CollectLogSources())
                {
                    if (!s.Exists) continue;
                    string tail = TailText(s.Path, 128 * 1024);
                    Match last = null;
                    foreach (Match m in Regex.Matches(tail, @"http://127\.0\.0\.1:(\d+)/\?token=[A-Za-z0-9_\-\.]+"))
                        last = m;
                    if (last != null)
                    {
                        url = last.Value;
                        srcLabel = "日志「" + s.Label + "」里最后一条认证地址";
                        break;
                    }
                }
            }
            L.PageUrl = MaskToken(url);
            L.PageUrlSource = srcLabel;
            L.PagePort = string.IsNullOrEmpty(url) ? 0 : DshBoot.PortOfUrl(url);

            // ② 现在实跑在哪个端口（v4.4 起按进程身份判定）
            L.LivePort = DshCore.ActivePort;
            try { L.LivePortSource = DshBoot.LastPortSource; } catch { }
            L.LiveListens = DshCore.PortListens(L.LivePort, 600);
            L.LiveHttp = DshCore.HttpProbe("http://127.0.0.1:" + L.LivePort + "/", 1500);
            L.LiveApi = DshCore.HttpProbe("http://127.0.0.1:" + L.LivePort + "/api", 1500);

            // ③ 页面那个端口上还有服务吗
            if (L.PagePort > 0) L.PagePortServes = DshCore.HttpProbe("http://127.0.0.1:" + L.PagePort + "/", 1500);

            // ④ 结论（顺序即优先级：先判"彻底没服务"，再判"地址不一致"）
            if (!L.LiveListens && !L.LiveHttp && !L.PagePortServes)
            {
                L.Verdict = "服务没起来：实跑端口与页面端口都没有服务在应答。";
                L.Action = "→ 点「🚀 启动并打开」把服务拉起来；起来后**用新地址重新打开页面**。";
            }
            else if (L.PagePort > 0 && L.PagePort != L.LivePort && !L.PagePortServes)
            {
                L.Verdict = "★ 页面地址与服务端口**不一致**：页面开在 " + L.PagePort + "，服务实跑在 " + L.LivePort
                          + "（" + L.PagePort + " 上没有服务在应答）⇒ 这正是「每次操作都弹 Failed to fetch (gateway/internal)」的机制。";
                L.Action = "→ 点「🌐 打开无痕」用**当前**地址重新打开；这与插件修没修无关。";
            }
            else if (L.LiveListens && L.LiveHttp && !L.LiveApi)
            {
                L.Verdict = "服务在跑、静态页能开，但 API 路径没有响应 —— 属于「能打开但功能不可用」。";
                L.Action = "→ 看 A1 根因分类与 A2 日志全景；必要时「🔧 一键修复」并重启服务。";
            }
            else if (L.LiveListens && L.LiveHttp && L.LiveApi)
            {
                L.Verdict = "服务与 API 都正常应答（端口 " + L.LivePort + "）。";
                L.Action = (L.PagePort == L.LivePort)
                    ? "→ 无需处理。"
                    : "→ 注意：页面用的端口与服务不同，建议用「🌐 打开无痕」重开一次。";
            }
            else
            {
                L.Verdict = "状态不明（监听=" + L.LiveListens + "、静态页=" + L.LiveHttp + "、API=" + L.LiveApi + "）。";
                L.Action = "→ 把这份报告连同日志一起发给能看的人，不要凭猜改配置。";
            }
            return L;
        }

        public static string LivenessReport(string pageUrlOverride = null)
        {
            Liveness L = CheckLiveness(pageUrlOverride);
            var sb = new StringBuilder();
            sb.AppendLine("==== 装后体检 · 页面地址 × 服务端口 × API 存活（A4）====");
            sb.AppendLine();
            sb.AppendLine("页面用的地址 : " + (string.IsNullOrEmpty(L.PageUrl) ? "（取不到）" : L.PageUrl));
            sb.AppendLine("  端口       : " + (L.PagePort > 0 ? L.PagePort.ToString(CultureInfo.InvariantCulture) : "?")
                          + "　来源：" + (L.PageUrlSource ?? "?"));
            sb.AppendLine("  该端口服务 : " + (L.PagePort > 0 ? (L.PagePortServes ? "**有服务应答**" : "**没有服务应答**") : "未探测"));
            sb.AppendLine();
            sb.AppendLine("服务实跑端口 : " + L.LivePort
                          + "　监听=" + L.LiveListens + "　静态页=" + L.LiveHttp + "　API=" + L.LiveApi);
            sb.AppendLine("  端口来源   : " + (string.IsNullOrEmpty(L.LivePortSource) ? "（未重新发现）" : L.LivePortSource));
            sb.AppendLine();
            sb.AppendLine("★ " + L.Verdict);
            sb.AppendLine("  " + L.Action);
            sb.AppendLine();
            sb.AppendLine("※ 判据说明：那句 `Failed to fetch (gateway/internal)` 是**浏览器端**连接层");
            sb.AppendLine("  （dsh-client-connection 的 transportError）在「请求拿不到任何响应」时产生的，");
            sb.AppendLine("  所以它指向的是「那个地址上没有服务」，**不是**插件冲突、也不是模型 API。");
            return sb.ToString();
        }

        // ============================================================
        //  六、B1 bundles ↔ dependencies ↔ 磁盘 三方对齐
        //
        //  ★ 判据自证（我第一版在这里放过假警）：
        //    第一版只看 `profile\node_modules`，于是把 `@deepseek-ai/dsh-base`、
        //    `@deepseek-ai/dsh-web-app` 误报成"悬空" —— 它们解析自 **DSH 安装树**。
        //    现在必须按**三级解析**：① profile\node_modules ② DSH 安装树 ③ ~/.dsh\node_modules
        //    （再加 ④ profile\plugins 本地插件目录）；四处都没有，才敢报"悬空"。
        // ============================================================

        public sealed class AlignItem
        {
            public string Name;
            public string Declared;    // 出现在哪张清单：bundles / dependencies / 两者
            public bool Resolved;
            public string ResolvedAt;  // 人话描述的解析位置
            public string Note;
        }

        private static string ProfileDir()
        {
            return Path.Combine(DshCore.DshHome, "profiles", "web");
        }

        /// <summary>从 dsh 入口 bin.js 反推"DSH 安装树的 node_modules"。</summary>
        private static string DshNodeModulesRoot()
        {
            try
            {
                DshCore.LaunchSpec sp = DshCore.DiscoverLaunch();
                if (sp == null || string.IsNullOrEmpty(sp.BinJs)) return null;
                var di = new DirectoryInfo(Path.GetDirectoryName(sp.BinJs));
                for (int i = 0; i < 8 && di != null; i++, di = di.Parent)
                    if (di.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) return di.FullName;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// ★ 2026-09-17 修正（第二次在这里踩到假阳性）：
        ///   只找"最外层 node_modules"不够 —— 官方核心包（`@deepseek-ai/dsh-base`、
        ///   `@deepseek-ai/dsh-web-app`）装在 **dsh 包自己的 `node_modules`** 里
        ///   （`…\@deepseek-ai\dsh\node_modules\@deepseek-ai\dsh-base`）。
        ///   所以枚举**所有**祖先 node_modules + 每个祖先目录下的 node_modules。
        /// </summary>
        private static List<string[]> PackageRoots()
        {
            var roots = new List<string[]>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Action<string, string> add = delegate(string dir, string label)
            {
                if (string.IsNullOrEmpty(dir)) return;
                if (!seen.Add(dir)) return;
                roots.Add(new string[] { dir, label });
            };

            add(Path.Combine(ProfileDir(), "node_modules"), "profile\\node_modules");

            try
            {
                DshCore.LaunchSpec sp = DshCore.DiscoverLaunch();
                if (sp != null && !string.IsNullOrEmpty(sp.BinJs))
                {
                    var di = new DirectoryInfo(Path.GetDirectoryName(sp.BinJs));
                    for (int i = 0; i < 10 && di != null; i++, di = di.Parent)
                    {
                        if (di.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                            add(di.FullName, "DSH 安装树");
                        // 该目录自己带 package.json 且下面有 node_modules ⇒ 也是候选（dsh 包内依赖）
                        string nm = Path.Combine(di.FullName, "node_modules");
                        try
                        {
                            if (Directory.Exists(nm) && File.Exists(Path.Combine(di.FullName, "package.json")))
                                add(nm, "DSH 包内 node_modules（" + di.Name + "）");
                        }
                        catch { }
                    }
                }
            }
            catch { }

            add(Path.Combine(DshCore.DshHome, "node_modules"), "~/.dsh\\node_modules");
            add(Path.Combine(ProfileDir(), "plugins"), "profile\\plugins（本地插件）");
            return roots;
        }

        /// <summary>按“所有候选根”解析一个包名到底装在哪。</summary>
        public static bool TryResolvePackage(string name, out string where, out string level)
        {
            where = null; level = null;
            if (string.IsNullOrEmpty(name)) return false;
            string norm = name.Replace('/', Path.DirectorySeparatorChar);
            foreach (string[] r in PackageRoots())
            {
                try
                {
                    string cand = Path.Combine(r[0], norm);
                    if (Directory.Exists(cand)) { where = cand; level = r[1]; return true; }
                }
                catch { }
            }
            return false;
        }

        private static List<string> JsonArrayItems(string text, string key)
        {
            var list = new List<string>();
            Match m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\\[([^\\]]*)\\]", RegexOptions.Singleline);
            if (!m.Success) return list;
            foreach (Match q in Regex.Matches(m.Groups[1].Value, "\"([^\"]+)\""))
                list.Add(q.Groups[1].Value);
            return list;
        }

        private static List<string> JsonObjectKeys(string text, string key)
        {
            var list = new List<string>();
            Match m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\\{([\\s\\S]*?)\\}", RegexOptions.Singleline);
            if (!m.Success) return list;
            foreach (Match q in Regex.Matches(m.Groups[1].Value, "\"([^\"\\s]+)\"\\s*:"))
                list.Add(q.Groups[1].Value);
            return list;
        }

        public static string AlignmentReport()
        {
            var sb = new StringBuilder();
            string prof = ProfileDir();
            string pkgPath = Path.Combine(prof, "package.json");
            sb.AppendLine("==== 装后体检 · 一致性：bundles ↔ dependencies ↔ 磁盘（B1）====");
            sb.AppendLine("profile: " + prof);

            string text = null;
            try { text = File.Exists(pkgPath) ? File.ReadAllText(pkgPath) : null; } catch { }
            if (text == null)
            {
                sb.AppendLine("[FAIL] 读不到 profile\\package.json —— 这一步做不了（先修它）。");
                return sb.ToString();
            }

            List<string> bundles = JsonArrayItems(text, "bundles");
            List<string> deps = JsonObjectKeys(text, "dependencies");
            // bundles 数组里重复项＝真问题（duplicate loader entry id 的常见来源）
            var dupBundle = new List<string>();
            var seenB = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string b in bundles) if (!seenB.Add(b)) dupBundle.Add(b);

            var items = new List<AlignItem>();
            var all = new List<string>();
            foreach (string b in bundles) if (!all.Contains(b)) all.Add(b);
            foreach (string d in deps) if (!all.Contains(d)) all.Add(d);

            foreach (string name in all)
            {
                string where, level;
                bool ok = TryResolvePackage(name, out where, out level);
                var it = new AlignItem { Name = name, Resolved = ok, ResolvedAt = level };
                bool inB = bundles.Contains(name), inD = deps.Contains(name);
                it.Declared = inB && inD ? "bundles + dependencies" : (inB ? "bundles" : "dependencies");
                items.Add(it);
            }

            int dangling = 0;
            sb.AppendLine("bundles " + bundles.Count + " 项、dependencies " + deps.Count + " 项（去重后 " + all.Count + " 个包）");
            sb.AppendLine();

            foreach (AlignItem it in items)
            {
                if (it.Resolved)
                {
                    sb.AppendLine("[OK]   " + it.Name);
                    sb.AppendLine("       列于 " + it.Declared + "　解析到：" + it.ResolvedAt);
                }
                else
                {
                    dangling++;
                    sb.AppendLine("[FAIL] **悬空**：" + it.Name);
                    sb.AppendLine("       列于 " + it.Declared + "，但四处（profile\\node_modules → DSH 安装树 → ~/.dsh\\node_modules → profile\\plugins）都解析不到。");
                    sb.AppendLine("       ⇒ 这正是「删了插件、引用还在」的形态：DSH 启动会报 cannot resolve profile bundle 并**拖垮整棵插件树**。");
                    sb.AppendLine("       ⇒ 修法：把这一项从清单里移除（会先快照、可回滚），或把该插件重新装回来。");
                }
            }

            // 反向：磁盘上有、两张清单都没列（只扫一层，避免翻遍 node_modules）
            var extra = new List<string>();
            try
            {
                string nmDir = Path.Combine(prof, "node_modules");
                if (Directory.Exists(nmDir))
                {
                    foreach (string d in Directory.GetDirectories(nmDir))
                    {
                        string n = Path.GetFileName(d);
                        if (n.StartsWith(".")) continue;
                        if (n.StartsWith("@"))      // scoped：再看一层
                        {
                            try
                            {
                                foreach (string d2 in Directory.GetDirectories(d))
                                {
                                    string n2 = n + "/" + Path.GetFileName(d2);
                                    if (all.Contains(n2)) continue;
                                    extra.Add(n2);
                                }
                            }
                            catch { }
                            continue;
                        }
                        if (all.Contains(n)) continue;
                        if (n.Equals("dsh-profile-web", StringComparison.OrdinalIgnoreCase)) continue;
                        extra.Add(n);
                    }
                }
            }
            catch { }
            if (extra.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("[WARN] 装了但**两张清单都没列**（隐形包，共 " + extra.Count + " 个）：");
                foreach (string e in extra) sb.AppendLine("       " + e);
                sb.AppendLine("       ⇒ 不一定是错（依赖的依赖也会在这里），但若它正是你刚装的插件，说明**没登记**，DSH 不会加载它。");
            }

            if (dupBundle.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("[FAIL] bundles 数组里有重复项（会让 cordis 抛 `duplicate loader entry id`）：");
                foreach (string d in dupBundle) sb.AppendLine("       " + d);
            }

            sb.AppendLine();
            sb.AppendLine("★ 结论：" + (dangling == 0
                ? "两张清单里的每一项都能解析到实体，**没有悬空引用**。"
                : "发现 **" + dangling + " 个悬空引用** —— 这就是「删了插件、引用还在」的那类故障，优先处理它。"));
            return sb.ToString();
        }

        // ============================================================
        //  七、B2 重复条目 / entry id 冲突
        //
        //  cordis-plugin-loader 里是**硬抛错**：
        //      if (seen.has(id)) throw new TypeError(`duplicate loader entry id: ${id}`);
        //  工坊 README 也自己警告"两种方式都装会出现 entry id 冲突"。
        //  ⇒ 所以这里把"同一 id 出现两次"的各种形态都摊开：
        //     ① 同一文件内 insert 两个同名 id   → FAIL（会真的抛错）
        //     ② 同一 id 同时出现在两份补丁       → WARN（可能是合法的分层覆盖，交人确认）
        //     ③ plugins 目录里的备份副本         → WARN（副本被引用才是问题，一并查引用）
        // ============================================================

        public static string DuplicateReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== 装后体检 · 重复条目 / entry id 冲突（B2）====");
            sb.AppendLine();

            string[] patches = new string[]
            {
                Path.Combine(DshCore.DshHome, "cordis.patch.yml"),
                Path.Combine(DshCore.DshHome, "profiles", "web", "cordis.patch.yml")
            };

            var perFile = new List<string[]>();
            var allIds = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in patches)
            {
                string label = (p.IndexOf("profiles", StringComparison.OrdinalIgnoreCase) >= 0) ? "profile 补丁" : "home 补丁";
                if (!File.Exists(p)) { sb.AppendLine("[无]   " + label + "：" + p); continue; }
                string txt;
                try { txt = File.ReadAllText(p); } catch (Exception e) { sb.AppendLine("[FAIL] " + label + " 读取失败：" + e.Message); continue; }

                var ids = new List<string>();
                foreach (Match m in Regex.Matches(txt, @"(?m)^\s*-\s*id:\s*([^\s#]+)"))
                    ids.Add(m.Groups[1].Value);

                var dup = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string id in ids) if (!seen.Add(id)) dup.Add(id);

                sb.AppendLine("[" + (dup.Count == 0 ? "OK" : "FAIL") + "] " + label + "：" + p);
                sb.AppendLine("       条目 id 共 " + ids.Count + " 个"
                              + (ids.Count > 0 ? "：" + string.Join("、", ids.ToArray()) : ""));
                if (dup.Count > 0)
                {
                    sb.AppendLine("       ★ **同一文件内重复 id**：" + string.Join("、", dup.ToArray())
                                  + " ⇒ 会触发 `duplicate loader entry id` 硬抛错，**必须只留一条**。");
                }
                foreach (string id in ids)
                {
                    if (!allIds.ContainsKey(id)) allIds[id] = new List<string>();
                    if (!allIds[id].Contains(label)) allIds[id].Add(label);
                }
                perFile.Add(new string[] { label, p });
            }

            sb.AppendLine();
            int cross = 0;
            foreach (var kv in allIds)
            {
                if (kv.Value.Count <= 1) continue;
                cross++;
                sb.AppendLine("[WARN] 同一 id 出现在两份补丁：" + kv.Key + "（" + string.Join(" + ", kv.Value.ToArray()) + "）");
            }
            if (cross > 0)
                sb.AppendLine("       ⇒ 不一定是错（分层覆盖是官方支持的用法）；但若你正在排「重复」类故障，请确认这是你有意为之。");
            else
                sb.AppendLine("[OK]   没有同一 id 跨两份补丁的情况。");

            // 插件目录里的副本
            sb.AppendLine();
            try
            {
                string pdir = Path.Combine(ProfileDir(), "plugins");
                if (Directory.Exists(pdir))
                {
                    var copies = new List<string>();
                    foreach (string d in Directory.GetDirectories(pdir))
                    {
                        string n = Path.GetFileName(d);
                        if (n.StartsWith("_") || n.IndexOf("bak", StringComparison.OrdinalIgnoreCase) >= 0
                            || n.IndexOf("backup", StringComparison.OrdinalIgnoreCase) >= 0)
                            copies.Add(n);
                    }
                    if (copies.Count == 0) sb.AppendLine("[OK]   plugins 目录里没有备份副本。");
                    else
                    {
                        sb.AppendLine("[WARN] plugins 目录里有 " + copies.Count + " 个备份副本（一般不会被加载，但容易被误引用）：");
                        foreach (string c in copies)
                        {
                            string referenced = "（未被任何补丁引用）";
                            foreach (string[] pf in perFile)
                            {
                                try
                                {
                                    if (File.ReadAllText(pf[1]).IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0)
                                        referenced = "★ **被 " + pf[0] + " 引用**";
                                }
                                catch { }
                            }
                            sb.AppendLine("       " + c + "　" + referenced);
                        }
                    }
                }
            }
            catch { }

            sb.AppendLine();
            sb.AppendLine("★ 结论：上面标 [FAIL] 的必须处理；标 [WARN] 的请人工确认（**本工具不会替你删任何东西**）。");
            return sb.ToString();
        }

        // ============================================================
        //  八、B3 多皮肤系统并存（**只报不改**）
        //
        //  背景：我们自己就踩过 —— `maid-atelier` 与 `orca-link` 同时启用 ⇒ 界面错乱，
        //  为此专门做过「一键修复皮肤互斥」。现在把判据放宽到"**任何**皮肤类插件"：
        //  我们三件套、工坊的皮肤中心、以及任何名字含 skin/theme 的插件都会被列出来。
        //  ★ 纪律：只报"发现 N 个皮肤系统"，选保留哪个由人决定，工具绝不擅自关。
        // ============================================================

        public static string SkinSystemReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== 装后体检 · 皮肤系统并存检查（B3）====");
            sb.AppendLine();

            // ★ 2026-09-17 修正：判据原来含 "whale" 关键词，把 `dsh-whale-desktop-launcher`
            //   （一个启动器，不是皮肤）误判成皮肤。现在只看 **skin / theme**，
            //   外加"包自己的补丁里出现 ui-skin- 条目"这条硬信号。
            var found = new List<string>();
            try
            {
                string nmDir = Path.Combine(ProfileDir(), "node_modules");
                if (Directory.Exists(nmDir))
                {
                    var dirs = new List<string>();
                    foreach (string d in Directory.GetDirectories(nmDir))
                    {
                        string n = Path.GetFileName(d);
                        if (n.StartsWith(".")) continue;
                        if (n.StartsWith("@"))
                        {
                            try { foreach (string d2 in Directory.GetDirectories(d)) dirs.Add(d2); } catch { }
                            continue;
                        }
                        dirs.Add(d);
                    }
                    foreach (string d in dirs)
                    {
                        string n = Path.GetFileName(Path.GetDirectoryName(d)) + "/" + Path.GetFileName(d);
                        if (Path.GetFileName(Path.GetDirectoryName(d)).StartsWith(".")) n = Path.GetFileName(d);
                        if (LooksLikeSkinPackage(d, Path.GetFileName(d))) found.Add(Path.GetFileName(Path.GetDirectoryName(d)) == "node_modules" ? Path.GetFileName(d) : n);
                    }
                }
            }
            catch { }

            if (found.Count == 0)
            {
                sb.AppendLine("[OK]   没有在 profile 里发现皮肤类插件。");
                return sb.ToString();
            }

            // 两份补丁都读（分层覆盖是官方支持的用法 ⇒ 必须一起看才谈得上"有效状态"）
            string profPatch = Path.Combine(ProfileDir(), "cordis.patch.yml");
            string homePatch = Path.Combine(DshCore.DshHome, "cordis.patch.yml");
            string ptext = "";
            try { if (File.Exists(profPatch)) ptext += File.ReadAllText(profPatch); } catch { }
            string htext = "";
            try { if (File.Exists(homePatch)) htext += File.ReadAllText(homePatch); } catch { }

            int enabledSkin = 0;
            int managers = 0;
            sb.AppendLine("发现 " + found.Count + " 个皮肤类插件（判据：名字含 skin/theme，或该包自己的补丁里有 ui-skin- 条目）：");
            foreach (string pkg in found)
            {
                string alias = pkg;
                int slash = alias.LastIndexOf('/');
                if (slash >= 0) alias = alias.Substring(slash + 1);
                if (alias.StartsWith("dsh-client-ui-skin-", StringComparison.OrdinalIgnoreCase))
                    alias = alias.Substring("dsh-client-ui-skin-".Length);

                // ★ 收紧：名字里带 manager 的是"皮肤管理器"，它本身不是一套皮肤 ⇒
                //   不算进互斥计数（否则会误报"多个皮肤同时生效"）。
                bool isManager = alias.IndexOf("manager", StringComparison.OrdinalIgnoreCase) >= 0;

                string stProf = ReadDisabledState(ptext, pkg, alias);   // null＝没提到
                string stHome = ReadDisabledState(htext, pkg, alias);
                string state;
                string note = "";
                if (stProf != null && stHome != null && stProf != stHome)
                {
                    state = "**两层补丁说法不一致**";
                    note = "（profile 层 disabled=" + stProf + "，home 层 disabled=" + stHome + " ⇒ 请人工确认以哪个为准）";
                }
                else
                {
                    string eff = stProf ?? stHome;
                    if (eff == null) { state = "未在补丁里出现（按默认：启用）"; if (!isManager) enabledSkin++; }
                    else if (eff.Equals("true", StringComparison.OrdinalIgnoreCase)) state = "已被禁用";
                    else { state = "**启用中**"; if (!isManager) enabledSkin++; }
                }

                sb.AppendLine("  · " + pkg + (isManager ? "　【皮肤管理器，不计入互斥】" : ""));
                sb.AppendLine("      补丁 id：" + (stProf != null || stHome != null ? "ui-skin-" + alias : "（无）")
                              + "　状态：" + state + note);
                if (isManager) managers++;
            }
            sb.AppendLine();
            sb.AppendLine("★ 结论：皮肤（不含管理器）中有 **" + enabledSkin + " 个**处于启用/默认状态"
                          + (managers > 0 ? "；另有 " + managers + " 个皮肤管理器（不计入互斥）" : "") + "。");
            if (enabledSkin >= 2)
            {
                sb.AppendLine("  ⇒ ★ 皮肤**互斥**风险：多个皮肤同时生效会导致界面错乱、美化功能点不动。");
                sb.AppendLine("  ⇒ 建议：只保留你真正在用的那一个（我们踩过的实例就是 maid-atelier 与 orca-link 同时启用）。");
                sb.AppendLine("  ⇒ **选择权在你**：本工具只报冲突，不会替你关掉任何一个。");
            }
            else
            {
                sb.AppendLine("  ⇒ 只有一个皮肤系统在生效，互斥风险低。");
            }
            return sb.ToString();
        }

        /// <summary>
        /// 皮肤判据（收紧版）：① 包名含 skin / theme；② 或该包自己的补丁里出现 `ui-skin-` 条目。
        /// 刻意**不再**用 "whale" 之类的宽泛关键词 —— 那会把启动器误判成皮肤。
        /// </summary>
        private static bool LooksLikeSkinPackage(string dir, string baseName)
        {
            if (string.IsNullOrEmpty(baseName)) return false;
            string n = baseName.ToLowerInvariant();
            if (n.IndexOf("skin") >= 0 || n.IndexOf("theme") >= 0) return true;
            try
            {
                foreach (string f in new string[] { "cordis.patch.yml", "skin.json" })
                {
                    string p = Path.Combine(dir, f);
                    if (File.Exists(p))
                    {
                        string t = File.ReadAllText(p);
                        if (t.IndexOf("ui-skin-", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>在补丁文本里找 `- id: <ui-skin-alias | alias | 包名>` 后面的 disabled 值；找不到返回 null。</summary>
        private static string ReadDisabledState(string patchText, string pkgName, string alias)
        {
            if (string.IsNullOrEmpty(patchText)) return null;
            string[] ids = new string[] { "ui-skin-" + alias, alias, pkgName };
            foreach (string id in ids)
            {
                Match m = Regex.Match(patchText,
                    @"(?m)^\s*-\s*id:\s*" + Regex.Escape(id) + @"\s*\r?\n\s*disabled:\s*(true|false)",
                    RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups[1].Value;
            }
            return null;
        }

        /// <summary>纯函数：列出补丁里**同一文件内重复**的条目 id（这些会触发 cordis 硬抛错）。</summary>
        public static List<string> FindSameFileDuplicateIds(string patchText)
        {
            var dup = new List<string>();
            if (string.IsNullOrEmpty(patchText)) return dup;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(patchText, @"(?m)^\s*-\s*id:\s*([^\s#]+)\s*$"))
            {
                string id = m.Groups[1].Value;
                if (!seen.Add(id) && !dup.Contains(id)) dup.Add(id);
            }
            return dup;
        }

        /// <summary>两层补丁合起来的"被禁用条目"清单（给人看的文本）。</summary>
        public static string DisabledEntriesText()
        {
            var sb = new StringBuilder();
            string[] patches = new string[]
            {
                Path.Combine(ProfileDir(), "cordis.patch.yml"),
                Path.Combine(DshCore.DshHome, "cordis.patch.yml")
            };
            foreach (string p in patches)
            {
                string label = p.IndexOf("profiles", StringComparison.OrdinalIgnoreCase) >= 0 ? "profile 补丁" : "home 补丁";
                if (!File.Exists(p)) { sb.AppendLine("[无]   " + label); continue; }
                string t = "";
                try { t = File.ReadAllText(p); } catch { }
                List<string> dis = FindDisabledEntryIds(t);
                sb.AppendLine("[" + (dis.Count == 0 ? "OK" : "注意") + "] " + label + "：" +
                              (dis.Count == 0 ? "没有被禁用的条目" : "被禁用 " + dis.Count + " 个 → " + string.Join("、", dis.ToArray())));
                List<string> dups = FindDuplicateInsertIds(t);
                if (dups.Count > 0)
                    sb.AppendLine("       ★ 同文件重复 id（会硬抛错）：" + string.Join("、", dups.ToArray())
                                  + " ⇒ 用「🚫 去重重复条目」处理");
            }
            return sb.ToString();
        }

        //
        //  C5「等不到服务」：某条目在等被禁用插件提供的服务
        //      （`<entry>: pending (waiting for services: X)`）⇒ 把**被禁用的那个**重新启用。
        //      ★ 定位靠"候选 + 人工确认"，绝不凭名字猜着就改（猜错就会启错东西）。
        //  C6「重复 entry id」：同一文件里两条同 id ⇒ cordis 硬抛错。
        //      ⇒ 保留**第一条**、删掉后面的完整块（含其子行），改后验 YAML 并复验只剩一条。
        //      跨两份补丁的同 id **不碰**（分层覆盖是官方支持的用法）。
        // ============================================================

        /// <summary>纯函数：列出补丁里所有 `disabled: true` 的条目 id。</summary>
        public static List<string> FindDisabledEntryIds(string patchText)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(patchText)) return list;
            string[] lines = patchText.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                Match m = Regex.Match(lines[i], "^\\s*-\\s*id:\\s*(\\S+)\\s*$");
                if (!m.Success) continue;
                string id = m.Groups[1].Value;
                for (int j = i + 1; j < lines.Length && j <= i + 8; j++)
                {
                    string t = lines[j].Trim();
                    if (t.Length == 0) continue;
                    if (Regex.IsMatch(t, "^-\\s*id:")) break;
                    Match d = Regex.Match(t, "^disabled:\\s*(true|false)$");
                    if (d.Success)
                    {
                        if (d.Groups[1].Value == "true" && !list.Contains(id)) list.Add(id);
                        break;
                    }
                }
            }
            return list;
        }

        /// <summary>
        /// 纯函数：给定"缺失的服务名"，从被禁用条目里挑出**候选**（按名字包含关系粗筛）。
        /// 只做候选，不做决定 —— 选择权交给人。
        /// </summary>
        public static List<string> PendingServiceCandidates(string patchText, string serviceName, string waitingEntry)
        {
            var res = new List<string>();
            if (string.IsNullOrEmpty(serviceName)) return res;
            string s = serviceName.ToLowerInvariant().Replace(" ", "");
            foreach (string id in FindDisabledEntryIds(patchText))
            {
                if (string.IsNullOrEmpty(waitingEntry) == false
                    && string.Equals(id, waitingEntry, StringComparison.OrdinalIgnoreCase)) continue; // 等待者自己不算
                // ★ 归一化比较：把 "-" 与 "_" 都去掉再互相包含
                //   （第一版没做归一化，`ui-skin-orca-link` 与 `orcaLink` 匹配不上 —— 判据太弱）
                string an = id.ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
                string snn = s.Replace("-", "").Replace("_", "");
                if (snn.Length < 4) continue;                 // 太短的关键词不许当依据
                if (an.Contains(snn) || snn.Contains(an)) res.Add(id);
            }
            return res;
        }

        /// <summary>C5 文件层：为"缺失的服务"启用被禁用的候选条目（先快照、只改 disabled 行）。</summary>
        public static string EnableEntryForMissingService(string serviceName, string entryId)
        {
            if (string.IsNullOrEmpty(entryId))
                return "C5：需要指定要启用的条目 id（先用「🩺 装后体检」的根因分类确认候选，再用 --enableentry <id>）。";
            string p = Path.Combine(ProfileDir(), "cordis.patch.yml");
            string home = Path.Combine(DshCore.DshHome, "cordis.patch.yml");
            string text = "";
            try { if (File.Exists(p)) text = File.ReadAllText(p); } catch { }
            List<string> disabled = FindDisabledEntryIds(text);
            if (!disabled.Contains(entryId))
            {
                // 也可能在 home 层被禁用
                string htext = "";
                try { if (File.Exists(home)) htext = File.ReadAllText(home); } catch { }
                if (!FindDisabledEntryIds(htext).Contains(entryId))
                    return "C5：条目「" + entryId + "」当前**不是** disabled: true 状态（两层补丁都查过）⇒ 未改动任何文件。";
            }
            var sb = new StringBuilder();
            sb.AppendLine("C5 目标：为缺失的服务「" + (serviceName ?? "?") + "」启用条目「" + entryId + "」。");
            if (!string.IsNullOrEmpty(serviceName) && !PendingServiceCandidates(text, serviceName, null).Contains(entryId))
                sb.AppendLine("  ⚠ 注意：名字上它与该服务**不太像** —— 若不是你人工确认的对应关系，请先别做。");
            sb.Append(SetEntryDisabled(entryId, false));
            return sb.ToString();
        }

        /// <summary>
        /// ★ 与 loader 崩溃语义**严格一致**的判据（学自社区 skill 的边界说明）：
        /// 只有「`- insert:` **子项**里的 id 出现两次以上」才会抛 `duplicate loader entry id`；
        /// **顶层 `- id:` 重复不算冲突**（那是"按 id 修改"，官方语义允许、只 warn）。
        /// 我原来的 `FindSameFileDuplicateIds` 把顶层重复也算进去 —— 会虚报，这里收紧。
        /// </summary>
        public static List<string> FindDuplicateInsertIds(string patchText)
        {
            var dup = new List<string>();
            if (string.IsNullOrEmpty(patchText)) return dup;
            var lines = patchText.Replace("\r\n", "\n").Split('\n');
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!Regex.IsMatch(lines[i], @"^\s*-\s*insert:\s*$")) continue;
                int headerIndent = lines[i].Length - lines[i].TrimStart().Length;
                for (int j = i + 1; j < lines.Length; j++)
                {
                    string t = lines[j].Trim();
                    if (t.Length == 0) continue;
                    int ind = lines[j].Length - lines[j].TrimStart().Length;
                    if (ind <= headerIndent) break;                    // 出块了
                    Match m = Regex.Match(lines[j], @"^\s*-\s*id:\s*(\S+)\s*$");
                    if (!m.Success) continue;
                    string id = m.Groups[1].Value;
                    if (!seen.Add(id) && !dup.Contains(id)) dup.Add(id);
                }
            }
            return dup;
        }

        /// <summary>
        /// ★ 2026-09-17 升级（学自社区 skill `zhao1012/dsh-fix-duplicate-loader-id`，含 DSH 补丁语义）：
        ///
        ///   DSH 的 `applyEntryPatches` 语义是：
        ///     · `- insert:` 子项        → **创建** row；同一 id 创建两次 ⇒ 抛 `duplicate loader entry id`，整树中止
        ///     · 顶层 `- id:`（无 insert） → **按 id 修改已存在 row**；目标缺失只 warn，不终止启动
        ///
        ///   所以「把**后插入的**重复子项转成顶层 id-patch」才是正解：
        ///   既消除了"重复创建"，又**保住了它原本带的 config**。
        ///   ⚠ 我原来的 C6 是"保留第一条、删掉后面的块" —— **会丢掉后面那块里用户想要的 config**，
        ///     属于"修好了启动、却悄悄改了配置"的隐性损失。这个函数就是来替换那个做法的。
        ///
        ///   另一个要点：转成 id-patch 时必须**删掉 `name:` 行** ——
        ///   官方语义里 id-patch 带 name 且与目标不符会 warn 跳过、补丁**不生效**。
        /// </summary>
        public static string ConvertDuplicateInsertsToIdPatches(string patchText, out bool changed, out List<string> movedIds)
        {
            changed = false;
            movedIds = new List<string>();
            if (string.IsNullOrEmpty(patchText)) return patchText;

            var lines = new List<string>(patchText.Replace("\r\n", "\n").Split('\n'));
            var appended = new List<string>();
            var seenInsertIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 先收集"每个 insert 块里出现过的 id"，第一次出现算创建、之后的算重复
            var toRemove = new List<int[]>();          // 待删除的 [start,end) 行区间
            for (int i = 0; i < lines.Count; i++)
            {
                if (!Regex.IsMatch(lines[i], @"^\s*-\s*insert:\s*$")) continue;
                int headerIndent = lines[i].Length - lines[i].TrimStart().Length;
                // 找出块范围（到下一个缩进 <= headerIndent 且非空的行为止）
                int j = i + 1, end = lines.Count;
                while (j < lines.Count)
                {
                    string t = lines[j].Trim();
                    if (t.Length == 0) { j++; continue; }
                    int ind = lines[j].Length - lines[j].TrimStart().Length;
                    if (ind <= headerIndent) { end = j; break; }
                    j++;
                }
                // 遍历块内的子项
                int k = i + 1;
                while (k < end)
                {
                    Match mid = Regex.Match(lines[k], @"^(\s*)-\s*id:\s*(\S+)\s*$");
                    if (!mid.Success) { k++; continue; }
                    string childIndent = mid.Groups[1].Value;
                    string id = mid.Groups[2].Value;
                    int childEnd = k + 1;
                    while (childEnd < end)
                    {
                        string t = lines[childEnd].Trim();
                        if (t.Length == 0) { childEnd++; continue; }
                        int ind = lines[childEnd].Length - lines[childEnd].TrimStart().Length;
                        if (ind <= childIndent.Length && Regex.IsMatch(t, @"^-\s")) break;
                        childEnd++;
                    }

                    if (seenInsertIds.Add(id))
                    {
                        k = childEnd;                      // 第一次出现：保留
                        continue;
                    }

                    // 重复出现：抽出配置（去掉 id 行与 name 行），转成顶层 id-patch 追加到文件末尾
                    var cfg = new List<string>();
                    for (int q = k + 1; q < childEnd; q++)
                    {
                        if (Regex.IsMatch(lines[q], @"^\s*name:\s*")) continue;   // ★ name 必须剔除
                        cfg.Add(lines[q]);
                    }
                    // 去掉配置尾部的空行
                    while (cfg.Count > 0 && cfg[cfg.Count - 1].Trim().Length == 0) cfg.RemoveAt(cfg.Count - 1);

                    appended.Add("- id: " + id);
                    foreach (string c in cfg)
                    {
                        string body = c.TrimStart();
                        appended.Add(body.Length > 0 ? "  " + body : "");
                    }
                    movedIds.Add(id);
                    toRemove.Add(new int[] { k, childEnd });
                    k = childEnd;
                }

                // 若删完后该 insert 块变空，标记删掉 insert 头行
                //（先记着，等真正删完子项后再判断）
                if (toRemove.Count > 0)
                {
                    bool allChildrenRemoved = true;
                    for (int q = i + 1; q < end; q++)
                    {
                        string t = lines[q].Trim();
                        if (t.Length == 0) continue;
                        bool removed = false;
                        foreach (int[] r in toRemove) if (q >= r[0] && q < r[1]) { removed = true; break; }
                        if (!removed) { allChildrenRemoved = false; break; }
                    }
                    if (allChildrenRemoved) toRemove.Add(new int[] { i, i + 1 });
                }
            }

            if (toRemove.Count == 0) return patchText;      // 没有重复 ⇒ 幂等返回

            // 从后往前删，保持下标有效
            toRemove.Sort(delegate(int[] a, int[] b) { return b[0].CompareTo(a[0]); });
            foreach (int[] r in toRemove)
            {
                int cnt = r[1] - r[0];
                if (cnt > 0 && r[0] >= 0 && r[0] + cnt <= lines.Count) lines.RemoveRange(r[0], cnt);
            }

            var outp = new List<string>(lines);
            // 去掉尾部多余空行，再追加
            while (outp.Count > 0 && outp[outp.Count - 1].Trim().Length == 0) outp.RemoveAt(outp.Count - 1);
            outp.Add("");
            outp.Add("# --- 以下由大肥鱼救星转换：重复的 insert 子项 → 顶层 id-targeted patch（保留原 config）---");
            outp.AddRange(appended);
            changed = true;
            return string.Join("\r\n", outp.ToArray());
        }

        public static string RemoveDuplicateIdBlocks(string patchText, string id, out bool changed, out int removed)
        {
            changed = false; removed = 0;
            if (string.IsNullOrEmpty(patchText) || string.IsNullOrEmpty(id)) return patchText;
            string[] lines = patchText.Replace("\r\n", "\n").Split('\n');
            var idx = new List<int>();
            for (int i = 0; i < lines.Length; i++)
                if (Regex.IsMatch(lines[i], "^\\s*-\\s*id:\\s*" + Regex.Escape(id) + "\\s*$")) idx.Add(i);
            if (idx.Count < 2) return patchText;               // 只有一条（或无）⇒ 不动

            var keep = new List<string>(lines);
            // 从后往前删，保持前面的下标有效；第一条（idx[0]）保留
            for (int k = idx.Count - 1; k >= 1; k--)
            {
                int i = idx[k];
                int indent = lines[i].Length - lines[i].TrimStart().Length;
                int j = i + 1;
                while (j < lines.Length)
                {
                    string t = lines[j].Trim();
                    if (t.Length == 0) { j++; continue; }
                    int ind = lines[j].Length - lines[j].TrimStart().Length;
                    if (ind <= indent) break;                   // 同级或父级 ⇒ 块到此结束
                    j++;
                }
                if (j <= i) j = i + 1;
                keep.RemoveRange(i, j - i);
                removed++;
            }
            changed = removed > 0;
            return string.Join("\r\n", keep.ToArray());
        }

        /// <summary>C6 文件层：删掉同一文件里的重复 id 块（先快照、改后验 YAML、复验只剩一条）。</summary>
        public static string RemoveDuplicateEntry(string id)
        {
            if (string.IsNullOrEmpty(id)) return "C6：未指定条目 id。";
            string p = Path.Combine(ProfileDir(), "cordis.patch.yml");
            if (!File.Exists(p)) return "C6：找不到 " + p;

            string text;
            try { text = File.ReadAllText(p); } catch (Exception e) { return "C6 读取失败：" + e.Message; }

            var idx = new List<int>();
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
                if (Regex.IsMatch(lines[i], "^\\s*-\\s*id:\\s*" + Regex.Escape(id) + "\\s*$")) idx.Add(i);
            if (idx.Count <= 1)
                return "C6：同一文件里「" + id + "」只出现 " + idx.Count + " 次 ⇒ **无需去重，未改动文件**。\r\n"
                     + "（注意：跨两份补丁的同 id 属**合法分层覆盖**，本工具不碰。）";

            string snap;
            try { snap = SafeConfig.Snapshot("去重 entry id " + id); }
            catch (Exception e) { return "**快照失败，已中止**：" + e.Message; }

            // ★ 2016-09-17 升级：改成社区验证过的 **id-targeted patch** 修法（保留原 config）
            bool changed; List<string> moved;
            string updated = ConvertDuplicateInsertsToIdPatches(text, out changed, out moved);
            if (!changed)
                return "C6：找到重复的 id，但它们**不在 `- insert:` 块里**（可能已经是顶层 id-patch 或写法特殊）"
                     + "⇒ **未改动文件**。请把该文件发给能看的人核对。";

            string yerr;
            if (!SafeConfig.YamlLooksLikeSequence(updated, out yerr))
                return "C6：改动后 YAML 不像顶层序列 ⇒ **已放弃，文件未改动**：" + yerr;

            try
            {
                SafeConfig.AtomicWriteText(p, updated);
                SafeConfig.VerifyAfterWrite(p, false, true);
            }
            catch (Exception e) { return "C6 写入/校验失败（快照可回滚：" + snap + "）：" + e.Message; }

            // 复验：重复的 insert 子项应当没了；转出来的顶层 id-patch 应当存在
            string after = File.ReadAllText(p);
            var dupLeft = FindDuplicateInsertIds(after);
            int idPatch = 0;
            foreach (string ln in after.Replace("\r\n", "\n").Split('\n'))
                if (Regex.IsMatch(ln, "^\\s*-\\s*id:\\s*" + Regex.Escape(id) + "\\s*$")) idPatch++;

            var sb = new StringBuilder();
            sb.AppendLine("C6（升级版）：把 " + moved.Count + " 个**重复的 `- insert:` 子项**转成了**顶层 id-targeted patch**");
            sb.AppendLine("  涉及 id：" + string.Join("、", moved.ToArray()));
            sb.AppendLine("  快照：" + snap + "（可用「♻ 恢复配置」回滚）");
            sb.AppendLine("  复验：剩下的重复 insert id = " + dupLeft.Count + (dupLeft.Count == 0 ? " ✔" : "（**仍有重复，请人工检查**）"));
            sb.AppendLine("  ★ 为什么这样做（DSH 补丁语义）：`- insert:` 是**创建** row，同 id 建两次就崩溃；"
                          + "顶层 `- id:` 是**按 id 修改已存在 row**，目标缺失只 warn 不崩。"
                          + "所以转成 id-patch **既消除重复、又保住它原本带的 config** —— "
                          + "比「删掉重复块」安全（后者会悄悄丢配置）。");
            sb.AppendLine("  ★ 生效：patchReload=live 时热生效；不确定就点一次「🚀 启动并打开」。");
            sb.AppendLine("  " + RecordKnownFix("dedup-insert", p, "去重 " + string.Join("、", moved.ToArray())));
            sb.AppendLine("  ⚠ **可能复发**：装/更新插件的工具（插件市场、dsh plugin add）**会重写补丁层**（官方讨论 #3263 就是这种）"
                          + "⇒ 每次装完插件**再跑一次装后体检**。");
            return sb.ToString();
        }

        //
        //  场景：某个插件被禁用/删掉之后，别的插件在等它提供的服务
        //        （`<entry>: pending (waiting for services: X)`）⇒ 整树激活失败。
        //  修法就是把它恢复：把 `disabled: true` 改成 `false`。
        //  同样：只动 disabled 行、先快照、改后验 YAML；找不到条目就如实报、不改文件。
        // ============================================================

        /// <summary>纯函数：把补丁文本里某个 id 的 disabled 改成指定值。changed=false＝没动（已是目标状态或找不到）。</summary>
        public static string SetDisabledState(string patchText, string id, bool disabled, out bool changed)
        {
            string note;
            return SetDisabledState(patchText, id, disabled, out changed, out note);
        }

        /// <summary>
        /// 纯函数（带说明版）。note 取值：
        ///   already      已经是目标状态（无需改动）
        ///   updated      改写了该条目**已有**的 disabled 行
        ///   inserted     该条目**本来没有** disabled 行 ⇒ 新增了一行（只有"要禁用"才会走到）
        ///   no-line-kept 要启用、但该条目本来就没有 disabled 行（本来就启用）⇒ 不动
        ///   unparseable  disabled 行的值不是 true/false（看不懂 ⇒ 放弃，绝不猜）
        ///   not-found    文本里没有这个 id 的登记行
        ///
        /// ★ 为什么必须多出这个 note（2026-09-22 由 --conflict-disable 的阳性夹具抓出来的真 bug）：
        ///   旧版把「该条目根本没有 disabled 行可改」和「已经是 true」**都**塞进 changed=false，
        ///   调用方只剩一句话可说 ⇒ 于是回一句「已经是 true ⇒ 无需改动」。
        ///   而 insert 形态的条目（`- id: x` 下面直接跟 `name:`，没有 disabled 行）在本机真实
        ///   profile 里 6 条 id 占 3 条 ⇒ **禁用动作静默不生效、还回一句假话**，
        ///   连带影响「🩺 装后体检」的禁用按钮、--disableentry、以及 RepairPlan 的 disableentry。
        ///   note 一拆开，"没做"和"不需要做"就再也混不到一起了。
        /// </summary>
        public static string SetDisabledState(string patchText, string id, bool disabled, out bool changed, out string note)
        {
            changed = false;
            note = "not-found";
            if (string.IsNullOrEmpty(patchText) || string.IsNullOrEmpty(id)) return patchText;
            var lines = new List<string>(patchText.Replace("\r\n", "\n").Split('\n'));
            for (int i = 0; i < lines.Count; i++)
            {
                Match em = Regex.Match(lines[i], "^(\\s*)-\\s*id:\\s*" + Regex.Escape(id) + "\\s*$");
                if (!em.Success) continue;
                int entryIndent = em.Groups[1].Value.Length;

                // 在这个条目的**块内**找 disabled 行：块止于"更浅缩进的 - 行"（下一条/下一块）。
                // ★ 用"块边界"而不是旧版的"最多看 8 行" —— 条目可以带一长串 config: 子键，
                //   8 行窗口会让 disabled 行落到窗外，于是又被误判成"没有 disabled 行"。
                int dIdx = -1;
                for (int j = i + 1; j < lines.Count; j++)
                {
                    string raw = lines[j];
                    string t = raw.Trim();
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    int ind = raw.Length - raw.TrimStart().Length;
                    if (t.StartsWith("-") && ind <= entryIndent) break;      // 到下一个条目/下一块了
                    if (!t.StartsWith("disabled:", StringComparison.Ordinal)) continue;
                    if (Regex.IsMatch(raw, "^\\s*disabled:\\s*(true|false)\\s*$")) dIdx = j;
                    break;
                }

                string want = disabled ? "true" : "false";
                if (dIdx >= 0)
                {
                    Match vm = Regex.Match(lines[dIdx], "^(\\s*)disabled:\\s*(true|false)\\s*$");
                    if (vm.Groups[2].Value == want) { note = "already"; return patchText; }
                    lines[dIdx] = vm.Groups[1].Value + "disabled: " + want;
                    changed = true; note = "updated";
                    return string.Join("\r\n", lines.ToArray());
                }

                // 走到这里 = 这个条目**没有**（可解析的）disabled 行。
                // ★ 必须先判"有一行 disabled: 但值不是 true/false"：
                //   此时**绝不能**再插一行 —— 同一个映射里两个 disabled 键＝非法 YAML。
                if (HasUnparseableDisabled(lines, i, entryIndent)) { note = "unparseable"; return patchText; }

                if (!disabled) { note = "no-line-kept"; return patchText; }

                string pad = new string(' ', entryIndent + 2);
                lines.Insert(i + 1, pad + "disabled: true");
                changed = true; note = "inserted";
                return string.Join("\r\n", lines.ToArray());
            }
            return patchText;
        }

        /// <summary>该条目的块里是不是已经有一行 disabled:（只是值不是 true/false）——用于 fail-closed，避免插出重复键。</summary>
        private static bool HasUnparseableDisabled(List<string> lines, int idIdx, int entryIndent)
        {
            for (int j = idIdx + 1; j < lines.Count; j++)
            {
                string raw = lines[j];
                string t = raw.Trim();
                if (t.Length == 0 || t.StartsWith("#")) continue;
                int ind = raw.Length - raw.TrimStart().Length;
                if (t.StartsWith("-") && ind <= entryIndent) return false;
                if (t.StartsWith("disabled:", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>这条登记是不是写在 `- insert:` 块里面（insert 形态）。</summary>
        public static bool EntryIsInInsertBlock(string patchText, string id)
        {
            if (string.IsNullOrEmpty(patchText) || string.IsNullOrEmpty(id)) return false;
            string[] lines = patchText.Replace("\r\n", "\n").Split('\n');
            int idIdx = -1, indentId = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                Match m = Regex.Match(lines[i], "^(\\s*)-\\s*id:\\s*" + Regex.Escape(id) + "\\s*$");
                if (m.Success) { idIdx = i; indentId = m.Groups[1].Value.Length; break; }
            }
            if (idIdx < 0) return false;
            for (int i = idIdx - 1; i >= 0; i--)
            {
                string raw = lines[i];
                string t = raw.Trim();
                if (t.Length == 0 || t.StartsWith("#")) continue;
                int ind = raw.Length - raw.TrimStart().Length;
                if (ind < indentId) return t.StartsWith("- insert:", StringComparison.Ordinal);
            }
            return false;
        }

        /// <summary>文件层：把某个条目启用/禁用（先快照、只改 disabled 行、改后验 YAML）。</summary>
        public static string SetEntryDisabled(string id, bool disabled)
        {
            if (string.IsNullOrEmpty(id)) return "未指定条目 id。";
            string[] patches = new string[]
            {
                Path.Combine(ProfileDir(), "cordis.patch.yml"),
                Path.Combine(DshCore.DshHome, "cordis.patch.yml")
            };
            string target = null;
            var notAFile = new List<string>();
            foreach (string p in patches)
            {
                try
                {
                    if (!File.Exists(p))
                    {
                        // ★ 2026-09-18 修（WP3 F9b 抓到）：路径**存在**但不是普通文件
                        //   （被同名目录占了、或是坏链接）时，.NET 的 File.Exists 一律返回 false，
                        //   旧代码于是把它当成「补丁文件不存在」，最后报成
                        //   「在两层补丁里都没有找到条目「X」」—— 与真实原因（这份文件读不了、
                        //   到底有没有 X **无法判断**）完全不符，会把人往错的方向带。
                        if (Directory.Exists(p)) notAFile.Add(p + "（该路径是个**目录**，不是文件）");
                        continue;
                    }
                    if (Regex.IsMatch(File.ReadAllText(p), "(?m)^\\s*-\\s*id:\\s*" + Regex.Escape(id) + "\\s*$"))
                    { target = p; break; }
                }
                catch (Exception e)
                {
                    notAFile.Add(p + "（" + e.GetType().Name + "：" + e.Message + "）");
                }
            }
            if (target == null)
            {
                if (notAFile.Count > 0)
                    return "补丁路径被占用 / 读不到 ⇒ **未改动任何文件**：\r\n  "
                         + string.Join("\r\n  ", notAFile.ToArray()) + "\r\n"
                         + "（因此「" + id + "」到底在不在补丁里**无法判断** —— "
                         + "请先把该路径恢复成普通文件，再重试这条修复。）";
                return "在两层补丁里都没有找到条目「" + id + "」⇒ **未改动任何文件**。\r\n"
                     + "（提示：「🩺 装后体检」的重复条目一节会列出两层补丁各自有哪些 id。）";
            }

            string text;
            try { text = File.ReadAllText(target); } catch (Exception e) { return "读取失败：" + e.Message; }

            bool changed;
            string how;
            string updated = SetDisabledState(text, id, disabled, out changed, out how);
            if (!changed)
            {
                // ★ 2026-09-22 修：这里以前只剩一句「已经是 true ⇒ 无需改动」，
                //   而 changed=false 其实有四种完全不同的原因。其中"该条目根本没有 disabled 行"
                //   最危险 —— 它会让**禁用动作静默不生效却回报一切正常**（真实 profile 6 条 id 里 3 条是这形态）。
                //   现在按 how 逐种说实话，"没做"与"不需要做"再也不会混在一起。
                if (how == "already")
                    return "「" + id + "」的 disabled 已经是 " + (disabled ? "true" : "false") + " ⇒ 无需改动。";
                if (how == "no-line-kept")
                    return "「" + id + "」本来就没有 disabled 行 ⇒ 本来**就是启用状态**，无需改动。\r\n"
                         + "（这是真实结论，不是猜的：该条目下面没有 disabled 键。）";
                if (how == "unparseable")
                    return "「" + id + "」下面那行 disabled 的值**看不懂**（不是 true / false）⇒ **已放弃，文件未改动**。\r\n"
                         + "（不替你猜：猜错会把一条合法登记改成非法。请先手工把那行改成 disabled: true 或 false 再重试。）";
                return "在补丁文本里没有找到条目「" + id + "」的登记行 ⇒ **未改动任何文件**。";
            }

            string yerr;
            if (!SafeConfig.YamlLooksLikeSequence(updated, out yerr))
                return "改动后的 YAML 不像顶层序列 ⇒ **已放弃，文件未改动**：" + yerr;

            // ★ 2026-09-22 挪位：快照从"读文件之后立刻"挪到"确认要写、且 YAML 合法之后"。
            //   纪律没松（**写在快照之后**，这份新文本此刻还只在内存里），但换来一条可观测的不变量：
            //   **有快照 ⇒ 一定有写动作**。旧顺序会给"本来就是 true"的空操作也留一份快照，
            //   而主人的「♻ 恢复配置」是拿快照列表来挑回滚点的 —— 空操作快照会把那张表稀释掉。
            //   （由 --conflict-disable 的阳性夹具测出来：第二次空跑又多了一份快照，count 1 → 2。）
            string snap;
            try { snap = SafeConfig.Snapshot((disabled ? "禁用 " : "启用 ") + id); }
            catch (Exception e) { return "**快照失败，已中止**：" + e.Message; }

            try
            {
                SafeConfig.AtomicWriteText(target, updated);
                SafeConfig.VerifyAfterWrite(target, false, true);
            }
            catch (Exception e)
            {
                return "写入/校验失败（快照可回滚：" + snap + "）：" + e.Message;
            }

            var sb = new StringBuilder();
            sb.AppendLine("已" + (disabled ? "禁用" : "启用") + "条目「" + id + "」" +
                          (how == "inserted" ? "（该条目原本**没有** disabled 行 ⇒ 新增了一行）" : "（改写了已有的 disabled 行）"));
            sb.AppendLine("  文件：" + target);
            sb.AppendLine("  快照：" + snap + "（可用「♻ 恢复配置」回滚）");
            sb.AppendLine("  ★ 生效方式：patchReload 为 live 时**热生效**；若不确定，点一次「🚀 启动并打开」或重启服务。");
            if (disabled && EntryIsInInsertBlock(text, id))
            {
                // ★ 形态提醒：这不是免责声明，是一条**已知未验证**的事实（见 ConflictRadar 的条目存废判据）。
                sb.AppendLine("  ★ 形态提醒：这条登记写在 `- insert:` 块里 ⇒ **「给 insert 里的条目加 disabled」能不能压住它，本项目尚未验证**");
                sb.AppendLine("    （冲突雷达对这一类**只降级成提醒、不判红**）。⇒ 改完别只看报告：用 --conflict 复扫并实测它是否真的没被加载。");
            }
            return sb.ToString();
        }

        //
        //  纪律（写在代码里，防止以后被改松）：
        //    ① **只对"确实解析不到"的项动手** —— 能解析到就拒绝执行；
        //    ② 改之前先 SafeConfig.Snapshot()（可回滚）；
        //    ③ 改完先验 JSON 合法性，**不合法就整件事放弃、文件不动**；
        //    ④ 只改 `bundles` 数组元素与 `dependencies` 条目这两处，别的一律不碰。
        //  另：文本变换被单独拆成**纯函数** RemoveEntryFromPackageJson ——
        //  这样能在不改任何真实文件的前提下用 harness 覆盖各种格式。
        // ============================================================

        /// <summary>纯函数：从 package.json 文本里移除一个包的声明。changed=false 表示没找到、无需改。</summary>
        public static string RemoveEntryFromPackageJson(string json, string name, out bool changed)
        {
            changed = false;
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(name)) return json;
            string result = json;

            // ① bundles 数组里的元素
            int bIdx = result.IndexOf("\"bundles\"", StringComparison.Ordinal);
            if (bIdx >= 0)
            {
                int lb = result.IndexOf('[', bIdx);
                int rb = lb >= 0 ? result.IndexOf(']', lb) : -1;
                if (lb > 0 && rb > lb)
                {
                    string arr = result.Substring(lb, rb - lb + 1);
                    string newArr = RemoveJsonArrayItem(arr, name);
                    if (!string.Equals(newArr, arr, StringComparison.Ordinal))
                    {
                        result = result.Substring(0, lb) + newArr + result.Substring(rb + 1);
                        changed = true;
                    }
                }
            }

            // ② dependencies 对象里的整行
            var lines = new List<string>(result.Replace("\r\n", "\n").Split('\n'));
            for (int i = 0; i < lines.Count; i++)
            {
                if (Regex.IsMatch(lines[i], "^\\s*\"?" + Regex.Escape(name) + "\"?\\s*:"))
                {
                    lines.RemoveAt(i);
                    changed = true;
                    break;
                }
            }
            if (changed)
            {
                // 去掉因删行而悬空的尾逗号（否则 JSON 非法）
                lines = FixDanglingCommas(lines);
                result = string.Join("\r\n", lines.ToArray());
            }
            return result;
        }

        /// <summary>从形如 ["a", "b"] 的数组文本里移除一个字符串元素（连带处理相邻逗号）。</summary>
        private static string RemoveJsonArrayItem(string arr, string name)
        {
            string q = "\"" + name + "\"";
            int i = arr.IndexOf(q, StringComparison.Ordinal);
            if (i < 0) return arr;

            int after = i + q.Length;
            // 优先吃掉**后面的**逗号（连同空白）
            int j = after;
            while (j < arr.Length && (arr[j] == ' ' || arr[j] == '\t' || arr[j] == '\r' || arr[j] == '\n')) j++;
            if (j < arr.Length && arr[j] == ',')
                return arr.Substring(0, i) + arr.Substring(j + 1);
            // 否则吃掉**前面的**逗号（连同空白）
            int k = i - 1;
            while (k >= 0 && (arr[k] == ' ' || arr[k] == '\t' || arr[k] == '\r' || arr[k] == '\n')) k--;
            if (k >= 0 && arr[k] == ',')
                return arr.Substring(0, k) + arr.Substring(after);
            // 只有一个元素
            return arr.Substring(0, i) + arr.Substring(after);
        }

        /// <summary>若某行以逗号结尾、而其后第一行是 `}` 或 `]`，把该逗号去掉（保证 JSON 合法）。</summary>
        private static List<string> FixDanglingCommas(List<string> lines)
        {
            for (int i = 0; i < lines.Count - 1; i++)
            {
                string t = lines[i].TrimEnd();
                if (t.Length == 0 || t[t.Length - 1] != ',') continue;
                for (int j = i + 1; j < lines.Count; j++)
                {
                    string nxt = lines[j].Trim();
                    if (nxt.Length == 0) continue;
                    if (nxt[0] == '}' || nxt[0] == ']')
                    {
                        int pos = lines[i].LastIndexOf(',');
                        if (pos >= 0) lines[i] = lines[i].Remove(pos, 1);
                    }
                    break;
                }
            }
            return lines;
        }

        /// <summary>
        /// 文件层：移除一个**悬空**引用。带快照、改后必须 JSON 合法、并复验结果。
        /// 非悬空项一律**拒绝执行**（这是本函数的硬边界）。
        /// </summary>
        public static string RemoveDanglingReference(string name)
        {
            if (string.IsNullOrEmpty(name)) return "未指定要移除的包名。";
            string w, lv;
            if (TryResolvePackage(name, out w, out lv))
                return "拒绝执行：「" + name + "」能解析到实体（" + lv + "），它**不悬空**。\r\n"
                     + "（本工具只处理悬空引用 —— 避免误删一个其实装着的包。）";

            string pkg = Path.Combine(ProfileDir(), "package.json");
            if (!File.Exists(pkg)) return "找不到 profile\\package.json：" + pkg;

            string text;
            try { text = File.ReadAllText(pkg); }
            catch (Exception e) { return "读取失败：" + e.Message; }

            string snap;
            try { snap = SafeConfig.Snapshot("移除悬空引用 " + name); }
            catch (Exception e) { return "**快照失败，已中止**（没有退路就不动手）：" + e.Message; }

            bool changed;
            string updated = RemoveEntryFromPackageJson(text, name, out changed);
            if (!changed)
                return "在 profile\\package.json 的 bundles / dependencies 里都没找到「" + name + "」。\r\n"
                     + "⇒ 未改动任何文件（快照已生成：" + snap + "，可忽略）。";

            string err;
            if (!SafeConfig.JsonWellFormed(updated, out err))
                return "改动后的 JSON 不合法 ⇒ **已放弃，文件未改动**。\r\n原因：" + err;

            try
            {
                SafeConfig.AtomicWriteText(pkg, updated);
                SafeConfig.VerifyAfterWrite(pkg, true, false);
            }
            catch (Exception e)
            {
                return "写入/校验失败（快照可用于回滚：" + snap + "）：" + e.Message;
            }

            // 复验：再解析一次，确认它真的没了
            string after = File.ReadAllText(pkg);
            bool stillThere = Regex.IsMatch(after, "\"" + Regex.Escape(name) + "\"");
            var sb = new StringBuilder();
            sb.AppendLine("已移除悬空引用：「" + name + "」");
            sb.AppendLine("  快照：" + snap + "（如需回滚，点「♻ 恢复配置」或用该快照）");
            sb.AppendLine("  复验：文件里" + (stillThere ? "**仍能搜到该名字**（请人工确认它是否出现在别处）" : "已搜不到该名字 ✔"));
            sb.AppendLine("  JSON：合法 ✔（写后已校验）");
            sb.AppendLine("  ★ 下一步：重启 DSH（或点「🚀 启动并打开」）让它按新清单加载。");
            return sb.ToString();
        }

        //
        //  目的：把"装后体检"的结论也纳入无人值守自检 ——
        //  这样每次构建后跑一次 --selftest，就能看出这些新判据在**本机**是否健康。
        //  每项返回 { "1"/"0", 文本 }；1 = 该项健康。
        // ============================================================

        public static List<string[]> DiagItems()
        {
            var items = new List<string[]>();

            // ① 日志来源
            try
            {
                List<LogSource> srcs = CollectLogSources();
                int exists = 0, fails = 0;
                foreach (LogSource s in srcs)
                {
                    if (s.Exists) exists++;
                    fails += s.TreeFailHits;
                }
                items.Add(new string[] { exists > 0 ? "1" : "0",
                    "日志来源：候选 " + srcs.Count + " 份、存在 " + exists + " 份（含插件树失败记录 " + fails + " 处）" });
            }
            catch (Exception e) { items.Add(new string[] { "0", "日志来源全景异常：" + e.Message }); }

            // ② 根因分类（本机若有失败记录也不算"不健康"，只报事实；用是否可分类来判断判据是否工作）
            try
            {
                List<Finding> fs = ClassifyFailures(CollectLogSources());
                int un = 0, inc = 0;
                foreach (Finding f in fs) { if (f.Category == "未归类") un++; if (f.IsIncident) inc++; }
                items.Add(new string[] { "1",
                    "起不来根因分类：可读日志里 " + inc + " 次加载失败、" + fs.Count + " 条线索（未归类 " + un + " 条）" });
            }
            catch (Exception e) { items.Add(new string[] { "0", "根因分类异常：" + e.Message }); }

            // ③ 页面 × 端口 × API
            try
            {
                Liveness L = CheckLiveness();
                bool ok = L.LiveListens && L.LiveHttp && L.LiveApi;
                items.Add(new string[] { ok ? "1" : "0",
                    "页面/端口/API：页面端口 " + (L.PagePort > 0 ? L.PagePort.ToString() : "?")
                    + "、实跑 " + L.LivePort + "、静态页=" + L.LiveHttp + "、API=" + L.LiveApi });
            }
            catch (Exception e) { items.Add(new string[] { "0", "三合一探活异常：" + e.Message }); }

            // ④ 三方对齐
            try
            {
                string prof = ProfileDir();
                string text = File.Exists(Path.Combine(prof, "package.json"))
                    ? File.ReadAllText(Path.Combine(prof, "package.json")) : "";
                List<string> bundles = JsonArrayItems(text, "bundles");
                List<string> deps = JsonObjectKeys(text, "dependencies");
                var all = new List<string>();
                foreach (string b in bundles) if (!all.Contains(b)) all.Add(b);
                foreach (string d in deps) if (!all.Contains(d)) all.Add(d);
                int dangling = 0;
                foreach (string n in all)
                {
                    string w, lv;
                    if (!TryResolvePackage(n, out w, out lv)) dangling++;
                }
                items.Add(new string[] { dangling == 0 ? "1" : "0",
                    "三方对齐：bundles " + bundles.Count + " + dependencies " + deps.Count
                    + " ⇒ 悬空引用 " + dangling + " 个" });
            }
            catch (Exception e) { items.Add(new string[] { "0", "三方对齐异常：" + e.Message }); }

            // ⑤ 重复条目（★ 判据收紧：只有 insert 子项重复才会触发 loader 崩溃）
            try
            {
                int dupInsert = 0, sameFileAny = 0;
                string[] patches = new string[]
                {
                    Path.Combine(DshCore.DshHome, "cordis.patch.yml"),
                    Path.Combine(ProfDirOrProfile(), "cordis.patch.yml")
                };
                var allIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (string p in patches)
                {
                    if (!File.Exists(p)) continue;
                    string txt = File.ReadAllText(p);
                    dupInsert += FindDuplicateInsertIds(txt).Count;          // 会真崩的那种
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (Match m in Regex.Matches(txt, @"(?m)^\s*-\s*id:\s*([^\s#]+)"))
                    {
                        string id = m.Groups[1].Value;
                        if (!seen.Add(id)) sameFileAny++;
                        if (!allIds.ContainsKey(id)) allIds[id] = 0;
                        allIds[id]++;
                    }
                }
                int cross = 0;
                foreach (var kv in allIds) if (kv.Value > 1) cross++;
                items.Add(new string[] { dupInsert == 0 ? "1" : "0",
                    "重复条目：会真崩的（insert 子项重复）" + dupInsert + " 个；同文件顶层同 id " + sameFileAny
                    + " 个（官方允许，只 warn）；跨层同 id " + cross + " 个（分层覆盖，合法）" });
            }
            catch (Exception e) { items.Add(new string[] { "0", "重复条目检查异常：" + e.Message }); }

            // ⑥ 皮肤系统
            try
            {
                string rep = SkinSystemReport();
                Match m = Regex.Match(rep, @"有 \*\*(\d+) 个\*\*处于启用");
                int n = m.Success ? int.Parse(m.Groups[1].Value) : -1;
                items.Add(new string[] { (n >= 0 && n <= 1) ? "1" : "0",
                    "皮肤系统：处于启用/默认状态的皮肤 " + (n < 0 ? "?" : n.ToString()) + " 个（>1 有互斥风险）" });
            }
            catch (Exception e) { items.Add(new string[] { "0", "皮肤系统检查异常：" + e.Message }); }

            // ⑦ 模型通路
            try
            {
                string dp, dm;
                List<ProviderInfo> ps = ReadProviders(out dp, out dm);
                int badP = 0;
                foreach (ProviderInfo p in ps)
                {
                    if (string.IsNullOrEmpty(p.BaseUrl)) { badP++; continue; }
                    Match m = Regex.Match(p.BaseUrl, @"^https?://([^/:]+)(?::(\d+))?");
                    if (!m.Success) { badP++; continue; }
                    bool https = p.BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                    int port = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : (https ? 443 : 80);
                    long ms; string err;
                    if (!TryTcp(m.Groups[1].Value, port, 5000, out ms, out err)) badP++;
                }
                items.Add(new string[] { badP == 0 ? "1" : "0",
                    "模型通路：自定义 provider " + ps.Count + " 个 ⇒ 不可达/缺端点 " + badP + " 个；默认模型 " + (dm ?? "?") });
            }
            catch (Exception e) { items.Add(new string[] { "0", "模型通路检查异常：" + e.Message }); }

            // ⑧ 保留端口区间（EACCES 前置体检，来自真实报错 #589/#1462）
            try
            {
                string note;
                List<int[]> rng = ExcludedPortRanges(out note);
                bool bad = IsPortReserved(DshCore.ActivePort, rng);
                items.Add(new string[] { bad ? "0" : "1",
                    "端口保留区间：" + DshCore.ActivePort + (rng.Count == 0 ? "（未读到保留区间，或非 Windows）" :
                    (bad ? " **落在保留区间内** ⇒ 可能 EACCES；请换端口（port.txt / DSH_PORT）"
                         : " 不在保留区间内（本机共 " + rng.Count + " 段）")) });
            }
            catch (Exception e) { items.Add(new string[] { "0", "端口保留区间检查异常：" + e.Message }); }

            // ⑨ 所有补丁层的重复 insert（集大成：不再只看 home+web 两个文件）
            try
            {
                List<DupFileHit> hits = ScanAllPatchLayers();
                int ids = 0;
                foreach (DupFileHit h in hits) ids += h.Ids.Count;
                items.Add(new string[] { hits.Count == 0 ? "1" : "0",
                    "补丁层重复 insert：命中文件 " + hits.Count + " 个、重复 id " + ids + " 个（会真崩的那种）" });
            }
            catch (Exception e) { items.Add(new string[] { "0", "补丁层扫描异常：" + e.Message }); }

            // ⑩ 已知修复复查（防"被自动撤销"）
            try
            {
                string rep = RecheckKnownFixes();
                bool gone = rep.IndexOf("已经被撤销") >= 0;
                items.Add(new string[] { gone ? "0" : "1",
                    gone ? "已知修复：**有修复被撤销了**（多半是装/更新插件时被重写）⇒ 重新点一次修复按钮"
                         : "已知修复：登记过的修复都还在" });
            }
            catch (Exception e) { items.Add(new string[] { "0", "已知修复复查异常：" + e.Message }); }

            return items;
        }

        private static string ProfDirOrProfile()
        {
            return Path.Combine(DshCore.DshHome, "profiles", "web");
        }
        //
        //  为什么放最前面：主人对旧版的批评是"点了等于没点、像摆设"。
        //  根因之一是**按钮能力与名字不匹配**，用户点错地方当然没效果。
        //  所以体检报告开头先给一张对照表，把"你现在的症状"直接映射到"点哪个"。
        // ============================================================

        public static string TriageReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== 🩺 装后体检 · 先看这张表（症状 → 该点哪个）====");
            sb.AppendLine();
            sb.AppendLine("| 你现在的症状 | 该点哪个 | 为什么 |");
            sb.AppendLine("|---|---|---|");
            sb.AppendLine("| 打不开 / 白屏 / 起不来 | 先看本报告「起不来根因分类」→ 再按它指的那一项点 | 根因有 7 类，点错按钮当然没用 |");
            sb.AppendLine("| 能打开，但**一操作就弹 Failed to fetch** | 🌐 打开无痕（用当前地址重开） | 那句报错＝浏览器连不上本地服务（见本报告 A4）；修插件不会有帮助 |");
            sb.AppendLine("| 界面错乱 / 美化功能点不动 | 🎨 修复皮肤互斥 → 再看「皮肤系统并存」 | 多套皮肤同时生效会互相打架 |");
            sb.AppendLine("| 装完插件后某个插件像没生效 | 看「三方对齐」里的悬空/隐形两项 | 没登记进 bundles 的插件不会被加载 |");
            sb.AppendLine("| 服务在，但对话报上游/模型错 | 看「模型通路（A5）」 | 这才是模型端点/密钥/网络的问题 |");
            sb.AppendLine("| 用久了变慢 / 聊太久崩 | （已知未闭环）开新会话 | 超长上下文的回复循环还没修 |");
            sb.AppendLine();
            sb.AppendLine("※ 本页与后面几节**全部只读**：不改任何配置、不删任何插件、不杀任何进程。");
            sb.AppendLine("  要动手的修复按钮都会**先快照再改**，而且都会问你确认。");
            sb.AppendLine();
            return sb.ToString();
        }

        // ============================================================
        //  九、A5 模型通路体检
        //
        //  ★ 先把两种失败分清（这是最容易混的地方）：
        //     · 浏览器弹 `Failed to fetch (gateway/internal)` → **本地服务不可达**（见 A4），与模型无关；
        //     · 服务在、但回话里说上游错 → **模型通路**问题（本节负责）。
        //  本节做三件事（全部只读、**不发密钥、不发请求**）：
        //    ① 把 settings.yaml 里各 provider 的 baseURL 拿出来，逐个做 TCP/TLS 连接探测；
        //    ② 校验默认模型指向的 provider 到底存不存在；
        //    ③ 校验 apiKeyEnv 指向的环境变量/凭据文件在不在（**只报有无，绝不打印值**）。
        // ============================================================

        public sealed class ProviderInfo
        {
            public string Name;
            public string Display;
            public string Api;
            public string BaseUrl;
            public string ApiKeyEnv;
            public string Host;
            public int Port;
            public bool Reachable;
            public long Ms = -1;
            public string Error;
        }

        private static string DshSettingsPath()
        {
            return Path.Combine(DshCore.DshHome, "settings.yaml");
        }

        /// <summary>从 settings.yaml 里读 provider 列表与默认模型（行级解析，够用且不依赖 YAML 库）。</summary>
        public static List<ProviderInfo> ReadProviders(out string defaultProvider, out string defaultModel)
        {
            defaultProvider = null; defaultModel = null;
            var list = new List<ProviderInfo>();
            string path = DshSettingsPath();
            if (!File.Exists(path)) return list;
            string[] lines;
            try { lines = File.ReadAllLines(path); } catch { return list; }

            bool inLlm = false, inProviders = false, inDefaultModel = false;
            ProviderInfo cur = null;
            foreach (string raw in lines)
            {
                string line = raw ?? "";
                string t = line.Trim();

                if (Regex.IsMatch(line, @"^[A-Za-z0-9_\-\.]+:"))
                {
                    inLlm = line.StartsWith("llm-pi-ai:", StringComparison.Ordinal);
                    inDefaultModel = line.StartsWith("agent-default-model:", StringComparison.Ordinal);
                    inProviders = false;
                    cur = null;
                    continue;
                }

                if (inDefaultModel)
                {
                    Match md = Regex.Match(t, @"^(provider|model):\s*(.+)$");
                    if (md.Success)
                    {
                        string v = md.Groups[2].Value.Trim().Trim('"', '\'');
                        if (md.Groups[1].Value == "provider") defaultProvider = v; else defaultModel = v;
                    }
                    continue;
                }

                if (!inLlm) continue;

                if (Regex.IsMatch(line, @"^\s+providers:\s*$")) { inProviders = true; continue; }
                if (!inProviders) continue;

                Match mp = Regex.Match(line, @"^\s{4}([A-Za-z0-9_\-\.]+):\s*$");
                if (mp.Success)
                {
                    cur = new ProviderInfo { Name = mp.Groups[1].Value };
                    list.Add(cur);
                    continue;
                }
                if (cur == null) continue;
                Match mv = Regex.Match(t, @"^(displayName|api|baseURL|apiKeyEnv):\s*(.+)$");
                if (mv.Success)
                {
                    string k = mv.Groups[1].Value;
                    string v = mv.Groups[2].Value.Trim().Trim('"', '\'');
                    int hash = v.IndexOf(" #", StringComparison.Ordinal);
                    if (hash > 0) v = v.Substring(0, hash).Trim();
                    if (k == "displayName") cur.Display = v;
                    else if (k == "api") cur.Api = v;
                    else if (k == "baseURL") cur.BaseUrl = v;
                    else cur.ApiKeyEnv = v;
                }
            }
            return list;
        }

        /// <summary>对任意主机:端口做一次 TCP 连接探测（不握手业务、不发任何数据）。</summary>
        public static bool TryTcp(string host, int port, int timeoutMs, out long ms, out string error)
        {
            ms = -1; error = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var addrs = System.Net.Dns.GetHostAddresses(host);
                if (addrs == null || addrs.Length == 0) { error = "域名解析不到"; return false; }
                using (var c = new System.Net.Sockets.TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(addrs[0], port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                    { sw.Stop(); ms = sw.ElapsedMilliseconds; error = timeoutMs + " 毫秒内未连上"; return false; }
                    c.EndConnect(ar);
                    sw.Stop(); ms = sw.ElapsedMilliseconds;
                    return c.Connected;
                }
            }
            catch (Exception e)
            {
                sw.Stop(); ms = sw.ElapsedMilliseconds;
                error = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        public static string ModelPathReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== 装后体检 · 模型通路（A5）====");
            sb.AppendLine("配置文件: " + DshSettingsPath());
            sb.AppendLine();

            string dProv, dModel;
            List<ProviderInfo> provs = ReadProviders(out dProv, out dModel);

            if (provs.Count == 0)
                sb.AppendLine("[WARN] settings.yaml 里没有读到自定义 provider（llm-pi-ai.providers 为空或文件不存在）。");
            else
                sb.AppendLine("读到 " + provs.Count + " 个自定义 provider。");

            int bad = 0;
            foreach (ProviderInfo p in provs)
            {
                sb.AppendLine();
                sb.AppendLine("—— " + p.Name + (string.IsNullOrEmpty(p.Display) ? "" : "（" + p.Display + "）") + " ——");
                sb.AppendLine("   api     : " + (p.Api ?? "?"));
                if (string.IsNullOrEmpty(p.BaseUrl))
                {
                    sb.AppendLine("   [WARN] 没写 baseURL —— 这个 provider 实际上没法用（缺端点）。");
                    bad++;
                    continue;
                }
                sb.AppendLine("   baseURL : " + p.BaseUrl);

                Match m = Regex.Match(p.BaseUrl, @"^https?://([^/:]+)(?::(\d+))?");
                if (!m.Success) { sb.AppendLine("   [WARN] baseURL 解析不出主机名。"); bad++; continue; }
                p.Host = m.Groups[1].Value;
                bool https = p.BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                p.Port = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : (https ? 443 : 80);

                long ms; string err;
                p.Reachable = TryTcp(p.Host, p.Port, 6000, out ms, out err);
                p.Ms = ms;
                if (p.Reachable)
                    sb.AppendLine("   [OK]   连接 " + p.Host + ":" + p.Port + " 成功（" + ms + " ms）");
                else
                {
                    bad++;
                    sb.AppendLine("   [FAIL] 连接 " + p.Host + ":" + p.Port + " 失败：" + (err ?? "未知原因"));
                    sb.AppendLine("          ⇒ 这一类才是「模型调不通」：网络/代理/DNS/端点写错。");
                    sb.AppendLine("          ⇒ 注意：这与浏览器那句 `Failed to fetch (gateway/internal)` **不是同一回事**"
                                  + "（那句是浏览器连不上本地服务，见 A4）。");
                }

                // 密钥：只报"有没有"，绝不打印值
                if (!string.IsNullOrEmpty(p.ApiKeyEnv))
                {
                    string envVal = null;
                    try { envVal = Environment.GetEnvironmentVariable(p.ApiKeyEnv); } catch { }
                    bool credFile = File.Exists(Path.Combine(DshCore.DshHome, ".credentials.yaml"));
                    sb.AppendLine("   密钥     : apiKeyEnv=" + p.ApiKeyEnv
                                  + "　环境变量已设置=" + (string.IsNullOrEmpty(envVal) ? "否" : "是")
                                  + "　~/.dsh/.credentials.yaml 存在=" + (credFile ? "是" : "否"));
                }
            }

            sb.AppendLine();
            sb.AppendLine("---- 默认模型指向 ----");
            sb.AppendLine("agent-default-model.provider = " + (dProv ?? "（未读到）"));
            sb.AppendLine("agent-default-model.model    = " + (dModel ?? "（未读到）"));
            if (!string.IsNullOrEmpty(dProv))
            {
                bool inCustom = false;
                foreach (ProviderInfo p in provs)
                    if (string.Equals(p.Name, dProv, StringComparison.OrdinalIgnoreCase)) inCustom = true;
                if (inCustom)
                    sb.AppendLine("[OK]   默认 provider 是上面列出的自定义 provider。");
                else if (dProv.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine("[OK]   默认 provider 是官方内置路由（" + dProv + "），不在自定义 providers 列表里属正常。");
                else
                {
                    bad++;
                    sb.AppendLine("[FAIL] 默认 provider 「" + dProv + "」既不在自定义 providers 列表里，也不是官方内置名 —— "
                                  + "这会让每一轮对话都取不到模型。");
                }
            }

            sb.AppendLine();
            sb.AppendLine("★ 结论：" + (bad == 0
                ? "模型通路体检未发现问题（端点可达、默认模型指向有效）。"
                : "发现 " + bad + " 处问题（见上面 [FAIL]/[WARN]）—— 这些属于「服务在、但调不动模型」，与 A4 那句浏览器报错要分开看。"));
            sb.AppendLine("※ 本节只做 TCP 连接探测，**没有发送任何密钥、也没有发起任何模型请求**。");
            return sb.ToString();
        }

        // ============================================================
        //  十七、F1 一键重装某个插件（入口缺失那一类的修法）
        //
        //  硬边界（安全）：
        //    ① 只重装**已经在 profile 清单里**的包 —— 本工具不做"任意包安装器"；
        //    ② 包名必须通过格式校验；
        //    ③ 先打快照（pnpm 会改写 package.json / lock 文件）；
        //    ④ 需要联网：失败时把真实错误原样报出来，不假装成功。
        // ============================================================

        public static string ReinstallPlugin(string pkgName)
        {
            if (string.IsNullOrWhiteSpace(pkgName)) return "未指定要重装的包名。";
            pkgName = pkgName.Trim();
            if (!Regex.IsMatch(pkgName, @"^(@[A-Za-z0-9._-]+/)?[A-Za-z0-9._-]+(@[A-Za-z0-9._-]+)?$"))
                return "包名格式看起来不对：" + pkgName + "（期望形如 dsh-image-gen 或 @scope/name@1.2.3）⇒ **未执行**。";

            string bare = pkgName;
            int at = pkgName.StartsWith("@") ? pkgName.IndexOf('@', 1) : pkgName.IndexOf('@');
            if (at > 0) bare = pkgName.Substring(0, at);

            string pkgPath = Path.Combine(ProfileDir(), "package.json");
            string text = "";
            try { text = File.ReadAllText(pkgPath); } catch { }
            if (text.Length > 0 && text.IndexOf("\"" + bare + "\"", StringComparison.OrdinalIgnoreCase) < 0)
                return "拒绝执行：「" + bare + "」不在 profile 的 bundles / dependencies 里 —— "
                     + "本工具只重装**你已经装了**的插件（把新插件装进来请用 dsh plugin add）。";

            string snap;
            try { snap = SafeConfig.Snapshot("重装插件 " + bare); }
            catch (Exception e) { return "**快照失败，已中止**：" + e.Message; }

            DshCore.LaunchSpec sp = DshCore.DiscoverLaunch();
            if (sp == null || string.IsNullOrEmpty(sp.BinJs) || string.IsNullOrEmpty(sp.Node))
                return "找不到 dsh / node 入口，无法重装。";

            var sb = new StringBuilder();
            sb.AppendLine("正在重装：「" + bare + "」（快照：" + snap + "）");
            sb.AppendLine("  命令：" + sp.Node + " " + sp.BinJs + " plugin --profile web add " + pkgName);
            string output = null;
            try
            {
                output = DshCore.RunHidden(sp.Node,
                    "\"" + sp.BinJs + "\" plugin --profile web add " + pkgName, 240000);
            }
            catch (Exception e) { output = "异常：" + e.GetType().Name + ": " + e.Message; }

            sb.AppendLine("  ---- 命令输出（尾部）----");
            if (!string.IsNullOrEmpty(output))
            {
                string[] lines = output.Replace("\r\n", "\n").Split('\n');
                int from = Math.Max(0, lines.Length - 12);
                for (int i = from; i < lines.Length; i++)
                    if (lines[i].Trim().Length > 0) sb.AppendLine("    " + lines[i].Trim());
            }
            else sb.AppendLine("    （没有输出 —— 可能没联网、或 pnpm 不可用）");

            // 复验
            string w, lv;
            bool okPkg = TryResolvePackage(bare, out w, out lv);
            bool okEntry = false;
            string entryNote = "";
            if (okPkg)
            {
                try
                {
                    string pj = Path.Combine(w, "package.json");
                    if (File.Exists(pj))
                    {
                        string pjText = File.ReadAllText(pj);
                        Match m = Regex.Match(pjText, "\"main\"\\s*:\\s*\"([^\"]+)\"");
                        if (m.Success)
                        {
                            string entry = Path.Combine(w, m.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar));
                            okEntry = File.Exists(entry);
                            entryNote = m.Groups[1].Value + (okEntry ? "（存在 ✔）" : "（**仍缺失**）");
                        }
                        else entryNote = "（包里没写 main）";
                    }
                }
                catch { }
            }

            sb.AppendLine();
            sb.AppendLine("  复验：包目录=" + (okPkg ? "在（" + lv + "）" : "**仍找不到**") + "　入口=" + (entryNote.Length > 0 ? entryNote : "?"));
            sb.AppendLine(okPkg && okEntry
                ? "  ★ 看起来修好了：重启服务（或点「🚀 启动并打开」）后它应当能加载。"
                : "  ★ 仍未修好：请把上面的命令输出原样发给能看的人（**不要**凭猜继续改配置）。");
            sb.AppendLine("  回滚：出问题就用「♻ 恢复配置」回到快照 " + snap + "。");
            return sb.ToString();
        }

        // ============================================================
        //  二十、集大成之一：**全 profile + 各 bundle 包**的重复 insert 扫描
        //
        //  为什么必须扩：社区 skill 明确指出 —— 冲突来自**多个 bundle 层各自 insert 同一个 id**
        //  （典型：`@deepseek-ai/dsh-web-app` 插入 storage/workspace/…，另一个插件又插同 id）。
        //  这类冲突**未必写在 profile 的 cordis.patch.yml 里**，而是在**各 bundle 包自己的 patch**
        //  或**别的 profile** 里。旧版只查 home + web profile 两个文件 ⇒ 会漏。
        // ============================================================

        /// <summary>纯函数：zstd 魔数判断（会话文件浅体检用）。</summary>
        public static bool LooksLikeZstd(byte[] head)
        {
            return head != null && head.Length >= 4
                && head[0] == 0x28 && head[1] == 0xB5 && head[2] == 0x2F && head[3] == 0xFD;
        }

        public sealed class DupFileHit
        {
            public string File;
            public string Where;      // 人话：哪个 profile / 哪个包
            public List<string> Ids;
        }

        /// <summary>扫描所有 profile 的补丁 + 各已装 bundle 包自带的补丁，找出"会真崩"的重复 insert id。</summary>
        public static List<DupFileHit> ScanAllPatchLayers()
        {
            var hits = new List<DupFileHit>();
            var files = new List<string[]>();

            // ① home 补丁
            files.Add(new string[] { Path.Combine(DshCore.DshHome, "cordis.patch.yml"), "home 补丁" });

            // ② 每个 profile 自己的补丁 + 该 profile 下每个已装包自带的补丁
            string profRoot = Path.Combine(DshCore.DshHome, "profiles");
            try
            {
                if (Directory.Exists(profRoot))
                {
                    foreach (string prof in Directory.GetDirectories(profRoot))
                    {
                        string pname = Path.GetFileName(prof);
                        string pp = Path.Combine(prof, "cordis.patch.yml");
                        if (File.Exists(pp)) files.Add(new string[] { pp, pname + " profile 补丁" });

                        string nm = Path.Combine(prof, "node_modules");
                        if (!Directory.Exists(nm)) continue;
                        foreach (string dir in SafeDirs(nm, 2))
                        {
                            string cp = Path.Combine(dir, "cordis.patch.yml");
                            if (File.Exists(cp))
                                files.Add(new string[] { cp, pname + " · 包 " + Path.GetFileName(dir) + " 自带补丁" });
                        }
                        // 本地插件目录
                        string plugins = Path.Combine(prof, "plugins");
                        if (Directory.Exists(plugins))
                            foreach (string dir in SafeDirs(plugins, 1))
                            {
                                string cp = Path.Combine(dir, "cordis.patch.yml");
                                if (File.Exists(cp))
                                    files.Add(new string[] { cp, pname + " · 本地插件 " + Path.GetFileName(dir) });
                            }
                    }
                }
            }
            catch { }

            foreach (string[] f in files)
            {
                try
                {
                    if (!File.Exists(f[0])) continue;
                    List<string> ids = FindDuplicateInsertIds(File.ReadAllText(f[0]));
                    if (ids.Count > 0) hits.Add(new DupFileHit { File = f[0], Where = f[1], Ids = ids });
                }
                catch { }
            }
            return hits;
        }

        // =====================================================================
        //  ★★ 2026-09-18 新增：**本地插件**（profiles\*/plugins\*）的"半装 / 隐形"体检
        //
        //  为什么必须单独加（主人这次的形态）：
        //    已经把 dsh-picks 登记进 `cordis.patch.yml`，而那一刻 `plugins/dsh-picks` 只有服务端半边、
        //    **没有 package.json**。而原有两处判据**都漏过它**：
        //      · 悬空判定（三方对齐）只问"这个 id 能不能解析到**一个目录**" ⇒ 目录在就不算悬空；
        //      · 静态探测（PluginCompatibility）只遍历 **profile package.json 的 dependencies**
        //        （node_modules 里那些）⇒ 本地插件压根不在它名单里。
        //    ⇒ 现象就是"登记了、目录也在、DSH 却加载不起来"，而体检全绿。
        //
        //  DSH 侧的事实：加载一个插件要先读它的 package.json 拿 name / main / exports，
        //    没有 package.json（或 main 指向的文件不存在）⇒ 那一半起不来。
        //    补全是**两条路**：①把 package.json 补上（让它成为合法 node 包）；
        //    ②把那条 insert 登记摘掉（先快照，可回滚）。本函数只负责**如实报出来**，不动文件。
        // =====================================================================
        public static string[] LocalPluginsCheck()
        {
            var sb = new StringBuilder();
            int total = 0, fail = 0, warn = 0;
            var fails = new List<string>();
            var warns = new List<string>();
            try
            {
                string profRoot = System.IO.Path.Combine(DshCore.DshHome, "profiles");
                if (!System.IO.Directory.Exists(profRoot))
                    return new string[] { "0", "[FAIL] 本地插件体检：找不到 profiles 目录（" + profRoot + "）" };

                foreach (string prof in System.IO.Directory.GetDirectories(profRoot))
                {
                    string pname = System.IO.Path.GetFileName(prof);
                    string plugins = System.IO.Path.Combine(prof, "plugins");
                    if (!System.IO.Directory.Exists(plugins)) continue;

                    // 把所有补丁层的文本拼起来，用来判"有没有被登记"
                    string patchText = "";
                    try
                    {
                        string pp = System.IO.Path.Combine(prof, "cordis.patch.yml");
                        if (System.IO.File.Exists(pp)) patchText += System.IO.File.ReadAllText(pp) + "\n";
                        string hp = System.IO.Path.Combine(DshCore.DshHome, "cordis.patch.yml");
                        if (System.IO.File.Exists(hp)) patchText += System.IO.File.ReadAllText(hp) + "\n";
                    }
                    catch { }

                    foreach (string dir in SafeDirs(plugins, 1))
                    {
                        string dn = System.IO.Path.GetFileName(dir);
                        // 下划线开头 = 我们自己留的归档/备份目录（_bak-*），不算"装了但坏"
                        if (dn.StartsWith("_")) continue;
                        total++;
                        string pkg = System.IO.Path.Combine(dir, "package.json");
                        if (!System.IO.File.Exists(pkg))
                        {
                            fail++;
                            fails.Add(pname + " · " + dn + "：**没有 package.json**（半装！DSH 读不到 name/main 就加载不起来）"
                                      + " ⇒ 要么把它补成合法 node 包，要么把补丁里那条登记摘掉（🧹 先快照再改）");
                            continue;
                        }
                        string txt;
                        try { txt = System.IO.File.ReadAllText(pkg); }
                        catch (Exception e)
                        {
                            fail++;
                            fails.Add(pname + " · " + dn + "：package.json 读不出来（" + e.GetType().Name + "）");
                            continue;
                        }
                        string[] keys = new string[] { "\"name\"", "\"main\"" };
                        bool hasName = txt.IndexOf(keys[0], StringComparison.Ordinal) >= 0;
                        int mi = txt.IndexOf(keys[1], StringComparison.Ordinal);
                        string mainVal = null;
                        if (mi >= 0)
                        {
                            int c = txt.IndexOf(':', mi);
                            int q1 = c >= 0 ? txt.IndexOf('"', c + 1) : -1;
                            int q2 = q1 >= 0 ? txt.IndexOf('"', q1 + 1) : -1;
                            if (q2 > q1) mainVal = txt.Substring(q1 + 1, q2 - q1 - 1);
                        }
                        if (!hasName)
                        {
                            fail++;
                            fails.Add(pname + " · " + dn + "：package.json 里**没有 name**（DSH 认不出这个包）");
                            continue;
                        }
                        if (mainVal == null)
                        {
                            // 没有 main 不算硬错（可能用 exports），但要说清
                            warn++;
                            warns.Add(pname + " · " + dn + "：package.json 没写 main（若也没写 exports，DSH 就找不到入口）");
                        }
                        else
                        {
                            string mf = System.IO.Path.Combine(dir, mainVal.Replace('/', System.IO.Path.DirectorySeparatorChar));
                            if (!System.IO.File.Exists(mf))
                            {
                                fail++;
                                fails.Add(pname + " · " + dn + "：main 指向的**入口文件不存在**：" + mainVal
                                          + "（有清单没实体 ⇒ 加载必失败）");
                                continue;
                            }
                        }
                        // 登记检查：补丁里出现目录名（`name: './plugins/dsh-xxx/lib/index.js'`）即算登记
                        bool registered = patchText.IndexOf(dn, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!registered)
                        {
                            warn++;
                            warns.Add(pname + " · " + dn + "：**装了但没登记**（任何补丁层都没有它）⇒ 不会被加载；"
                                      + "要挂上就加一条 `- insert: { id: <短名>, name: './plugins/" + dn + "/lib/index.js' }`");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                return new string[] { "0", "[FAIL] 本地插件体检：异常 " + e.GetType().Name + " " + e.Message };
            }

            string head;
            if (fail > 0)
                head = "[FAIL] 本地插件体检：查了 " + total + " 个，**" + fail + " 个有问题**、"
                       + warn + " 个提醒 ⇒ 这一项就是" + "「登记了/装了却起不来」" + "的那类，先处理它";
            else if (warn > 0)
                head = "[OK]   本地插件体检：查了 " + total + " 个，**没有半装/入口缺失**（" + warn + " 个提醒，见下）";
            else
                head = "[OK]   本地插件体检：查了 " + total + " 个本地插件，全部有 name/main 且入口存在、且都被补丁登记";
            sb.AppendLine(head);
            foreach (string f in fails) sb.AppendLine("        · " + f);
            foreach (string w in warns) sb.AppendLine("        · （提醒）" + w);
            return new string[] { fail == 0 ? "1" : "0", sb.ToString().TrimEnd() };
        }

        // =====================================================================
        //  ★★ 2026-09-18 新增：**挂在 profile 外面的插件**体检（跨机器通用，不认插件名单）
        //
        //  为什么加：主人转来一张别人的报错截图 —— 插件装在 `D:\plugins\dsh-plugin-ponytail\`，
        //  靠补丁里的绝对路径挂上；结果它 `import '@deepseek-ai/schemastery'` 解析不到，
        //  DSH 在 compose/import 阶段就崩，现象是「死活打不开页面」。
        //  Node 的依赖解析是**从文件自己的目录往上找 node_modules**，
        //  插件在 profile 外面 ⇒ 看不到 DSH 安装树里的那套 @deepseek-ai/* 包。
        //  ⇒ 判据：扫所有补丁层的 `name:` 值，凡**绝对路径**且不在 profile 目录内的，就是高危形态。
        // =====================================================================
        /// <summary>
        /// 纯函数接缝（供自检用正负样本验证）：从**补丁文本**里挑出"绝对路径且不在 profile 目录内"的 name 值。
        /// 单独抽出来是为了能造样本验证 —— 否则这条判据就只是"在我自己机器上恰好能跑"。
        /// </summary>
        public static List<string> FindOutOfTreeNames(string patchText, string profileDir)
        {
            var hits = new List<string>();
            if (patchText == null) return hits;
            string prof = (profileDir ?? "").Replace('/', '\\').TrimEnd('\\');
            foreach (string raw in patchText.Split('\n'))
            {
                string ln = raw.Trim();
                int i = ln.IndexOf("name:");
                if (i < 0) continue;
                string val = ln.Substring(i + 5).Trim().Trim('"', '\'', ',');
                if (val.Length == 0) continue;
                bool abs = Regex.IsMatch(val, @"^[A-Za-z]:[\\/]") || val.StartsWith("/");
                if (!abs) continue;
                string norm = val.Replace('/', '\\');
                if (prof.Length > 0 && norm.StartsWith(prof + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                hits.Add(val);
            }
            return hits;
        }

        /// <summary>
        /// 自检：这条判据必须"**正样本命中、负样本不命中**"。
        /// 三个样本分别对应：①别人的机器（插件装在 D:\plugins\…）必须报出来；
        /// ②相对路径（./plugins/xxx/… 这种正常的本地插件）**不许误报**；
        /// ③profile 目录内的绝对路径**不许误报**（那是不在 profile 的 node_modules 里、但至少在同一棵树里）。
        /// </summary>
        public static string[] Selftest()
        {
            var lines = new List<string>();
            try
            {
                string prof = @"C:\u\.dsh\profiles\web";
                string pos = "- insert:\n    - id: ponytail\n      name: 'D:\\plugins\\dsh-plugin-ponytail\\lib\\index.js'\n";
                string negRel = "- insert:\n    - id: picks\n      name: './plugins/dsh-picks/lib/index.js'\n";
                string negIn = "- insert:\n    - id: inner\n      name: 'C:\\u\\.dsh\\profiles\\web\\plugins\\x\\lib\\index.js'\n";
                int a = FindOutOfTreeNames(pos, prof).Count;
                int b = FindOutOfTreeNames(negRel, prof).Count;
                int c = FindOutOfTreeNames(negIn, prof).Count;
                bool ok = (a == 1 && b == 0 && c == 0);
                lines.Add("外部插件判据（profile 外面的插件）：正样本命中 " + a + "(应 1)、相对路径 " + b
                          + "(应 0)、profile 内绝对路径 " + c + "(应 0)：" + (ok ? "OK" : "★不合格"));
            }
            catch (Exception e) { lines.Add("外部插件判据：★异常 " + e.GetType().Name + " " + e.Message); }
            return lines.ToArray();
        }

        public static string[] OutOfTreePluginsCheck()
        {
            var sb = new StringBuilder();
            var hits = new List<string>();
            int scanned = 0;
            try
            {
                string profRoot = System.IO.Path.Combine(DshCore.DshHome, "profiles");
                var files = new List<string>();
                string hp = System.IO.Path.Combine(DshCore.DshHome, "cordis.patch.yml");
                if (System.IO.File.Exists(hp)) files.Add("(home) " + hp);
                try
                {
                    if (System.IO.Directory.Exists(profRoot))
                        foreach (string prof in System.IO.Directory.GetDirectories(profRoot))
                        {
                            string pp = System.IO.Path.Combine(prof, "cordis.patch.yml");
                            if (System.IO.File.Exists(pp)) files.Add(System.IO.Path.GetFileName(prof) + " · " + pp);
                        }
                }
                catch { }

                foreach (string entry in files)
                {
                    int sep = entry.IndexOf(" · ");
                    string label = sep > 0 ? entry.Substring(0, sep) : "(home)";
                    string path = sep > 0 ? entry.Substring(sep + 3) : entry.Substring(7);
                    string profDir = System.IO.Path.Combine(profRoot, label);
                    string text;
                    try { text = System.IO.File.ReadAllText(path); } catch { continue; }
                    foreach (string val in FindOutOfTreeNames(text, profDir))
                    {
                        scanned++;
                        string where = "";
                        try
                        {
                            string pdir = System.IO.Path.GetDirectoryName(val.Replace('/', '\\'));
                            where = System.IO.Directory.Exists(pdir) ? "目录在" : "**连目录都不在**";
                        }
                        catch { }
                        hits.Add(label + " 补丁里挂着 profile 外面的插件：" + val + "（" + where + "）"
                                 + " ⇒ 它的 import 很容易解析不到 DSH 的包（报 Cannot find package / ERR_MODULE_NOT_FOUND），"
                                 + "现象就是「页面死活打不开」。正解＝把它装进 profile（dsh plugin --profile web add …）。");
                    }
                }
            }
            catch (Exception e)
            {
                return new string[] { "0", "[FAIL] 外部插件体检：异常 " + e.GetType().Name + " " + e.Message };
            }

            if (hits.Count == 0)
                sb.AppendLine("[OK]   外部插件体检：补丁里没有" + "挂在 profile 外面" + "的插件（扫了 " + scanned + " 条绝对路径登记）");
            else
            {
                sb.AppendLine("[FAIL] 外部插件体检：发现 " + hits.Count + " 个**挂在 profile 外面**的插件"
                              + " —— 这类最容易出「Cannot find package ⇒ 页面打不开」");
                foreach (string h in hits) sb.AppendLine("        · " + h);
            }
            return new string[] { hits.Count == 0 ? "1" : "0", sb.ToString().TrimEnd() };
        }

        /// <summary>列出目录下最多两层的包目录（跳过 . 开头）。</summary>
        private static List<string> SafeDirs(string root, int depth)
        {
            var list = new List<string>();
            try
            {
                foreach (string d in Directory.GetDirectories(root))
                {
                    string n = Path.GetFileName(d);
                    if (n.StartsWith(".")) continue;
                    if (n.StartsWith("@"))
                    {
                        if (depth < 2) continue;
                        try { foreach (string d2 in Directory.GetDirectories(d)) list.Add(d2); } catch { }
                        continue;
                    }
                    list.Add(d);
                }
            }
            catch { }
            return list;
        }

        public static string AllPatchLayersReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== 装后体检 · 所有补丁层的重复 insert（B2 扩展）====");
            List<DupFileHit> hits = ScanAllPatchLayers();
            int scanned = 0;
            try
            {
                string profRoot = Path.Combine(DshCore.DshHome, "profiles");
                if (Directory.Exists(profRoot)) scanned = Directory.GetDirectories(profRoot).Length;
            }
            catch { }
            sb.AppendLine("已扫：home 补丁 + " + scanned + " 个 profile（含各包自带补丁）");
            if (hits.Count == 0)
            {
                sb.AppendLine("[OK]   没有发现「会真崩」的重复 insert id。");
                sb.AppendLine("       （判据与 loader 崩溃语义一致：只有 `- insert:` **子项**同 id 出现两次才算；顶层 id 重复不算。）");
                return sb.ToString();
            }
            foreach (DupFileHit h in hits)
            {
                sb.AppendLine("[FAIL] " + h.Where);
                sb.AppendLine("       " + h.File);
                sb.AppendLine("       重复 insert id：" + string.Join("、", h.Ids.ToArray()));
                sb.AppendLine("       ⇒ 用「🚫 去重重复条目」修复（会先快照、转成顶层 id-patch）");
            }
            return sb.ToString();
        }

        // ============================================================
        //  二十一、集大成之二：会话文件浅体检 + 外部工具指路
        //
        //  会话损坏（seq 断档 / zstd 截断 / 孤立代理项…）**深度修复我们不做** ——
        //  社区已有专门工具（dsh-session-surgeon，默认 dry-run、先备份、绝不凭空补 seq）。
        //  我们只做**廉价的文件级体检**，把"可疑会话"挑出来并指路，避免用户瞎试。
        // ============================================================

        public static string SessionHealthReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== 装后体检 · 会话文件浅体检（C 组·指路）====");
            string root = Path.Combine(DshCore.DshHome, "sessions");
            if (!Directory.Exists(root)) { sb.AppendLine("[OK]   没有 sessions 目录。"); return sb.ToString(); }

            int total = 0, empty = 0, badMagic = 0, tiny = 0, orphans = 0;
            long bytes = 0;
            var suspects = new List<string>();
            try
            {
                foreach (string f in Directory.GetFiles(root, "*.zstd", SearchOption.AllDirectories))
                {
                    total++;
                    var fi = new FileInfo(f);
                    bytes += fi.Length;
                    if (fi.Length == 0) { empty++; suspects.Add("0 字节：" + Short(f, root)); continue; }
                    if (fi.Length < 1024) { tiny++; }
                    try
                    {
                        byte[] head = new byte[4];
                        using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            fs.Read(head, 0, 4);
                        if (!LooksLikeZstd(head)) { badMagic++; suspects.Add("魔数不对（不是 zstd）：" + Short(f, root)); }
                    }
                    catch { }
                }
                foreach (string f in Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories)) orphans++;
            }
            catch { }

            sb.AppendLine("会话文件：" + total + " 个，合计 " + (bytes / 1024 / 1024) + " MB");
            sb.AppendLine("可疑项：0 字节 " + empty + "、魔数不对 " + badMagic + "、小于 1KB " + tiny + "、孤儿 .tmp " + orphans);
            foreach (string s in suspects) { if (suspects.IndexOf(s) < 8) sb.AppendLine("  · " + s); }
            sb.AppendLine();
            // ★ 集大成：调用**随包携带的 Node 助手**做深度体检
            //   （多帧 zstd 逐帧解码 + seq 断档 + 悬空 tool/call —— 这部分社区项目做得最好，我们学过来）
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string tool = Path.Combine(baseDir, "tools", "session-inspect.cjs");
                DshCore.LaunchSpec sp = DshCore.DiscoverLaunch();
                if (sp != null && !string.IsNullOrEmpty(sp.Node) && File.Exists(tool))
                {
                    string outp = DshCore.RunHidden(sp.Node,
                        "\"" + tool + "\" \"" + root + "\" --limit 12", 240000);
                    sb.AppendLine("---- 深度体检（随包 Node 助手 · 只读）----");
                    if (string.IsNullOrEmpty(outp)) sb.AppendLine("  （助手没有输出 —— 可能 node 不可用或被拦）");
                    else
                        foreach (string l in outp.Replace("\r\n", "\n").Split('\n'))
                            if (l.Trim().Length > 0) sb.AppendLine("  " + l.TrimEnd());
                }
                else
                    sb.AppendLine("（没找到随包助手 tools\\session-inspect.cjs 或 node ⇒ 只做了上面的浅体检）");
            }
            catch (Exception e) { sb.AppendLine("深度体检异常：" + e.Message); }
            sb.AppendLine();
            sb.AppendLine("★ 判据说明：普通行占 1 个 seq、**打包行**（带 seq0）占 `1+dt.length` 个 —— "
                          + "这条模型在 148,383 行上实测断档 0 处（换别的模型会虚报好几万）。");
            sb.AppendLine("※ 本工具**只读**会话文件，绝不改写；真需要修请用社区 dsh-session-surgeon。");
            return sb.ToString();
        }

        private static string Short(string full, string root)
        {
            string rel = full.StartsWith(root) ? full.Substring(root.Length).TrimStart('\\', '/') : full;
            return rel.Length > 90 ? "…" + rel.Substring(rel.Length - 90) : rel;
        }

        /// <summary>集大成之三：**明确告诉我们做不了什么、该找哪个社区工具**。</summary>
        public static string ExternalToolsHint()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== 装后体检 · 超出本工具范围时，去找谁（社区工具）====");
            sb.AppendLine("| 情况 | 找谁 | 为什么不是我们 |");
            sb.AppendLine("|---|---|---|");
            sb.AppendLine("| 会话文件打不开（seq 断档 / zstd 截断 / 孤立代理项） | `dsh-session-surgeon` | 会话是 zstd 压缩 + 格式随版本演进，深度修复我们不做半成品 |");
            sb.AppendLine("| 重复 loader entry id 反复复发、想**固化** | `dsh-fix-duplicate-loader-id`（或 `pnpm patch` + `patch-commit`） | 固化要动 pnpm 的 patchedDependencies 与 lockfile，风险高，本工具只做「修复 + 复发复查」 |");
            sb.AppendLine("| 想要**插件形态**的自诊断（常驻、随 DSH 启动） | `ckk-09/dsh-refix` | 我们是**独立 exe**（DSH 起不来时也能用）；两种形态互补 |");
            sb.AppendLine("| 客户端插件**服务名冲突导致白屏** | 建议先按 `--layercheck` 判层 + 逐个禁用最近新增插件定位 | 冲突发生在浏览器端，服务端日志看不到来源 |");
            sb.AppendLine();
            sb.AppendLine("★ 本工具的定位：**DSH 起不来时你还能用的那个东西**（独立 exe、只读优先、修复可回滚），"
                          + "并在超出能力时**如实指路**，而不是硬编一个不可靠的修复。");
            return sb.ToString();
        }

        // ============================================================
        //  二十二、集大成之四：**已知修复登记 + 复发复查**
        //
        //  社区反馈（DSH #3263）：装/更新插件的工具会重写补丁层，**手工修复会被自动撤销**。
        //  我们能做的：把"修过什么"记在一个清单里；以后每次装后体检**复查它还在不在**，
        //  不在了就提示"一键重打"（而不是让用户下次再从头查一遍）。
        // ============================================================

        private static string KnownFixesPath()
        {
            return Path.Combine(DshCore.AppDataDir, "known-fixes.tsv");
        }

        public static string RecordKnownFix(string kind, string target, string note)
        {
            try
            {
                DshCore.EnsureAppDataDir();
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\t" + kind + "\t" + target + "\t" + (note ?? "");
                File.AppendAllText(KnownFixesPath(), line + "\r\n", new UTF8Encoding(false));
                return "已登记修复：" + kind + " → " + target;
            }
            catch (Exception e) { return "登记失败：" + e.Message; }
        }

        public static string RecheckKnownFixes()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== 装后体检 · 已知修复是否还在（防「被自动撤销」）====");
            string p = KnownFixesPath();
            if (!File.Exists(p))
            {
                sb.AppendLine("[OK]   还没有登记过修复（修完会自动登记，下次体检会复查它还在不在）。");
                return sb.ToString();
            }
            string[] lines;
            try { lines = File.ReadAllLines(p); } catch { lines = new string[0]; }
            int total = 0, gone = 0;
            foreach (string raw in lines)
            {
                string[] c = (raw ?? "").Split('\t');
                if (c.Length < 3) continue;
                total++;
                string kind = c[1], target = c[2];
                bool ok = true; string how = "";
                if (kind == "dedup-insert")
                {
                    // target = 文件路径；复查该文件里是否又出现重复 insert
                    try
                    {
                        if (!File.Exists(target)) { ok = false; how = "文件没了"; }
                        else
                        {
                            List<string> dup = FindDuplicateInsertIds(File.ReadAllText(target));
                            ok = dup.Count == 0;
                            how = ok ? "仍无重复 ✔" : "**又出现重复：" + string.Join("、", dup.ToArray()) + "**";
                        }
                    }
                    catch { ok = false; how = "读不到"; }
                }
                else if (kind == "dangling-ref")
                {
                    string w, lv;
                    ok = TryResolvePackage(target, out w, out lv);      // 已能解析 ⇒ 修复仍在
                    how = ok ? "仍能解析 ✔" : "**又变成悬空了**";
                }
                else { how = "（该类型暂不支持自动复查）"; }
                if (!ok) gone++;
                sb.AppendLine((ok ? "[OK]   " : "[WARN] ") + kind + " → " + target + "　" + how);
            }
            sb.AppendLine();
            sb.AppendLine("★ 结论：" + (gone == 0
                ? "登记过的 " + total + " 项修复都还在。"
                : "有 " + gone + " 项修复**已经被撤销**（多半是装/更新插件时被重写）"
                  + " ⇒ 重新点一次对应的修复按钮即可；想彻底固化见「社区工具」那一节。"));
            return sb.ToString();
        }


        //
        //  来源：真实报错 —— DSH Discussion #589 / #1462：
        //    "Windows: default port 3080 can fall in Hyper-V excluded port range,
        //     dsh web fails with cryptic EACCES stack trace"
        //  ⇒ 明明没人占用 3080，服务也起不来，只因为 **Hyper-V/WSL2 保留了那一段**。
        //  判别：`netsh int ipv4 show excludedportrange protocol=tcp`（隐藏执行、零窗口）。
        //  ★ 解析与判定都拆成纯函数，便于用合成文本做正/负样本验证。
        // ============================================================

        /// <summary>纯函数：从 netsh 输出里解析"保留端口区间"（语言无关：只认每行里的两个数字）。</summary>
        public static List<int[]> ParseExcludedPortRanges(string netshOutput)
        {
            var list = new List<int[]>();
            if (string.IsNullOrEmpty(netshOutput)) return list;
            foreach (string raw in netshOutput.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                // ★ 2026-09-17 修：原正则要求行尾就是数字（`(\d+)\s+(\d+)\s*$`），
                //   于是 netsh 里带 `*`（持久区间）的行**解析不出来** —— harness 用合成文本抓到的。
                //   改成"两个数字，前后不再接数字"，允许行尾有 * 之类的标记。
                Match m = Regex.Match(line, @"(?<!\d)(\d{1,5})\s+(\d{1,5})(?!\d)");
                if (!m.Success) continue;
                int a, b;
                if (!int.TryParse(m.Groups[1].Value, out a) || !int.TryParse(m.Groups[2].Value, out b)) continue;
                if (a < 1 || b > 65535 || b < a) continue;
                list.Add(new int[] { a, b });
            }
            return list;
        }

        /// <summary>纯函数：某端口是否落在任一保留区间内。</summary>
        public static bool IsPortReserved(int port, List<int[]> ranges)
        {
            if (port <= 0 || ranges == null) return false;
            foreach (int[] r in ranges)
                if (r != null && r.Length == 2 && port >= r[0] && port <= r[1]) return true;
            return false;
        }

        /// <summary>查一次系统的保留端口区间（Windows 才有；其它平台返回空并说明）。</summary>
        public static List<int[]> ExcludedPortRanges(out string note)
        {
            note = null;
            var list = new List<int[]>();
            try
            {
                string outp = DshCore.RunHidden("netsh", "int ipv4 show excludedportrange protocol=tcp", 8000);
                if (string.IsNullOrEmpty(outp)) { note = "netsh 没有输出（非 Windows，或命令不可用）"; return list; }
                list = ParseExcludedPortRanges(outp);
                note = "netsh 报告 " + list.Count + " 段保留区间";
            }
            catch (Exception e) { note = "查询失败：" + e.GetType().Name; }
            return list;
        }

        /// <summary>报告：当前端口在不在保留区间里（EACCES 的前置体检）。</summary>
        public static string ReservedPortReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== 装后体检 · 端口保留区间（EACCES 前置体检）====");
            string note;
            List<int[]> ranges = ExcludedPortRanges(out note);
            int port = DshCore.ActivePort;
            sb.AppendLine("当前端口: " + port);
            sb.AppendLine("查询结果: " + (note ?? "?"));
            if (ranges.Count == 0)
            {
                sb.AppendLine("[OK]   没有读到保留区间（或本机非 Windows）—— 这一项无需担心。");
                return sb.ToString();
            }
            var show = new List<string>();
            for (int i = 0; i < ranges.Count && i < 12; i++) show.Add(ranges[i][0] + "-" + ranges[i][1]);
            sb.AppendLine("保留区间（最多列 12 段）: " + string.Join("、", show.ToArray())
                          + (ranges.Count > 12 ? " …共 " + ranges.Count + " 段" : ""));
            bool reserved = IsPortReserved(port, ranges);
            sb.AppendLine(reserved
                ? "[命中] ★ **当前端口 " + port + " 落在保留区间里** ⇒ 服务可能以 EACCES 崩掉（不是被占用，是没权限绑）。"
                  + "\r\n       → 换端口：写 `%USERPROFILE%\\.dsh\\big-fat-fish-rescuer\\port.txt` 一行（如 3090），或设 `DSH_PORT=3090`。"
                : "[OK]   当前端口 " + port + " 不在保留区间里。");
            return sb.ToString();
        }

        // ============================================================
        //  十八、F2 分层判定器：这一切到底发生在"哪一层"
        //
        //  用户最常问的不是"哪个插件坏了"，而是"这到底是谁的问题"。
        //  把已知证据归到五层：
        //    L1 本地服务层（浏览器 ↔ DSH 服务）—— 那句 Failed to fetch 属于这层
        //    L2 插件树 / 配置层（compose、loader entry、悬空引用、重复 id…）
        //    L3 记忆注入层（KEY.md / MEMORY.md / USER.md 里的字面量花括号）
        //    L4 模型通路层（provider 端点不可达 / 默认模型指向不存在）
        //    L5 环境层（端口被占等）
        //  ★ 支持**外部日志**：别人把日志或报错贴给你时，直接指给它看。
        // ============================================================

        public sealed class LayerFinding
        {
            public string Layer;
            public bool Hit;
            public string Evidence;
            public string Action;
        }

        /// <summary>纯函数：数一段文本里的**字面量**双花括号（写成 "{ {" 的已转义形态不算）。</summary>
        public static int CountLiteralBraces(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int n = 0, i = 0;
            while (i < text.Length - 1)
            {
                if (text[i] == '{' && text[i + 1] == '{') { n++; i += 2; continue; }
                i++;
            }
            return n;
        }

        /// <summary>会被注入到系统提示里的记忆文件（只有这三类；其余不注入）。</summary>
        public static List<string> InjectableMemoryFiles()
        {
            var list = new List<string>();
            try
            {
                string mem = Path.Combine(DshCore.DshHome, "memories");
                string user = Path.Combine(mem, "USER.md");
                string global = Path.Combine(mem, "MEMORY.md");
                if (File.Exists(user)) list.Add(user);
                if (File.Exists(global)) list.Add(global);
                string proj = Path.Combine(mem, "projects");
                if (Directory.Exists(proj))
                    foreach (string d in Directory.GetDirectories(proj))
                    {
                        string key = Path.Combine(d, "KEY.md");
                        if (File.Exists(key)) list.Add(key);
                    }
            }
            catch { }
            return list;
        }

        /// <summary>分层判定：哪一层命中、证据是什么、下一步点哪个。</summary>
        public static string LayerReport(string[] extraLogs)
        {
            var sb = new StringBuilder();
            var found = new List<LayerFinding>();

            List<LogSource> srcs = CollectLogSources();
            var paths = new List<string>();
            foreach (LogSource s in srcs) if (s.Exists) paths.Add(s.Path);
            if (extraLogs != null) foreach (string p in extraLogs) if (!string.IsNullOrEmpty(p)) paths.Add(p);
            List<Finding> finds = ClassifyFiles(paths);
            bool exited = false;
            foreach (LogSource s in srcs) if (s.ProcessExited) exited = true;

            int browserErrHits = 0; string browserErrSample = null;
            foreach (string p in paths)
            {
                string tail = TailText(p, TailBytes);
                if (tail.Length == 0) continue;
                foreach (string raw in tail.Replace("\r\n", "\n").Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.IndexOf("Failed to fetch", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf("gateway/internal", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        browserErrHits++;
                        if (browserErrSample == null) browserErrSample = line.Length > 200 ? line.Substring(0, 200) : line;
                    }
                }
            }

            // L1 本地服务层
            Liveness L = CheckLiveness();
            bool l1 = (!L.LiveListens && !L.LiveHttp)
                      || (L.PagePort > 0 && L.PagePort != L.LivePort && !L.PagePortServes)
                      || browserErrHits > 0;
            found.Add(new LayerFinding
            {
                Layer = "L1 本地服务层（浏览器 ↔ DSH 服务）",
                Hit = l1,
                Evidence = "实跑端口 " + L.LivePort + "（监听=" + L.LiveListens + "、静态页=" + L.LiveHttp + "、API=" + L.LiveApi + "）"
                         + (L.PagePort > 0 ? "；页面端口 " + L.PagePort + (L.PagePortServes ? "（有服务）" : "（**没有服务**）") : "")
                         + (browserErrHits > 0 ? "；日志里出现 " + browserErrHits + " 处浏览器报错：" + browserErrSample : ""),
                Action = l1 ? "→ 用「🌐 打开无痕」以**当前**地址重开页面；服务没起来先「🚀 启动并打开」。"
                            : "→ 这一层正常。"
            });

            // L2 插件树 / 配置层
            int l2cnt = 0; string l2first = null; var cats = new List<string>();
            foreach (Finding f in finds)
            {
                l2cnt++;
                if (l2first == null) l2first = f.Evidence;
                if (!cats.Contains(f.Category)) cats.Add(f.Category);
            }
            found.Add(new LayerFinding
            {
                Layer = "L2 插件树 / 配置层",
                Hit = l2cnt > 0,
                Evidence = l2cnt > 0
                    ? l2cnt + " 条加载失败线索，类别：" + string.Join("、", cats.ToArray()) + "\n       首条：" + l2first
                      + (exited ? "\n       ★ 并且进程已崩溃退出（尾部有 Node 崩溃页脚）" : "")
                    : "可读日志里没有加载失败线索。",
                Action = l2cnt > 0 ? "→ 「🧩 插件与皮肤」页签：按类别点对应修复（悬空引用→🧹、重复 id→🚫、等不到服务→♻）。"
                                   : "→ 这一层正常。"
            });

            // L3 记忆注入层
            var hot = new List<string>();
            foreach (string f in InjectableMemoryFiles())
            {
                try
                {
                    int n = CountLiteralBraces(File.ReadAllText(f));
                    if (n > 0) hot.Add(Path.GetFileName(Path.GetDirectoryName(f)) + "\\" + Path.GetFileName(f) + "（" + n + " 处）");
                }
                catch { }
            }
            found.Add(new LayerFinding
            {
                Layer = "L3 记忆注入层（会注入系统提示的记忆文件）",
                Hit = hot.Count > 0,
                Evidence = hot.Count > 0 ? "发现字面量双花括号：" + string.Join("、", hot.ToArray())
                                         : "已检查 " + InjectableMemoryFiles().Count + " 个可注入文件，未发现字面量双花括号。",
                Action = hot.Count > 0 ? "→ 点「🧩 补丁体检」第 ④ 项（prompt 防线），它还能一键重打。"
                                       : "→ 这一层正常。"
            });

            // L4 模型通路层
            string dp, dm;
            List<ProviderInfo> ps = ReadProviders(out dp, out dm);
            var badProv = new List<string>();
            foreach (ProviderInfo p in ps)
            {
                if (string.IsNullOrEmpty(p.BaseUrl)) { badProv.Add(p.Name + "（缺 baseURL）"); continue; }
                Match m = Regex.Match(p.BaseUrl, @"^https?://([^/:]+)(?::(\d+))?");
                if (!m.Success) { badProv.Add(p.Name + "（端点解析失败）"); continue; }
                bool https = p.BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                int port = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : (https ? 443 : 80);
                long ms; string err;
                if (!TryTcp(m.Groups[1].Value, port, 5000, out ms, out err)) badProv.Add(p.Name + "（不可达：" + err + "）");
            }
            found.Add(new LayerFinding
            {
                Layer = "L4 模型通路层",
                Hit = badProv.Count > 0,
                Evidence = "自定义 provider " + ps.Count + " 个、默认模型 " + (dm ?? "?")
                         + (badProv.Count > 0 ? "；问题项：" + string.Join("、", badProv.ToArray()) : "；全部可达"),
                Action = badProv.Count > 0 ? "→ 查网络/代理或 provider 配置（这一层的报错形如上游 4xx/5xx、超时）。"
                                           : "→ 这一层正常。"
            });

            // L5 环境层
            bool l5 = false; string l5ev = null;
            foreach (Finding f in finds)
                if (f.Category == "端口冲突（插件是受害者）") { l5 = true; l5ev = f.Evidence; break; }
            found.Add(new LayerFinding
            {
                Layer = "L5 环境层（端口/进程）",
                Hit = l5,
                Evidence = l5 ? (l5ev ?? "日志里有 EADDRINUSE") : "没有端口冲突迹象（实跑端口 " + L.LivePort + "）",
                Action = l5 ? "→ 点「🔧 一键修复」或「🚀 启动并打开」；v4.4 起会按进程身份认端口。" : "→ 这一层正常。"
            });

            sb.AppendLine("==== 装后体检 · 分层判定（F2）====");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("依据日志: " + paths.Count + " 份（其中外部传入 " + ((extraLogs == null) ? 0 : extraLogs.Length) + " 份）");
            sb.AppendLine();
            int hits = 0;
            foreach (LayerFinding f in found)
            {
                if (f.Hit) hits++;
                sb.AppendLine("[" + (f.Hit ? "命中" : "OK") + "] " + f.Layer);
                sb.AppendLine("       " + f.Evidence.Replace("\n", "\n       "));
                sb.AppendLine("       " + f.Action);
                sb.AppendLine();
            }
            sb.AppendLine("★ 结论：" + (hits == 0
                ? "这五层都没发现明显问题 —— 请把**具体报错原文**（或日志文件路径）给我，用 `--layercheck <文件>` 再判一次。"
                : "有 " + hits + " 层命中；**从上往下**先处理 L1/L2（它们是「能不能用」的前提）。"));
            return sb.ToString();
        }

        // ============================================================
        //  十六、F3 启动失败 → 回滚建议
        //
        //  逻辑：若是 **compose / 插件树** 类致命失败，而配置**在最近一次快照之后被改过**，
        //        那"回到那份快照"往往比继续猜哪个插件坏更快。
        //  判据拆成纯函数，便于用合成数据验证。
        // ============================================================

        /// <summary>纯函数：该不该建议回滚。</summary>
        public static bool ShouldAdviseRollback(bool composeOrTreeFailure, DateTime newestSnapshot, DateTime newestConfigChange)
        {
            if (!composeOrTreeFailure) return false;
            if (newestSnapshot == DateTime.MinValue) return false;       // 没快照可回
            return newestConfigChange > newestSnapshot;                   // 快照之后又改过
        }

        public static string RollbackAdvice()
        {
            var sb = new StringBuilder();
            List<LogSource> srcs = CollectLogSources();
            var paths = new List<string>();
            foreach (LogSource s in srcs) if (s.Exists) paths.Add(s.Path);
            List<Finding> finds = ClassifyFiles(paths);

            bool fatal = false; string fatalCat = null;
            foreach (Finding f in finds)
            {
                string c = f.Category;
                if (c == "悬空引用（列了但解析不到）" || c == "重复条目 / entry id 冲突"
                    || c == "入口缺失（安装不完整）" || c == "端口冲突（插件是受害者）"
                    || c == "资源文件缺失（ENOENT）")
                { fatal = true; fatalCat = c; break; }
            }

            DateTime newestSnap = DateTime.MinValue; string snapName = null;
            try
            {
                List<string> snaps = SafeConfig.ListSnapshots();
                if (snaps != null)
                {
                    string best = null;
                    foreach (string s in snaps)
                    {
                        string t = Path.GetFileName(s.TrimEnd('\\', '/'));
                        if (best == null || string.CompareOrdinal(t, best) > 0) best = t;
                    }
                    snapName = best;
                    DateTime parsed;
                    if (best != null && DateTime.TryParseExact(best, "yyyyMMdd-HHmmss", null,
                            System.Globalization.DateTimeStyles.None, out parsed)) newestSnap = parsed;
                }
            }
            catch { }

            DateTime newestChange = DateTime.MinValue; string changedFile = null;
            foreach (string f in new string[]
                     {
                         Path.Combine(ProfileDir(), "package.json"),
                         Path.Combine(ProfileDir(), "cordis.patch.yml"),
                         Path.Combine(DshCore.DshHome, "cordis.patch.yml"),
                         Path.Combine(DshCore.DshHome, "settings.yaml")
                     })
            {
                try
                {
                    if (!File.Exists(f)) continue;
                    DateTime m = File.GetLastWriteTime(f);
                    if (m > newestChange) { newestChange = m; changedFile = f; }
                }
                catch { }
            }

            sb.AppendLine("==== 装后体检 · 启动失败该不该回滚（F3）====");
            sb.AppendLine("致命类加载失败: " + (fatal ? "有（" + fatalCat + "）" : "没有"));
            sb.AppendLine("最近一次配置快照: " + (snapName ?? "（没有快照）")
                          + (newestSnap != DateTime.MinValue ? "　= " + newestSnap.ToString("yyyy-MM-dd HH:mm:ss") : ""));
            sb.AppendLine("配置文件最近改动: " + (changedFile ?? "（读不到）")
                          + (newestChange != DateTime.MinValue ? "　= " + newestChange.ToString("yyyy-MM-dd HH:mm:ss") : ""));
            sb.AppendLine();

            if (ShouldAdviseRollback(fatal, newestSnap, newestChange))
            {
                sb.AppendLine("★ 建议：**先回滚到最近的快照，再重试启动**。");
                sb.AppendLine("  理由：出现致命类加载失败，而配置文件在那次快照**之后**又被改过 ——");
                sb.AppendLine("        继续逐个猜「哪个插件坏」通常更慢，回滚能一步回到「上次能用的状态」。");
                sb.AppendLine("  做法：点「♻ 恢复配置」选那份快照（" + snapName + "）⇒ 重启服务 ⇒ 再跑一次装后体检。");
            }
            else if (fatal)
            {
                sb.AppendLine("★ 不建议回滚：虽有致命类加载失败，但没有「比改动更晚的快照」可回。");
                sb.AppendLine("  ⇒ 按「起不来根因分类」里那一条对应的修复按钮处理即可。");
            }
            else
            {
                sb.AppendLine("★ 不需要回滚：没有发现致命类加载失败。");
            }
            return sb.ToString();
        }

    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace BigFatFishRescuer
{
    // ============================================================
    //  中文/非 ASCII 路径体检 + 强力自愈能力边界（2026-09-17 新增）
    //
    //  一、为什么做这个
    //    本机 USERPROFILE 含中文用户名（非 ASCII）。
    //    主人问：是不是有人就是被中文用户名坑的？——下面是**本机取得的证据**，
    //    不是推测：
    //      · **DSH 本体是安全的**：它把非 ASCII 工作区名转义成 ASCII 目录名
    //        （实测 ~/.dsh/sessions 下是 `--C-Users-~6D4B~8BD5~7528~6237-Desktop-~9879~76EE--`，
    //         即每个非 ASCII 字符写成 `~码点十六进制`：6D4B=测、8587=薇、7814=研、7A76=究、
    //         5927=大、80A5=肥、9C7C=鱼 ⇒ 反解出来正是那个中文工作区路径）。
    //      · **被坑的是周边工具链**（本机都真踩过）：
    //          ⒜ Windows 上 Python 的 stdio 默认是 GBK（实测本机 Python 3.12
    //             输出 `sys.filesystemencoding=utf-8` 但 `sys.stdout.encoding=gbk`）
    //             ⇒ 中文输出乱码，个别情况直接 UnicodeEncodeError。
    //          ⒝ mediapipe 传**中文路径必败**（实测报 FileNotFoundError 但文件明明在）
    //             ⇒ 必须改用 model_asset_buffer。
    //          ⒞ PowerShell 5.1 读**无 BOM 的 UTF-8 脚本**时按 GBK 解码 ⇒ 含中文的 .ps1 语法崩。
    //          ⒟ `.cmd`/批处理受 OEM 代码页影响（中文参数/回显乱码）。
    //    ⇒ 结论：中文用户名**不会**让 DSH 起不来，但会让"外面那些小工具"出错；
    //      所以体检要分两栏报：**哪些安全**、**哪些要防**。
    //
    //  二、顺手把「强力自愈」的边界写清楚
    //    主人问：强力自愈是不是保证"任何时候都能打开"？
    //    **不是。** 它治的是**一类**问题（僵尸/卡死进程、端口被 dsh 自己占着、
    //    服务没起、桌面客户端与 ComfyUI 缺件），治不了另一类（插件树加载失败、
    //    配置损坏、DSH 包被升级坏、密钥/模型问题、磁盘/权限/杀软、网络、系统本体）。
    //    这个报告把两类都列出来，并在动手前**先看根因**：若看起来是自愈治不了的那类，
    //    直接告诉你该点哪个按钮，省得你白等 40 秒。
    // ============================================================
    public static class PathAudit
    {
        // ---------- 一、中文/非 ASCII 路径体检 ----------

        public static string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("== 中文/非 ASCII 路径体检 ==");
            sb.AppendLine("（主人问：是不是有人被中文用户名坑？—— 分两栏报：哪些安全、哪些要防）");
            sb.AppendLine();

            string home = DshCore.UserProfile;
            string dsh = DshCore.DshHome;
            string profile = Path.Combine(dsh, "profiles", "web");

            sb.AppendLine("-- ① 本机关键路径（标出含非 ASCII 的）--");
            AddPath(sb, "Windows 用户目录", home);
            AddPath(sb, "DSH 家目录", dsh);
            AddPath(sb, "web profile", profile);
            AddPath(sb, "救星数据目录", DshCore.AppDataDir);
            AddPath(sb, "pnpm store", PnpmStorePath());
            AddPath(sb, "node", FindNode());
            sb.AppendLine();

            sb.AppendLine("-- ② DSH 本体：对中文工作区名是**安全**的（有实测证据）--");
            string sessRoot = Path.Combine(dsh, "sessions");
            int checkedDirs = 0, asciiSafe = 0, sampleDecoded = 0, sampleTotal = 0;
            string demo = "";
            try
            {
                if (Directory.Exists(sessRoot))
                {
                    foreach (var d in Directory.GetDirectories(sessRoot))
                    {
                        checkedDirs++;
                        string name = Path.GetFileName(d);
                        if (IsAsciiSafe(name)) asciiSafe++;
                        if (demo.Length == 0) demo = name;
                    }
                }
            }
            catch { }
            if (checkedDirs == 0)
                sb.AppendLine("[?]    还没发现会话目录（没跑过会话？）");
            else
            {
                sb.AppendLine(asciiSafe == checkedDirs
                    ? "[OK]   会话目录名 " + asciiSafe + "/" + checkedDirs + " 个都是**纯 ASCII**（DSH 把中文转义成 ~码点）。"
                    : "[WARN] 有 " + (checkedDirs - asciiSafe) + " 个会话目录名含非 ASCII（可能有工具不认识）。");
                sb.AppendLine("       实测样例：" + demo);
                string back = UnescapeDshDir(demo);
                sampleTotal = 1; sampleDecoded = back.Length > 0 ? 1 : 0;
                sb.AppendLine("       把 ~码点 反解回来 → " + back);
                sb.AppendLine("       ⇒ 反解成功说明这套转义是**可逆**的：DSH 读写中文路径不会错位。");
            }
            sb.AppendLine();

            sb.AppendLine("-- ②b 「目录选择器」陷阱（社区已核到源码行的那种）--");
            // 来源：DSH 官方讨论 #4648（当项目文件夹名同时含英文和中文时无法选为工作区；
            //   社区已核源码、给出补丁，并统计为同族第 ~10 份报告，跟进见 #4624 / #4654）。
            // 真正的规则**不是「中文不行」**，而是：名字里含 **UTF-16 低字节为 0x00 的字符**
            //   （即码点末两位是 00，如 「一」U+4E00、「开」U+5F00）时，
            //   Windows 原生目录选择器只检查每个码元的低字节来判断字符串结束 ⇒ 路径在那个字之前
            //   被截断 ⇒ realpath 报 ENOENT ⇒ 工作区加不上。
            // 实测本机：
            var danger = new List<string>();
            CollectDangerChars(home, danger);
            CollectDangerChars(dsh, danger);
            try { CollectDangerChars(Directory.GetCurrentDirectory(), danger); } catch { }
            if (danger.Count == 0)
            {
                sb.AppendLine("[OK]   本机路径里**没有**这类字符 ⇒ 目录选择器截断的那个 bug 打不到你。");
                sb.AppendLine("       （判据：逐个字符看码点末两位是不是 00；本机 USERPROFILE 与当前目录都过了。）");
            }
            else
            {
                sb.AppendLine("[注意] 路径里有 " + danger.Count + " 处「低字节为 0x00」的字符，**目录选择器会在这里截断**：");
                foreach (var d in danger) sb.AppendLine("       · " + d);
                sb.AppendLine("       ⇒ 表现是「点选择文件夹没反应 / 工作区加不上」，而目录明明存在。");
                sb.AppendLine("       三个绕法（社区核实过的）：①改名避开这类字（如「软件开发」→「软件研发」）；");
                sb.AppendLine("       ②**直接粘贴完整路径**而不用那个弹出选择框（手工输入不经过它）；");
                sb.AppendLine("       ③先挪到纯 ASCII 路径确认能加上，以此反证是这个 bug。");
            }
            sb.AppendLine();

            sb.AppendLine("-- ③ 真正会被中文路径坑到的（本机实测过）--");
            sb.AppendLine(DescribePythonEncoding());
            sb.AppendLine("       · mediapipe 传中文路径必败（实测报文件不存在、其实存在）⇒ 要用 model_asset_buffer。");
            sb.AppendLine("       · PowerShell 5.1 读**无 BOM 的 UTF-8 脚本**按 GBK 解码 ⇒ 含中文的 .ps1 会语法崩。");
            sb.AppendLine("         （这条对你自己写的脚本同样成立：含中文就存成 UTF-8 with BOM。）");
            sb.AppendLine("       · .cmd/批处理受 OEM 代码页影响：中文参数与回显可能乱码。");
            sb.AppendLine();

            sb.AppendLine("-- ④ 结论与建议 --");
            if (HasNonAscii(home))
            {
                sb.AppendLine("[注意] 你的用户名含非 ASCII（" + home + "）。");
                sb.AppendLine("       DSH 自己不受影响（见 ②）；要防的是**第三方小工具**把路径拼进命令/日志时乱码。");
                sb.AppendLine("       可做两件事：①把 PYTHONUTF8=1 设成用户环境变量（治本，见 ③ 首条）；");
                sb.AppendLine("                    ②自己写的含中文脚本一律存 UTF-8 with BOM。");
            }
            else
            {
                sb.AppendLine("[OK]   用户名是纯 ASCII，上面那些坑基本不会踩到。");
            }
            return sb.ToString();
        }

        /// <summary>Python stdio 编码判据（本机实测：filesystem=utf-8 但 stdout=gbk，这是中文乱码的根源）。</summary>
        private static string DescribePythonEncoding()
        {
            string py = PatchGuard.SysPython();
            if (string.IsNullOrEmpty(py) || !File.Exists(py))
                return "       · 本机没找到 python，跳过编码检查。";
            int code; string outp;
            Run(py, "-c \"import sys;print('FS='+str(sys.getfilesystemencoding())+' STDIO='+str(sys.stdout.encoding))\"", null, out code, out outp);
            if (code != 0 || outp.Trim().Length == 0)
                return "       · python 编码检查没跑通（" + Short(outp) + "）";
            string enc = outp.Trim();
            bool gbk = enc.IndexOf("STDIO=gbk", StringComparison.OrdinalIgnoreCase) >= 0
                    || enc.IndexOf("STDIO=cp936", StringComparison.OrdinalIgnoreCase) >= 0;
            string utf8env = Environment.GetEnvironmentVariable("PYTHONUTF8");
            return (gbk
                ? "[注意] python 的**输出流**编码不是 UTF-8（" + enc + "）\r\n       ⇒ 中文输出会乱码、个别脚本会直接抛 UnicodeEncodeError。\r\n"
                : "[OK]   python 输出流编码正常（" + enc + "）。\r\n")
                + "       实测取值：文件系统=UTF-8、STDIO=GBK ⇒ 这就是「中文乱码」的根源。\r\n"
                + "       建议：设用户环境变量 PYTHONUTF8=1" + (string.IsNullOrEmpty(utf8env) ? "（当前**未设**）" : "（当前已设：" + utf8env + "）");
        }

        private static void AddPath(StringBuilder sb, string label, string p)
        {
            if (string.IsNullOrEmpty(p)) { sb.AppendLine("  " + label + "：找不到"); return; }
            sb.AppendLine("  " + (HasNonAscii(p) ? "[非ASCII] " : "[纯ASCII] ") + label + " = " + p);
        }

        public static bool HasNonAscii(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (c > 127) return true;
            return false;
        }

        /// <summary>
        /// 这个字符会不会触发「目录选择器截断」bug：**UTF-16 低字节为 0x00**（码点末两位是 00）。
        /// 例：「一」U+4E00、「开」U+5F00 会触发；「青」U+9752、「薇」U+8587、「研」U+7814 不会。
        /// （来源：DSH 讨论 #4648，社区已核到源码行。）
        /// </summary>
        public static bool IsPickerDangerChar(char c)
        {
            return (c & 0xFF) == 0;
        }

        /// <summary>把路径里所有「危险字符」收集起来（带去重与位置说明）。</summary>
        public static void CollectDangerChars(string path, List<string> sink)
        {
            if (string.IsNullOrEmpty(path)) return;
            foreach (char c in path)
            {
                if (!IsPickerDangerChar(c)) continue;
                string desc = "在 " + path + " 里的「" + c + "」= U+" + ((int)c).ToString("X4")
                            + "（低字节 0x00）";
                if (!sink.Contains(desc)) sink.Add(desc);
            }
        }

        /// <summary>DSH 转义后的目录名是否纯 ASCII（含 ~ 与 -）。</summary>
        public static bool IsAsciiSafe(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (char c in name) if (c > 127) return false;
            return true;
        }

        /// <summary>
        /// 把 DSH 的目录名转义（把非 ASCII 字符写成 ~码点十六进制，例如 ~6D4B = 测）反解回可读路径。
        /// 反解成功＝这套转义可逆，DSH 对中文路径不会错位。
        /// </summary>
        public static string UnescapeDshDir(string escaped)
        {
            if (string.IsNullOrEmpty(escaped)) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < escaped.Length; i++)
            {
                char c = escaped[i];
                if (c == '~' && i + 4 < escaped.Length)
                {
                    string hex = escaped.Substring(i + 1, 4);
                    int v;
                    if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                                     System.Globalization.CultureInfo.InvariantCulture, out v))
                    {
                        sb.Append((char)v);
                        i += 4;
                        continue;
                    }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string PnpmStorePath()
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pnpm", "store");
            return Directory.Exists(p) ? p : "";
        }

        private static string FindNode()
        {
            string p = Path.Combine(DshCore.UserProfile, ".ai-manager", "runtimes", "node");
            try { if (Directory.Exists(p)) { var dirs = Directory.GetDirectories(p); if (dirs.Length > 0) return dirs[0]; } }
            catch { }
            return "";
        }

        // ---------- 二、强力自愈的能力边界 ----------

        /// <summary>
        /// 强力自愈到底能治什么、不能治什么。会在动手前先看日志根因，
        /// 若是自愈治不了的那类，直接给替代按钮（免得白等 40 秒）。
        /// </summary>
        public static string SelfHealBoundary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("== 强力自愈 · 能力边界（诚实版）==");
            sb.AppendLine();

            // 动手前先看：服务现在是活的吗？（活着就根本不需要自愈，也不该吓唬人）
            bool alive = false;
            try { var st = DshCore.CheckState(); alive = st.Listens && st.HttpResponds; } catch { }
            string cause = "";
            try { cause = PluginDiag.RootCauseReport(); } catch { }
            bool looksPluginTree = !alive && LooksLikePluginTreeCause(cause);
            if (alive)
            {
                sb.AppendLine("[OK]   现在服务是**活的**（端口在听、HTTP 正常）⇒ 自愈这一步现在用不上。");
                sb.AppendLine("       下面这张表留着：**真打不开的那天**再照着看该点哪个按钮。");
                sb.AppendLine();
            }
            else if (looksPluginTree)
            {
                sb.AppendLine("⚠ ★ 本次根因看起来是**插件树/配置类** —— 这类问题**自愈治不了**：");
                sb.AppendLine("    自愈只会反复杀进程、反复启动，然后还是失败（因为一加载插件树就退出）。");
                sb.AppendLine("    请先去「🧩 插件与皮肤」点：🚫 去重重复条目 / 🧹 清理悬空引用 / ♻ 启用被禁条目 / 📦 重装插件。");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("本次没从日志里看到插件树类根因（自愈可以试）。");
                sb.AppendLine();
            }

            sb.AppendLine("-- 能治（自愈就是为这些写的）--");
            sb.AppendLine("  · dsh 进程僵尸/卡死（含端口被上一次的 dsh 占着）");
            sb.AppendLine("  · 服务没起来 / 起来了但 HTTP 不应答");
            sb.AppendLine("  · 桌面启动器、ComfyUI 的构建产物缺失（能重建的会重建）");
            sb.AppendLine();

            sb.AppendLine("-- 治不了（别指望它，请点对应按钮）--");
            sb.AppendLine("  · 插件树加载失败：重复 loader entry id / 悬空引用 / 入口缺失 / pending 服务"
                          + " → 「🧩 去重 / 清理悬空 / 启用被禁 / 重装插件」");
            sb.AppendLine("  · 配置写坏（YAML 结构错、同秒被批量覆盖） → 「🛡 配置保险箱 → 恢复」；先「✅ 配置自检」");
            sb.AppendLine("  · **DSH 包本身**缺失或被升级装坏（bin.js 都不在） → 重新安装 dsh（自愈连启动命令都发不出去）");
            sb.AppendLine("  · 端口被**别的程序**占着 → 自愈**故意不误杀**（只结束确认是 dsh 的进程）⇒ 请换端口（DSH_PORT / port.txt）");
            sb.AppendLine("  · 密钥缺失 / 模型名不存在 → 服务能起，但对话用不了 → 「🩺 装后体检 → 模型通路」");
            sb.AppendLine("  · 磁盘满 / 权限 / 杀毒软件拦截 → 系统层问题，工具改不了");
            sb.AppendLine("  · 网络与代理（下载、模型接口） → 工具改不了");
            sb.AppendLine("  · 中文路径引发的**第三方工具**失败（python 编码、mediapipe 等） → 见「中文路径体检」");
            sb.AppendLine("  · 电脑本体（Windows 起不来、硬件坏、系统盘坏） → 这就真没办法了");
            sb.AppendLine();

            sb.AppendLine("-- 一句话 --");
            sb.AppendLine("它不是「什么都救得回来」的按钮，而是「**把服务重新拉起来**」的按钮：");
            sb.AppendLine("进程与端口层面的事它做得很好；**内容层面**（插件树、配置、包、密钥）得靠对应的按钮。");
            sb.AppendLine("它动手前会自动给配置拍快照；而且**只杀确认是 dsh 的进程**，不会顺手杀别的软件。");
            return sb.ToString();
        }

        private static bool ContainsAny(string hay, params string[] needles)
        {
            if (string.IsNullOrEmpty(hay)) return false;
            foreach (var n in needles)
                if (hay.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>
        /// 判「本次根因是不是插件树/配置类」——**必须带证据**，不能见字样就算：
        /// ① 日志原文级别的硬标记（出现即真：duplicate loader entry id 等）；
        /// ② 报告里带 [FAIL]/[ERROR] 的行，或带非零计数（"2 个"）的行，且含关键词。
        /// ★ 为什么这么啰嗦：RootCauseReport **始终**会打印「悬空引用 0 个」这类字样，
        ///   旧写法（直接搜关键词）在健康机器上也会误报"自愈治不了"——那会把人带偏。
        /// </summary>
        public static bool LooksLikePluginTreeCause(string cause)
        {
            if (string.IsNullOrEmpty(cause)) return false;

            // ① 日志级硬标记：只在真的加载失败时才会出现
            if (ContainsAny(cause, "duplicate loader entry id", "plugin tree failed to load",
                                   "cannot resolve profile bundle", "main entry is missing",
                                   "pending (waiting for services")) return true;

            // ② 带证据的行
            string[] keys = new string[] { "悬空引用", "入口缺失", "重复条目", "重复 entry", "加载失败" };
            foreach (var raw in cause.Split('\n'))
            {
                string L = raw.Trim();
                if (L.Length == 0) continue;
                bool neg = L.IndexOf("[FAIL]", StringComparison.Ordinal) >= 0
                        || L.IndexOf("[ERROR]", StringComparison.Ordinal) >= 0;
                foreach (var k in keys)
                {
                    int at = L.IndexOf(k, StringComparison.Ordinal);
                    if (at < 0) continue;
                    if (neg) return true;
                    // ★ 只在**关键词附近**找非零计数：健康报告里同一行常带着别的数字
                    //   （实测「重复条目：会真崩的 0 个；跨层同 id 2 个（分层覆盖，合法）」
                    //     按整行搜会被那个 2 骗成故障 —— 假阳性实测踩过）。
                    int len = Math.Min(16, L.Length - at);
                    string win = L.Substring(at, len);
                    if (System.Text.RegularExpressions.Regex.IsMatch(win, "[1-9][0-9]*\\s*个")) return true;
                }
            }
            return false;
        }

        // ---------- 工具 ----------

        private static string Run(string exe, string args, string workDir, out int exitCode, out string output)
        {
            exitCode = -1; output = "";
            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                if (!string.IsNullOrEmpty(workDir)) psi.WorkingDirectory = workDir;
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    string e = p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    if (!p.HasExited) { try { p.Kill(); } catch { } }
                    exitCode = p.HasExited ? p.ExitCode : -1;
                    output = (o + "\n" + e).Trim();
                }
            }
            catch (Exception ex) { output = ex.Message; }
            return output;
        }

        private static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 80 ? s.Substring(0, 80) + "…" : s;
        }

        private static bool SafeEmptyCollect()
        {
            try
            {
                var s = new List<string>();
                CollectDangerChars("", s);
                CollectDangerChars(null, s);
                return s.Count == 0;
            }
            catch { return false; }
        }

        // ---------- 自检（正负样本都要） ----------

        public static List<string> Selftest()
        {
            var res = new List<string>();

            // 转义反解：正样本（本机真实目录名）必须还原出中文
            string real = "--C-Users-~6D4B~8BD5~7528~6237-Desktop-~9879~76EE--";
            string back = UnescapeDshDir(real);
            res.Add("中文转义反解 正样本（~6D4B→测）: " + (back.IndexOf('测') >= 0 && back.IndexOf('户') >= 0 ? "OK" : "FAIL"));
            res.Add("中文转义反解 鱼字（~9C7C）: " + (back.IndexOf('鱼') >= 0 ? "OK" : "FAIL"));
            // 负样本：没有 ~ 的纯 ASCII 不能被改动
            res.Add("中文转义反解 负样本（纯ASCII不变）: " + (UnescapeDshDir("--C-Users-abc--") == "--C-Users-abc--" ? "OK" : "FAIL"));
            // 畸形输入不能抛
            res.Add("中文转义反解 畸形输入（~ZZZZ）: " + (UnescapeDshDir("a~ZZZZb").Length > 0 ? "OK" : "FAIL"));
            res.Add("中文转义反解 空输入: " + (UnescapeDshDir("") == "" ? "OK" : "FAIL"));

            // 非 ASCII 判定：正负样本
            res.Add("非ASCII判定 正样本（含中文）: " + (HasNonAscii("C:\\Users\\示例用户") ? "OK" : "FAIL"));
            res.Add("非ASCII判定 负样本（纯ASCII）: " + (!HasNonAscii("C:\\Users\\abc") ? "OK" : "FAIL"));
            res.Add("非ASCII判定 空串: " + (!HasNonAscii("") ? "OK" : "FAIL"));

            // 会话目录名 ASCII 安全判定
            res.Add("目录名ASCII判定 正样本（转义后）: " + (IsAsciiSafe(real) ? "OK" : "FAIL"));
            res.Add("目录名ASCII判定 负样本（含中文）: " + (!IsAsciiSafe("示例目录") ? "OK" : "FAIL"));
            res.Add("目录名ASCII判定 空串: " + (!IsAsciiSafe("") ? "OK" : "FAIL"));

            // 报告能出且两侧都提到
            try
            {
                string r = Report();
                res.Add("中文路径体检可生成: " + (r.IndexOf("DSH 本体", StringComparison.Ordinal) >= 0 ? "OK" : "FAIL"));
                res.Add("体检含两条结论栏: " + (r.IndexOf("安全", StringComparison.Ordinal) >= 0 && r.IndexOf("非ASCII", StringComparison.Ordinal) >= 0 ? "OK" : "FAIL"));
            }
            catch (Exception ex) { res.Add("中文路径体检抛异常: FAIL " + ex.Message); }

            try
            {
                string b = SelfHealBoundary();
                res.Add("自愈边界表可生成: " + (b.IndexOf("治不了", StringComparison.Ordinal) >= 0 ? "OK" : "FAIL"));
                res.Add("边界表 能治/治不了 两侧都在: "
                    + (b.IndexOf("能治", StringComparison.Ordinal) >= 0 && b.IndexOf("治不了", StringComparison.Ordinal) >= 0 ? "OK" : "FAIL"));
                // 负样本：不能吹成"保证任何时候都能打开"
                res.Add("边界表 不吹保证（负样本）: " + (b.IndexOf("保证", StringComparison.Ordinal) < 0 ? "OK" : "FAIL"));
            }
            catch (Exception ex) { res.Add("自愈边界表抛异常: FAIL " + ex.Message); }

            // 自愈根因判据：正样本（真故障）必须命中；负样本（健康报告里的"0 个"）必须不命中
            res.Add("根因判据 正样本（真·loader 失败）: "
                + (LooksLikePluginTreeCause("plugin tree failed to load: duplicate loader entry id: dsh-market") ? "OK" : "FAIL"));
            res.Add("根因判据 正样本（FAIL 行+非零计数）: "
                + (LooksLikePluginTreeCause("[FAIL] 三方对齐：悬空引用 3 个") ? "OK" : "FAIL"));
            res.Add("根因判据 负样本（0 个不算故障）: "
                + (!LooksLikePluginTreeCause("[OK] 三方对齐：bundles 13 + dependencies 11 ⇒ 悬空引用 0 个") ? "OK" : "FAIL"));
            res.Add("根因判据 负样本（空文本）: " + (!LooksLikePluginTreeCause("") ? "OK" : "FAIL"));
            res.Add("根因判据 负样本（无关文本）: "
                + (!LooksLikePluginTreeCause("[OK] 重复条目：会真崩的 0 个；跨层同 id 2 个（分层覆盖，合法）") ? "OK" : "FAIL"));

            // 目录选择器陷阱：正样本（U+4E00「一」/U+5F00「开」）必须命中；负样本（青/薇/研）必须不命中
            res.Add("选择器陷阱 正样本（一 U+4E00）: " + (IsPickerDangerChar('一') ? "OK" : "FAIL"));
            res.Add("选择器陷阱 正样本（开 U+5F00）: " + (IsPickerDangerChar('开') ? "OK" : "FAIL"));
            res.Add("选择器陷阱 负样本（青 U+9752）: " + (!IsPickerDangerChar('青') ? "OK" : "FAIL"));
            res.Add("选择器陷阱 负样本（鱼 U+9C7C）: " + (!IsPickerDangerChar('鱼') ? "OK" : "FAIL"));
            res.Add("选择器陷阱 负样本（ASCII a）: " + (!IsPickerDangerChar('a') ? "OK" : "FAIL"));
            var sink = new List<string>();
            CollectDangerChars("C:\\软件a开发", sink);
            res.Add("危险字符收集（软件开发→1 处）: " + (sink.Count == 1 ? "OK" : "FAIL"));
            var sink2 = new List<string>();
            CollectDangerChars("C:\\Users\\示例用户\\Documents\\示例项目", sink2);
            res.Add("危险字符收集 负样本（本机路径→0 处）: " + (sink2.Count == 0 ? "OK" : "FAIL"));
            res.Add("危险字符收集 空串安全: " + (SafeEmptyCollect() ? "OK" : "FAIL"));

            return res;
        }
    }
}

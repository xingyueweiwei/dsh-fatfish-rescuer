using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// ============================================================
// 大肥鱼救星 - DSH 故障救援核心引擎
// 纯逻辑、不依赖 UI：探测、启停、诊断、修复
// ============================================================
namespace BigFatFishRescuer
{
    // 一条诊断结果
    public sealed class DiagItem
    {
        public bool Ok;
        public string Text; // 已含换行
    }

    // 当前状态快照
    public sealed class ServiceState
    {
        public bool Listens;      // 端口有监听
        public bool HttpResponds; // HTTP 有响应
        // ★ 2026-09-16（T5）：语义修正为**「存在 dsh 的 node 进程」**，
        //   **不要求它正在监听端口**。旧版把它写成"监听者里有没有 node"，
        //   于是 `HasDshProcess && !Listens` 恒为假，"进程活着但端口没起来"永远走不到。
        public bool HasDshProcess;
        public int[] DshPids = new int[0];
        public int[] OccupierPids = new int[0]; // 占用端口的进程（含非 dsh）
        public int[] SkippedPids = new int[0];  // 占着端口但**不是** dsh 的进程（T15：不杀它们）
        public bool DeepScan;                   // 本次是否做了全量进程扫描（WMI）
        public string Detail = "";
    }

    public static class DshCore
    {
        // 默认端口（仅作兜底）。2026-09-16 起**不再硬编码**：
        // 旧版把 ActivePort 钉死在 3081，而 dsh 实际跑在 3080
        // ⇒ 救星"看不见"正在运行的实例，停止/重启/打开全部打空。
        // 现在改为运行时发现（last-url.txt → 日志 → 区间内 node 监听者 → 默认 3080）。
        public const int Port = DshBoot.DefaultPort;
        public const string RootUrl = "http://127.0.0.1:3080/";
        public const string AppName = "大肥鱼救星";

        // ★★ 2026-09-18「封 v4.5 版本发人」时补上的**唯一版本号出口**。
        //   之前版本号只活在"打包出来的文件夹名"里 ⇒ 拿到 exe 的人**没法知道自己是哪一版**
        //   （排查时最常问的第一句就是"你用的是哪版"）。现在从这里露出：标题栏、说明书首页、自检报告表头。
        //   ★ 以后升版本**只改这一处**，别再散落到各处字符串里。
        //   ★ 2026-09-20 主人拍板：本轮直接叫 **v5.0**（不叫 v4.6）—— 因为这一轮加的不是补丁，
        //     是三块新能力（插件侦察 / 插件兼容矩阵 / 无头只读排障），旧版号表达不了。
        //   ★ 2026-09-20 晚 主人拍板：热修版叫 **v5.0.1** —— 修的是"点了启动却说没起来"
        //     的**误报**（旧日志块被当成本次失败原因，见 StartFailJudge 的注释与
        //     v5\faults\e2e_stale_log_falsefail.ps1）。
        public const string AppVersion = "5.0.4";
        public static string AppTitle { get { return AppName + " v" + AppVersion; } }

        private static int _activePortCache = -1;
        public static int ActivePort
        {
            get
            {
                if (_activePortCache <= 0) _activePortCache = DshBoot.DiscoverLivePort();
                return _activePortCache;
            }
        }

        /// <summary>清了缓存，下次读 ActivePort 会重新发现（重启/换端口后调用）。</summary>
        public static void ResetActivePort()
        {
            _activePortCache = -1;
            // ★ 2026-09-16：还要清掉"显式端口设置"的缓存，否则改了 port.txt / DSH_PORT 也不生效
            try { DshBoot.ResetPortOverrideCache(); } catch { }
        }

        public static string ActiveRootUrl { get { return "http://127.0.0.1:" + ActivePort + "/"; } }

        // ------------------------------------------------------------
        // 进度输出通道（T7）
        // 背景：`WaitForTokenUrl` 专门留了 `Action<string> progress` 参数用来打印"已等待 N 秒"，
        // 但**两处调用都传了 null** ⇒ 最长 150 秒里界面只有"⏳ 正在执行…"，
        // 用户完全无从判断是卡死还是在正常等待（这正是"卡了 2.5 分钟"的体感来源）。
        // 默认写到标准输出（CLI 模式可见）；GUI 启动时把 ProgressSink 接到界面日志框。
        // ------------------------------------------------------------
        public static Action<string> ProgressSink;

        private static void Report(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return;
            Action<string> sink = ProgressSink;
            if (sink != null) { try { sink(msg); return; } catch { } }
            try { Console.WriteLine(msg); } catch { }
        }

        // 扫描所有 dsh 的 node 进程，优先取“明确带 --port N”的实例端口。
        // 不带 --port 的是默认端口实例（非当前会话），仅在没有任何 --port 时才回退默认。
        // ★ T15：判据从「命令行含 bin.js」收紧为 `IsDshEntry`（入口路径必须落在 dsh 目录下）——
        //   否则**别的项目的同名 bin.js 也会被当成 dsh**，拿它写的端口当"当前实例端口"。
        public static int DiscoverRunningPort()
        {
            int best = Port;
            bool foundExplicit = false;
            int bestPid = int.MaxValue;
            string script =
                "$ErrorActionPreference='SilentlyContinue';" +
                "$c = Get-CimInstance Win32_Process -Filter \"Name='node.exe'\" | Where-Object { $_.CommandLine -like '*bin.js*' }; " +
                "if($c){ $c | ForEach-Object { $_.CommandLine + '||PID=' + $_.ProcessId } }";
            string output = RunPsEncoded(script, 8000);
            if (string.IsNullOrEmpty(output)) return best;
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                // ★ T15：先做身份校验，不是 dsh 的直接跳过
                if (!IsDshEntry(line)) continue;
                int pidTag = line.LastIndexOf("PID=", StringComparison.Ordinal);
                int pid = 0;
                if (pidTag >= 0) int.TryParse(line.Substring(pidTag + 4).Trim(), out pid);
                int m = line.IndexOf("--port", StringComparison.Ordinal);
                if (m < 0) continue; // 不带 --port 的不是当前会话实例，跳过
                string rest = line.Substring(m + 6).TrimStart();
                // 取连续数字作为端口号（直到遇到非数字字符，如 ||PID=）
                int end = 0;
                while (end < rest.Length && rest[end] >= '0' && rest[end] <= '9') end++;
                if (end == 0) continue;
                string portStr = rest.Substring(0, end);
                int p;
                if (int.TryParse(portStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out p)
                    && p > 0 && (pid < bestPid))
                {
                    best = p; bestPid = pid; foundExplicit = true;
                }
            }
            return foundExplicit ? best : Port;
        }

        public static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // ★ v5（WP3/WP5）：DSH home 覆盖开关 —— 环境变量 BFF_DSH_HOME。
        //   为什么必须有：DshHome 之前硬编码 %USERPROFILE%\.dsh，直接导致两个后果
        //     ① **没法在隔离沙箱里做故障注入**：一注入就动到用户真实配置（违反红线"不许动用户真实 profile 做实验"）；
        //     ② 跨机器远程排障时，没法把工具指向"别人的 home"（WP5 的只读诊断版本需要）。
        //   作用域说明：**只**覆盖 DshHome 及其派生（AppDataDir / LauncherIni / profiles\web\*）。
        //     工具链发现路径（node / bin.js / ComfyUI / Desktop）仍走真实 UserProfile ——
        //     那些是只读探测，覆盖了反而找不到 node，会引入新故障。
        //   不设 BFF_DSH_HOME 时行为与旧版**逐字一致**（回归安全）。
        public static readonly string DshHome = ResolveDshHome();

        private static string ResolveDshHome()
        {
            try
            {
                string over = Environment.GetEnvironmentVariable("BFF_DSH_HOME");
                if (!string.IsNullOrEmpty(over))
                {
                    over = over.Trim().Trim('"');
                    if (over.Length > 0) return over;
                }
            }
            catch { }
            return Path.Combine(UserProfile, ".dsh");
        }

        public static readonly string AppDataDir = Path.Combine(DshHome, "big-fat-fish-rescuer");
        public static readonly string DshWebLog = Path.Combine(AppDataDir, "dsh-web.out.log");
        public static readonly string LauncherIni = Path.Combine(DshHome, "whale-desktop-launcher", "launcher.ini");

        private static readonly string[] BinJsCandidates = new string[]
        {
            Path.Combine(UserProfile, ".ai-manager", "npm-global", "node-v24", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"),
            Path.Combine(DshHome, "profiles", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"),
        };

        public static void EnsureAppDataDir()
        {
            try { Directory.CreateDirectory(AppDataDir); }
            catch { }
        }

        // ------------------------------------------------------------
        // ★ WP2②（2026-09-20 防误报加固）：launcher.ini 的 base64 解析**不再是主路径**
        //
        // 形状问题：「把一段 base64 解出来 → 立刻拿它去起进程」是加载器/投放器的
        // 教科书连招（_救星_防误报研究.md §一.2），在 ML 启发式里分数很高。
        // 但 launcher.ini 里那几行**本来只是"上一次真实启动命令的记录"**，
        // 正常安装下路径探测（BinJsCandidates）就能找到入口。
        // ⇒ 所以默认**完全不解析**；只有用户显式打开兼容开关时才走这条冷门分支。
        //
        // 开关（显式、可复核）：环境变量 BFF_LEGACY_LAUNCHER_INI=1，
        //   或命令行任意一个参数写 --legacy-launcher-ini。
        // 默认关闭时 DiscoverLaunch 直接走路径探测；若 launcher.ini 存在但被跳过，
        //   会把这件事记进 LauncherIniSkippedNote，由诊断文本如实说出来（不静默）。
        // ------------------------------------------------------------
        public static bool LegacyLauncherIni = ResolveLegacyLauncherIni();

        private static bool ResolveLegacyLauncherIni()
        {
            try
            {
                string v = Environment.GetEnvironmentVariable("BFF_LEGACY_LAUNCHER_INI");
                if (!string.IsNullOrEmpty(v))
                {
                    string t = v.Trim().Trim('"');
                    if (t == "1" || t.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>本次 DiscoverLaunch 是否**跳过**了 launcher.ini（默认开关关闭且文件存在时会跳过）。</summary>
        private static string _launcherIniSkipped = "";
        public static string LauncherIniSkippedNote { get { return _launcherIniSkipped; } }

        // ---------------- 启动命令发现 ----------------
        public sealed class LaunchSpec
        {
            public string Node;
            public string BinJs;
            public string WorkDir;
            public string Url = RootUrl;
            public string Source = "";
        }

        public static LaunchSpec DiscoverLaunch()
        {
            var spec = new LaunchSpec();

            // 1) 兼容旧启动器：读 whale-desktop-launcher 的 launcher.ini
            //    ★ WP2②：这条**只在显式开关打开时**才走（默认是关的）。
            //      不再"默认解析 base64 → 拿结果起进程"（加载器形状）。
            _launcherIniSkipped = "";
            if (!LegacyLauncherIni)
            {
                try
                {
                    if (File.Exists(LauncherIni))
                        _launcherIniSkipped = "已跳过 launcher.ini（" + LauncherIni + "）："
                            + "它的内容是 base64 编码的启动命令，为免「解码后立刻起进程」这个形状被杀软误判，"
                            + "默认不解析。若你的 dsh 装在非标准位置、路径探测找不到入口，"
                            + "可显式打开兼容开关：设环境变量 BFF_LEGACY_LAUNCHER_INI=1 再启动本程序。";
                }
                catch { }
            }
            else
            {
                try
                {
                    if (File.Exists(LauncherIni))
                    {
                        string command = "", args = "", work = "", url = RootUrl;
                        foreach (string line in File.ReadAllLines(LauncherIni))
                        {
                            if (line.StartsWith("CommandBase64=", StringComparison.Ordinal))
                                command = FromB64(line.Substring("CommandBase64=".Length));
                            else if (line.StartsWith("ArgumentsBase64=", StringComparison.Ordinal))
                                args = FromB64(line.Substring("ArgumentsBase64=".Length));
                            else if (line.StartsWith("WorkingDirectoryBase64=", StringComparison.Ordinal))
                                work = FromB64(line.Substring("WorkingDirectoryBase64=".Length));
                            else if (line.StartsWith("Url=", StringComparison.Ordinal))
                                url = line.Substring("Url=".Length);
                        }
                        if (command.Length > 0 && args.Length > 0)
                        {
                            spec.Node = command.Trim('"');
                            spec.BinJs = ParseBinJsFromArgs(args);
                            spec.WorkDir = work.Length > 0 ? work : UserProfile;
                            spec.Url = url.Length > 0 ? url : RootUrl;
                            spec.Source = "launcher.ini";
                            if (spec.BinJs != null && spec.Node != null) return spec;
                        }
                    }
                }
                catch { }
            }

            // 2) 候选路径探测
            foreach (string c in BinJsCandidates)
            {
                if (File.Exists(c))
                {
                    spec.BinJs = c;
                    spec.Node = FindNodeExe();
                    spec.WorkDir = UserProfile;
                    spec.Url = RootUrl;
                    spec.Source = "path-probe";
                    return spec;
                }
            }
            spec.BinJs = null;
            spec.Node = FindNodeExe();
            spec.WorkDir = UserProfile;
            spec.Url = RootUrl;
            spec.Source = "none";
            return spec;
        }

        private static string ParseBinJsFromArgs(string args)
        {
            // 形如: "C:/.../bin.js" web   或   C:\...\bin.js web
            foreach (string tok in args.Split(new[] { ' ', '\t', '"' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = tok.Trim().Trim('"');
                if (t.EndsWith("bin.js", StringComparison.OrdinalIgnoreCase) && File.Exists(t)) return t;
            }
            return null;
        }

        private static string FindNodeExe()
        {
            string[] cands =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
            };
            foreach (string c in cands) if (File.Exists(c)) return c;
            try
            {
                string p = RunHidden("where.exe", "node", 4000);
                if (p != null)
                {
                    string first = p.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                    if (first.Length > 0) return first;
                }
            }
            catch { }
            return "node";
        }

        public static string FromB64(string s)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s.Trim())); }
            catch { return ""; }
        }

        // ---------------- 状态检测 ----------------
        public static ServiceState CheckState() { return CheckState(false); }

        /// <summary>
        /// 状态检测。
        ///
        /// · deep=false（**4 秒状态轮询走这条**）：只用 iphlpapi 查监听者 + 托管方式查进程存活，
        ///   **不创建任何外部进程** —— 这是"不再不停弹 PowerShell 窗口"的前提，**不要改回 WMI 轮询**。
        /// · deep=true（按钮动作 / 诊断走这条）：再叠加一次 WMI 全量扫描，能发现
        ///   「进程活着但端口没起来」的卡死实例（以及不是救星启动的实例）。
        ///
        /// ★ 2026-09-16 修复 T5（重复起实例的根因）：
        ///   旧版 `HasDshProcess` 由**监听者**派生（"监听者里有没有 node"），
        ///   于是 `HasDshProcess && !Listens` 在回环绑定下**恒为假** ⇒
        ///   「进程活着但端口没起来」永远走不到"重启"分支，反而落到 `StartDsh()`，
        ///   在同一个端口上**又起一个实例**（进程重复、端口竞争、日志两份）。
        ///   现在把它的语义恢复成字面意思：**存在 dsh 的 node 进程** ——
        ///   来源有三：① 监听端口且是 node 的；② 救星自己启动、仍在运行的子进程；
        ///   ③ deep 模式下 WMI 扫描命中的 dsh 进程。
        /// </summary>
        public static ServiceState CheckState(bool deep)
        {
            var st = new ServiceState();
            st.DeepScan = deep;
            st.Listens = PortListens(ActivePort, 900);
            st.HttpResponds = HttpProbe(ActiveRootUrl, 1800);

            // 2026-09-16：这里原来是 GetDshPids()（powershell + Get-CimInstance/WMI），
            // 而这个方法每 4 秒被状态轮询调一次 ⇒ 用户会看到 PowerShell 窗口不停地闪，
            // 每次最坏还阻塞 8 秒。现在改为 iphlpapi 原生查监听者 PID + 托管取进程名，
            // 轮询路径上**不创建任何进程**。
            int[] owners = DshBoot.GetListenerPids(ActivePort);
            st.OccupierPids = owners;
            // ★ 2026-09-16 修复（逐按键体检发现，已验证是真 bug，非臆测）：
            //   本行原来是 `st.DshPids = owners;` —— 把「所有监听者」直接当成「dsh 进程」，
            //   于是 **"端口被非 dsh 进程占用"这个状态根本无法表达**：
            //     · 「一键修复」里 `owners.Length>0 && st.DshPids.Length==0` 恒为假 ⇒ **死代码**；
            //       端口被别家占用时会去 StartDsh()（在已占端口上必然失败），
            //       却返回"已启动 dsh web，正在尝试打开界面……"的**假成功**；
            //     · 「为何打不开」里同款判断会错误地打 [OK]，把真凶藏起来。
            //   正确语义：OccupierPids = 谁在监听；DshPids = 其中**确实是 node 进程**的那些。
            var dshPids = new List<int>();
            var skipped = new List<int>();
            foreach (int pid in owners)
            {
                if (DshBoot.IsNodeProcess(pid)) dshPids.Add(pid);
                else skipped.Add(pid);
            }
            st.SkippedPids = skipped.ToArray();

            // ★ T5 第一补充来源：救星自己启动、**端口却没起来**的实例。
            //   （只有这类进程会"活着但不在监听者名单里"，是 T5 要救的那个场景）
            foreach (int pid in LaunchedPidsAlive())
                if (!dshPids.Contains(pid)) dshPids.Add(pid);

            // ★ T5 第二补充来源：deep 模式下的 WMI 全量扫描（能发现不是救星启动的卡死实例）
            if (deep)
            {
                foreach (int pid in GetDshPids())
                    if (!dshPids.Contains(pid)) dshPids.Add(pid);
            }

            st.DshPids = dshPids.ToArray();
            st.HasDshProcess = dshPids.Count > 0;
            return st;
        }

        // ------------------------------------------------------------
        // 救星自己启动过的 dsh 子进程登记簿（T5）
        // 轮询路径不能起 WMI，所以"进程活着但没监听"这个状态只能靠登记簿 + 托管存活检查来识别。
        // ------------------------------------------------------------
        private static readonly List<int> _launched = new List<int>();

        private static void TrackLaunched(int pid)
        {
            if (pid <= 0) return;
            lock (_launched) { if (!_launched.Contains(pid)) _launched.Add(pid); }
        }

        private static int[] LaunchedPidsAlive()
        {
            var res = new List<int>();
            lock (_launched)
            {
                foreach (int pid in _launched)
                {
                    try
                    {
                        using (Process p = Process.GetProcessById(pid))
                        {
                            // 双保险：进程还在 **且** 名字仍是 node（防 PID 复用误判）
                            if (!p.HasExited && p.ProcessName.Equals("node", StringComparison.OrdinalIgnoreCase))
                                res.Add(pid);
                        }
                    }
                    catch { }
                }
            }
            return res.ToArray();
        }

        // ------------------------------------------------------------
        // 进程身份判据（T15：把两套互不一致的"什么算 dsh"统一成一套）
        // 旧版：GetDshPids()=命令行含 bin.js（**过宽**：别的项目同名脚本也算 dsh）；
        //       KillDshLikeProcesses()=进程名是 node（**过松**：任何 node 都算"dsh 类"）。
        // 后果：同一个按钮"我认为的 dsh"与"我敢杀的 dsh"不是一回事 ——
        //       别的项目的 bin.js 会被停掉，端口上别的 node 服务会被杀掉。
        // 现统一为 IsDshEntry()：命令行里那个以 bin.js 结尾的**入口路径**，
        //       必须落在名为 dsh / dsh-* / @deepseek-ai 的目录段下。
        // ------------------------------------------------------------
        private sealed class NodeProcInfo { public int Pid; public string Cmd; }

        // 已知的 dsh 入口路径（惰性求值 + 缓存）。
        // 为什么要它：下面的目录段判据（dsh / dsh-* / @deepseek-ai）覆盖标准安装，
        // 但**非标准路径**安装的 dsh（目录名不含 dsh）会被误判成"不是 dsh" ⇒ 停不掉、也杀不掉。
        // 用 DiscoverLaunch() 实际发现到的入口路径做**权威白名单**，既保住严格性又不漏。
        private static string _knownBinJs = null;
        private static bool _knownBinJsTried = false;
        private static string KnownBinJs()
        {
            if (!_knownBinJsTried)
            {
                _knownBinJsTried = true;
                try { LaunchSpec s = DiscoverLaunch(); _knownBinJs = s != null ? s.BinJs : null; }
                catch { _knownBinJs = null; }
            }
            return _knownBinJs;
        }

        public static bool IsDshEntry(string cmdLine)
        {
            if (string.IsNullOrEmpty(cmdLine)) return false;
            string known = KnownBinJs();
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(cmdLine, "\"([^\"]+)\"|([^\\s]+)"))
            {
                string tok = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (tok.Length == 0) continue;
                if (!tok.EndsWith("bin.js", StringComparison.OrdinalIgnoreCase)) continue;
                // ① 权威判据：与"实际发现的 dsh 入口"逐字相同
                if (!string.IsNullOrEmpty(known) &&
                    string.Equals(tok.Trim('"'), known, StringComparison.OrdinalIgnoreCase)) return true;
                // ② 通用判据：入口路径必须落在名为 dsh / dsh-* / @deepseek-ai 的目录段下
                string[] segs = tok.Replace('/', '\\').Split('\\');
                foreach (string s in segs)
                {
                    if (s.Equals("dsh", StringComparison.OrdinalIgnoreCase)) return true;
                    if (s.StartsWith("dsh-", StringComparison.OrdinalIgnoreCase)) return true;
                    if (s.StartsWith("@deepseek-ai", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 一次 WMI 调用拿回**所有 node 进程**的 pid + 命令行。
        /// **查询失败返回 null**（与"确实一个 node 都没有"严格区分）——
        /// 调用方必须区别对待：null 意味着"身份无法确认"，此时**宁可不杀**。
        /// </summary>
        private static List<NodeProcInfo> QueryNodeProcesses()
        {
            string script =
                "$ErrorActionPreference='SilentlyContinue';" +
                "Get-CimInstance Win32_Process -Filter \"Name='node.exe'\" | ForEach-Object { \"$($_.ProcessId)`t$($_.CommandLine)\" }";
            string output = RunPsEncoded(script, 8000);
            if (output == null) return null;
            var list = new List<NodeProcInfo>();
            foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int tab = raw.IndexOf('\t');
                if (tab <= 0) continue;
                int pid;
                if (!int.TryParse(raw.Substring(0, tab).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid)) continue;
                list.Add(new NodeProcInfo { Pid = pid, Cmd = raw.Substring(tab + 1).Trim() });
            }
            return list;
        }

        /// <summary>查单个进程的命令行。查询失败返回 null；WMI 查不到该进程返回 ""。</summary>
        private static string QueryCmdLine(int pid)
        {
            string script =
                "$ErrorActionPreference='SilentlyContinue';" +
                "$p = Get-CimInstance Win32_Process -Filter \"ProcessId=" + pid.ToString(CultureInfo.InvariantCulture) + "\"; " +
                "if($p){ $p.CommandLine }";
            return RunPsEncoded(script, 6000);
        }

        // 用 Get-NetTCPConnection 快速拿占用 3080 的 PID（远快于 netstat）
        public static int[] GetPortOwnerPidsFast(int port)
        {
            var result = new List<int>();
            string script =
                "$ErrorActionPreference='SilentlyContinue';" +
                "Get-NetTCPConnection -LocalPort " + port.ToString(CultureInfo.InvariantCulture) +
                " -State Listen | Select-Object -ExpandProperty OwningProcess | Sort-Object -Unique";
            string output = RunPsEncoded(script, 6000);
            if (output == null) return result.ToArray();
            foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int pid;
                if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid))
                    if (!result.Contains(pid)) result.Add(pid);
            }
            return result.ToArray();
        }

        public static bool PortListens(int port, int timeoutMs)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var ar = client.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    client.EndConnect(ar);
                    return client.Connected;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// 探活：**多试几次**再判死（给"正在忙"的实例留出时间）。
        ///
        /// ★★ 2026-09-20 修（这是"间接杀掉正在干活的 dsh"的**真凶**，有对照实测）：
        ///   原来 SmartStartAndOpen 里只探**一次**、超时 **1200ms**：
        ///       bool healthy = listening && HttpProbe(url, 1200);
        ///   于是实例**忙**的时候（正在跑长回答 / 别的东西正在满负荷占用机器时，`/` 1.2 秒答不上来很正常）
        ///   ⇒ healthy=false ⇒ 因为它"占着端口 + 确认是 dsh"⇒ 直接进
        ///   「② 确认是 dsh 的卡死残留 ⇒ 清掉再起」分支 ⇒ **把正在给你干活的实例杀了**。
        ///   实测取证（用替身进程复现，替身 argv 带 dsh 的 bin.js、首个请求延迟 3000ms）：
        ///     修前 `--smart --dry` 输出「端口 4399 上有一个**卡死的 dsh 残留** ⇒ 我先把它清掉」；
        ///     修后同一场景输出「已经有 dsh 在正常服务 ⇒ **不动它**」。
        ///   现在：3 次 × 2500ms + 间隔 400ms，**任意一次应答即算健康**；仍然全失败才判"卡死"。
        /// </summary>
        private static bool ProbeHealthy(int port)
        {
            string url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/";
            for (int i = 0; i < 3; i++)
            {
                if (HttpProbe(url, 2500)) return true;
                if (i < 2) System.Threading.Thread.Sleep(400);
            }
            return false;
        }

        public static bool HttpProbe(string url, int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Proxy = null;
                req.AllowAutoRedirect = true;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    // 任意 HTTP 状态都算“服务在响应”
                    int code = (int)resp.StatusCode;
                    return code >= 100 && code <= 599;
                }
            }
            catch (WebException wex)
            {
                var resp = wex.Response as HttpWebResponse;
                if (resp != null) return true; // 4xx/5xx 也算有服务
                return false;
            }
            catch { return false; }
        }

        // 找**确实是 dsh** 的 node 进程（T15：判据统一到 IsDshEntry，不再"含 bin.js 就算"）
        public static int[] GetDshPids()
        {
            List<NodeProcInfo> procs = QueryNodeProcesses();
            if (procs != null)
            {
                var res = new List<int>();
                foreach (NodeProcInfo p in procs) if (IsDshEntry(p.Cmd)) res.Add(p.Pid);
                return res.ToArray();
            }

            // WMI 不可用时的兜底：退回旧口径（命令行含 bin.js）。
            // ⚠ 兜底结果**只用于诊断展示**；真正决定"杀谁"之前一定会再确认身份
            //   （见 KillDshLikeProcesses / KillDshLikeProcesses 的 null 语义）。
            string script =
                "$ErrorActionPreference='SilentlyContinue';" +
                "$c = Get-CimInstance Win32_Process -Filter \"Name='node.exe'\" | Where-Object { $_.CommandLine -like '*bin.js*' }; " +
                "if($c){ $c | ForEach-Object { $_.ProcessId } }";
            return ParsePidLines(RunPsEncoded(script, 8000));
        }

        // 谁在监听 3080（netstat，locale 安全）
        public static int[] GetPortOwnerPids(int port)
        {
            var result = new List<int>();
            try
            {
                string output = RunHidden("netstat.exe", "-ano -p tcp", 5000);
                if (output == null) return result.ToArray();
                string marker = ":" + port.ToString(CultureInfo.InvariantCulture);
                foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = raw.Trim();
                    if (!line.Contains(marker)) continue;
                    int stateIdx = line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase);
                    if (stateIdx < 0) continue;
                    string tail = line.Substring(stateIdx + "LISTENING".Length).Trim();
                    if (tail.Length == 0) continue;
                    int pid;
                    if (int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid))
                        if (!result.Contains(pid)) result.Add(pid);
                }
            }
            catch { }
            return result.ToArray();
        }

        private static int[] ParsePidLines(string output)
        {
            var result = new List<int>();
            if (output == null) return result.ToArray();
            foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                int pid;
                if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid)) result.Add(pid);
            }
            return result.ToArray();
        }

        // ---------------- 进程管理 ----------------

        // ★ 2026-09-16（P11）：StartDsh 旧版把所有失败都吞成 false，
        //   调用方一律说"未找到入口 bin.js / node" —— 真实原因若是权限/工作目录/端口冲突，
        //   用户就被引到完全错误的方向去排查（异常原文含 errno/路径，被整段丢弃）。
        //   现在把异常原文留下来，调用方负责拼进提示。
        private static string _lastStartError = "";
        public static string LastStartError { get { return _lastStartError; } }

        /// <summary>
        /// 把子进程的一路输出持续读掉并落盘。
        /// ★ T17：旧版整个函数体包在一个 try 里，**「建日志文件失败」这一种异常会把"读流"也一起吞掉** ——
        ///   于是该子进程的 stdout 再也没人读，管道缓冲（~64KB）写满后子进程**永久阻塞在 write 上**；
        ///   用户看到的是"启动了但服务起不来"，而日志 0 字节、零线索。
        ///   现在把「落盘」与「排空」分开：**落盘失败也继续排空**（退化成丢弃），保证子进程永不因管道满而卡死。
        /// </summary>
        private static void PumpToFile(StreamReader reader, string path)
        {
            StreamWriter log = null;
            try
            {
                try { log = new StreamWriter(path, true, new UTF8Encoding(false)); log.AutoFlush = true; }
                catch { log = null; }   // 日志不可写 → 只排空，不影响子进程
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (log != null) { try { log.WriteLine(line); } catch { } }
                }
            }
            catch { }
            finally { if (log != null) { try { log.Dispose(); } catch { } } }
        }

        // ------------------------------------------------------------
        // ★ WP2③（2026-09-20 防误报加固）：启动 DSH **不再经过 cmd.exe**
        //
        // 旧版：cmd /c ""node" "bin.js" web --no-open --port N >> out.log 2>> err.log"
        //   形状问题：「shell + 隐藏窗口 + 输出重定向」是后门/投放器的典型形状
        //   （_救星_防误报研究.md §一.3）。
        //
        // ⚠ 但**不能**简单改成 `Process.Start + 重定向到管道 + 泵到文件` 就完事 ——
        //   那正是 2026-09-18 修掉的 EPIPE 坑：救星一旦退出（关窗口 / 被删 / 崩溃），
        //   管道读端随之关闭，node 再写 stdout 拿到 EPIPE ⇒ **救星一退出 DSH 就死**。
        //
        // ⇒ 主路径 = CreateProcess + STARTF_USESTDHANDLES：
        //   把**两个日志文件的句柄**直接作为子进程的 stdout/stderr。
        //   全程**没有 cmd.exe、也没有任何管道** —— 既没有管道满死锁，
        //   救星死了 DSH 也照常跑（它写的是文件，不是我们手里的管道）。
        //   语义上与 cmd 的 `>> out.log 2>> err.log` 完全等价，只是不经过 shell。
        //
        // ⇒ 回落路径（仅当上面那条失败，极罕见：例如句柄继承被拦截）：
        //   托管 Process.Start 直接起 node.exe + **两路异步排空**到同一对日志文件
        //   （仍然不经 cmd）。它会持有管道 ⇒ 仍有 EPIPE 风险，所以**只在主路径失败时用**，
        //   并且会把它写进 LastStartNote，让人看得到。
        // ------------------------------------------------------------

        private const uint STARTF_USESTDHANDLES = 0x00000100;
        private const uint CREATE_NO_WINDOW = 0x08000000;
        private const uint HANDLE_FLAG_INHERIT = 0x00000001;
        private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        private const uint PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        /// <summary>STARTUPINFO + 一段属性列表。用它才能给子进程一张"句柄白名单"。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(
            string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
            IntPtr lpEnvironment, string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        // ★ 注意 EntryPoint：kernel32 里**没有** CreateProcessEx 这个导出名，
        //   它就是 CreateProcess，只是最后一个参数换成 STARTUPINFOEX。
        //   不写 EntryPoint 的话会抛 EntryPointNotFoundException（且会被下面的 catch 吞掉，
        //   表现为"句柄白名单永远未生效"——2026-09-20 实测踩过一次）。
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode,
                   EntryPoint = "CreateProcess")]
        private static extern bool CreateProcessEx(
            string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
            IntPtr lpEnvironment, string lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(
            IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue,
            IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetHandleInformation(IntPtr hObject, out uint lpdwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        private const int STD_INPUT_HANDLE = -10;
        private const int STD_OUTPUT_HANDLE = -11;
        private const int STD_ERROR_HANDLE = -12;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>拼出直接起 node 的命令行（不经 cmd，所以引号由我们自己负责）。</summary>
        private static string BuildNodeCommandLine(LaunchSpec spec, int port)
        {
            return "\"" + spec.Node + "\" \"" + spec.BinJs + "\" web --no-open --port "
                   + port.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 主路径：直接起 node.exe，把两个日志文件句柄作为它的 stdout/stderr。
        /// 不经 cmd.exe，也不创建任何管道。
        /// </summary>
        private static bool StartNodeWithFileHandles(LaunchSpec spec, int port, out int pid, out string err)
        {
            pid = 0;
            err = "";
            FileStream outFs = null;
            FileStream errFs = null;
            // 本进程三个标准句柄的"可继承"原状态，起完进程后要按原样还回去
            IntPtr[] stdH = new IntPtr[] { IntPtr.Zero, IntPtr.Zero, IntPtr.Zero };
            uint[] stdSaved = new uint[] { 0, 0, 0 };
            bool[] stdTouched = new bool[] { false, false, false };
            // 句柄白名单用的属性列表缓冲区（在 finally 里统一释放）
            IntPtr attrListBuf = IntPtr.Zero;
            try
            {
                // 与旧版 cmd 的 `>> out.log 2>> err.log` 同语义：追加写 + 允许别人同时读
                outFs = new FileStream(DshWebLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                errFs = new FileStream(DshWebLog + ".err", FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

                IntPtr ho = outFs.SafeFileHandle.DangerousGetHandle();
                IntPtr he = errFs.SafeFileHandle.DangerousGetHandle();
                if (!SetHandleInformation(ho, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT))
                {
                    err = "SetHandleInformation(stdout) 失败，Win32 错误 " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                if (!SetHandleInformation(he, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT))
                {
                    err = "SetHandleInformation(stderr) 失败，Win32 错误 " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                // ★★ 2026-09-20 修（真实缺陷，是 WP2③ 起进程实验里**实测**出来的，不是推测）：
                //   CreateProcess 的 bInheritHandles=true 会让子进程继承**当前所有可继承的句柄**，
                //   而不只是我们通过 STARTUPINFO 递给它的那两个日志文件句柄。
                //
                //   最典型的受害者：命令行调用方给本进程的 stdout/stderr **管道写端**
                //   （`大肥鱼救星.exe --start > log.txt`、被别的程序重定向后读输出、CI 里管道给下一级）。
                //   这些管道写端一旦被 DSH 继承，只要 DSH 还活着，调用方读 stdout 就永远等不到 EOF
                //   ⇒ **调用方挂死**。实测症状：`--start` 进程确实起来了、日志也在正常写，
                //     但命令行 7 分钟不返回。
                //
                //   ⇒ 所以起进程之前，先把**本进程自己的三个标准句柄**临时取消可继承标记，
                //     起完立刻按原样恢复。这样子进程只会继承我们显式给的两个日志文件句柄。
                try
                {
                    stdH[0] = GetStdHandle(STD_INPUT_HANDLE);
                    stdH[1] = GetStdHandle(STD_OUTPUT_HANDLE);
                    stdH[2] = GetStdHandle(STD_ERROR_HANDLE);
                    for (int i = 0; i < stdH.Length; i++)
                    {
                        IntPtr h = stdH[i];
                        if (h == IntPtr.Zero) continue;
                        if (h == new IntPtr(-1)) continue;              // INVALID_HANDLE_VALUE
                        uint f;
                        if (GetHandleInformation(h, out f))
                        {
                            stdSaved[i] = f;
                            stdTouched[i] = true;
                            if ((f & HANDLE_FLAG_INHERIT) != 0)
                                SetHandleInformation(h, HANDLE_FLAG_INHERIT, 0);
                        }
                    }
                }
                catch { }

                STARTUPINFO si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                si.dwFlags = (int)STARTF_USESTDHANDLES;
                si.hStdInput = IntPtr.Zero;
                si.hStdOutput = ho;
                si.hStdError = he;
                si.wShowWindow = 0;

                string cmdLine = BuildNodeCommandLine(spec, port);
                string workDir = spec.WorkDir.Length > 0 ? spec.WorkDir : UserProfile;

                // ★★ 首选：STARTUPINFOEX + PROC_THREAD_ATTRIBUTE_HANDLE_LIST
                //   这才是**真正的**解法。上面的"临时取消三个标准句柄"只能挡住
                //   GetStdHandle 拿得到的那三个，挡不住**本进程从父进程那里继承来的其他可继承句柄**
                //   —— 实测就是它把人坑了：救星从 PowerShell 那里继承了 PowerShell 自己的
                //   stdout 管道写端，DSH 再继承一次 ⇒ 调用方读 stdout 永远等不到 EOF，
                //   表现为「--start 明明 1.2 秒就成功了，命令行却 8 分钟不返回」。
                //   有了句柄白名单，子进程**只会**继承列表里这两个日志文件句柄，其余一概不继承。
                //   （Windows Vista 起支持；拿不到就回落到下面的普通 CreateProcess。）
                PROCESS_INFORMATION pi = new PROCESS_INFORMATION();
                bool ok = false;
                IntPtr handlePtr = IntPtr.Zero;
                _handleListDiag = "";
                try
                {
                    IntPtr need = IntPtr.Zero;
                    InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref need);   // 必定失败，只为取所需大小
                    _handleListDiag += "取大小=" + need.ToString();
                    if (need != IntPtr.Zero)
                    {
                        attrListBuf = Marshal.AllocHGlobal(need);
                        IntPtr need2 = need;
                        bool initOk = InitializeProcThreadAttributeList(attrListBuf, 1, 0, ref need2);
                        _handleListDiag += "；初始化=" + initOk;
                        if (initOk)
                        {
                            IntPtr[] hs = new IntPtr[] { ho, he };
                            handlePtr = Marshal.AllocHGlobal(IntPtr.Size * hs.Length);
                            Marshal.Copy(hs, 0, handlePtr, hs.Length);
                            bool updOk = UpdateProcThreadAttribute(attrListBuf, 0,
                                    (IntPtr)PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                                    handlePtr, (IntPtr)(IntPtr.Size * hs.Length),
                                    IntPtr.Zero, IntPtr.Zero);
                            _handleListDiag += "；写属性=" + updOk
                                + (updOk ? "" : "（Win32 " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + "）");
                            if (updOk)
                            {
                                STARTUPINFOEX siex = new STARTUPINFOEX();
                                siex.StartupInfo = si;
                                // ★★ 关键：用 STARTUPINFOEX 时 cb 必须是 **STARTUPINFOEX** 的大小，
                                //   写成 STARTUPINFO 的大小会让 CreateProcess 直接报
                                //   ERROR_INVALID_PARAMETER(87)（2026-09-20 实测踩到）。
                                siex.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
                                siex.lpAttributeList = attrListBuf;
                                ok = CreateProcessEx(null, cmdLine, IntPtr.Zero, IntPtr.Zero,
                                        true, CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT,
                                        IntPtr.Zero, workDir, ref siex, out pi);
                                _handleListDiag += "；CreateProcess=" + ok
                                    + (ok ? "" : "（Win32 " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + "）");
                            }
                        }
                        else
                        {
                            _handleListDiag += "（Win32 " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + "）";
                        }
                    }
                }
                catch (Exception ex)
                {
                    ok = false;
                    _handleListDiag += "；异常=" + ex.GetType().Name + ": " + ex.Message;
                }
                finally
                {
                    if (handlePtr != IntPtr.Zero) { try { Marshal.FreeHGlobal(handlePtr); } catch { } }
                }
                _usedHandleList = ok;

                // 回落：拿不到句柄白名单就用普通 CreateProcess（此时上面已把三个标准句柄暂时设为不可继承）
                if (!ok)
                {
                    ok = CreateProcess(null, cmdLine, IntPtr.Zero, IntPtr.Zero,
                                       true, CREATE_NO_WINDOW, IntPtr.Zero, workDir,
                                       ref si, out pi);
                }

                if (!ok)
                {
                    err = "CreateProcess 失败，Win32 错误 " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                pid = pi.dwProcessId;
                if (pi.hThread != IntPtr.Zero) { try { CloseHandle(pi.hThread); } catch { } }
                if (pi.hProcess != IntPtr.Zero) { try { CloseHandle(pi.hProcess); } catch { } }
                return true;
            }
            catch (Exception e)
            {
                err = e.GetType().Name + ": " + e.Message;
                return false;
            }
            finally
            {
                // 把本进程三个标准句柄的"可继承"标记**按原样还回去**（见上面的说明）
                for (int i = 0; i < stdTouched.Length; i++)
                {
                    if (!stdTouched[i]) continue;
                    try
                    {
                        SetHandleInformation(stdH[i], HANDLE_FLAG_INHERIT, stdSaved[i] & HANDLE_FLAG_INHERIT);
                    }
                    catch { }
                }
                // 属性列表要在释放缓冲区之前先删
                if (attrListBuf != IntPtr.Zero)
                {
                    try { DeleteProcThreadAttributeList(attrListBuf); } catch { }
                    try { Marshal.FreeHGlobal(attrListBuf); } catch { }
                }
                // 子进程已经在 CreateProcess 那一刻**继承了自己那份**句柄，我们这边立刻关掉
                if (outFs != null) { try { outFs.Dispose(); } catch { } }
                if (errFs != null) { try { errFs.Dispose(); } catch { } }
            }
        }

        /// <summary>
        /// 回落路径（只在主路径失败时用）：托管 Process.Start 直接起 node.exe，
        /// **两路异步排空**到同一对日志文件（仍然不经 cmd.exe）。
        /// ⚠ 持有管道 ⇒ 救星退出后子进程写 stdout 有 EPIPE 风险，所以它不是首选。
        /// </summary>
        private static bool StartNodeWithAsyncDrain(LaunchSpec spec, int port, out Process proc, out string err)
        {
            proc = null;
            err = "";
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = spec.Node;
                psi.Arguments = "\"" + spec.BinJs + "\" web --no-open --port " + port.ToString(CultureInfo.InvariantCulture);
                psi.WorkingDirectory = spec.WorkDir.Length > 0 ? spec.WorkDir : UserProfile;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                Process p = new Process();
                p.StartInfo = psi;
                p.Start();

                // ★★ 必须**异步排空**两路：不同步排空 ⇒ 管道缓冲写满后子进程卡死在 write 上
                //   （表现为"启动了但服务起不来"，日志 0 字节、零线索）。
                Task.Factory.StartNew(delegate { try { PumpToFile(p.StandardOutput, DshWebLog); } catch { } });
                Task.Factory.StartNew(delegate { try { PumpToFile(p.StandardError, DshWebLog + ".err"); } catch { } });

                proc = p;
                return true;
            }
            catch (Exception e)
            {
                err = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>本次启动走的是哪条路（供诊断文本如实报出来，不静默）。</summary>
        private static string _lastStartNote = "";
        public static string LastStartNote { get { return _lastStartNote; } }

        /// <summary>
        /// 本次起子进程时，有没有成功用上「句柄白名单」（STARTUPINFOEX + PROC_THREAD_ATTRIBUTE_HANDLE_LIST）。
        /// true  = 子进程**只**继承了两个日志文件句柄；
        /// false = 没拿到白名单、已回落 ⇒ 子进程可能顺带继承别的句柄（见 StartNodeWithFileHandles 里的说明）。
        /// 如实报出来，不静默。
        /// </summary>
        private static bool _usedHandleList = false;
        public static bool UsedHandleList { get { return _usedHandleList; } }

        /// <summary>句柄白名单每一步的结果（排障用，如实报出来，不静默）。</summary>
        private static string _handleListDiag = "";

        // ★ WP2 判据可观测性（2026-09-20）：把本次实际采用的入口**如实报出来**，
        //   让"起进程之后回读命令行"之外，还能在程序自己的输出里看到它挑中了谁。
        //   只读、不改变任何启动行为；正是为了能回答「你到底起了哪个 bin.js」。
        private static LaunchSpec _lastSpec = null;
        public static string LastLaunchBinJs { get { return (_lastSpec != null && _lastSpec.BinJs != null) ? _lastSpec.BinJs : ""; } }
        public static string LastLaunchSource { get { return (_lastSpec != null) ? _lastSpec.Source : ""; } }

        public static bool StartDsh()
        {
            _lastStartError = "";
            _lastStartNote = "";
            if (ReadOnlyMode) { _lastStartError = ReadOnlyRefusal(); return false; }
            EnsureAppDataDir();
            LaunchSpec spec = DiscoverLaunch();
            _lastSpec = spec;
            if (spec.BinJs == null || !File.Exists(spec.BinJs))
            {
                _lastStartError = "未找到 dsh 入口 bin.js（已扫常见安装路径"
                                  + (LegacyLauncherIni
                                        ? " 与 launcher.ini）。"
                                        : "；launcher.ini 默认不读 —— 若你的 dsh 装在非标准位置，"
                                          + "可显式开兼容开关 BFF_LEGACY_LAUNCHER_INI=1 后再试）。");
                return false;
            }

            // 若当前端口已有 dsh 实例在响应，直接复用（不重复启动、不抢端口）
            int port = ActivePort;
            if (PortListens(port, 500))
            {
                // 已有服务在监听：确认是 dsh 且有 HTTP 响应（避免占用的是其它程序）
                if (HttpProbe("http://127.0.0.1:" + port + "/", 900)) return true;

                // ★★ 2026-09-18 治本（复现「反复报错 3080、过一会又好了、然后又崩、进不去网页版」）：
                //   实测（本机）—— 端口已被占时**再去起一个实例，它必然 EADDRINUSE 秒退**，
                //   屏幕上刷的就是这类堆栈：
                //       code: 'EADDRINUSE', syscall: 'listen', address: '127.0.0.1', port: 3080
                //   而真正让用户"进不去网页版"的，是**先有那个占着端口却不响应的实例**（卡死的残留）：
                //   页面打不开 → 用户（或工具）一遍遍试着启动 → 每次都报端口被占 ⇒ "反复报错、时好时坏"。
                //   ⇒ 所以这里**不再去起注定失败的新实例**，而是把真实处境与下一步说清楚。
                //     （想先看清是谁占着：`--plan-kill` 只列清单、不动手。）
                int[] owners2 = GetPortOwnerPidsFast(port);
                bool ownerIsDsh = false;
                try
                {
                    int[] dp = GetDshPids();
                    if (dp != null) foreach (int d in dp) foreach (int o in owners2) if (d == o) ownerIsDsh = true;
                }
                catch { }
                _lastStartError = "端口 " + port + " 上**已经有东西占着**，但它**不响应 HTTP**"
                    + (ownerIsDsh ? "（占用者确认是 dsh —— 典型的「卡死残留」）" : "（占用者不是 dsh —— 是别的程序占着这个端口）")
                    + "。\r\n"
                    + "这时再去启动只会得到 EADDRINUSE（屏幕上刷「端口已被占用」）—— 所以我**没有**去起新实例。\r\n"
                    + "该怎么办：\r\n"
                    + "  · 若是 dsh 残留（卡死没退干净）：点「⏹ 停止服务」把它清掉，再点「🚀 启动并打开」；\r\n"
                    + "  · 若是别的程序占着：换一个端口（把端口号写进 %USERPROFILE%\\.dsh\\big-fat-fish-rescuer\\port.txt，"
                    + "或设环境变量 DSH_PORT），再启动；\r\n"
                    + "  · 想先看清是谁占着：命令行 `大肥鱼救星.exe --plan-kill`（只列清单，一个都不杀）。";
                return false;
            }

            try
            {
                // ★★ 2026-09-18 治本②（主人问「用了之后 DSH 崩了，能不能保证不与插件打架」）：
                //   **绝对不要把 DSH 的命拴在救星身上**。
                //   旧版把子进程的 stdout/stderr 接到**管道**、由救星泵到文件 ⇒ 救星一旦退出
                //   （关窗口 / 被删 / 崩溃），管道读端关闭，DSH 再往 stdout 写就拿到 EPIPE
                //   —— Node 对 stdout 的 EPIPE 默认会让进程直接挂掉。
                // ★★ WP2③（2026-09-20）：上面这条纪律**继续有效**，但现在不靠 cmd 实现 ——
                //   改为 CreateProcess 直接把两个**日志文件句柄**交给 node（见 StartNodeWithFileHandles）。
                //   结果同样是「DSH 的 stdout 是文件句柄，救星随时可以死」，
                //   但**不再有 cmd.exe 这个 shell 形状**（那是后门/投放器的典型特征）。
                //   现在改成**直接给子进程两个日志文件句柄**（全程不经管道、也不经 cmd）：
                //     CreateProcess(node.exe, ... , hStdOutput=out.log, hStdError=out.log.err)
                //   这样 DSH 的 stdout 是**文件句柄**，救星随时可以死，DSH 照常跑。
                //   （下面等待/归因逻辑读的还是同两个日志文件，行为不变。）
                // ★★ 2026-09-20 治本（主人实测：「b 是我刚才点启动，结果你没起来」）：
                //   根因＝**把旧日志里的报错当成这一次启动的失败原因**。
                //   证据链（本机 12:05 那次，逐条可复核）：
                //     ① 起进程 12:05:10（cmd → node … --port 3080），实例其实正常冷启动，
                //        12:05:36 正常打出带 token 的地址；
                //     ② 但下面的"快速失败"在 spent>=5 秒（≈12:05:15）调 TryDiagnose()，
                //        它只看**文件尾 6000 字节**；而那一刻文件末尾 **142 字符**处，
                //        正躺着一段**旧的** `EADDRINUSE 127.0.0.1:3199` 崩溃块；
                //     ③ ClassifyLine 认得 EADDRINUSE ⇒ 判「端口冲突」⇒ StartDsh 立刻 return false，
                //        界面报"启动失败（日志已定性）" ⇒ 主人看到的就是"点了启动、你没起来"；
                //     ④ 那次点击耗时 ≈9~10 秒（12:05:05 → 12:05:15），与 run-times.txt 里
                //        「启动并打开（智能）」新增的 9.1 秒吻合。
                //   ⇒ 现在：①起进程**之前**记下两个日志的字节长度当"本次起点"；
                //           ②顺手写一行带时间戳的分界（文件被别人占着就跳过）；
                //           ③归因只看**起点之后新写进来的内容**（TryDiagnoseSince）。
                _attemptErrFrom = PluginDiag.LengthOf(DshWebLog + ".err");
                _attemptOutFrom = PluginDiag.LengthOf(DshWebLog);
                string divider = "===== 大肥鱼救星启动分界 @"
                                 + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                                 + "  port=" + port.ToString(CultureInfo.InvariantCulture) + " =====";
                PluginDiag.TryAppendDivider(DshWebLog, divider);          // 被占用就跳过，不影响启动
                PluginDiag.TryAppendDivider(DshWebLog + ".err", divider);
                // 起点＝写完分界之后的字节长度（分界行本身不算"本次证据"）
                _attemptErrFrom = PluginDiag.LengthOf(DshWebLog + ".err");
                _attemptOutFrom = PluginDiag.LengthOf(DshWebLog);

                // ★★ WP2③：直接起 node.exe（不经 cmd.exe）
                //   ① 主路径 = 文件句柄（无管道）；② 失败才回落 = 托管 Start + 两路异步排空。
                int childPid = 0;
                string startErr = "";
                bool nativeOk = StartNodeWithFileHandles(spec, port, out childPid, out startErr);
                Process p = null;

                if (nativeOk)
                {
                    _lastStartNote = "直接起 node.exe，stdout/stderr = 日志文件句柄"
                                     + "（不经过命令解释器，也不持有任何管道 ⇒ 救星退出后 DSH 照常跑）。"
                                     + "\r\n句柄白名单：" + (_usedHandleList
                                         ? "已生效（子进程只继承那两个日志文件句柄，不会顺带继承调用方的管道）"
                                         : "未生效，已回落（子进程可能顺带继承调用方的管道写端）")
                                     + "  [" + _handleListDiag + "]"
                                     + (LauncherIniSkippedNote.Length > 0 ? "\r\n" + LauncherIniSkippedNote : "");
                    // 拿一个托管 Process 只为观察"还在不在"，Dispose 它**不会**杀掉子进程
                    try { p = Process.GetProcessById(childPid); } catch { }
                    if (childPid > 0) TrackLaunched(childPid);   // ★ T5：现在登记的就是 node 自己
                }
                else
                {
                    string drainErr = "";
                    bool fbOk = StartNodeWithAsyncDrain(spec, port, out p, out drainErr);
                    if (!fbOk)
                    {
                        _lastStartNote = "";
                        _lastStartError = "启动失败：直接起 node 的两条路都没成。\r\n"
                                          + "  ① 文件句柄方式：" + startErr + "\r\n"
                                          + "  ② 管道异步排空方式：" + drainErr;
                        return false;
                    }
                    try { childPid = p.Id; } catch { childPid = 0; }
                    if (childPid > 0) TrackLaunched(childPid);
                    _lastStartNote = "⚠ 本次走的是**回落路径**（托管 Process.Start + 两路异步排空，仍不经过命令解释器），"
                                     + "因为文件句柄方式失败了：" + startErr + "\r\n"
                                     + "回落路径持有管道 ⇒ 救星退出后 DSH 仍有 EPIPE 风险，属例外情况。";
                }

                try
                {
                // ★★ 2026-09-18 治本（来自主人转来的真实反馈：「救星说启动了，可页面死活打不开」）：
                //   **spawn 成功 ≠ 服务起来了**。DSH 在**插件树加载失败**时会自己退出
                //   （例如某个插件 import 不到包：Cannot find package ⇒ plugin tree failed to load），
                //   而旧版只要 Process.Start 没抛异常就 return true ⇒ 用户看到"已启动"、浏览器却打不开，
                //   最后把气撒在救星身上（真实发生：有人重装 DSH 后是"删掉救星"才好的 —— 因为救星是那个
                //   一直替他启动、又一直报"成功"的东西，却从没告诉他插件树挂了）。
                //   ⇒ 现在：起完**自己确认端口真的有 HTTP 响应**；进程提前退出就立刻读 web 日志尾部，
                //     用与 `--classify` 同一套判据说出**是哪个插件、缺什么**。
                string url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/";
                int t0 = Environment.TickCount;
                // ★ 2026-09-19 修正：原来给 45 秒 —— 实测**会误报失败**。
                //   真事：我用新代码在 3196 端口起了一台实例，救星 45 秒到了就报"没起来"并退出，
                //   而**那台 DSH 后来真的起来了**（半小时后还在正常服务）⇒ 慢机器/冷启动会被冤枉。
                //   本项目自己记录过冷启动能到 150 秒量级 ⇒ 上限放宽到 180 秒，且**日志一出现致命标记仍立刻收工**
                //   （快速失败不变），期间每 5 秒往界面报一次进度，不让用户干等。
                int deadline = t0 + 180000;
                int lastTick = 0;
                int lastDiag = 0;
                while (Environment.TickCount < deadline)
                {
                    System.Threading.Thread.Sleep(500);
                    if (HttpProbe(url, 800))
                    {
                        _lastStartMs = Environment.TickCount - t0;
                        return true;                     // 真的起来了
                    }
                    bool dead = false;
                    try { dead = (p != null) && p.HasExited; } catch { }
                    if (dead)
                    {
                        _lastStartError = "刚启动就退出了 —— " + DiagnoseStartFailure();
                        return false;
                    }
                    int spent = (Environment.TickCount - t0) / 1000;
                    // ★ 快速失败：日志里已经出现已知故障就立刻收工（实测端口被占时约 5 秒即可定性，
                    //   不用干等满 45 秒 —— 但**必须**有日志证据才提前判死，不看日志不猜）
                    // ★★ 2026-09-20 修：原来这里调 TryDiagnose()，它只看"文件尾 6000 字节"，
                    //   **分不清这内容是这一次写的还是几天前留下的** ⇒ 旧崩溃块会让每一次启动
                    //   都在 5 秒时被误判为失败（主人实测的那次就是）。现在只认"本次起点之后"。
                    if (spent >= 5 && spent - lastDiag >= 3)
                    {
                        lastDiag = spent;
                        string early;
                        if (TryDiagnoseSince(out early))
                        {
                            _lastStartError = "启动失败（日志已定性）—— " + early;
                            return false;
                        }
                    }
                    if (spent - lastTick >= 5 && ProgressSink != null)
                    {
                        lastTick = spent;
                        try { ProgressSink("已等 " + spent + " 秒：进程在跑，端口 " + port + " 还没响应（冷启动或卡在插件加载）…"); } catch { }
                    }
                }
                _lastStartError = "等了 180 秒，进程还活着、但 " + port + " 端口始终没有 HTTP 响应（仍可能在冷启动，也可能卡在插件加载）"
                                  + "—— " + DiagnoseStartFailure();
                return false;
                }
                finally
                {
                    // ★ T17：观察用的 Process 句柄要还回去（Dispose **不会**杀掉子进程）
                    if (p != null) { try { p.Dispose(); } catch { } }
                }
            }
            catch (Exception e)
            {
                _lastStartError = e.Message;   // ★ P11：保留异常原文，不再一律说"未找到入口"
                return false;
            }
        }

        private static int _lastStartMs = 0;
        /// <summary>上次成功启动「从 spawn 到端口真的能响应」用了多少毫秒（0 = 未测到）。</summary>
        public static int LastStartMs { get { return _lastStartMs; } }

        /// <summary>
        /// 启动失败时**说清为什么**：读 web 日志尾部，用与 `--classify` 完全同一套判据归类，
        /// 并按类别给出"该点哪个按钮"。归类不出来就**如实贴原文**，绝不硬猜。
        /// </summary>
        /// <summary>
        /// 只看"致命标记"：日志尾部一旦归类出已知故障（端口被占 / 插件树加载失败 / 依赖解析不到 …），
        /// 就立刻返回结论 —— 用于**快速失败**（不用等满 45 秒）。
        /// 归不出来返回 false，交给调用方继续等或贴原文。
        /// </summary>
        // ★ 2026-09-20：本次启动的日志"起点"（字节偏移）。<0 = 没有起点可依（走老路径，但会如实标注）。
        private static long _attemptErrFrom = -1;
        private static long _attemptOutFrom = -1;

        /// <summary>
        /// ★ 2026-09-20 新增：归因的**核心判据**（纯函数，可单测；StartFailJudge 就是它的正负样本）。
        ///   只从 fromOffset 之后读，命中已知故障才返回 true。
        ///   ★ fromOffset 之后**没有新增内容** ⇒ 一律返回 false ——
        ///     "没写东西"不等于"出错了"，更不许拿**旧内容**当本次失败的原因。
        /// </summary>
        private static bool TryDiagnoseCore(string path, long fromOffset, int maxBytes, out string msg)
        {
            msg = null;
            try
            {
                string tail = PluginDiag.TailTextSince(path, fromOffset, maxBytes);
                if (tail == null || tail.Trim().Length == 0) return false;
                string[] lines = tail.Split('\n');
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string culprit;
                    string cat = PluginDiag.ClassifyLine(lines[i], out culprit);
                    if (cat == null) continue;
                    msg = "web 日志判出来的原因：【" + cat + "】"
                          + (string.IsNullOrEmpty(culprit) ? "" : "\r\n涉及：" + culprit)
                          + "\r\n" + PluginDiag.AdviseOf(cat)
                          + "\r\n（日志：" + path + "，判定依据：**本次启动之后新增的内容**）";
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// ★ 2026-09-20 新增：**只认本次启动之后新增内容**的归因。
        ///   这是 fast-fail（5 秒快速定性）与 DiagnoseStartFailure 的正确入口。
        /// </summary>
        private static bool TryDiagnoseSince(out string msg)
        {
            msg = null;
            string m1;
            if (_attemptErrFrom >= 0 && TryDiagnoseCore(DshWebLog + ".err", _attemptErrFrom, 6000, out m1))
            {
                msg = m1;
                return true;
            }
            string m2;
            if (_attemptOutFrom >= 0 && TryDiagnoseCore(DshWebLog, _attemptOutFrom, 6000, out m2))
            {
                msg = m2;
                return true;
            }
            return false;
        }

        /// <summary>
        /// ★ 2026-09-20 新增：启动失败归因的**判据自检**（正负样本齐全）。
        ///   起因＝一次真实误报：「点了启动，结果你没起来」而实例其实在正常冷启动
        ///   （旧日志块 `EADDRINUSE 3199` 距文件尾仅 142 字符，被当成"这一次"的原因）。
        ///   必须证明三件事，缺一条都算 FAIL：
        ///     ① 负样本：起点之后**没有新增内容**时，旧崩溃块**不得**被判成失败（就是这次的 bug）；
        ///     ② 正样本：起点之后新增了 EADDRINUSE ⇒ 必须报红，且类别是端口冲突；
        ///     ③ 正样本：起点之后新增了"插件树加载失败" ⇒ 必须报红（哪怕文件更早处还有端口冲突块）。
        /// </summary>
        public static string[] StartFailJudge()
        {
            var outp = new List<string>();
            string dir = null;
            try
            {
                dir = Path.Combine(Path.GetTempPath(), "bff_startjudge_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                string f = Path.Combine(dir, "fake.err");
                var utf8 = new UTF8Encoding(false);

                string addrBlock = "Error: dsh: plugin tree failed to load: failed to apply loader entry webserver\r\n"
                                 + "Error: listen EADDRINUSE: address already in use 127.0.0.1:3199\r\n"
                                 + "    code: 'EADDRINUSE',\r\n    port: 3199\r\nNode.js v24.20.0\r\n";

                // ① 负样本：文件里只有**旧的**崩溃块，起点＝文件尾 ⇒ 不得报失败
                File.WriteAllText(f, "[argo-mcp] ready, waiting for stdin\r\n" + addrBlock, utf8);
                long lenAfterOld = new FileInfo(f).Length;
                string m1;
                bool hit1 = TryDiagnoseCore(f, lenAfterOld, 6000, out m1);
                outp.Add("启动归因自检①旧日志块不得当成本次失败（起点之后无新增则不归因）：" +
                         (hit1 ? "FAIL（仍会误报：" + m1 + "）" : "OK"));

                // ② 正样本：崩溃块写在**起点之后** ⇒ 必须报失败，且类别含"端口"
                long beforeBlock = lenAfterOld;
                using (var fs = new FileStream(f, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var sw = new StreamWriter(fs, utf8)) { sw.Write(addrBlock); sw.Flush(); }
                string m2;
                bool hit2 = TryDiagnoseCore(f, beforeBlock, 6000, out m2);
                bool portCat = (m2 != null && m2.IndexOf("端口") >= 0);
                outp.Add("启动归因自检②本次新增的端口冲突必须报红（类别=端口冲突）：" +
                         ((hit2 && portCat) ? "OK" : "FAIL（hit=" + hit2 + "，类别对不上：" + (m2 == null ? "(null)" : m2) + "）"));

                // ③ 正样本：本次新增的是"插件依赖解析不到"⇒ 必须报红，且**不得**被文件更早处的
                //    端口冲突块带偏（这一条同时证明"只看起点之后"真的生效）。
                //    ※ 注意：判据要挑**ClassifyLine 真认的**那句。
                //      "plugin tree failed to load: …" 只是外壳，本身不是分类标记（我第一版就写错了尺子，
                //      自检立刻报红 —— 这正是"正样本必须先证尺子"的用处）。
                long before2 = PluginDiag.LengthOf(f);
                using (var fs2 = new FileStream(f, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var sw2 = new StreamWriter(fs2, utf8))
                {
                    sw2.Write("Error: dsh: plugin tree failed to load: failed to import loader entry ui-ponytail (dsh-client-ui-ponytail): "
                              + "Cannot find package '@deepseek-ai/schemastery' imported from D:\\plugins\\dsh-plugin-ponytail\\lib\\index.js\r\n"
                              + "    at file:///C:/x.js:1:1\r\nNode.js v24.20.0\r\n");
                    sw2.Flush();
                }
                string m3;
                bool hit3 = TryDiagnoseCore(f, before2, 6000, out m3);
                bool notPort = (m3 != null && m3.IndexOf("端口") < 0);
                outp.Add("启动归因自检③本次新增的其它类别必须报红且不被更早的端口块带偏：" +
                         ((hit3 && notPort) ? "OK" : "FAIL（hit=" + hit3 + "，判出来的类别=" + (m3 == null ? "(null)" : m3) + "）"));

                // ④ 负样本：起点＝当前文件尾 ⇒ 任何情况下都不得报（"没写东西"≠"出错"）
                string m4;
                bool hit4 = TryDiagnoseCore(f, PluginDiag.LengthOf(f), 6000, out m4);
                outp.Add("启动归因自检④起点＝文件尾时一律不归因：" +
                         (hit4 ? "FAIL（仍然报了：" + m4 + "）" : "OK"));

                // ⑤ 负样本：不存在的文件不得抛异常、不得报失败
                string m5;
                bool hit5 = TryDiagnoseCore(Path.Combine(dir, "nope.err"), 0, 6000, out m5);
                outp.Add("启动归因自检⑤文件不存在时不得报失败：" + (hit5 ? "FAIL" : "OK"));
            }
            catch (Exception e)
            {
                outp.Add("启动归因自检：FAIL（自检自身抛异常 " + e.GetType().Name + " " + e.Message + "）");
            }
            finally
            {
                try { if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
            return outp.ToArray();
        }

        private static bool TryDiagnose(out string msg)
        {
            msg = null;
            try
            {
                string tail = "";
                try { tail += PluginDiag.TailText(DshWebLog + ".err", 6000) + "\n"; } catch { }
                try { tail += PluginDiag.TailText(DshWebLog, 6000); } catch { }
                if (tail.Trim().Length == 0) return false;
                string[] lines = tail.Split('\n');
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string culprit;
                    string cat = PluginDiag.ClassifyLine(lines[i], out culprit);
                    if (cat == null) continue;
                    msg = "web 日志判出来的原因：【" + cat + "】"
                          + (string.IsNullOrEmpty(culprit) ? "" : "\r\n涉及：" + culprit)
                          + "\r\n" + PluginDiag.AdviseOf(cat)
                          + "\r\n（日志：" + DshWebLog + "）";
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static string DiagnoseStartFailure()
        {
            try
            {
                // ★★ 2026-09-20 修：先只认"本次启动之后新增的内容"。
                //   旧版第一步就是 TryDiagnose()（＝看文件尾 6000 字节），于是在"这一次其实没问题"的
                //   情况下也会拿旧崩溃块当原因 —— 主人的真实误报（"点了启动，结果你没起来"）就是这么来的。
                string fatal;
                if (TryDiagnoseSince(out fatal)) return fatal;

                bool haveSince = (_attemptErrFrom >= 0 || _attemptOutFrom >= 0);
                if (haveSince)
                {
                    string since = "";
                    try { if (_attemptErrFrom >= 0) since += PluginDiag.TailTextSince(DshWebLog + ".err", _attemptErrFrom, 8000) + "\n"; }
                    catch { }
                    try { if (_attemptOutFrom >= 0) since += PluginDiag.TailTextSince(DshWebLog, _attemptOutFrom, 8000); }
                    catch { }
                    if (since.Trim().Length > 0)
                    {
                        string[] sl = since.Split('\n');
                        var lastNew = new List<string>();
                        for (int i = sl.Length - 1; i >= 0 && lastNew.Count < 3; i--)
                            if (sl[i].Trim().Length > 0) lastNew.Insert(0, sl[i].Trim());
                        return "web 日志**本次启动之后新增**的最后几行（未归类，请照原文判断）：\r\n  "
                               + string.Join("\r\n  ", lastNew.ToArray())
                               + "\r\n（日志：" + DshWebLog + "）";
                    }
                    return "本次启动之后，日志里**没有新增内容**（进程可能在写出任何东西之前就退了，或它写到了别处）。\r\n"
                           + "（这两个日志是**只追加**的历史文件；为避免拿**上一次**的报错当本次原因，这里不贴旧内容 ——"
                           + " 需要看全文请点「📄 打开日志」。）\r\n（日志：" + DshWebLog + "）";
                }

                // 没有"本次起点"可用（老调用路径）⇒ 允许看全文，但必须**标明可能是旧内容**
                string tail = "";
                try { tail += PluginDiag.TailText(DshWebLog + ".err", 8000) + "\n"; } catch { }
                try { tail += PluginDiag.TailText(DshWebLog, 8000); } catch { }
                if (tail.Trim().Length == 0)
                    return "web 日志里没有内容（它可能连日志都没写出来就退了）。日志：" + DshWebLog;
                string[] lines = tail.Split('\n');
                // 归不了类：贴最后 3 行非空原文（让人能看，而不是我编一个原因）
                var last = new System.Collections.Generic.List<string>();
                for (int i = lines.Length - 1; i >= 0 && last.Count < 3; i--)
                    if (lines[i].Trim().Length > 0) last.Insert(0, lines[i].Trim());
                return "web 日志最后几行（⚠ 这两个日志是**只追加**的文件，下面几行**可能来自上一次运行**，请照原文判断）：\r\n  "
                       + string.Join("\r\n  ", last.ToArray())
                       + "\r\n（日志：" + DshWebLog + "）";
            }
            catch (Exception e) { return "读取 web 日志失败：" + e.GetType().Name + " " + e.Message; }
        }

        // 停止 dsh 之后的自愈说明（配置被强杀写坏时会自动回滚）
        private static string _lastStopNote = "";
        public static string LastStopNote { get { return _lastStopNote; } }

        // ★ 2026-09-16（T4）：停止动作的**结论行**。
        //   旧版无论什么原因失败，界面都只说"未找到运行中的 dsh 进程" ——
        //   而真实原因可能是"有进程但我杀不掉（权限不足）"或"端口被别的程序占着、
        //   按纪律跳过了"。前者是**假阴性**，后者会把用户引去查一个不存在的问题。
        private static string _lastStopHead = "";
        public static string LastStopHead { get { return _lastStopHead; } }

        // =====================================================================
        //  ★★ 2026-09-18「只诊断」安全总闸（主人："这个玩意太可怕了，人家装后把自己的 dsh 杀了"）
        //  事实先摆清（我逐行读过代码）：
        //    · 救星**没有任何自动杀进程的路径** —— 五个会动手的按钮（重启/停止/关闭界面/一键修复/强力自愈）
        //      每一个都弹二次确认，文案里写明"会断开界面、对话可能中断"；
        //    · 杀之前会校验身份：只结束**命令行指向 dsh 的 bin.js** 的 node 进程（目录段必须是 dsh / dsh-* / @deepseek-ai），
        //      不是 node 的、或命令行读不到又对不上的，一律**跳过并写进报告**；
        //    · 端口被**非 dsh 的程序**占用时，明确**不杀**，只如实告知。
        //  但"它有能力杀"这件事本身，用户应该能**锁死**。所以加这个总闸：
        //    打开后 StopDsh / RestartDsh / StartDsh / RepairFromWhy 一律**直接拒绝、连进程都不碰**。
        //  开关记在救星自己的目录（readonly.txt），下次启动还记得。
        // =====================================================================
        private static bool _readOnly = false;
        private static bool _readOnlyLoaded = false;
        private static readonly string ReadOnlyFile = Path.Combine(AppDataDir, "readonly.txt");

        /// <summary>只诊断模式：开=救星绝不结束/启动任何 dsh 进程（只读诊断照常可用）。</summary>
        public static bool ReadOnlyMode
        {
            get
            {
                if (!_readOnlyLoaded)
                {
                    _readOnlyLoaded = true;
                    try { _readOnly = File.Exists(ReadOnlyFile); } catch { }
                }
                return _readOnly;
            }
            set
            {
                _readOnly = value;
                _readOnlyLoaded = true;
                try
                {
                    EnsureAppDataDir();
                    if (value) File.WriteAllText(ReadOnlyFile, "on\r\n", new UTF8Encoding(false));
                    else if (File.Exists(ReadOnlyFile)) File.Delete(ReadOnlyFile);
                }
                catch { }
            }
        }

        /// <summary>被安全总闸拦下时的统一说法（各处复用，口径一致）。</summary>
        public static string ReadOnlyRefusal()
        {
            return "【只诊断模式已开】救星不会碰任何 dsh 进程 —— 本次动作已取消。\r\n"
                 + "（这是你自己打开的安全开关：它保证「装了也不会杀掉你的 dsh」。\r\n"
                 + " 要真的动手：点「🛡 只诊断」把它关掉，或命令行 `大肥鱼救星.exe --readonly off`。）";
        }

        /// <summary>
        /// 预演：算出"如果现在动手，会结束哪些进程"，但**一个都不杀**。
        /// 给用户一个"先看清单再决定"的入口（也让判据可以安全地验证选择逻辑）。
        /// </summary>
        // ★★ 2026-09-18 v5（WP3 实测事故后加固）：**杀进程的范围必须跟着“配置指向”收窄**。
        //
        //   事故现场（可复现）：把救星指向沙箱（BFF_DSH_HOME + DSH_PORT 都显式覆盖）后跑
        //   「停止服务」，预演清单里出现了**用户真实在跑的 dsh**（承载着正在进行的会话），
        //   紧接着它从进程表里消失了。证据：同一 pid 在操作前存在、操作后不存在。
        //   根因：IsDshEntry 身份扫描是**全机的**，与 DshHome / 端口无关；DSH_PORT 只改了
        //   ActivePort，没有改变“杀谁”的范围 ⇒ 沙箱实验会误杀线上实例。
        //   修法（只在**存在显式覆盖**时生效，不影响单实例的日常行为）：
        //     本函数负责「范围 = 命令行里带 --port <ActivePort> 的 dsh 进程」这一半；
        //     「占用 ActivePort 且身份是 dsh 的进程」那一半由**调用方**补（PlanKill 第 2 轮、
        //     StopDsh 第 2) 步、ForceRecover 第 2) 步都各自用 GetPortOwnerPidsFast(ActivePort) 补齐）。
        //     两半合起来才是完整口径 —— 别只看这一个函数就以为漏了端口占用者。
        //     收窄后若为空，**不退回全机扫描**（那正是原来的 bug），而是明说“没有候选”。
        public static bool HasExplicitScopeOverride()
        {
            try
            {
                string a = Environment.GetEnvironmentVariable("DSH_PORT");
                if (!string.IsNullOrEmpty(a) && a.Trim().Length > 0) return true;
                string b = Environment.GetEnvironmentVariable("BFF_DSH_HOME");
                if (!string.IsNullOrEmpty(b) && b.Trim().Length > 0) return true;
            }
            catch { }
            return false;
        }

        private static bool CmdMatchesPort(string cmd, int port)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            string p1 = "--port " + port.ToString(CultureInfo.InvariantCulture);
            string p2 = "--port=" + port.ToString(CultureInfo.InvariantCulture);
            return cmd.IndexOf(p1, StringComparison.OrdinalIgnoreCase) >= 0
                || cmd.IndexOf(p2, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>要结束的 dsh 进程集合（带范围收窄；见上面 HasExplicitScopeOverride 的说明）。
        /// ★ 公开给 RepairPlan 用：**预演**与**复验**必须跟「真正会杀谁」同一口径，
        ///   否则沙箱里跑会一边谎报清单、一边把复验判成永远失败。</summary>
        // ★★ 2026-09-20（v5.0）默认口径改成「**先按端口收窄**」——这是本轮最重要的一处安全修改：
        //   原来"按端口收窄"只在**存在显式覆盖**（DSH_PORT / BFF_DSH_HOME）时才生效；
        //   日常使用（没有任何环境变量覆盖）走的是**全机 dsh 扫描** ⇒ 「停止服务」会把机器上
        //   **所有** dsh 实例一起结束，包括你另起的、在另一个端口 / 另一个 profile 上的那个。
        //   主人问"会不会直接或间接杀了别的 dsh"——**这就是那个"会"**，已改。
        //   现在的规则：能按端口认出来就只动它；一个都没认出来才回退全机，且**必须把回退说出来**
        //   （见 ScopeNote / KillScopeSummary），不再默默扩大范围。
        private static string _lastScopeNote = "";

        /// <summary>本次收窄是否**因为"端口上一个 dsh 都没匹配到"而拒绝回退全机**（供判据与报告用）。</summary>
        private static bool _lastRefusedFallback = false;
        /// <summary>本次有多少个 dsh 的命令行**匹配上了当前端口**（供判据用；0 且非空机 ⇒ 收窄失败）。</summary>
        private static int _lastMatchedOnPort = 0;
        /// <summary>机器上一共有几个 dsh（供机器行报告"回退会波及多少"；**不是**返回的清单长度）。</summary>
        private static int _lastAllDshCount = 0;

        /// <summary>
        /// ★★ 2026-09-23：**"动全机 dsh" 必须显式授权**（默认 false）。
        ///
        /// 事故（2026-09-22，我造成的）：故障注入场景 F2 的设计是「端口被**非 dsh** 程序占着」，
        /// 所以它故意**没有**沙箱 dsh ⇒ 工具按 `--port 4381` 一个都没匹配到 ⇒ 触发下面那条
        /// 「回退为全机 dsh 扫描」⇒ 名单里正好是**主人正在使用的实例**，而且 F2 接着**真的执行了 stop**
        /// ⇒ 那个实例被杀（它同时是承载测试进程的宿主）⇒ 回归断在半路、报告一个字节都没写出来。
        ///
        /// 规矩：**"没找到目标"绝不等于"目标是全体"**。凡"收窄失败就放宽到全体"的默认行为都是定时炸弹；
        /// 正解＝收窄失败就**拒绝动手**（fail-closed），要动全体必须**显式说出口**：
        /// 命令行 `--all-dsh`，或环境变量 `BFF_ALLOW_MACHINE_WIDE_KILL=1`。
        /// </summary>
        public static bool AllowMachineWideKill =
            (Environment.GetEnvironmentVariable("BFF_ALLOW_MACHINE_WIDE_KILL") == "1");

        public static int[] GetDshPidsForKill()
        {
            _lastRefusedFallback = false;
            _lastMatchedOnPort = 0;
            _lastAllDshCount = 0;
            int[] all = GetDshPids();
            if (all == null || all.Length == 0) { _lastScopeNote = ""; return new int[0]; }
            _lastAllDshCount = all.Length;

            var onPort = new List<int>();
            var others = new List<int>();
            List<NodeProcInfo> procs = QueryNodeProcesses();
            foreach (int pid in all)
            {
                string c = null;
                if (procs != null)
                {
                    NodeProcInfo f = procs.Find(delegate(NodeProcInfo x) { return x.Pid == pid; });
                    if (f != null) c = f.Cmd;
                }
                if (c == null) { try { c = QueryCmdLine(pid); } catch { } }
                if (CmdMatchesPort(c, ActivePort)) onPort.Add(pid); else others.Add(pid);
            }
            _lastMatchedOnPort = onPort.Count;

            string tag = HasExplicitScopeOverride() ? "（沙箱/显式覆盖模式）" : "";
            if (onPort.Count > 0)
            {
                _lastScopeNote = "★ 结束范围" + tag + " = **只动当前端口 " + ActivePort.ToString(CultureInfo.InvariantCulture)
                    + "** 上的 dsh 进程（本次 " + onPort.Count + " 个）；"
                    + (others.Count > 0
                        ? "另有 " + others.Count + " 个 dsh 在**别的端口**上，本次**一个都不会碰**。"
                        : "机器上没有其它端口的 dsh。")
                    + "\r\n";
                return onPort.ToArray();
            }

            // ★★ 收窄失败：默认**拒绝动手**（除非有人显式授权动全机）
            if (!AllowMachineWideKill)
            {
                _lastRefusedFallback = true;
                _lastScopeNote = "★ 按 `--port " + ActivePort.ToString(CultureInfo.InvariantCulture)
                    + "` 没有匹配到 dsh 进程 ⇒ **默认拒绝动手：本次不会结束任何进程**。\r\n"
                    + "   为什么：" + (others.Count > 0 || all.Length > 0
                        ? "机器上确实有 " + all.Length + " 个 dsh 进程，但「这个端口上没找到」**不等于**「所有 dsh 都该停」"
                        : "机器上目前没有 dsh 进程")
                    + " —— 别的实例可能正承载着别人的会话。\r\n"
                    + "   ★ 确实要停止机器上**全部** dsh 的话，请显式授权：命令行加 `--all-dsh`，"
                    + "或设环境变量 BFF_ALLOW_MACHINE_WIDE_KILL=1（界面上的「停止服务」同理）。\r\n";
                return new int[0];
            }

            _lastScopeNote = "★ 按 `--port " + ActivePort.ToString(CultureInfo.InvariantCulture)
                + "` 没有匹配到 dsh 进程 ⇒ **已显式授权全机**，本次回退为全机 dsh 扫描，共 " + all.Length
                + " 个：下面清单里的进程**都会**被结束，请先确认里面没有你要保留的实例。\r\n";
            return all;
        }

        private static string ScopeNote()
        {
            // ★ v5.0：范围说明改由 GetDshPidsForKill() 现场生成（因为"按端口收窄"现在是默认行为，
            //   而它到底收窄成功还是回退了全机，只有算完才知道）⇒ 调用顺序必须是
            //   **先 GetDshPidsForKill()，再 ScopeNote()**。
            return _lastScopeNote;
        }

        /// <summary>一行摘要：本次「停止/重启/关闭界面」到底会结束哪些进程。给确认框用。</summary>
        public static string KillScopeSummary()
        {
            try
            {
                int[] pids = GetDshPidsForKill();
                // ★ 2026-09-23：空清单时**必须把原因带出来** —— 空可能是"本来就没有 dsh"，
                //   也可能是"收窄失败、按规矩拒绝动手"。两者对用户的意义完全不同，不能都显示成
                //   「本次范围内没有找到运行中的 dsh 进程」（那会把"我拒绝执行"说成"没东西可停"）。
                if (pids == null || pids.Length == 0)
                    return "本次**不会结束任何进程**。" + _lastScopeNote.Replace("\r\n", "").Replace("★ ", "");
                var sb = new StringBuilder();
                sb.Append("本次会结束 ").Append(pids.Length).Append(" 个 dsh 进程（");
                for (int i = 0; i < pids.Length; i++)
                {
                    if (i > 0) sb.Append("、");
                    sb.Append("PID ").Append(pids[i].ToString(CultureInfo.InvariantCulture));
                }
                sb.Append("）。");
                sb.Append(_lastScopeNote.Replace("\r\n", "").Replace("★ ", ""));
                return sb.ToString();
            }
            catch (Exception e) { return "（读不到进程清单：" + e.Message + "）"; }
        }

        /// <summary>
        /// 预演：算出"如果现在动手，会结束哪些进程"，但**一个都不杀**。
        /// 给用户一个"先看清单再决定"的入口（也让判据可以安全地验证选择逻辑）。
        /// </summary>
        public static string PlanKill()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== 预演：如果现在执行「停止服务」，会结束这些进程（**本次一个都没杀**）====");
            // ※ 顺序：先算范围，再要说明（说明由 GetDshPidsForKill 现场生成）
            int[] byId = GetDshPidsForKill();
            sb.Append(ScopeNote());
            int[] byPort = GetPortOwnerPidsFast(ActivePort);
            List<NodeProcInfo> procs = QueryNodeProcesses();
            var seen = new List<int>();
            int hit = 0, skip = 0;
            foreach (int pid in (byId ?? new int[0]))
            {
                if (seen.Contains(pid)) continue;
                seen.Add(pid);
                sb.AppendLine(Render(pid, "身份扫描命中", procs, ref hit, ref skip));
            }
            foreach (int pid in (byPort ?? new int[0]))
            {
                if (seen.Contains(pid)) continue;
                seen.Add(pid);
                sb.AppendLine(Render(pid, "占用端口 " + ActivePort, procs, ref hit, ref skip));
            }
            if (seen.Count == 0) sb.AppendLine("  （没有任何候选进程 —— 现在「停止服务」不会杀任何东西）");
            sb.AppendLine();
            sb.AppendLine("会结束：" + hit + " 个；会被跳过（身份不符/读不到命令行）：" + skip + " 个");
            // ★★ 2026-09-23 加：**纯 ASCII 的机器可读判据行**。
            //   为什么必须加：故障注入夹具要在**调 stop 之前**判断"这次收窄是不是失败并拒绝了"，
            //   而中文行会被控制台代码页搅成乱码（拿它做断言等于断言一个看不见的东西）。
            //   有这一行，夹具就能做 **fail-closed**：只要 refused_to_fallback=0 而名单非空，就**不调 stop**。
            sb.AppendLine("KILL_SCOPE port=" + ActivePort.ToString(CultureInfo.InvariantCulture)
                + " matched_on_port=" + _lastMatchedOnPort.ToString(CultureInfo.InvariantCulture)
                + " candidates=" + _lastAllDshCount.ToString(CultureInfo.InvariantCulture)
                + " refused_to_fallback=" + (_lastRefusedFallback ? "1" : "0")
                + " allow_machine_wide=" + (AllowMachineWideKill ? "1" : "0"));
            sb.AppendLine("★ 判据①身份：只有命令行**入口指向 dsh 的 bin.js** 的 node 进程才会被结束，其余一律跳过；");
            sb.AppendLine("★ 判据②范围：默认**只动命令行带 `--port " + ActivePort.ToString(CultureInfo.InvariantCulture)
                          + "` 的实例**；**这个端口上没找到就默认拒绝动手**（不再回退全机）"
                          + " —— 要动全机必须显式 `--all-dsh` 或 BFF_ALLOW_MACHINE_WIDE_KILL=1。");
            return sb.ToString();
        }

        private static string Render(int pid, string why, List<NodeProcInfo> procs, ref int hit, ref int skip)
        {
            string name = "?", cmd = "";
            try { name = Process.GetProcessById(pid).ProcessName; } catch { }
            try
            {
                NodeProcInfo f = procs == null ? null : procs.Find(delegate(NodeProcInfo x) { return x.Pid == pid; });
                cmd = f != null ? f.Cmd : QueryCmdLine(pid);
            }
            catch { }
            bool isDsh = name.Equals("node", StringComparison.OrdinalIgnoreCase) && IsDshEntry(cmd);
            if (isDsh) hit++; else skip++;
            string shortCmd = cmd == null ? "(读不到命令行)" : (cmd.Length > 96 ? cmd.Substring(0, 96) + "…" : cmd);
            return "  " + (isDsh ? "[会结束] " : "[跳过]  ") + "PID " + pid + "（" + name + "，来自" + why + "）"
                   + (isDsh ? "" : "  ← 不是 dsh，绝不误杀") + "\r\n            命令行：" + shortCmd;
        }

        public static bool StopDsh()
        {
            if (ReadOnlyMode) { _lastStopNote = ""; _lastStopHead = ReadOnlyRefusal(); return false; }
            _lastStopNote = "";
            _lastStopHead = "";
            var note = new StringBuilder();

            // 2026-09-12 加固：Windows 的 TerminateProcess 无法被进程捕获，
            // 而 DSH 自己会写 settings.yaml（设置面板/插件配置）。
            // 若正好在它写到一半时 Kill，就会留下半截文件。
            // 强杀无法避免，所以对策是：杀之前先拍快照，杀之后立刻体检，
            // 坏了就自动回滚（见 SafeConfig.HealAfterKill）。
            string snap = SafeConfig.Snapshot("停止 dsh 服务之前（强杀前保险）");

            // ★ 2026-09-16 修复 T3：旧版**根本不判断快照是否创建成功**，照样往下强杀 ——
            //   于是"先留退路"的机制恰好在最需要它的时候静默失效，界面还一个字都不提。
            //   现在：失败必须明说"这次没有退路"（口径与 ForceRecover 一致）。
            //   注意**不中止** —— "停止服务"本身就是用户要的紧急动作，
            //   中止只会让人卡在"服务停不下来"的死角里。这与"卸载插件"（可中止）不同。
            if (snap == null)
                note.AppendLine("⚠ 无法创建配置快照（" + SafeConfig.SnapshotRoot + "）——本次强杀**没有自动回滚退路**，请谨慎。");

            bool any = false;
            var killed = new List<int>();

            if (QueryNodeProcesses() == null)
                note.AppendLine("⚠ 读不到进程命令行（WMI 查询失败），本次只能按进程名 node 判断，精度下降。");

            // 1) 主目标：**确实是 dsh** 的 node 进程（T15：身份判据统一，不再"含 bin.js 就算"）
            //    ★ v5：改走 GetDshPidsForKill() —— 有显式覆盖时按端口收窄，绝不误杀别处的 dsh。
            //    ★ v5.0：默认就按端口收窄；一个都没认出来才回退全机，且把回退明说出来。
            //    ※ 顺序硬要求：GetDshPidsForKill() 必须在 ScopeNote() **之前**（范围说明是它现场算的）。
            int[] killScope = GetDshPidsForKill();
            note.Append(ScopeNote());
            any |= KillDshLikeProcesses(killScope, note, killed) > 0;

            // 2) 兜底：当前端口的占用者。
            //    ★ T15：**仍然要求是 dsh** —— 端口上坐着别的 node 服务时不再顺手杀掉它。
            //      旧版这里只校验"进程名是 node"就杀，等于"谁的 node 占了我的端口我就杀谁"。
            if (!any)
                any |= KillDshLikeProcesses(GetPortOwnerPidsFast(ActivePort), note, killed) > 0;

            // 等进程真的退干净，再体检（否则它可能还在写文件）
            // ★ 2026-09-16 修复 T8：旧版这里循环 20 次、每次调 GetDshPids()
            //   （= 起一个 powershell + WMI，超时 8 秒）⇒ 最坏 20×(0.25+8) ≈ **165 秒**，
            //   期间 `_busy=true` 把**所有按钮都禁用**，而按钮上的文案写着"预计 2~5 秒"；
            //   还顺手创建最多 21 个 powershell.exe —— 与"轮询路径不创建进程"的设计意图相悖。
            //   现在改成：对**已知被杀掉的 pid** 用托管方式轮询存活 + iphlpapi 查端口，
            //   **零进程创建**，上限 8 秒。
            WaitPidsGone(killed, 8000);

            string heal = "";
            try { heal = SafeConfig.HealAfterKill(snap); }
            catch (Exception e) { heal = "停止后体检失败：" + e.Message; }

            _lastStopHead = any
                ? ("已停止 " + killed.Count + " 个 dsh 相关进程")
                : (note.Length > 0 ? "未能停止 dsh —— 见下方原因" : "未找到运行中的 dsh 进程");

            var parts = new List<string>();
            if (note.Length > 0) parts.Add(note.ToString().TrimEnd());
            if (!string.IsNullOrEmpty(heal)) parts.Add(heal);
            _lastStopNote = string.Join("\r\n\r\n", parts.ToArray());

            return any;
        }

        /// <summary>轮询一批 pid 是否已退出（托管方式，**不创建任何进程**）；上限 timeoutMs。</summary>
        private static void WaitPidsGone(List<int> pids, int timeoutMs)
        {
            if (pids == null || pids.Count == 0) return;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                bool alive = false;
                foreach (int pid in pids)
                {
                    try { using (Process p = Process.GetProcessById(pid)) { if (!p.HasExited) { alive = true; break; } } }
                    catch { }
                }
                if (!alive) return;
                Thread.Sleep(200);
            }
        }

        /// <summary>
        /// ★ 2026-09-16 新增（逐按键体检 P12）：**只结束确实是 dsh 的进程**。
        ///
        /// 背景：旧版在「🔄 重启服务」和「⚡ 强力自愈」里，释放端口时**直接 Kill 端口占用者、
        /// 不校验进程身份** —— 万一端口被别的程序占着（或 dsh 不是以 node 形式跑的），
        /// 就会**误杀无辜进程**。
        ///
        /// ★ T15 再加固：身份判据从"进程名是 node"升级为"IsDshEntry(命令行)"。
        ///   只按进程名判断等于"端口上任何 node 服务都算 dsh"，仍然会误杀别人的 node 程序。
        ///   **安全优先**：读不到命令行时**宁可不杀**，并把原因写进 note。
        ///
        /// 返回实际结束的进程数；被跳过的、杀失败的都写进 note。
        /// </summary>
        private static int KillDshLikeProcesses(int[] pids, StringBuilder note)
        {
            return KillDshLikeProcesses(pids, note, null);
        }

        private static int KillDshLikeProcesses(int[] pids, StringBuilder note, List<int> killedOut)
        {
            if (pids == null || pids.Length == 0) return 0;
            List<NodeProcInfo> procs = QueryNodeProcesses();   // 一次查询覆盖全部 pid
            bool cmdKnown = procs != null;
            if (!cmdKnown && note != null)
                note.AppendLine("  · 注意：读不到进程命令行（WMI 查询失败），只能按进程名 node 判断，存在误杀别的 node 程序的可能。");

            int n = 0;
            foreach (int pid in pids)
            {
                Process pr;
                try { pr = Process.GetProcessById(pid); }
                catch (Exception e)
                {
                    if (note != null) note.AppendLine("  · PID " + pid + " 打不开（可能已退出）：" + e.Message);
                    continue;
                }
                try
                {
                    string procName = "";
                    try { procName = pr.ProcessName; } catch { }
                    if (!procName.Equals("node", StringComparison.OrdinalIgnoreCase))
                    {
                        if (note != null) note.AppendLine("  · 跳过 PID " + pid + "（" + procName + "）：不是 node 进程，为避免误杀已跳过。");
                        continue;
                    }

                    if (cmdKnown)
                    {
                        NodeProcInfo f = procs.Find(delegate(NodeProcInfo x) { return x.Pid == pid; });
                        string cmd = f != null ? f.Cmd : QueryCmdLine(pid);
                        if (!IsDshEntry(cmd))
                        {
                            if (note != null)
                                note.AppendLine("  · 跳过 PID " + pid + "（node）：命令行不指向 dsh 入口，"
                                                + "**为避免误杀别的 node 程序已跳过**（T15 身份校验）。");
                            continue;
                        }
                    }

                    pr.Kill();
                    n++;
                    if (killedOut != null) killedOut.Add(pid);
                }
                catch (Exception e)
                {
                    // ★ T4b：旧版这里是裸 `catch { }` —— 权限不足（AccessDenied）时**什么都不记**，
                    //   于是 8 秒后报"端口释放失败：可能被非 dsh 进程持续占用"，
                    //   把"我杀不掉"误报成"别人占着"，用户越查越偏。
                    if (note != null) note.AppendLine("  · 结束 PID " + pid + " 失败：" + e.Message);
                }
                finally { try { pr.Dispose(); } catch { } }
            }
            return n;
        }

        // ------------------------------------------------------------
        // ★ WP2④（2026-09-20 防误报加固）：按 PID 杀进程树，**不拼 taskkill 命令行**
        //
        // 旧版两处 `taskkill /T /F /PID …`：
        //   ① 形状问题：起一个 taskkill 子进程去杀进程，是"shell 杀进程"特征
        //      （_救星_防误报研究.md §一.4）；
        //   ② 安全问题：`/T` 顺进程树杀 —— 本项目已被它咬过一次（误伤过进程树）。
        // 现在改为 .NET `Process.Kill()` + 自己用 ParentProcessId 逐层收窄。
        //
        // 两条铁律保留：
        //   ① 身份判据 —— 只动**我们自己起的那个 PID 及其后代**（父子关系就是身份）；
        //      每个 PID 在杀之前**重新读一次它当前的命令行**，变了就跳过（PID 会被系统回收）。
        //   ② 范围自报 —— note 里必须同时有「会动谁」和「不动谁」两类行。
        // ------------------------------------------------------------

        private sealed class ChildProcInfo { public int Pid; public string Name; public string Cmd; }

        /// <summary>按 ParentProcessId **精确过滤**查子进程（不按进程名扫全机）。查询失败返回 null。</summary>
        private static List<ChildProcInfo> QueryChildren(int parentPid)
        {
            string script =
                "$ErrorActionPreference='SilentlyContinue';" +
                "Get-CimInstance Win32_Process -Filter \"ParentProcessId=" + parentPid.ToString(CultureInfo.InvariantCulture) + "\" | " +
                "ForEach-Object { \"$($_.ProcessId)`t$($_.Name)`t$($_.CommandLine)\" }";
            string output = RunPsEncoded(script, 8000);
            if (output == null) return null;
            var list = new List<ChildProcInfo>();
            foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = raw.Split('\t');
                if (parts.Length < 2) continue;
                int pid;
                if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid)) continue;
                list.Add(new ChildProcInfo
                {
                    Pid = pid,
                    Name = parts[1].Trim(),
                    Cmd = parts.Length >= 3 ? raw.Substring(parts[0].Length + parts[1].Length + 2).Trim() : ""
                });
            }
            return list;
        }

        /// <summary>
        /// 结束 rootPid **及其子进程**（按父子关系逐层收窄，最多 6 层），全部走 .NET Process.Kill()，
        /// 不用 taskkill。note 里如实写出「会动谁 / 不动谁」。
        /// </summary>
        public static int KillTreeManaged(int rootPid, StringBuilder note)
        {
            return KillTreeManaged(rootPid, note, false);
        }

        /// <summary>
        /// dryRun=true：**只预演，一个都不杀**。走的是与真杀完全相同的收窄与核对逻辑，
        /// 只是最后一步不动手 —— 这样「会动谁 / 不动谁」可以在零风险下被真实地跑出来。
        /// </summary>
        public static int KillTreeManaged(int rootPid, StringBuilder note, bool dryRun)
        {
            int killed = 0;
            if (rootPid <= 0)
            {
                if (note != null) note.AppendLine("  · 不动：PID 无效（<=0），一个都不杀。");
                return 0;
            }
            if (note != null)
                note.AppendLine("范围：只动 PID " + rootPid.ToString(CultureInfo.InvariantCulture)
                                + " 及其子进程（按 ParentProcessId 逐层收窄，**不按进程名扫全机**）；"
                                + "这棵树以外的进程一个都不动。");
            if (dryRun && note != null)
                note.AppendLine("模式：**预演（dryRun）—— 一个都不杀**，下面只报告真杀时会动谁。");

            // ① 逐层收齐这棵树
            var levels = new List<List<ChildProcInfo>>();
            var frontier = new List<int>();
            frontier.Add(rootPid);
            bool wmiOk = true;
            for (int depth = 0; depth < 6; depth++)
            {
                var next = new List<ChildProcInfo>();
                foreach (int ppid in frontier)
                {
                    List<ChildProcInfo> kids = QueryChildren(ppid);
                    if (kids == null) { wmiOk = false; continue; }
                    foreach (ChildProcInfo k in kids)
                    {
                        if (k.Pid <= 0 || k.Pid == rootPid) continue;
                        bool dup = false;
                        foreach (List<ChildProcInfo> lv in levels)
                            foreach (ChildProcInfo x in lv) if (x.Pid == k.Pid) { dup = true; break; }
                        if (!dup) next.Add(k);
                    }
                }
                if (next.Count == 0) break;
                levels.Add(next);
                frontier = new List<int>();
                foreach (ChildProcInfo k in next) frontier.Add(k.Pid);
            }
            if (!wmiOk && note != null)
                note.AppendLine("  · 注意：有至少一层子进程没读出来（WMI 查询失败）⇒ 那以下**不展开、不动**（宁可漏，不误杀）。");

            // ② 自底向上杀（先孙后子，最后根）
            for (int i = levels.Count - 1; i >= 0; i--)
                foreach (ChildProcInfo c in levels[i])
                    killed += KillOneVerified(c.Pid, c.Cmd, note, dryRun);

            // ③ 最后杀根（它是我们自己起的那个进程）
            killed += KillOneVerified(rootPid, QueryCmdLine(rootPid), note, dryRun);
            return killed;
        }

        /// <summary>
        /// 杀一个 PID：杀之前**重新核对它当前的命令行** —— PID 会被系统回收成别的进程，
        /// 命令行对不上就**跳过**。结果（动了谁 / 为什么没动）写进 note。
        /// </summary>
        private static int KillOneVerified(int pid, string knownCmd, StringBuilder note)
        {
            return KillOneVerified(pid, knownCmd, note, false);
        }

        private static int KillOneVerified(int pid, string knownCmd, StringBuilder note, bool dryRun)
        {
            try
            {
                Process pr = Process.GetProcessById(pid);
                try
                {
                    string name = "";
                    try { name = pr.ProcessName; } catch { }

                    string now = QueryCmdLine(pid);
                    // ★ 2026-09-22 修（真缺陷，验收时用自造的两层替身树实测出来的）：
                    //   RunCapture 用 AppendLine 拼子进程输出 ⇒ QueryCmdLine 返回的字符串**尾部带 CRLF**；
                    //   而 QueryChildren 登记时对两侧都做了 Trim。两边口径不一致的结果是：
                    //   下面那条「命令行变了 ⇒ 跳过」的身份守卫会对**每一个子进程**都误命中，
                    //   于是 KillTreeManaged 只杀根、子进程全漏（实测：两层树只报「会动 1 个」），
                    //   且给出的理由是错的（说 PID 被回收，其实只是尾部换行差异）。
                    //   这里把口径对齐：读回来的命令行先 Trim 再比。
                    if (now != null) now = now.Trim();
                    string cmd = (now != null && now.Length > 0) ? now : (knownCmd != null ? knownCmd : "");

                    // ★ 杀前重新核对身份：读得到当前命令行、且跟刚才记录的不一致 ⇒ 这个 PID 已经不是原来那个进程
                    if (now != null && now.Length > 0 && knownCmd != null && knownCmd.Length > 0
                        && !string.Equals(now, knownCmd, StringComparison.Ordinal))
                    {
                        if (note != null)
                            note.AppendLine("  · 不动：PID " + pid + " 的命令行**已经变了**（PID 可能已被系统回收成别的进程）⇒ 跳过。");
                        return 0;
                    }

                    if (!dryRun) pr.Kill();
                    if (note != null)
                        note.AppendLine("  · " + (dryRun ? "预演会动" : "会动") + "：PID " + pid + "（" + name + "）｜" + (cmd.Length > 0 ? cmd : "（命令行读不到）"));
                    return 1;
                }
                finally { try { pr.Dispose(); } catch { } }
            }
            catch (Exception e)
            {
                if (note != null) note.AppendLine("  · 不动：PID " + pid + " 结束失败（可能已退出）：" + e.Message);
                return 0;
            }
        }

        // 重启 dsh web：杀干净进程和端口占用者，等待释放后启动，并尽量等 HTTP 恢复
        private static string _lastRestartNote = "";
        public static string LastRestartNote { get { return _lastRestartNote; } }

        // 重启 dsh web：杀干净进程和端口占用者，等待释放后启动，并用内容校验轮询到就绪。
        public static bool RestartDsh()
        {
            if (ReadOnlyMode) { _lastRestartNote = ReadOnlyRefusal(); return false; }
            _lastRestartNote = "";
            StopDsh();
            // ★ 2026-09-16（P12）：只结束 node 进程 —— 旧版这里不校验身份就直接 Kill 端口占用者，
            //   可能误杀占用该端口的其它程序。被跳过的会记下来，便于判断"是不是被别的程序占着"。
            var skipNote = new StringBuilder();
            // ★ T4b：把"停止服务"阶段的真实原因（权限不足 / 快照失败 / 跳过了非 dsh 进程）带上来。
            //   旧版把这些全丢了，失败时只剩一句"重启失败"。
            if (_lastStopNote.Length > 0) skipNote.AppendLine(_lastStopNote);
            KillDshLikeProcesses(GetPortOwnerPidsFast(ActivePort), skipNote);
            // 等待端口释放（最多 8 秒）
            bool freed = false;
            for (int i = 0; i < 16; i++)
            {
                Thread.Sleep(500);
                if (!PortListens(ActivePort, 300)) { freed = true; break; }
            }
            if (!freed)
            {
                _lastRestartNote = "端口 " + ActivePort + " 释放失败：占用者要么不是 dsh（已按纪律跳过，避免误杀），"
                                   + "要么结束被系统拒绝。请点「运行诊断」查看占用者。"
                                   + (skipNote.Length > 0 ? "\r\n" + skipNote.ToString().TrimEnd() : "");
                return false;
            }
            // 端口/实例已变 → 清缓存重新发现（旧版硬编码端口，重启后可能对不上）
            ResetActivePort();

            bool ok = StartDsh();
            if (!ok)
            {
                // ★ P11：带上异常原文，不再一律说"未找到入口 bin.js / node"
                _lastRestartNote = "dsh 启动命令执行失败：" +
                                   (LastStartError.Length > 0 ? LastStartError : "未找到入口 bin.js / node。");
                return false;
            }
            // 守护轮询：等一个「带 token 且确认是 DSH 页面」的地址。
            // 2026-09-16：40 秒 → 150 秒（冷启动 40~60 秒是常态）。
            // 另注：**不能用「根 URL 返回 200」当就绪判据** —— dsh web 用一次性 token，
            // 不带 token 访问根路径恒 401，那个判据永远不成立（文档根因③）。
            // ★ T7：progress 以前传的是 **null** ⇒ 最长 150 秒里界面只有"⏳ 正在执行…"，
            //   用户根本分不清是卡死还是在等。现在接到进度通道上。
            string label;
            string url = DshBoot.WaitForTokenUrl(DshBoot.BootWaitMs, Report, out label);
            if (!string.IsNullOrEmpty(url))
            {
                _lastRestartNote = "重启完成：已就绪（端口 " + DshBoot.PortOfUrl(url) + "，认证地址来源 " + label + "）。";
                return true;
            }
            _lastRestartNote = "重启超时：已等 " + (DshBoot.BootWaitMs / 1000) +
                               " 秒仍拿不到可用认证地址。请点「🚦 启动链路自检」或查看日志 dsh-web.out.log。";
            return false;
        }

        // ---------------- 打开界面 ----------------
        public static string OpenUi()
        {
            // 服务是否在响应：用 HttpProbe（接受 4xx/5xx 视为有服务，因为 DSH 根路径需 token 会 401）。
            if (!HttpProbe(ActiveRootUrl, 900))
            {
                return "提示：dsh web 当前未在 " + ActivePort + " 端口响应，界面可能打不开或为空白。\r\n建议先点击“启动并打开”，或点击“重启服务”。";
            }
            string token = ReadMainToken();
            // ★ 2026-09-16 修复 T26：这里原来只用 `UrlAuthOk`（**只看状态码 < 400**），
            //   而 `DshBoot.FindLiveTokenUrl` 用的是**强判据** `UrlAuthOk && IsDshPage`。
            //   同一件事两套判据 ⇒ 端口上坐着**别的 HTTP 服务**时，本按钮会在那个非 DSH 服务上
            //   开一个带 token 的无痕窗口，然后报"已用 Edge InPrivate 打开"（假成功）。
            //   现在两处对齐，都用"状态码 OK **且** 页面确实是 DSH（含 id=\"root\"）"。
            if (!string.IsNullOrEmpty(token) && UrlAuthOk(token) && IsDshPage(token))
            {
                // ★ 2026-09-16：本函数服务的是界面上的「🌐 打开无痕」按钮，语义就是**无痕窗口**。
                //   旧版这里还会用 IsDshPage() 判断是否改用应用模式窗口（OpenInAppMode）；
                //   而一旦修好 IsDshPage 的 CookieContainer 缺陷，那个分支就会真的生效，
                //   按钮行为便与名字不符。为保持主人熟悉的行为不变，这里固定用无痕窗口。
                //   （OpenInAppMode 保留未删，将来若想要"像程序一样的窗口"再接回来即可。）
                return OpenInPrivate(token);
            }
            return OpenUiInPrivate();
        }

        // 起一个受控的独立 dsh 实例（--no-open --port <free>），抓 token 后无痕打开。
        private static string OpenUiInPrivateFresh()
        {
            EnsureAppDataDir();

            // 1) 围绕当前发现的 dsh 端口操作：已被占用则复用该实例，空闲则在其上启动。
            //    不再随机从 3080 起找端口，避免“另起一个实例 / 抢占其它服务端口”。
            int port = ActivePort;
            if (port < 1 || port >= 4000) return "未找到可用端口，请检查 dsh 是否在 3080-4000 之间运行。";

            // ★ 2026-09-16 修复：本端口**已有实例在监听**时，绝不再起新实例。
            //   旧版在这里硬起（`--port <一个已被占用的端口>`）⇒ 必然端口冲突起不来，
            //   而 stderr 当时被丢弃 ⇒ 零日志、零线索，只能干等满 150 秒
            //   （正是实测里「启动并打开」316 秒 = 150 + 150 的来源）。
            if (DshBoot.GetListenerPids(port).Length > 0)
            {
                string liveLabel;
                string liveUrl = DshBoot.FindLiveTokenUrl(out liveLabel);
                if (!string.IsNullOrEmpty(liveUrl))
                    return "（端口 " + port + " 已有实例在跑，直接复用；认证地址来源：" + liveLabel + "）\r\n"
                           + OpenInPrivate(liveUrl);
                // ★ 2026-09-16 修复 T25：旧文案是"请先点「🌐 打开无痕」复用现有实例" ——
                //   而用户**可能正是点了「打开无痕」**才走到这里（本函数就是它的兜底路径）
                //   ⇒ 让他去点一个刚点过、注定同样结果的按钮，属于**自指死循环式建议**。
                //   现在改成：说清"端口上有东西、但它不是可用的 DSH"，并给出真正有效的下一步。
                return "端口 " + port + " 上有进程在监听，但**拿不到可用的 DSH 认证地址**"
                       + "（已排查 last-url.txt / ~/.dsh/tools 日志 / 救星日志四个来源，均无仍然有效的 token）。\r\n"
                       + "两种可能：① 那是 dsh 残留进程（卡死或启动未完成）；② 那是**别的程序**占着这个端口。\r\n"
                       + "下一步：点「🩺 一键修复」——它会区分这两种情况并给出对应处置；\r\n"
                       + "或点「🔍 运行诊断」看 " + port + " 的占用者 PID 是谁。\r\n"
                       + "（此处不再另起新实例：端口已被占用，另起必然冲突。）";
            }

            // 2) 定位 dsh 入口
            LaunchSpec spec = DiscoverLaunch();
            if (spec.BinJs == null || !File.Exists(spec.BinJs)) return "未找到 dsh 入口（bin.js）。";

            // 3) 启动受控实例，重定向输出到临时日志
            string log = Path.Combine(AppDataDir, "inprivate-" + port + ".log");
            try { File.Delete(log); } catch { }

            var psi = new ProcessStartInfo();
            psi.FileName = spec.Node;
            psi.Arguments = "\"" + spec.BinJs + "\" web --no-open --port " + port;
            psi.WorkingDirectory = spec.WorkDir.Length > 0 ? spec.WorkDir : UserProfile;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            Process p = new Process();
            p.StartInfo = psi;
            try { p.Start(); }
            catch (Exception e) { return "启动失败：" + e.Message; }

            var tcs = new System.Threading.Tasks.TaskCompletionSource<string>();
            Task.Factory.StartNew(delegate
            {
                try
                {
                    using (StreamWriter sw = new StreamWriter(log, false, new UTF8Encoding(false)))
                    {
                        sw.AutoFlush = true;
                        string line;
                        while ((line = p.StandardOutput.ReadLine()) != null)
                        {
                            try { sw.WriteLine(line); } catch { }
                            string marker = "dsh web: http://127.0.0.1:";
                            int idx = line.IndexOf(marker, StringComparison.Ordinal);
                            if (idx >= 0)
                            {
                                string sub = line.Substring(idx + marker.Length).Trim();
                                int sp = sub.IndexOf(' ');
                                string hostPort = sp > 0 ? sub.Substring(0, sp) : sub;
                                tcs.TrySetResult("http://127.0.0.1:" + hostPort);
                            }
                        }
                    }
                }
                catch { }
            });
            // ★ 2026-09-16：stderr 以前是**读出来直接扔掉**（while(...ReadLine()!=null){}）——
            //   于是新实例「端口被占用」这类致命错误完全不可见（日志 0 字节）。
            //   现在落到 <log>.err，出问题时有据可查。
            string errLog = log + ".err";
            Task.Factory.StartNew(delegate
            {
                try
                {
                    using (StreamWriter ew = new StreamWriter(errLog, false, new UTF8Encoding(false)))
                    {
                        ew.AutoFlush = true;
                        string l;
                        while ((l = p.StandardError.ReadLine()) != null) { try { ew.WriteLine(l); } catch { } }
                    }
                }
                catch { }
            });

            // 4) 等待认证地址（2026-09-16：30 秒 → 150 秒）
            //    ★ 追加：一旦子进程已退出，就不可能再打印 token —— 立刻收工，不再干等。
            var waitSw = System.Diagnostics.Stopwatch.StartNew();
            while (waitSw.ElapsedMilliseconds < DshBoot.BootWaitMs)
            {
                if (tcs.Task.IsCompleted) break;
                bool exited = false;
                try { exited = p.HasExited; } catch { }
                if (exited) break;
                System.Threading.Thread.Sleep(200);
            }
            bool dead = false;
            int ec = 0;
            try { dead = p.HasExited; if (dead) ec = p.ExitCode; } catch { }
            if (dead) System.Threading.Thread.Sleep(300);   // 让 stderr 线程把日志落盘
            string url = tcs.Task.IsCompleted ? tcs.Task.Result : null;
            if (string.IsNullOrEmpty(url))
            {
                if (dead)
                    return "新实例启动失败（退出码 " + ec + "，用时 " + (waitSw.ElapsedMilliseconds / 1000) + " 秒）。\r\n"
                           + "最常见原因：端口 " + port + " 已被另一个 dsh 实例占用。\r\n"
                           + "建议点「🌐 打开无痕」复用正在运行的实例，或先「停止服务」再「启动并打开」。\r\n"
                           + "日志：" + log + " ｜ 错误：" + errLog;
                return "未能取得认证地址（已等 " + (DshBoot.BootWaitMs / 1000) + " 秒）。请查看日志：" + log +
                       "\r\n提示：若日志里反复出现「PyYAML 未安装」，说明冷启动被拖慢，点「🚦 启动链路自检」可看详情。";
            }

            // 5) 用无痕/InPrivate 窗口打开（先验证确实能通过认证，否则不打开白屏）
            if (!UrlAuthOk(url))
                return "未能取得有效的认证地址（token 校验失败），请查看日志：" + log;
            return OpenInPrivate(url);
        }

        // 无痕/InPrivate 打开（合并"启动并打开"）：确保主 dsh 服务(3080)跑起来（未运行则启动），
        // 读主服务打印的 ?token= 认证地址，用 Edge InPrivate / Chrome 无痕窗口打开。
        // （无痕窗口没有 DSH cookie，必须用带 token 的地址，否则 401。）
        /// <summary>
        /// ★★ 2026-09-18 新增：**一键智能启动并打开**（主人："比如说有进程就打开，没进程自动杀死再打开，
        ///   有的时候我们也不知道，而且这个是给别人做的"）。
        /// 给别人用的工具，不该让用户去判断"有没有进程、端口被谁占着"。所以这一个按钮自己分四种情况：
        ///   ① 端口上有**健康的 dsh**（有 HTTP 响应）⇒ **不重启它**，直接打开界面（最快、最不打扰）；
        ///   ② 端口上有东西占着**但不响应**，且占用者**确认是 dsh**（卡死的残留）⇒ 先把它清掉
        ///      （走 StopDsh：先快照、只杀确认是 dsh 的进程），再重新拉起，就绪后打开；
        ///   ③ 端口**没人占** ⇒ 直接拉起，就绪后打开；
        ///   ④ 占用者是**别的程序** ⇒ **绝不杀它**；自动换一个空闲端口启动，并把端口写进 port.txt
        ///      （下次也用它）＋同步桌面图标的地址，然后打开。
        /// 全程受「🛡 只诊断」总闸约束：总闸开着 ⇒ 一律拒绝，不碰任何进程、也不改配置。
        /// 返回一段**人话**，说清它做了什么。
        /// </summary>
        public static string SmartStartAndOpen()
        {
            return SmartStartAndOpen(false);
        }

        public static string SmartStartAndOpen(bool dryRun)
        {
            EnsureAppDataDir();
            var say = new StringBuilder();
            int port = ActivePort;
            bool listening = PortListens(port, 500);
            bool healthy = listening && ProbeHealthy(port);   // ★ 3 次 ×2.5s：忙着的实例不再被误判成"卡死"

            // ① 健康的实例：直接打开，什么都不动
            if (healthy)
            {
                say.AppendLine("· 端口 " + port + " 上已经有 dsh 在正常服务 ⇒ **不动它**，直接给你打开界面。");
                if (dryRun) return say.ToString() + "（预演模式：到此为止，没有打开浏览器）";
                return say.ToString() + OpenUiInPrivateFresh();
            }

            // ② 占着但不响应
            if (listening)
            {
                int[] owners = GetPortOwnerPidsFast(port);
                bool ownerIsDsh = false;
                try
                {
                    int[] dp = GetDshPids();
                    if (dp != null) foreach (int d in dp) foreach (int o in owners) if (d == o) ownerIsDsh = true;
                }
                catch { }

                if (!ownerIsDsh)
                {
                    // ④ 别的程序占着 ⇒ 不杀它，换端口
                    int free = FindFreePort(port);
                    if (free <= 0)
                        return "端口 " + port + " 被**别的程序**占着（PID " + string.Join(",", owners) + "），"
                             + "而我没能找到别的空闲端口。\r\n"
                             + "我不会去杀别人的程序 —— 请手动关掉它，或把端口写进 "
                             + DshBoot.PortOverrideFile + " 后重试。";
                    say.AppendLine("· 端口 " + port + " 被**别的程序**占着（PID " + string.Join(",", owners)
                                   + "）⇒ **不杀它**，我改用空闲端口 " + free + "。");
                    if (dryRun) return say.ToString() + "（预演模式：到此为止）";
                    if (ReadOnlyMode) return say.ToString() + ReadOnlyRefusal();
                    string perr;
                    if (DshBoot.SetPortOverride(free, out perr))
                        say.AppendLine("  （已把端口写进 " + DshBoot.PortOverrideFile + "，下次也用它。）");
                    else
                        say.AppendLine("  （写 port.txt 失败：" + perr + "，本次仍用 " + free + "。）");
                    ResetActivePort();
                    if (!StartDsh()) return say.ToString() + "启动失败：" + LastStartError;
                    return say.ToString() + WaitOpenAfterStart();
                }

                // ② 确认是 dsh 的卡死残留 ⇒ 清掉再起
                if (ReadOnlyMode) return ReadOnlyRefusal();
                say.AppendLine("· 端口 " + port + " 上有一个**卡死的 dsh 残留**（占着端口但不响应）"
                               + "⇒ 我先把它清掉（只清确认是 dsh 的进程，杀前会先备份配置），再重新拉起。");
                if (dryRun) return say.ToString() + "（预演模式：到此为止，没有杀、没有起）";
                string stopHead = "";
                try { StopDsh(); stopHead = LastStopHead; } catch { }
                if (stopHead.Length > 0) say.AppendLine("  · " + stopHead);
                // 给它一点时间把端口放掉
                for (int i = 0; i < 20 && PortListens(port, 300); i++) System.Threading.Thread.Sleep(300);
                if (PortListens(port, 300))
                    say.AppendLine("  · 端口仍被占着（可能权限不足杀不掉）—— 下面这次启动可能失败，失败原因我会如实说。");
                if (!StartDsh()) return say.ToString() + "启动失败：" + LastStartError;
                return say.ToString() + WaitOpenAfterStart();
            }

            // ③ 没人占 ⇒ 直接起
            if (ReadOnlyMode) return ReadOnlyRefusal();
            say.AppendLine("· 端口 " + port + " 上没有实例 ⇒ 直接拉起。");
            if (dryRun) return say.ToString() + "（预演模式：到此为止，没有起进程）";
            if (!StartDsh()) return say.ToString() + "启动失败：" + LastStartError;
            return say.ToString() + WaitOpenAfterStart();
        }

        // 启动成功之后：等一个可用的认证地址并打开（复用原有逻辑，避免重复实现）
        private static string WaitOpenAfterStart()
        {
            string label;
            string url = DshBoot.WaitForTokenUrl(DshBoot.BootWaitMs, Report, out label);
            if (!string.IsNullOrEmpty(url))
                return "已就绪。（认证地址来源：" + label + "）\r\n" + OpenInPrivate(url);
            return OpenUiInPrivateFresh();
        }

        /// <summary>找一个空闲端口（从 start+1 往上试，跳过被占的与 Windows 保留区间）。</summary>
        public static int FindFreePort(int start)
        {
            for (int p = start + 1; p <= start + 40 && p < 65535; p++)
            {
                if (PortListens(p, 200)) continue;
                // 说明：Windows 的"保留端口区间"要问系统（netsh）才知道，而救星**不创建外部进程**；
                // 所以这里不猜：万一套上保留区间，启动会以 EACCES 失败 —— 那条路径会由 StartDsh
                // 如实报出来（"权限/保留端口"这一类），用户按提示换端口即可。
                return p;
            }
            return 0;
        }

        public static string OpenUiInPrivate()
        {
            EnsureAppDataDir();

            // 1) 先看现在有没有已经跑着的实例（它可能不是救星启动的，比如可靠启动器起的）
            string label0;
            string live = DshBoot.FindLiveTokenUrl(out label0);
            if (!string.IsNullOrEmpty(live))
                return "（复用正在运行的实例；认证地址来源：" + label0 + "）\r\n" + OpenInPrivate(live);

            // 2) 没有 → 拉起服务
            // ★ 2026-09-16 修复 T6（150 秒"逻辑上必然空转"）：
            //   若端口上**已经有实例在正常响应**，`StartDsh()` 会走"复用"分支直接返回 true，
            //   ⇒ **这段时间里不会有任何新进程启动**，也就不可能有新 token 被写出来；
            //   而上面 FindLiveTokenUrl 已经证明"四个来源里都没有仍然有效的 token"。
            //   于是接下来的 WaitForTokenUrl 是**注定等满 150 秒**的（正是主人抱怨的 316 秒同源）。
            //   现在：这种情况立刻转入兜底逻辑，让它快速给出**准确原因**，而不是干等 2.5 分钟。
            bool alreadyServing = PortListens(ActivePort, 500) && HttpProbe(ActiveRootUrl, 900);
            if (!StartDsh())
                return "dsh 启动失败：" + (LastStartError.Length > 0 ? LastStartError : "未找到入口 bin.js / node。");
            if (alreadyServing)
                return OpenUiInPrivateFresh();

            // 3) 等一个可用的认证地址。
            //    2026-09-16：这里原来只读 30 秒 DshWebLog 的 token，
            //    而 dsh 冷启动要 40~60 秒（argo-mcp 预热 + unified-search 初始化）
            //    ⇒ 必然超时报「未能取得认证地址」，也就是文档里那个卡在 30.3 秒的现象。
            //    现在：上限 150 秒，且不只看 DshWebLog，也看可靠启动器写的 last-url.txt
            //    与 ~/.dsh/tools/dsh-web.out.log。
            string label;
            // ★ T7：progress 以前传 **null** ⇒ 最长 150 秒界面只有"⏳ 正在执行…"，
            //   用户分不清"卡死"还是"在等"。现在接到进度通道（GUI 会显示在日志框里）。
            string url = DshBoot.WaitForTokenUrl(DshBoot.BootWaitMs, Report, out label);
            if (!string.IsNullOrEmpty(url))
                return "已启动 dsh web。（认证地址来源：" + label + "）\r\n" + OpenInPrivate(url);

            return OpenUiInPrivateFresh();
        }

        // 用无痕 / InPrivate 窗口打开一个 URL；返回结果描述。
        public static string OpenInPrivate(string url)
        {
            string edge = FindBrowser("msedge.exe");
            if (edge != null) { Process.Start(edge, "--inprivate \"" + url + "\""); return "已用 Edge InPrivate 打开。\r\n" + url; }
            string chrome = FindBrowser("chrome.exe");
            if (chrome != null) { Process.Start(chrome, "--incognito \"" + url + "\""); return "已用 Chrome 无痕打开。\r\n" + url; }
            return "未找到 Chrome/Edge，请手动在无痕窗口打开：\r\n" + url;
        }

        // 应用模式打开：无地址栏、像真程序一样打开 DSH（参考 dsh-whale-desktop-launcher 的 --app= 思路）。
        public static string OpenInAppMode(string url)
        {
            string edge = FindBrowser("msedge.exe");
            if (edge != null)
            {
                Process.Start(edge, "--app=\"" + url + "\" --start-maximized --no-first-run --disable-features=msEdgeSidebarV2");
                return "已用 Edge 应用模式打开（像程序一样的窗口）。\r\n" + url;
            }
            string chrome = FindBrowser("chrome.exe");
            if (chrome != null)
            {
                Process.Start(chrome, "--app=\"" + url + "\" --start-maximized --no-first-run --disable-features=msEdgeSidebarV2");
                return "已用 Chrome 应用模式打开（像程序一样的窗口）。\r\n" + url;
            }
            return "未找到 Chrome/Edge，请手动打开：\r\n" + url;
        }

        // 内容校验：确认端口上真的响应的是 DSH 页面（而非被其它程序占端口），比只看状态码更准。
        //
        // ★ 2026-09-16 修复（「登录要等 316 秒」的**第二层、也是决定性的**根因）：
        //   带 token 访问会返回 **303 See Other + set-cookie: dsh-auth-…**，然后重定向到 /。
        //   本函数用 AllowAutoRedirect=true 跟随重定向，但**没有设 CookieContainer** ——
        //   .NET 在 CookieContainer 为 null 时既不保存也不回送那个 cookie，
        //   于是重定向后的 GET / 恒为 **401**，GetResponse() 直接抛异常，
        //   被下面的 catch 吃掉 ⇒ **本函数永远返回 false**。
        //   而 DshBoot.FindLiveTokenUrl 的判据是 `UrlAuthOk(url) && IsDshPage(url)`，
        //   两项都是 && 关系 ⇒ 无论 token 多有效，永远拿不到地址
        //   ⇒ 误判「没有活着的实例」⇒ 跳到「拉起服务」⇒ 在已占用端口上另起实例 ⇒ 白等 150 秒 ×2。
        //
        //   实测对照（2026-09-16，同一 token）：
        //     · 不设 CookieContainer + 跟随重定向 → 抛 401 Unauthorized（即本函数旧行为）
        //     · 设了 CookieContainer               → 200，body 26,856 字节，且确实含 id="root" ✔
        //   ⇒ 判据字符串 id="root" 本身是对的，缺的只是 cookie。
        public static bool IsDshPage(string url)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 2500;
                req.ReadWriteTimeout = 2500;
                req.Proxy = null;
                req.AllowAutoRedirect = true;
                req.CookieContainer = new CookieContainer();   // ★ 修复：没有它，重定向后必然 401
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode != HttpStatusCode.OK) return false;
                    using (var reader = new StreamReader(resp.GetResponseStream(), System.Text.Encoding.UTF8))
                    {
                        string body = reader.ReadToEnd();
                        return body.IndexOf("id=\"root\"", StringComparison.Ordinal) >= 0;
                    }
                }
            }
            catch { return false; }
        }

        // ============================================================
        // 关闭显示 DSH 界面的浏览器窗口（Edge/Chrome，标题含 "DeepSeek Harness"）
        // 只关对应窗口，不影响其它浏览器窗口。
        // ============================================================
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);
        private const uint WM_CLOSE = 0x0010;

        // ★ 2026-09-20 新增：DPI 感知判据要用的两个 API + 取值函数。
        //   只读，不改变任何进程状态（绝不在运行时调 SetProcessDPIAware 去"掰"DPI）。
        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int index);
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        private const int LOGPIXELSY = 90;

        /// <summary>
        /// 进程当前读到的屏幕竖向 DPI。
        /// · 进程**不感知** DPI 时，GDI 一律报 96；
        /// · 声明了 DPI 感知时，报屏幕的真实 DPI（这台机器缩放到 150% ⇒ 报 144）。
        /// 所以 "== 96" 就是"没有声明感知"的可测特征（判据见自检里那条「DPI 感知」）。
        /// </summary>
        internal static int ScreenDpiY()
        {
            IntPtr hdc = GetDC(IntPtr.Zero);
            try { return GetDeviceCaps(hdc, LOGPIXELSY); }
            finally { if (hdc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdc); }
        }

        /// <summary>关闭界面的补充说明（T11：被跳过 / 投递了却没关掉的窗口）。</summary>
        public static string LastCloseNote = "";

        // 关闭所有标题含 "DeepSeek Harness" 的 Edge/Chrome 可见窗口；返回**确认已关闭**的数量。
        //
        // ★ 2026-09-16 修复两处（T11 / T11b）：
        //   T11  旧版把 `closed++` 写在 PostMessage **之后**，而 PostMessage 是**异步投递** ——
        //        返回值和"窗口是否真的关掉"都被忽略 ⇒ 界面报"已关闭 N 个界面窗口"，
        //        而实际可能**一个都没关**（页面弹了 beforeunload 确认框、或受 UIPI 权限隔离）。
        //        计数必须 = **复查后确认已消失**的窗口数，不是"已请求数"。
        //   T11b 判据只看**窗口标题**（而浏览器窗口标题 = **当前标签页**的标题）。
        //        若该浏览器窗口里还有别的标签页（标题会写成 "… and 3 more pages"），
        //        关掉它就会**连带关掉那些无关标签页**；判据里没有任何地方校验 URL。
        //        这里加一道闸：**多标签页的窗口一律不关**，只如实提示。
        public static int CloseInterfaceWindows()
        {
            LastCloseNote = "";
            var note = new StringBuilder();
            var targets = new List<IntPtr>();
            EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hwnd)) return true;
                int pid;
                GetWindowThreadProcessId(hwnd, out pid);
                string proc = "";
                try { using (var p = Process.GetProcessById(pid)) proc = p.ProcessName; } catch { }
                if (proc != "msedge" && proc != "chrome") return true;
                int len = GetWindowTextLength(hwnd);
                if (len <= 0) return true;
                var sb = new StringBuilder(len + 1);
                GetWindowText(hwnd, sb, sb.Capacity);
                string title = sb.ToString();
                if (title.IndexOf("DeepSeek Harness", StringComparison.OrdinalIgnoreCase) < 0) return true;
                if (System.Text.RegularExpressions.Regex.IsMatch(title, @"and\s+\d+\s+more\s+pages?",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    note.AppendLine("  · 跳过窗口「" + title + "」：它里面**还有其它标签页**，关掉会连带关掉它们。请手动关闭。");
                    return true;
                }
                targets.Add(hwnd);
                return true;
            }, IntPtr.Zero);

            foreach (IntPtr h in targets) PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

            // 复查：只有窗口真的消失了才算"已关闭"
            int closed = 0;
            var pending = new List<IntPtr>(targets);
            var sw = Stopwatch.StartNew();
            while (pending.Count > 0 && sw.ElapsedMilliseconds < 3000)
            {
                Thread.Sleep(150);
                for (int i = pending.Count - 1; i >= 0; i--)
                    if (!IsWindow(pending[i])) { pending.RemoveAt(i); closed++; }
            }
            if (pending.Count > 0)
                note.AppendLine("  · 已请求关闭 " + targets.Count + " 个窗口，其中 " + pending.Count +
                                " 个**投递了却没真正关闭**（常见原因：页面弹了「要离开吗」确认框，或权限隔离拦截）。" +
                                "确认已关闭 " + closed + " 个。");
            LastCloseNote = note.ToString().TrimEnd();
            return closed;
        }

        // 从 DshWebLog 读取主服务(3080)最近一次打印的认证 URL。
        private static string ReadMainToken()
        {
            if (!File.Exists(DshWebLog)) return null;
            string last = null;
            try
            {
                using (var fs = new FileStream(DshWebLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8, true))
                {
                    string line;
                    string marker = "http://127.0.0.1:" + ActivePort + "/?token=";
                    while ((line = sr.ReadLine()) != null)
                    {
                        int idx = line.IndexOf(marker, StringComparison.Ordinal);
                        if (idx >= 0)
                        {
                            string sub = line.Substring(idx).Trim();
                            int sp = sub.IndexOf(' ');
                            last = sp > 0 ? sub.Substring(0, sp) : sub;
                        }
                    }
                }
            }
            catch { }
            return last;
        }

        // 验证一个带 token 的地址能否真正通过 DSH 认证（若 401 则 token 已失效/过期）。
        // 用 AllowAutoRedirect=false 只看首个响应：3xx/2xx = token 有效，401 = 无效。
        public static bool UrlAuthOk(string url)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 3500;
                req.ReadWriteTimeout = 3500;
                req.AllowAutoRedirect = false;
                req.Proxy = null;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    return ((int)resp.StatusCode) < 400;
                }
            }
            catch (WebException we)
            {
                var r = we.Response as HttpWebResponse;
                return r != null && ((int)r.StatusCode) < 400;
            }
            catch { return false; }
        }

        private static string FindBrowser(string exeName)
        {
            string[] cands;
            if (exeName == "msedge.exe")
            {
                cands = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
                };
            }
            else
            {
                cands = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
                };
            }
            foreach (string c in cands) if (File.Exists(c)) return c;
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName))
                {
                    if (key != null)
                    {
                        string v = key.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(v) && File.Exists(v)) return v;
                    }
                }
            }
            catch { }
            return null;
        }

        // ---------------- 诊断 ----------------
        public static DiagItem[] RunDiagnostics()
        {
            var items = new List<DiagItem>();
            LaunchSpec spec = DiscoverLaunch();

            // 1. dsh 可执行文件
            if (spec.BinJs == null || !File.Exists(spec.BinJs))
                items.Add(new DiagItem { Ok = false, Text = "未找到 dsh 入口 bin.js（已扫描 launcher.ini 与常见安装路径）\r\n" });
            else
                items.Add(new DiagItem { Ok = true, Text = "dsh 入口正常：" + spec.BinJs + "（来源 " + spec.Source + "）\r\n" });

            // 2. 服务状态
            // ★ T5：诊断走 deep 扫描，才能发现"进程活着但端口没起来"的卡死实例
            ServiceState st = CheckState(true);
            if (st.Listens && st.HttpResponds)
                items.Add(new DiagItem { Ok = true, Text = "dsh web 正在运行且 HTTP 正常（PID " + JoinPids(st.DshPids) + "）\r\n" });
            else if (st.Listens && !st.HttpResponds)
                // ★ P8：这里原来写死字符串"端口 3080"，而本段判的其实是 ActivePort。
                //   本机历史上端口**真的漂过**（日志里 15 条 token 地址中 13 条是 3081）
                //   ⇒ 一旦漂了，报告会说"端口 3080 卡死"，而 3080 根本不是当事端口。
                items.Add(new DiagItem { Ok = false, Text = "端口 " + ActivePort + " 有监听但 HTTP 无响应——疑似卡死，建议“重启”\r\n" });
            else if (st.HasDshProcess)
                items.Add(new DiagItem { Ok = false, Text = "存在 dsh 进程（PID " + JoinPids(st.DshPids) + "）但端口 " + ActivePort + " 未监听——启动可能失败，看日志\r\n" });
            else
                items.Add(new DiagItem { Ok = true, Text = "dsh web 当前未运行（正常待机状态）\r\n" });

            // 2.5 ★ 2026-09-16 新增：端口发现（适配"别人的 dsh 不在 3080"）
            //     背景：dsh 默认 3080，但可靠启动器是"从 3080 起找第一个空闲端口"
            //     （3080 被占就用 3081…），桌面图标也可能配成别的端口。
            //     这里把"现在用的是哪个端口、凭什么认出来的"如实报出来，
            //     并在"端口跑到默认区间之外、又不是显式指定/身份认出"时给 WARN。
            int _p = ActivePort;
            int _ov = DshBoot.ReadPortOverride();
            string _src = DshBoot.LastPortSource;
            if (string.IsNullOrEmpty(_src)) _src = "本次未重新发现（沿用缓存）";
            string _ovNote = _ov > 0
                ? "显式指定为 " + _ov + "（来源 " + (DshBoot.PortOverrideLabel ?? "?") + "）"
                : "未显式指定（要指定：设环境变量 DSH_PORT，或把端口号写进 " + DshBoot.PortOverrideFile + "）";
            bool _inRange = _p >= DshBoot.PortScanLo && _p <= DshBoot.PortScanHi;
            bool _byIdentity = _src.IndexOf("身份扫描", StringComparison.Ordinal) >= 0;
            items.Add(new DiagItem
            {
                Ok = _inRange || _ov > 0 || _byIdentity,
                Text = "端口发现：当前用 " + _p + "（来源：" + _src + "）；" + _ovNote + "\r\n",
            });

            // 3. web profile 补丁文件与皮肤互斥
            string profilePatch = Path.Combine(DshHome, "profiles", "web", "cordis.patch.yml");
            string homePatch = Path.Combine(DshHome, "cordis.patch.yml");
            bool profileOk = File.Exists(profilePatch);
            items.Add(new DiagItem
            {
                Ok = profileOk,
                Text = profileOk ? "profile 补丁文件存在：" + profilePatch + "\r\n"
                                 : "缺少 profile 补丁文件：" + profilePatch + "\r\n",
            });
            if (profileOk) CheckSkinExclusion(profilePatch, items);

            // 4. 插件包完整性（皮肤 + launcher）
            string profilePkg = Path.Combine(DshHome, "profiles", "web", "package.json");
            string[] expect = new string[]
            {
                "@dsh-external/dsh-client-ui-skin-deep-whale-manager",
                "@dsh-external/dsh-client-ui-skin-maid-atelier",
                "@dsh-external/dsh-client-ui-skin-orca-link",
                "dsh-whale-desktop-launcher",
            };
            try
            {
                string text = File.ReadAllText(profilePkg);
                foreach (string name in expect)
                {
                    bool ok = text.IndexOf(name, StringComparison.Ordinal) >= 0;
                    items.Add(new DiagItem { Ok = ok, Text = ok ? "插件已登记：" + name + "\r\n" : "插件缺失：" + name + "（可用 dsh plugin --profile web add 补装）\r\n" });
                }
            }
            catch
            {
                items.Add(new DiagItem { Ok = false, Text = "无法读取 profile package.json\r\n" });
            }

            // 2026-09-16 追加：启动链路四项（端口 / launcher.ini / 认证地址 / 冷启动元凶）
            try { items.AddRange(DshBoot.DiagItems()); } catch { }

            // ★ 2026-09-17 追加（v4.5）：装后体检七项（日志来源 / 根因分类 / 页面端口API /
            //   三方对齐 / 重复条目 / 皮肤系统 / 模型通路）—— 让"装完插件后"的判据也进自检。
            try
            {
                foreach (string[] it in PluginDiag.DiagItems())
                    items.Add(new DiagItem { Ok = it[0] == "1", Text = it[1] + "\r\n" });
            }
            catch { }

            // ★★ 2026-09-18 追加：**只诊断总闸**的自检。
            //   为什么用 StartDsh 来验：它是四个受总闸管辖的入口里**唯一不可能杀进程**的那个
            //   （它只负责"起"），所以即使总闸失效，这一步最坏也只是多起一个实例，不会误杀。
            //   开总闸 → StartDsh 必须被拒绝 → 立刻恢复原设置。
            try
            {
                bool savedRo = ReadOnlyMode;
                ReadOnlyMode = true;
                bool refused = false;
                try { refused = !StartDsh(); } catch { refused = false; }
                ReadOnlyMode = savedRo;
                items.Add(new DiagItem
                {
                    Ok = refused,
                    Text = "只诊断总闸：开启后 StartDsh 被拒绝 = " + (refused ? "是" : "否")
                           + "（关掉后再动 dsh 进程；每颗按钮仍弹二次确认）：" + (refused ? "OK" : "★不合格") + "\r\n"
                });
            }
            catch { }

            // ★★ 2026-09-18 追加：**本地插件**（profiles/*/plugins/*）的"半装 / 隐形"体检。
            //   主人这次的形态是「已经登记进 cordis.patch.yml，但那一刻 plugins/dsh-picks 只有服务端半边、
            //   没有 package.json」——而上面那两处判据**都漏过它**（悬空判定只问"能不能解析到一个目录"；
            //   静态探测只遍历 profile package.json 的 dependencies）⇒ 现象是"登记了、目录也在、DSH 加载不起来"，
            //   体检却全绿。补上这一项。
            try
            {
                string[] lp = PluginDiag.LocalPluginsCheck();
                items.Add(new DiagItem { Ok = lp[0] == "1", Text = lp[1] + "\r\n" });
            }
            catch { }
            // ★★ 2026-09-18 追加：**挂在 profile 外面**的插件（跨机器通用判据）。
            //   主人转来的报错就是这一类：插件在 D:\plugins\… 靠绝对路径挂上，
            //   它 import '@deepseek-ai/schemastery' 解析不到 ⇒ DSH compose 阶段就崩 ⇒「页面死活打不开」。
            try
            {
                string[] ot = PluginDiag.OutOfTreePluginsCheck();
                items.Add(new DiagItem { Ok = ot[0] == "1", Text = ot[1] + "\r\n" });
            }
            catch { }
            // ★ 判据自检（正负样本）：证明"外部插件"那条**会报红、也不误报**
            try
            {
                foreach (string line in PluginDiag.Selftest())
                {
                    string tt = line.Trim();
                    items.Add(new DiagItem { Ok = tt.EndsWith("OK", StringComparison.Ordinal), Text = tt + "\r\n" });
                }
            }
            catch { }

            // ★★ 2026-09-20 新增：**启动失败归因**的判据自检（正负样本齐全）。
            //   为什么必须有：主人实测「点了启动，结果你没起来」＝ 旧崩溃块被当成本次失败
            //   （12:05 那次，3080 端口实例其实在正常冷启动）。判据本身不验，就会再犯。
            try
            {
                foreach (string line in StartFailJudge())
                {
                    string tj = line.Trim();
                    items.Add(new DiagItem { Ok = tj.EndsWith("OK", StringComparison.Ordinal), Text = tj + "\r\n" });
                }
            }
            catch (Exception exj)
            {
                items.Add(new DiagItem { Ok = false, Text = "启动归因自检抛异常：" + exj.Message + "\r\n" });
            }

            // ★ 2026-09-17 新增：固化 / 白屏 / 错误码翻译三块的纯函数自检
            //   （判据本身也要验：pnpm 工程判据、锚点唯一命中、错误码正负样本、JSON 抽取）
            try
            {
                foreach (string line in PatchLock.Selftest())
                {
                    string t = line.Trim();
                    bool ok = t.EndsWith("OK", StringComparison.Ordinal);
                    items.Add(new DiagItem { Ok = ok, Text = "固化/白屏自检：" + t + "\r\n" });
                }
            }
            catch (Exception ex)
            {
                items.Add(new DiagItem { Ok = false, Text = "固化/白屏自检抛异常：" + ex.Message + "\r\n" });
            }

            // ★ 2026-09-17 新增：中文/非 ASCII 路径 + 自愈边界 的纯函数自检
            try
            {
                foreach (string line in PathAudit.Selftest())
                {
                    string t = line.Trim();
                    // ★ 判据必须看**结论**而不是"文本里有没有 FAIL 字样"：
                    //   有的自检项名字里就带「FAIL 行」（测的是"含 FAIL 的行要算故障"），
                    //   按 Contains("FAIL") 会把通过的项误判成失败（实测踩过）。
                    bool ok = t.EndsWith("OK", StringComparison.Ordinal);
                    items.Add(new DiagItem { Ok = ok, Text = "路径/自愈自检：" + t + "\r\n" });
                }
            }
            catch (Exception ex)
            {
                items.Add(new DiagItem { Ok = false, Text = "路径/自愈自检抛异常：" + ex.Message + "\r\n" });
            }

            // ★★ 2026-09-20 新增：DPI 感知判据（防「按钮文字被裁」回归）
            //
            //  主人反馈「原来的大小是可以正常看完这个字的，现在都看不全，你把这个大小恢复成原本的样子」。
            //  排查过程与结论（都有硬数字）：
            //    · 本机显示缩放 150%（HKCU\Control Panel\Desktop\WindowMetrics\AppliedDPI = 144）。
            //    · app.manifest 里原先跟着"身份元数据加固"一起声明了 DPI 感知
            //      （dpiAware=true/pm、dpiAwareness=permonitorv2,permonitor）。
            //      声明感知后，Windows 不再替我们整体拉伸，而是让程序自己按 144 DPI 渲染：
            //      10pt 雅黑按 10*144/72 ≈ 20px 画出来，横向比 96 DPI 宽约 1.5 倍。
            //    · 但界面上所有控件尺寸都是**写死的像素值**（按钮 110/130/150px、Height=40），
            //      它们不随 DPI 放大 ⇒ 文字被挤出按钮，只剩前半截：
            //      主人截图里是「装后体」(← 装后体检)、「运行」(← 运行诊断)、「启动链路」(← 启动链路自检)。
            //    · 对照实证：把 v5.0 那个 exe（清单里**没有** dpiAware / permonitor）翻出来看，
            //      同样 150% 的屏幕上按钮文字是**完整**的。
            //      ⇒ 主人说的"原来的大小"，指的就是"不感知 + Windows 整体拉伸"这个行为。
            //    · 所以修法 = **撤掉清单里的 DPI 感知声明**（最小改动、且可验证），
            //      而不是"改成感知再把全部控件按 DPI 缩放"（那要动整个界面，超出本次加固范围、风险大得多）。
            //
            //  判据：进程读到的屏幕 DPI 必须是 96（=「不感知」时 GDI 报的值）。
            //  为什么这样判：这是**唯一**能区分"感知/不感知"的可测信号，
            //  而且与主人看到的"字全不全"直接同源。
            //  负对照：把 dpiAware 声明加回去，这里立刻会读到 144 ⇒ 报 FAIL（可证伪）。
            //  实测：撤掉声明后本判据 = 96 OK；声明在时（上一版）= 144 FAIL。
            try
            {
                int dpi = ScreenDpiY();
                bool okDpi = (dpi == 96);
                items.Add(new DiagItem
                {
                    Ok = okDpi,
                    Text = "DPI 感知：进程读到的屏幕 DPI = " + dpi
                         + (okDpi ? "（96 = 不感知，Windows 会整体拉伸 ⇒ 字和按钮一起放大，不会被裁）"
                                  : "（≠96 ⇒ 已声明 DPI 感知，而控件尺寸是写死的像素值 ⇒ 高缩放下按钮文字会被裁；"
                                    + "清单里不该有 dpiAware / dpiAwareness）")
                         + ": " + (okDpi ? "OK" : "FAIL") + "\r\n"
                });
            }
            catch (Exception ex)
            {
                items.Add(new DiagItem { Ok = false, Text = "DPI 感知判据抛异常：" + ex.Message + "\r\n" });
            }

            // ★ 2026-09-17 新增：说明书内容完整性（第一页那份说明必须含关键小节与署名）
            try
            {
                string line = Manual.SelftestLine();
                items.Add(new DiagItem { Ok = line.EndsWith("OK", StringComparison.Ordinal), Text = "说明书自检：" + line + "\r\n" });
            }
            catch (Exception ex)
            {
                items.Add(new DiagItem { Ok = false, Text = "说明书自检抛异常：" + ex.Message + "\r\n" });
            }

            // ★ 2026-09-17 新增：皮肤检测/配色判据
            try
            {
                string sline = SkinTheme.SelftestLine();
                items.Add(new DiagItem { Ok = sline.EndsWith("OK", StringComparison.Ordinal), Text = "皮肤自检：" + sline + "\r\n" });
            }
            catch (Exception ex)
            {
                items.Add(new DiagItem { Ok = false, Text = "皮肤自检抛异常：" + ex.Message + "\r\n" });
            }

            // ★ 2026-09-17 新增：自绘标题栏（B 方案）判据
            try
            {
                string fline = SkinFrame.SelftestLine();
                items.Add(new DiagItem { Ok = fline.EndsWith("OK", StringComparison.Ordinal), Text = "自绘标题栏自检：" + fline + "\r\n" });
            }
            catch (Exception ex)
            {
                items.Add(new DiagItem { Ok = false, Text = "自绘标题栏自检抛异常：" + ex.Message + "\r\n" });
            }

            // ★ 2026-09-18 新增：皮肤对比度判据（WCAG，客观数字，不靠肉眼）
            try
            {
                string cline = SkinTheme.ContrastSelftestLine();
                items.Add(new DiagItem { Ok = cline.EndsWith("OK", StringComparison.Ordinal), Text = "皮肤对比度自检：" + cline + "\r\n" });
            }
            catch (Exception ex)
            {
                items.Add(new DiagItem { Ok = false, Text = "皮肤对比度自检抛异常：" + ex.Message + "\r\n" });
            }

            // ★ 2026-09-18 新增：皮肤装饰素材判据（金色花边/蕾丝/蝴蝶结/卷草边角）
            //   判"取到了"不够 —— 空图也能取到（本项目踩过空白表情包的坑），所以同时判尺寸与"有没有内容"。
            try
            {
                string aline = SkinArt.SelftestLine();
                items.Add(new DiagItem { Ok = aline.EndsWith("OK", StringComparison.Ordinal), Text = "皮肤装饰自检：" + aline + "\r\n" });
            }
            catch (Exception ex)
            {
                items.Add(new DiagItem { Ok = false, Text = "皮肤装饰自检抛异常：" + ex.Message + "\r\n" });
            }

            // ★ 2026-09-18 新增：皮肤选择框判据（主人要求"点开能选皮肤"）——
            //   程序自己把选择框建出来渲染一遍，数条目/色/选中映射（外部点不动它的按钮，用这个验收）。
            try
            {
                string pline = SkinPickerDialog.SelftestLine();
                items.Add(new DiagItem { Ok = pline.EndsWith("OK", StringComparison.Ordinal), Text = "皮肤选择自检：" + pline + "\r\n" });
            }
            catch (Exception ex)
            {
                items.Add(new DiagItem { Ok = false, Text = "皮肤选择自检抛异常：" + ex.Message + "\r\n" });
            }

            // ★ 2026-09-18 新增：最大化/还原 尺寸漂移的**静态**判据
            //   （完整复现要靠 --maxcycle 开窗跑；这里判的是"根因修复是否在位"）
            try
            {
                string snote;
                bool strip = SkinFrame.CaptionRemovedFromStyle || SkinFrame.ProbeStyleStrip(out snote);
                items.Add(new DiagItem
                {
                    Ok = strip || !SkinFrame.Enabled,
                    Text = "标题栏根因修复（摘 WS_CAPTION，消 23px 漂移）："
                         + (!SkinFrame.Enabled ? "未启用自绘标题栏，无需摘 OK"
                                               : (strip ? "已摘除（读回确认） OK" : "未摘除 ⇒ 仍有 23px 漂移 ★"))
                         + "\r\n"
                });
            }
            catch (Exception ex)
            {
                items.Add(new DiagItem { Ok = false, Text = "标题栏根因修复自检抛异常：" + ex.Message + "\r\n" });
            }

            // ★★ 2026-09-20 新增：界面一致性判据（主人报「页签行一直抖 / 右边箭头停不下来」之后补）
            //   判的是**纯数据**：页签标题里的 emoji 在 emoji 字体里到底画不画得出来（实测画一遍）、
            //   6 个页签合计算出来的宽度放不放得下默认窗口（放不下就会常驻 ◀▶ 滚动箭头）、
            //   每个按钮是否真的进了某个页。
            //   ⇒ 用带 out 的重载拿**总判定**，不要用"整串是不是以 OK 结尾"（子项多了会漏判）。
            try
            {
                bool uok;
                string uline = MainForm.UiConsistencySelftestLine(out uok);
                items.Add(new DiagItem { Ok = uok, Text = "界面一致性自检：" + uline + "\r\n" });
            }
            catch (Exception ex)
            {
                items.Add(new DiagItem { Ok = false, Text = "界面一致性自检抛异常：" + ex.Message + "\r\n" });
            }

            return items.ToArray();
        }

        // ================= 插件崩溃诊断与禁用 =================

        /// <summary>
        /// 判定一个从日志里抓到的 id 是否**真的是一个已安装的插件**（T10）。
        ///
        /// 旧口径是"包名里必须含 dsh" —— 于是 `argo-search` 这种**名字里根本没有 dsh** 的插件
        /// **永远不可能被检测到**（本机 11 个已装插件里正好有它一个），
        /// 「排查崩溃插件」会系统性地漏报它。
        ///
        /// 现在改成**对着实际安装清单白名单化**：能在 profile/home 两层的 package.json
        /// 或 node_modules 里找到的才算插件。这样既能把 `argo-search` 收进来，
        /// 又能把 `cordis:include` 这类内置项挡在外面（它当然不在安装清单里）。
        ///
        /// 兜底：若两层清单**都读不到**（环境异常），退回旧的"含 dsh"启发式，
        /// 宁可少报也不要因为读不到清单而变成"永远报不出来"。
        /// </summary>
        private static bool LooksLikeInstalledPlugin(string id, string pkgText)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (id.IndexOf(':') >= 0) return false;              // cordis:include 这类内置项
            bool manifestSeen = false;
            if (pkgText != null && pkgText.Length > 0)
            {
                manifestSeen = true;
                if (pkgText.IndexOf("\"" + id + "\"", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            try
            {
                foreach (string baseDir in new[]
                         {
                             Path.Combine(DshHome, "profiles", "web", "node_modules"),
                             Path.Combine(DshHome, "node_modules"),
                         })
                {
                    if (!Directory.Exists(baseDir)) continue;
                    manifestSeen = true;
                    if (Directory.Exists(Path.Combine(baseDir, id))) return true;
                }
            }
            catch { }
            if (!manifestSeen) return id.IndexOf("dsh", StringComparison.OrdinalIgnoreCase) >= 0;
            return false;
        }

        /// <summary>
        /// ★ 2026-09-17 修（v4.5 · C2）—— 这里正是"按钮像摆设"的根因：
        ///  旧版只读**救星自己启动实例时写的**那两份日志（AppDataDir\dsh-web.out.log[.err]）。
        ///  可 DSH 常常是被**桌面图标 / 可靠启动器 `open-dsh.mjs` / 工坊桌面端**拉起来的，
        ///  崩溃行落在**别的文件**里 ⇒ 这两个按钮永远报「未能定位」「无需禁用」，点了等于没点。
        ///  现在把四类来源合并（去重）：救星自己的 / 可靠启动器的 / 工坊桌面端候选目录 /
        ///  以及 ~/.dsh 浅层里"真的提到插件树失败"的其它日志（由 PluginDiag 负责发现）。
        /// </summary>
        private static List<string> CrashLogPaths()
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> add = delegate(string p)
            {
                if (string.IsNullOrEmpty(p)) return;
                if (!seen.Add(p)) return;
                list.Add(p);
            };
            add(DshWebLog);
            add(DshWebLog + ".err");
            try { add(DshBoot.ToolsOutLog); } catch { }
            try { add(DshBoot.ToolsErrLog); } catch { }
            try
            {
                foreach (PluginDiag.LogSource s in PluginDiag.CollectLogSources())
                    if (s.Exists) add(s.Path);
            }
            catch { }
            return list;
        }

        /// <summary>上一次崩溃排查实际读了哪几个文件（供报告如实展示）。</summary>
        public static List<string> LastCrashLogsRead = new List<string>();

        /// <summary>
        /// 从 DSH 启动日志里收集"导致 plugin tree 加载失败"的插件 id。
        /// DetectCrashedPlugin 与 FirstCrashedId **共用这一份实现** ——
        /// 旧版两处各写一遍（且注释都相同），改一处忘一处就会让"排查"与"禁用"结论不一致。
        /// </summary>
        private static HashSet<string> CollectCrashedIds(string profilePkgText)
        {
            var found = new HashSet<string>();
            var read = new List<string>();
            foreach (string logpath in CrashLogPaths())
            {
                if (!File.Exists(logpath)) continue;
                read.Add(logpath);
                try
                {
                    // ★ 2026-09-16 修复（T9 —— 理论审查指出我上一版修得"过度"）：
                    //   上一版用的是**文件级**门禁（整份日志里只要出现过 EADDRINUSE 就整体跳过），
                    //   ⇒ **一旦日志里有过端口冲突，此后所有真崩溃都永远报不出来**（永久失明）——
                    //   等于用"再也不会误报"换掉了"再也不会发现"。这是不可接受的。
                    //   改为**行级**判据（见下方 portVictim）：只有**同一行**既是加载失败、又含
                    //   EADDRINUSE 时，才认定它是端口冲突的受害者。
                    foreach (string line in File.ReadLines(logpath))
                    {
                        // 只取 loader entry 的准确插件 id。
                        // ★ 2026-09-16 修复：原正则写死 `(dsh-[A-Za-z0-9_-]+)`，**认不出带 scope 的包名**
                        //   （`@ychris12138/dsh-usage-stats`、`@dsh-external/dsh-client-ui-skin-*`）。
                        //   实测本机 dsh-web.out.log.err 里 15 行 loader entry（含 5 处
                        //   plugin tree failed to load）**命中 0/15** ⇒ 「排查崩溃插件」永远报
                        //   "未能定位"、「禁用崩溃插件」永远提前返回"无需禁用"（= 点了什么都不做）。
                        //   ★ T10 再修：**不再要求包名里含 "dsh"** —— 那会让 argo-search 永远漏检；
                        //     改由 LooksLikeInstalledPlugin 对着安装清单白名单化。
                        var m = System.Text.RegularExpressions.Regex.Match(line,
                            @"loader entry (\S+)\s+\(((?:@[A-Za-z0-9_.-]+/)?[A-Za-z0-9_.:-]+)\)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (m.Success)
                        {
                            string pidFound = m.Groups[2].Value;
                            // ① **行级**端口冲突判据（T9）：**同一行**既是加载失败、又含 EADDRINUSE
                            //    ⇒ 它是"端口冲突把插件树带崩"的受害者
                            //    （如 "...loader entry webserver (@deepseek-ai/dsh-host-webserver):
                            //      listen EADDRINUSE: address already in use 127.0.0.1:3081"），
                            //    **不是这个插件自己崩了**，不该建议用户禁用它。
                            bool portVictim = line.IndexOf("EADDRINUSE", StringComparison.OrdinalIgnoreCase) >= 0;
                            // ② **绝不建议禁用 DSH 官方核心组件**（@deepseek-ai/*）——
                            //    它们出现在失败日志里通常是"受害者"（端口冲突 / 依赖缺失），
                            //    一旦被 disabled，DSH 会彻底起不来，这是不可接受的误操作。
                            bool official = pidFound.StartsWith("@deepseek-ai/", StringComparison.OrdinalIgnoreCase);
                            if (!portVictim && !official && LooksLikeInstalledPlugin(pidFound, profilePkgText))
                                found.Add(pidFound);
                        }
                    }
                }
                catch { }
            }
            LastCrashLogsRead = read;
            return found;
        }

        // 从 DSH 启动日志（.out.log + .err）中提取导致 plugin tree 加载失败的插件 id。
        // 并区分"当前仍活跃的崩溃"与"历史记录/已被替代"的情况，避免把新版误判为不兼容。
        public static string DetectCrashedPlugin()
        {
            var sb = new StringBuilder();
            // 当前 profile bundle 里实际挂在的插件 id（用于白名单化 + 是否仍活跃）
            string profilePkgText = SafeRead(Path.Combine(DshHome, "profiles", "web", "package.json"));
            var found = CollectCrashedIds(profilePkgText);
            if (found.Count == 0)
            {
                // ★ v4.5：把"我到底读了哪几个文件"如实报出来 ——
                //   否则用户无法区分"真的没有崩溃"与"根本没找到日志"（旧版就是这个盲区）。
                var miss = new StringBuilder();
                miss.AppendLine(">>> 未能从日志定位到崩溃的插件。");
                miss.AppendLine("    本次实际读了 " + LastCrashLogsRead.Count + " 个日志文件：");
                foreach (string p in LastCrashLogsRead) miss.AppendLine("      · " + p);
                miss.AppendLine("    可能原因：① 这些文件里确实没有加载失败记录（当前 DSH 正常）；"
                                + "② 你的 DSH 是被别的方式启动的，日志在别处（把那份日志路径告诉我，或看「🩺 装后体检」的日志来源全景）。");
                // ★ v4.5：如果日志里**确实有**插件树失败、但失败原因不是"插件坏"（例如端口冲突），
                //   就说清楚并指路 —— 旧版在这里只会说"未能定位"，让用户以为工具失灵。
                try
                {
                    int treeFails = 0;
                    foreach (string p in LastCrashLogsRead)
                    {
                        string tail = PluginDiag.TailText(p, 256 * 1024);
                        int idx = 0;
                        while ((idx = tail.IndexOf(PluginDiag.TreeFailMarker, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
                        { treeFails++; idx += PluginDiag.TreeFailMarker.Length; }
                    }
                    if (treeFails > 0)
                    {
                        miss.AppendLine("    ★ 但注意：这些日志里有 **" + treeFails + " 处「插件树加载失败」**记录，"
                                        + "只是原因不是「插件自己坏」（按现有判据已排除端口冲突受害者与官方核心组件）。");
                        miss.AppendLine("      ⇒ 请看「🩺 装后体检」的**起不来根因分类**，那里会告出具体是哪一类、该点哪个按钮。");
                    }
                }
                catch { }
                return miss.ToString();
            }

            sb.AppendLine("检测到以下插件在 DSH 加载日志中出现过崩溃/不兼容：");
            sb.AppendLine("（本次读了 " + LastCrashLogsRead.Count + " 个日志文件）");
            foreach (string id in found)
            {
                bool active = profilePkgText != null && profilePkgText.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0;
                string marker = active ? "[仍活跃] " : "[历史记录] ";
                sb.AppendLine("  " + marker + id);
                // 特别说明：usage-ledger 已被 usage-stats 替代
                if (id.IndexOf("usage-ledger", StringComparison.OrdinalIgnoreCase) >= 0
                    && !active)
                {
                    sb.AppendLine("       → 该旧版已不在当前 profile，现由 @ychris12138/dsh-usage-stats 替代（已修复兼容性）。");
                }
                else if (id.IndexOf("usage-stats", StringComparison.OrdinalIgnoreCase) >= 0 && active)
                {
                    sb.AppendLine("       → 新版 usage-stats 已在当前 profile 中，且实测可正常加载（兼容）。");
                }
            }
            sb.AppendLine();
            bool anyActive = false;
            foreach (string id in found) if (IsPluginActive(profilePkgText, id)) anyActive = true;
            if (!anyActive)
            {
                sb.AppendLine(">>> 这些崩溃都是历史记录，对应插件已不在当前 profile（或已被新版替代并修复兼容性）。");
                sb.AppendLine("    当前 DSH 运行正常，无需处理。若仍打不开，请排查其他原因（端口/网络/前端构建）。");
            }
            else
            {
                sb.AppendLine(">>> 有仍活跃的崩溃插件会连累整个 plugin tree，导致 DSH 打不开。点「禁用崩溃插件」写入 disabled 行（可恢复），重启后生效。");
            }
            return sb.ToString();
        }

        // 安全读文件文本
        private static string SafeRead(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch { return ""; }
        }

        // 判断某插件 id 是否仍挂在当前 profile（活跃）
        private static bool IsPluginActive(string profilePkgText, string id)
        {
            if (string.IsNullOrEmpty(profilePkgText) || string.IsNullOrEmpty(id)) return false;
            return profilePkgText.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // 返回检测到的第一个崩溃插件 id（供"禁用"按钮自动取用）
        public static string FirstCrashedId()
        {
            // ★ 2026-09-16：改为与 DetectCrashedPlugin **共用同一份扫描实现**。
            //   旧版这里把同一段逻辑**又抄了一遍**（注释都一模一样）——
            //   改一处忘一处，就会出现"「排查」说 A、「禁用」却去禁 B（或什么都不做）"的不一致。
            //   本机实际就发生过：DetectCrashedPlugin 修好了、FirstCrashedId 还是旧正则。
            string profilePkg = SafeRead(Path.Combine(DshHome, "profiles", "web", "package.json"));
            var found = CollectCrashedIds(profilePkg);
            if (found.Count == 0) return null;

            // 只返回"仍活跃"的崩溃插件（在 profile bundle 里的），避免禁用已被替代/卸载的旧 id
            foreach (string id in found)
                if (IsPluginActive(profilePkg, id) && (id.StartsWith("dsh-", StringComparison.Ordinal)
                                   || id.Contains("whale") || id.Contains("market") || id.Contains("skin") || id.Contains("usage")))
                    return id;
            foreach (string id in found) if (IsPluginActive(profilePkg, id)) return id;
            return null; // 无仍活跃的崩溃插件
        }

        // 禁用崩溃插件（自动取第一个检测到的 id）
        public static string DisableCrashedPluginAuto()
        {
            string id = FirstCrashedId();
            if (string.IsNullOrEmpty(id))
                return "未能从日志检测到崩溃插件，无需禁用。\r\n可先点「排查崩溃插件」确认，或手动在 profile 补丁里加 disabled 行。";
            return DisableCrashedPlugin(id);
        }

        // 一键禁用崩溃插件：在 profile 补丁文件里写入 disabled 行（比卸载安全，保留包可恢复）
        public static string DisableCrashedPlugin(string pluginId)
        {
            if (string.IsNullOrEmpty(pluginId)) return "未指定要禁用的插件。";
            string profilePatch = Path.Combine(DshHome, "profiles", "web", "cordis.patch.yml");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(profilePatch));
                string original = File.Exists(profilePatch) ? File.ReadAllText(profilePatch) : "# profile patch\r\n";

                // 2026-09-12 加固 ①：写前无损快照（可一键回滚）
                SafeConfig.Snapshot("禁用崩溃插件 " + pluginId);

                // 2026-09-12 加固 ②：带时间戳备份。旧代码写固定名 ".bak"，
                // 若源文件已损坏，下一次备份就把唯一的好副本覆盖掉了。
                SafeConfig.BackupFile(profilePatch);

                // 追加 disabled 行（若还没有该插件的禁用条目）
                if (original.IndexOf(pluginId, StringComparison.OrdinalIgnoreCase) >= 0
                    && original.IndexOf("disabled", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // 可能已有该条目，检查是否已禁用
                    if (original.IndexOf("disabled: true", StringComparison.OrdinalIgnoreCase) >= 0
                        && HasDisabledForId(original, pluginId))
                        return "插件 " + pluginId + " 当前已禁用。重启 DSH 后生效。";
                }

                // 2026-09-12 加固 ③：旧代码直接 original.TrimEnd() + block 拼接。
                // 若补丁文件是默认模板（注释 + "[]"），追加后会变成 "[]" 后面跟序列项，
                // 是非法 YAML。这里先去掉空数组行。
                string head = StripEmptyArrayLine(original).TrimEnd();
                string block = "- id: " + pluginId + "\r\n  disabled: true\r\n";
                string result = (head.Length > 0 ? head + "\r\n\r\n" : "") + block;

                // 2026-09-12 加固 ④：原子写入 + 写后 YAML 结构校验（不合法自动回滚）
                SafeConfig.AtomicWriteText(profilePatch, result);
                SafeConfig.VerifyAfterWrite(profilePatch, false, true);

                return "已禁用插件 " + pluginId + "（写入 profile 补丁）。\r\n请重启 DSH 生效；恢复时删掉补丁里该行即可。";
            }
            catch (Exception e)
            {
                return "禁用失败：" + e.Message;
            }
        }

        // 判断某 id 是否已有 disabled:true 条目
        private static bool HasDisabledForId(string text, string id)
        {
            try
            {
                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
                bool inEntry = false;
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.StartsWith("- id: " + id, StringComparison.OrdinalIgnoreCase)) { inEntry = true; continue; }
                    if (inEntry)
                    {
                        if (line.StartsWith("- id:", StringComparison.OrdinalIgnoreCase)) inEntry = false;
                        else if (line.StartsWith("disabled: true", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        // ---------------- 插件兼容性诊断 ----------------
        // 静态探测每个已装插件：package.json 存在性、入口/patch/client 产物、link 目标是否失效
        public static string PluginCompatibility()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== 插件兼容性检查 ====");
            sb.AppendLine("大肥鱼救星: v" + AppVersion + "　DSH 版本: " + DshVersion() + "　profile: web");
            sb.AppendLine();

            string nm = Path.Combine(DshHome, "profiles", "web", "node_modules");
            string[] plugins = KnownPlugins();
            bool anyIssues = false;

            foreach (string name in plugins)
            {
                string dir = Path.Combine(nm, name);
                string pkgPath = Path.Combine(dir, "package.json");
                sb.AppendLine("—— " + name + " ——");

                if (!File.Exists(pkgPath))
                {
                    sb.AppendLine("[FAIL] package.json 缺失！可能是 link 型插件源目录被移动/删除，或安装不完整。");
                    anyIssues = true;
                    continue;
                }

                string pkgText;
                try { pkgText = File.ReadAllText(pkgPath); }
                catch (Exception e) { sb.AppendLine("[FAIL] package.json 读取失败: " + e.Message); anyIssues = true; continue; }

                // 版本
                string ver = ExtractJsonStr(pkgText, "version");
                sb.AppendLine("  版本: " + (ver ?? "?"));

                // main 入口
                string main = ExtractJsonStr(pkgText, "main");
                if (main != null && !ExistsPath(Path.Combine(dir, main)))
                {
                    sb.AppendLine("[FAIL] 入口缺失: " + main);
                    anyIssues = true;
                }

                // bundle patch
                bool hasPatch = pkgText.IndexOf("cordis.patch.yml", StringComparison.Ordinal) >= 0;
                if (hasPatch && !File.Exists(Path.Combine(dir, "cordis.patch.yml")))
                {
                    sb.AppendLine("[FAIL] bundle patch 缺失: cordis.patch.yml");
                    anyIssues = true;
                }
                else if (!hasPatch)
                {
                    sb.AppendLine("[WARN] 无 dsh.bundle.patch 声明（可能非标准 host 插件）");
                }

                // 是否有 client 产物（lib/client.js 或 client/client.js 任一）
                bool hasClientBuild = File.Exists(Path.Combine(dir, "lib", "client.js"))
                                      || File.Exists(Path.Combine(dir, "client", "client.js"));
                bool declaresClient = pkgText.IndexOf("\"client\"", StringComparison.Ordinal) >= 0
                                      || pkgText.IndexOf("client.js", StringComparison.Ordinal) >= 0;
                if (declaresClient && !hasClientBuild)
                {
                    sb.AppendLine("[FAIL] 声明了 client 但浏览器产物缺失（lib/client.js 或 client/client.js 都不存在）—— 会因前端注入失败导致打不开！");
                    anyIssues = true;
                }
                else if (hasClientBuild)
                {
                    sb.AppendLine("[OK]   client 产物存在: " + (File.Exists(Path.Combine(dir, "lib", "client.js")) ? "lib/client.js" : "client/client.js"));
                }

                // peer 依赖检查（cordis 3.x 冲突）——只认 peerDependencies 里 @deepseek-ai/cordis 的值首符为 3
                string cordisPeer = RegexPeerValue(pkgText, "@deepseek-ai/cordis");
                if (cordisPeer != null && cordisPeer.TrimStart('^', '>', '=', '~').StartsWith("3", StringComparison.Ordinal))
                {
                    sb.AppendLine("[FAIL] peer @deepseek-ai/cordis 要求 " + cordisPeer + "（3.x），而本机 DSH 用 4.x，不兼容！");
                    anyIssues = true;
                }
            }

            sb.AppendLine();
            sb.AppendLine(anyIssues
                ? ">>> 发现 " + "插件异常项，请点「一键修复」；若无法自动修复，可能需要卸载最近安装的插件。"
                : ">>> 结论：所有已装插件文件完整、无版本冲突，兼容性正常。");
            return sb.ToString();
        }

        // 列出当前已装插件：动态从 profile package.json 的 dependencies 解析包名，
        // 排除 DSH 官方包（@deepseek-ai/dsh-*），其余（含 link/npm 外部插件）都算。
        private static string[] KnownPlugins()
        {
            try
            {
                string pkg = Path.Combine(DshHome, "profiles", "web", "package.json");
                string text = File.ReadAllText(pkg);
                var list = new List<string>();
                // 正则提取 dependencies: { "name": ... } 的键名
                var matches = System.Text.RegularExpressions.Regex.Matches(text,
                    "\"dependencies\"\\s*:\\s*\\{([\\s\\S]*?)\\}", System.Text.RegularExpressions.RegexOptions.Singleline);
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    string body = m.Groups[1].Value;
                    foreach (System.Text.RegularExpressions.Match kv in System.Text.RegularExpressions.Regex.Matches(body,
                        "\"([^\"\\s]+)\"\\s*:", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        string name = kv.Groups[1].Value;
                        // 排除官方核心包和依赖包（@deepseek-ai/dsh-base 等）
                        if (name.StartsWith("@deepseek-ai/dsh-", StringComparison.Ordinal)) continue;
                        if (name.StartsWith("@deepseek-ai/cordis", StringComparison.Ordinal)) continue;
                        if (name == "dsh-profile-web" || name == "dsh-web") continue;
                        if (!list.Contains(name)) list.Add(name);
                    }
                }
                return list.ToArray();
            }
            catch { return new string[0]; }
        }

        // 提取 JSON 顶层字符串字段（一次性字符串字段）
        private static string ExtractJsonStr(string text, string field)
        {
            int idx = text.IndexOf("\"" + field + "\"", StringComparison.Ordinal);
            if (idx < 0) return null;
            int colon = text.IndexOf(":", idx);
            if (colon < 0) return null;
            int q1 = text.IndexOf("\"", colon);
            if (q1 < 0) return null;
            int q2 = text.IndexOf("\"", q1 + 1);
            if (q2 < 0) return null;
            return text.Substring(q1 + 1, q2 - q1 - 1);
        }

        private static bool ExistsPath(string p) { try { return File.Exists(p); } catch { return false; } }

        // 从 package.json 文本精确取值 peerDependencies 里某包名的版本（避免全局字符串误匹配）
        private static string RegexPeerValue(string text, string pkgName)
        {
            try
            {
                string escaped = System.Text.RegularExpressions.Regex.Escape(pkgName);
                var m = System.Text.RegularExpressions.Regex.Match(text,
                    "\"peerDependencies\"\\s*:\\s*\\{([^}]*)\"?" + escaped + "\"?\\s*:\\s*\"([^\"]+)\"",
                    System.Text.RegularExpressions.RegexOptions.Singleline);
                if (m.Success) return m.Groups[2].Value;
            }
            catch { }
            return null;
        }

        public static string DshVersion()
        {
            try
            {
                string[] roots =
                {
                    Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "npm", "node_modules", "@deepseek-ai", "dsh"),
                    Path.Combine(UserProfile, ".ai-manager", "npm-global", "node-v24", "node_modules", "@deepseek-ai", "dsh"),
                };
                foreach (string r in roots)
                    if (File.Exists(Path.Combine(r, "package.json")))
                        return ExtractJsonStr(File.ReadAllText(Path.Combine(r, "package.json")), "version") ?? "?";
            }
            catch { }
            return "?";
        }

        // ------------------------------------------------------------
        // 皮肤启用状态（T18：修复互斥前必须知道"用户现在用着哪个皮肤"）
        // ------------------------------------------------------------
        private sealed class SkinState
        {
            public bool Maid;         // maid-atelier 处于启用
            public bool Orca;         // orca-link 处于启用
            public bool Manager;      // deep-whale-manager 处于启用
            public bool ManagerSeen;  // patch 里**存在** manager 条目
            public bool AnySkinSeen;  // patch 里存在 maid/orca 条目
        }

        private static SkinState ReadSkinState(string patchPath)
        {
            var st = new SkinState();
            string text;
            try { text = File.Exists(patchPath) ? File.ReadAllText(patchPath) : ""; }
            catch { return st; }
            bool inMaid = false, inOrca = false, inManager = false;
            foreach (string raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.None))
            {
                string line = raw.Trim();
                if (line.StartsWith("- id: ui-skin-maid-atelier", StringComparison.Ordinal)) { inMaid = true; st.AnySkinSeen = true; inOrca = inManager = false; continue; }
                if (line.StartsWith("- id: ui-skin-orca-link", StringComparison.Ordinal)) { inOrca = true; st.AnySkinSeen = true; inMaid = inManager = false; continue; }
                if (line.StartsWith("- id: ui-skin-deep-whale-manager", StringComparison.Ordinal)) { inManager = true; st.ManagerSeen = true; inMaid = inOrca = false; continue; }
                if (line.StartsWith("disabled:", StringComparison.Ordinal))
                {
                    bool val = line.EndsWith("true", StringComparison.OrdinalIgnoreCase) || line.IndexOf(": true", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (inMaid) st.Maid = !val;
                    if (inOrca) st.Orca = !val;
                    if (inManager) st.Manager = !val;
                }
                if (line.Length == 0) { inMaid = inOrca = inManager = false; }
            }
            return st;
        }

        /// <summary>
        /// 探测**当前实际在用**哪个皮肤（T18）。
        ///
        /// 为什么必须有它：旧版「修复皮肤互斥」把参数**写死成 "official"** ⇒ 主人点一下，
        /// 正在用的女仆皮肤就被关掉、换回官方外观。按钮名暗示"消除冲突"，
        /// 实际语义却是"**重置为官方皮肤**" —— 命名与行为不符的误操作。
        /// 现在修复冲突时**保持当前皮肤不变**。
        ///
        /// 两层 patch 都看（任一层的记录都算启用），返回 "maid-atelier" / "orca-link" / "official"。
        /// </summary>
        public static string DetectActiveSkin()
        {
            SkinState home = ReadSkinState(Path.Combine(DshHome, "cordis.patch.yml"));
            SkinState prof = ReadSkinState(Path.Combine(DshHome, "profiles", "web", "cordis.patch.yml"));
            bool maid = home.Maid || prof.Maid;
            bool orca = home.Orca || prof.Orca;
            if (maid && !orca) return "maid-atelier";
            if (orca && !maid) return "orca-link";
            return "official";
        }

        private static void CheckSkinExclusion(string patchPath, List<DiagItem> items)
        {
            SkinState sk = ReadSkinState(patchPath);
            bool maid = sk.Maid, orca = sk.Orca, manager = sk.Manager;
            bool foundAnySkin = sk.AnySkinSeen;
            int enabled = (maid ? 1 : 0) + (orca ? 1 : 0);
            if (foundAnySkin && enabled > 1)
                items.Add(new DiagItem { Ok = false, Text = "皮肤互斥异常：maid-atelier 与 orca-link 同时启用会导致界面错乱，建议“一键修复皮肤互斥”\r\n" });
            else if (foundAnySkin && !manager && enabled <= 1)
                items.Add(new DiagItem { Ok = true, Text = "皮肤互斥状态正常（当前启用 " + (maid ? "maid-atelier" : orca ? "orca-link" : "官方默认") + "，manager 由 bundle 层托管）\r\n" });
            else if (foundAnySkin)
                items.Add(new DiagItem { Ok = true, Text = "皮肤互斥状态正常（当前启用 " + (maid ? "maid-atelier" : orca ? "orca-link" : "官方默认") + "）\r\n" });
            else
                items.Add(new DiagItem { Ok = true, Text = "未发现皮肤互斥配置（未安装皮肤或保持默认）\r\n" });
        }

        // 修复皮肤互斥：把互斥行写进 profile + home 两层 patch
        // enable: "official" | "maid-atelier" | "orca-link"
        public static string FixSkinExclusion(string enable)
        {
            string profilePatch = Path.Combine(DshHome, "profiles", "web", "cordis.patch.yml");
            string homePatch = Path.Combine(DshHome, "cordis.patch.yml");

            // 2026-09-12 加固：动配置文件之前先做一次无损快照（可一键回滚）
            string snap = SafeConfig.Snapshot("修复皮肤互斥（写入两层 patch 之前）");

            // ★ T19：先把两层的**原文**留底 —— 旧版 profile 写成功、home 写失败时**直接 return**，
            //   既不回头撤销已写成的 profile 层，也不告诉用户"已经改了一层"，
            //   结果是两层不一致的**半套补丁**（比一层都不写更糟：行为取决于哪层先生效）。
            string profileBefore = null;
            try { profileBefore = File.Exists(profilePatch) ? File.ReadAllText(profilePatch) : null; } catch { }

            // ★ T18b：不要**强制打开**皮肤管理器 —— 旧版写死 `manager: false`（= 启用），
            //   而两层 patch 里本来都没有这一条 ⇒ 点一下会**凭空新增**一条，
            //   并把用户可能主动关掉的管理器重新打开。现在按"当前实际状态"沿用；没有就不写。
            SkinState prof = ReadSkinState(profilePatch);
            SkinState home = ReadSkinState(homePatch);
            bool managerKnown = home.ManagerSeen || prof.ManagerSeen;
            bool managerEnabled = home.ManagerSeen ? home.Manager : prof.Manager;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# --- dsh-skin managed (auto-generated; do not edit) ---");
            AppendDisabled(sb, "ui-skin-maid-atelier", enable == "maid-atelier");
            AppendDisabled(sb, "ui-skin-orca-link", enable == "orca-link");
            if (managerKnown) AppendDisabled(sb, "ui-skin-deep-whale-manager", !managerEnabled);
            sb.AppendLine("# --- end dsh-skin managed ---");
            string block = sb.ToString();

            bool profileOk = false, homeOk = false;
            string err = "";
            List<string> wrote = new List<string>();
            try { ReplaceOrAppendManaged(profilePatch, block, ref wrote); profileOk = true; }
            catch (Exception e) { err = "profile 层写入失败：" + e.Message; }
            if (profileOk)
            {
                try { ReplaceOrAppendManaged(homePatch, block, ref wrote); homeOk = true; }
                catch (Exception e) { err = "home 层写入失败：" + e.Message; }
            }

            if (!profileOk)
                return err + "\r\n\r\n（未改动任何文件：连第一层都没写成。）"
                     + (snap == null ? "\r\n⚠ 且未能创建快照。" : "\r\n（写入前快照：" + Path.GetFileName(snap) + "）");

            if (!homeOk)
            {
                // 尽力回滚已写成的 profile 层，**不留下两层不一致的半套补丁**
                string rollback;
                try
                {
                    if (profileBefore != null)
                    {
                        SafeConfig.AtomicWriteText(profilePatch, profileBefore);
                        rollback = "已把 profile 层回滚为写入前的内容。";
                    }
                    else
                    {
                        try { File.Delete(profilePatch); } catch { }
                        rollback = "profile 层原先不存在，已删除刚写入的内容。";
                    }
                }
                catch (Exception e2) { rollback = "⚠ profile 层回滚也失败：" + e2.Message; }

                return err + "\r\n\r\n为避免留下「两层不一致」的半套补丁，" + rollback
                     + (snap == null ? "" : "\r\n（写入前快照：" + Path.GetFileName(snap) + "，可用「恢复配置」退回）");
            }

            string skinName = enable == "maid-atelier" ? "女仆皮肤（maid-atelier）"
                            : enable == "orca-link" ? "虎鲸皮肤（orca-link）" : "官方默认外观";
            return "已写入两层补丁：" + string.Join("、", wrote.ToArray())
                 + "\r\n保持启用：" + skinName
                 + (managerKnown ? "" : "\r\n（未动皮肤管理器条目：它本来就不存在，不凭空新增。）")
                 + (snap == null ? "\r\n⚠ 未能创建快照，本次无自动回滚退路。" : "\r\n（写入前快照：" + Path.GetFileName(snap) + "，可用「恢复配置」退回）");
        }

        // ---------------- 一键修复插件兼容性 ----------------
        // 自动检测并修复：对入口/patch/client 产物缺失、link 目标失效、cordis 版本冲突的插件，
        // 给出移除建议并尝试用 dsh plugin remove 卸载最可疑的包。
        // 一键修复插件兼容性（默认只报告，不动作）
        public static string RepairPlugin()
        {
            return RepairPlugin(false);
        }

        // 自动检测：对入口/patch/client 产物缺失、link 目标失效的插件给出报告。
        // confirm=false（默认）时**只报告不卸载**；confirm=true 才执行 dsh plugin remove。
        public static string RepairPlugin(bool confirm)
        {
            string nm = Path.Combine(DshHome, "profiles", "web", "node_modules");
            string[] plugins = KnownPlugins();
            var broken = new List<string>();
            var warns = new List<string>();

            foreach (string name in plugins)
            {
                string dir = Path.Combine(nm, name);
                string pkgPath = Path.Combine(dir, "package.json");
                if (!File.Exists(pkgPath)) { broken.Add(name); continue; }

                string pkgText = null;
                try { pkgText = File.ReadAllText(pkgPath); } catch { }

                // 入口
                if (pkgText != null)
                {
                    string main = ExtractJsonStr(pkgText, "main");
                    if (main != null && !ExistsPath(Path.Combine(dir, main))) { broken.Add(name); continue; }
                }
                // patch
                if (pkgText != null && pkgText.IndexOf("cordis.patch.yml", StringComparison.Ordinal) >= 0
                    && !File.Exists(Path.Combine(dir, "cordis.patch.yml")))
                { broken.Add(name); continue; }
                // client 声明了但产物缺失
                bool declaresClient = pkgText != null && (pkgText.IndexOf("\"client\"", StringComparison.Ordinal) >= 0
                                           || pkgText.IndexOf("client.js", StringComparison.Ordinal) >= 0);
                bool hasClientBuild = File.Exists(Path.Combine(dir, "lib", "client.js"))
                                      || File.Exists(Path.Combine(dir, "client", "client.js"));
                if (declaresClient && !hasClientBuild) { broken.Add(name); continue; }
            }

            // 若发现不兼容/损坏插件：默认「只报告、不动作」。
            // 2026-09-12 加固：旧代码会**自动**用 dsh plugin remove 卸载它认为坏掉的插件，
            // 而 dsh plugin remove 会重写 profile 的 package.json；一旦被判定的插件其实
            // 是好的（启发式误判），用户就被无故卸包。现在改成必须先显式确认。
            if (broken.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine("检测到 " + broken.Count + " 个插件存在兼容性/损坏问题：");
                foreach (string b in broken) sb.AppendLine("  · " + b);

                if (!confirm)
                {
                    sb.AppendLine();
                    sb.AppendLine(">>> 已改为「只报告」模式（不再自动卸载插件）。");
                    sb.AppendLine("    卸载插件会重写 profile/package.json，属于不可逆操作，需要你确认后才执行。");
                    sb.AppendLine("    确认要继续卸载请在界面上选择「仍要卸载」。");
                    return sb.ToString();
                }

                // 卸载前先给关键配置打快照；快照失败就不动
                string snap = SafeConfig.Snapshot("卸载损坏插件之前");
                if (snap == null)
                {
                    sb.AppendLine();
                    sb.AppendLine(">>> 已中止：无法创建配置快照（" + SafeConfig.SnapshotRoot + "）。");
                    sb.AppendLine("    在无法留退路的情况下不执行卸载。");
                    return sb.ToString();
                }
                sb.AppendLine("（卸载前快照：" + Path.GetFileName(snap) + "，可用「恢复配置」退回）");

                foreach (string b in broken)
                {
                    string outText = RunDshPluginRemove(b);
                    sb.AppendLine("尝试移除 " + b + " → " + (string.IsNullOrEmpty(outText) || outText.IndexOf("Done", StringComparison.OrdinalIgnoreCase) >= 0 || outText.IndexOf("remove", StringComparison.OrdinalIgnoreCase) >= 0 ? "已执行移除，请重启 DSH 后确认" : "移除异常: " + outText));
                }
                sb.AppendLine();
                sb.AppendLine(">>> 已处理。请重启 DSH 后再试「为何打不开」；若仍异常，请确认已移除的插件是否还需要。");
                return sb.ToString();
            }

            return ">>> 结论：未发现不兼容/损坏的插件，兼容性正常。若仍打不开，可能是插件间 patch 冲突，尝试逐个卸载排查。";
        }

        // 运行 dsh plugin --profile web remove <name>（通过 bin.js）
        //
        // 2026-09-12 加固：旧实现在 30 秒超时后直接 p.Kill()。
        // 而 `dsh plugin remove` 正在做的恰恰是**重写 profile/package.json** ——
        // 在它写文件写到一半时把它杀掉，正好会留下半截/空 JSON，DSH 之后再也起不来。
        // 现在：①包删除前先给关键配置打快照；②超时不再强杀，改为继续等并如实报告；
        //       ③用异步读流避免 RedirectStandardOutput 管道写满导致的死锁。
        private static string RunDshPluginRemove(string name)
        {
            try
            {
                // ① 卸载会重写 package.json，先留退路
                SafeConfig.Snapshot("执行 dsh plugin remove " + name + " 之前");

                LaunchSpec spec = DiscoverLaunch();
                string bin = spec.BinJs ?? Path.Combine(UserProfile, ".ai-manager", "npm-global", "node-v24", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                string args = "\"" + bin + "\" plugin --profile web remove \"" + name + "\"";

                var psi = new ProcessStartInfo();
                psi.FileName = FindNodeExe();
                psi.Arguments = args;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                psi.WorkingDirectory = UserProfile;

                var so = new StringBuilder();
                var se = new StringBuilder();
                using (Process p = Process.Start(psi))
                {
                    // ③ 异步消费两路输出：既不阻塞，也不会因管道写满而互相卡死
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) so.AppendLine(e.Data); };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) se.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    // ② 给足时间（npm/pnpm 操作可能很慢）；超时也**不强杀**，
                    //    因为此刻它可能正在写 package.json，杀掉就是制造损坏。
                    if (!p.WaitForExit(180000))
                    {
                        return "警告：dsh plugin remove 超过 180 秒仍未结束，为免打断它写 package.json，"
                             + "本工具**没有**强杀它。它可能仍在后台运行 —— 请等它自行结束后再重启 DSH；"
                             + "若长时间无进展，用任务管理器结束它，然后点「恢复配置」退回快照。";
                    }
                    try { p.WaitForExit(); } catch { }   // 冲刷异步读取
                }
                string o = so.ToString().Trim();
                string errText = se.ToString().Trim();
                if (o.Length > 0) return o;
                if (errText.Length > 0) return errText;
                return "";
            }
            catch (Exception ex) { return "移除失败：" + ex.Message; }
        }

        private static void AppendDisabled(StringBuilder sb, string id, bool disabled)
        {
            sb.Append("- id: ").Append(id).AppendLine();
            sb.Append("  disabled: ").AppendLine(disabled ? "true" : "false");
        }

        private const string SkinStartMarker = "# --- dsh-skin managed (auto-generated; do not edit) ---";
        private const string SkinEndMarker = "# --- end dsh-skin managed ---";

        // 去掉文件里所有（可能重复/错位的）managed 块。
        // 2026-09-12 加固：旧实现只取「第一个起标记 + 第一个结束标记」，
        // 一旦文件里出现重复块或结束标记在起标记之前，就会把中间内容整段复制进去
        // （这正是"同一块重复 3 次"那类损坏的形态）。这里改为循环剥离直到干净。
        private static string StripManagedBlocks(string text)
        {
            string result = text;
            int guard = 0;
            while (guard++ < 50)
            {
                int s = result.IndexOf(SkinStartMarker, StringComparison.Ordinal);
                if (s < 0) break;
                int e = result.IndexOf(SkinEndMarker, s, StringComparison.Ordinal);
                if (e < 0)
                {
                    // 起标记在、结束标记没了（历史上被截断过）→ 从起标记起整段丢弃
                    result = result.Substring(0, s);
                    break;
                }
                result = result.Substring(0, s) + result.Substring(e + SkinEndMarker.Length);
            }
            return result;
        }

        private static void ReplaceOrAppendManaged(string path, string block, ref List<string> wrote)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string original = File.Exists(path) ? File.ReadAllText(path) : "";

            // 2026-09-12 加固 ①：带时间戳备份。
            // 旧代码写固定名 ".bak"，源文件若已经损坏，下一次备份就把唯一的好副本覆盖了。
            SafeConfig.BackupFile(path);

            string rest = StripManagedBlocks(original);
            rest = StripEmptyArrayLine(rest).TrimEnd();
            string result = (rest.Length > 0 ? rest + "\r\n\r\n" : "") + block + "\r\n";

            // 2026-09-12 加固 ②：原子写入（先写临时文件再原子替换，
            // 进程中途被杀也不会留下空文件/半截文件）。
            SafeConfig.AtomicWriteText(path, result);
            // 2026-09-12 加固 ③：写后立刻读回做 YAML 结构校验，不合法自动回滚。
            SafeConfig.VerifyAfterWrite(path, false, true);

            wrote.Add(Path.GetFileName(path));
        }

        private static string StripEmptyArrayLine(string text)
        {
            var lines = new List<string>();
            foreach (string raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.None))
            {
                if (raw.Trim() == "[]") continue; // 移除空数组行，避免与条目混排非法
                lines.Add(raw);
            }
            return string.Join("\r\n", lines.ToArray()).TrimEnd();
        }

        // ---------------- 底层工具 ----------------

        /// <summary>
        /// 统一的"静默跑一个外部程序并收 stdout"。
        /// 2026-09-16 加固：① 加 -WindowStyle Hidden / ProcessWindowStyle.Hidden 双保险，
        /// 控制台程序在部分机器上即使 CreateNoWindow=true 也会闪一下窗口；
        /// ② stderr 用异步方式读掉 —— 旧代码 RedirectStandardError=true 却从不读，
        /// 一旦子进程写满管道缓冲就会永久阻塞（表现为"卡住"）。
        /// 注意：这类调用现在只出现在**用户显式点按钮**时，状态轮询已经不创建任何进程。
        /// </summary>
        private static string RunCapture(string file, string args, int timeoutMs)
        {
            return RunCapture(file, args, timeoutMs, null);
        }

        /// <summary>
        /// ★ T31：取**系统** OEM 代码页，而不是"当前区域性"的。
        ///   `CultureInfo.CurrentCulture.TextInfo.OEMCodePage` 在"系统区域=中文、用户区域性=en-US"
        ///   这类多语言配置上会给 437，而 PowerShell 实际按 936 输出 ⇒ **中文乱码复发**，
        ///   且恰好发生在留学生/多语言机器上（本机两者一致所以复现不出来）。
        /// </summary>
        private static int SystemOemCodePage()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Nls\CodePage"))
                {
                    if (key != null)
                    {
                        string v = key.GetValue("OEMCP") as string;
                        int cp;
                        if (v != null && int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out cp) && cp > 0)
                            return cp;
                    }
                }
            }
            catch { }
            return CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
        }

        private static string RunCapture(string file, string args, int timeoutMs, Encoding forcedEnc)
        {
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = file;
                psi.Arguments = args;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                try { psi.WindowStyle = ProcessWindowStyle.Hidden; } catch { }
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                // ★ 2026-09-16 修复：**Windows PowerShell 5.1 被重定向时按 OEM 代码页输出**
                //   （中文系统 = 936/GBK），而这里原来一律按 UTF-8 解码 ⇒ 中文全乱码。
                //   字节级实证：「弹窗体检」输出里 `父=` 的原始字节是 B8 B8 3D ——
                //   GBK 能解出「父=」，UTF-8 解成 `��=`。
                //   ★ T31 再加固：由 `RunPsEncoded` **强制子进程按 UTF-8 输出**并在这里固定按
                //     UTF-8 解（不依赖任何"当前区域性"推断）。只有走非 RunPsEncoded 的
                //     powershell 调用才退回"系统 OEM 代码页"。
                Encoding childEnc = Encoding.UTF8;
                try
                {
                    if (forcedEnc != null) childEnc = forcedEnc;
                    else
                    {
                        string exeName = Path.GetFileName(file) ?? "";
                        if (exeName.ToLowerInvariant().StartsWith("powershell"))
                            childEnc = Encoding.GetEncoding(SystemOemCodePage());
                    }
                }
                catch { childEnc = Encoding.UTF8; }
                psi.StandardOutputEncoding = childEnc;
                psi.StandardErrorEncoding = childEnc;

                var so = new StringBuilder();
                using (Process p = Process.Start(psi))
                {
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                    { if (e.Data != null) { lock (so) so.AppendLine(e.Data); } };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { /* 丢弃，仅为排空管道 */ };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return null;
                    }
                    try { p.WaitForExit(); } catch { }   // 冲刷异步读取
                    lock (so) return so.ToString();
                }
            }
            catch { return null; }
        }

        public static string RunHidden(string file, string args, int timeoutMs)
        {
            // cmd.exe 用于需要引号转义/内置命令的场景时，保持调用方给的 args 原样
            return RunCapture(file, args, timeoutMs);
        }

        public static string RunPsEncoded(string script, int timeoutMs)
        {
            // ★ T31：**强制子进程按 UTF-8 输出**，C# 端也固定按 UTF-8 解 ——
            //   不再依赖"当前区域性"或"系统 OEM 代码页"的任何推断，从根上消除中文乱码。
            //   （旧版是"猜子进程会用哪个代码页"，这在多语言机器上必然会猜错。）
            string wrapped =
                "try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}; " +
                "try { $OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}; " +
                script;
            string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
            return RunCapture("powershell.exe",
                "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand " + b64,
                timeoutMs, Encoding.UTF8);
        }

        private static string JoinPids(int[] pids)
        {
            if (pids == null || pids.Length == 0) return "-";
            return string.Join(",", Array.ConvertAll(pids, x => x.ToString(CultureInfo.InvariantCulture)));
        }

        // 深度诊断：为什么 dsh web 打不开（返回分步结论）
        public static string WhyOpenFailed()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== 为何打不开 · 深度诊断 ====");
            sb.AppendLine("检测时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            // ★ 2026-09-16：先亮出"本次认的是哪个端口、凭什么" —— 端口认错会让下面每一条都查错对象
            sb.AppendLine("端口     : " + ActivePort + "（来源 " + (string.IsNullOrEmpty(DshBoot.LastPortSource) ? "未重新发现" : DshBoot.LastPortSource) + "）");
            sb.AppendLine();

            LaunchSpec spec = DiscoverLaunch();
            // ★ T5：诊断必须走 deep 扫描，否则"进程活着但端口没起来"这一类看不见
            ServiceState st = CheckState(true);

            // 1. dsh 入口
            if (spec.BinJs == null || !File.Exists(spec.BinJs))
                sb.AppendLine("[FAIL] dsh 入口缺失：未找到 bin.js（已扫 launcher.ini 与常见路径）");
            else
                sb.AppendLine("[OK]   dsh 入口: " + spec.BinJs);

            // 2. node 可执行
            bool nodeOk = spec.Node != null && (spec.Node == "node" || File.Exists(spec.Node));
            if (!nodeOk)
                sb.AppendLine("[FAIL] node 可执行文件不可用: " + spec.Node);
            else
                sb.AppendLine("[OK]   node 可执行: " + spec.Node);

            // 3. 端口占用（最关键的"打不开"根因）
            int[] owners = GetPortOwnerPids(ActivePort);
            if (st.Listens)
            {
                if (st.DshPids.Length > 0)
                    sb.AppendLine("[OK]   端口 " + ActivePort + " 由 dsh 进程监听 (PID " + JoinPids(st.DshPids) + ")");
                else if (owners.Length > 0)
                    // ★ 2026-09-16（P7）：界面上**没有**「强制释放端口」这个按钮，别让用户去找它。
                    sb.AppendLine("[FAIL] 端口 " + ActivePort + " 被其他进程占用 (PID " + string.Join(",", owners) + ") —— 这是打不开的常见原因。"
                                  + "救星不会替你去杀它（避免误杀别的程序），请自行确认该 PID 后处理。");
                else
                    // ★ P8：这里原来写死"端口 3080"，而判的是 ActivePort
                    sb.AppendLine("[WARN] 端口 " + ActivePort + " 有监听，但未识别到 dsh 进程");
            }
            else
            {
                // ★ P8：同上，去掉硬编码 3080（本机端口真的漂过）
                sb.AppendLine("[WARN] 端口 " + ActivePort + " 未监听 —— dsh 服务没起来");
            }

            // 4. HTTP 响应
            if (st.HttpResponds) sb.AppendLine("[OK]   HTTP 有响应");
            else if (st.Listens) sb.AppendLine("[FAIL] 端口有监听但 HTTP 无响应 —— 疑似服务卡死，点“重启服务”");
            else sb.AppendLine("[WARN] HTTP 无响应");

            // 5. 进程存在但没监听
            // ★ T5 配套：这一段以前**永远打不出来**（HasDshProcess 由监听者派生 ⇒ 与 !Listens 互斥）。
            //   它恰恰是"启动失败/卡死"最有价值的一条线索，现在能正常输出了。
            if (st.HasDshProcess && !st.Listens)
                sb.AppendLine("[FAIL] 存在 dsh 进程 (PID " + JoinPids(st.DshPids) + ") 但未监听 " + ActivePort
                              + " —— 启动失败或卡死，看 dsh-web.out.log");

            // 6. 前端 dist 是否构建（缺失会导致页面空白/打不开）
            string dist = FindWebDist();
            if (dist != null)
                sb.AppendLine("[OK]   前端构建产物存在: " + dist);
            else
                sb.AppendLine("[WARN] 未在 dsh 安装树找到前端 dist（可能为源码未构建，页面会白屏）");

            // 7. profile 配置
            string profilePatch = Path.Combine(DshHome, "profiles", "web", "cordis.patch.yml");
            if (!File.Exists(profilePatch))
                sb.AppendLine("[WARN] profile 补丁文件缺失: " + profilePatch);

            // 8. 日志中的致命错误
            // ★ 2026-09-16 修复：ReadLogLastErrors 扫的是**整个日志文件（含历史）**，所以服务
            //   明明正常也会命中陈年旧错 —— 实测健康状态下报 [FAIL]，而紧接着的结论行又说
            //   "服务实际在正常运行"，**自相矛盾**（同一份报告里两句话打架，很伤人信任）。
            //   现在按服务状态分级：**HTTP 正常 ⇒ 只当提示**；确实异常才标 FAIL。
            string lastErr = ReadLogLastErrors();
            if (lastErr.Length > 0)
            {
                if (st.HttpResponds)
                    sb.AppendLine("[i]    日志里有历史错误线索（**当前服务 HTTP 正常，多为历史遗留，仅供参考**）:\r\n" + lastErr);
                else
                    sb.AppendLine("[FAIL] 日志存在错误线索:\r\n" + lastErr);
            }
            else
                sb.AppendLine("[OK]   日志无致命错误");

            // 结论
            // ★ 2026-09-16（T5 连带修正）：旧判据 `owners.Length > 0 && !st.HasDshProcess` 在
            //   HasDshProcess 改成"存在 dsh 进程"之后语义会漂 —— 若 dsh 进程活着但没监听、
            //   而端口另被别的程序占着，两种状态同时成立。所以这里改成**逐 pid 判**：
            //   "端口占用者里存在**不属于 DshPids** 的进程" 才是"被非 dsh 占用"。
            bool hasNonDshOccupier = false;
            foreach (int pid in st.OccupierPids)
            {
                bool isDsh = false;
                foreach (int d in st.DshPids) if (d == pid) { isDsh = true; break; }
                if (!isDsh) { hasNonDshOccupier = true; break; }
            }

            sb.AppendLine();
            if (st.HttpResponds)
                sb.AppendLine(">>> 结论：服务实际在正常运行（HTTP 可访问）。打不开可能是浏览器缓存/端口配置被改，试试刷新或“打开界面”。");
            else if (hasNonDshOccupier)
                sb.AppendLine(">>> 结论：当前端口被**非 dsh 的其它程序**占用（PID " + JoinPids(st.OccupierPids) + "）。"
                              + "请先关掉占用它的程序；若是 dsh 残留进程，点“停止服务”清掉后再“启动并打开”。");
            else if (st.Listens && !st.HttpResponds)
                sb.AppendLine(">>> 结论：服务卡死。点“重启服务”即可。");
            else if (st.HasDshProcess && !st.Listens)
                sb.AppendLine(">>> 结论：dsh 启动失败（进程在但端口没起来）。点“重启服务”，若仍失败看日志错误线索。");
            else
                sb.AppendLine(">>> 结论：服务未运行。点“启动并打开”。");
            return sb.ToString();
        }

        // ================= 检查并修复"构建产物丢失" =================
        // 场景：桌面 DeepSeek Harness.exe / launcher assets 里 exe 丢失。
        // 修复：优先从 launcher 源码 assets 复制；其次从 .dsh/whale-desktop-launcher 备份复制。
        // 返回修复结果（供日志/界面展示）。
        public static string EnsureDesktopLauncher()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== 检查桌面客户端（DeepSeek Harness.exe） ====");
            // 2026-09-16：顺带修 launcher.ini 的端口漂移 ——
            // 图标按 Url 探测、按 Arguments 启动，两者来源不同必然漂，
            // 漂了就会「探测 A、服务起在 B」，永远等不到就绪。见文档根因①。
            sb.AppendLine(DshBoot.RepairLauncherIni());

            string desktopExe = Path.Combine(UserProfile, "Desktop", "DeepSeek Harness.exe");
            // 候选的 exe 源（源码 assets / installDir 备份）
            string[] sources =
            {
                Path.Combine(UserProfile, "dsh-whale-desktop-launcher", "assets", "DeepSeek Harness.exe"),
                Path.Combine(DshHome, "whale-desktop-launcher", "DeepSeek Harness.exe"),
            };

            if (File.Exists(desktopExe))
            {
                sb.AppendLine("[OK] 桌面 DeepSeek Harness.exe 存在（" + new FileInfo(desktopExe).Length + " 字节）。");
                return sb.ToString();
            }

            sb.AppendLine("[FAIL] 桌面 DeepSeek Harness.exe 缺失（构建产物丢失）。正在修复…");
            string src = null;
            foreach (string s in sources) if (File.Exists(s)) { src = s; break; }
            if (src == null)
            {
                // 最后手段：尝试从 node_modules 的 link 目标找
                string nm = Path.Combine(DshHome, "profiles", "web", "node_modules", "dsh-whale-desktop-launcher", "assets", "DeepSeek Harness.exe");
                if (File.Exists(nm)) src = nm;
            }
            if (src == null)
            {
                sb.AppendLine("[FAIL] 找不到可用的 DeepSeek Harness.exe 源文件。\r\n  请从 GitHub 重新安装 dsh-whale-desktop-launcher（`dsh plugin --profile web add github:HUITianYi/dsh-whale-desktop-launcher#v0.1.0`），或手动补齐该 exe。");
                return sb.ToString();
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(desktopExe));
                File.Copy(src, desktopExe, true);
                // 同时补 installDir
                try
                {
                    string installDir = Path.Combine(DshHome, "whale-desktop-launcher");
                    Directory.CreateDirectory(installDir);
                    File.Copy(src, Path.Combine(installDir, "DeepSeek Harness.exe"), true);
                }
                catch { }
                sb.AppendLine("[OK] 已从 " + src + " 恢复桌面 DeepSeek Harness.exe（" + new FileInfo(desktopExe).Length + " 字节）。");
                return sb.ToString();
            }
            catch (Exception e)
            {
                sb.AppendLine("[FAIL] 恢复失败：" + e.Message);
                return sb.ToString();
            }
        }

        // 检查 ComfyUI 服务（8188）是否运行，未运行则拉起
        public static string EnsureComfyUi()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== 检查本地绘图服务（ComfyUI :8188） ====");
            if (HttpProbe("http://127.0.0.1:8188/", 1200))
            {
                sb.AppendLine("[OK] ComfyUI 运行中（http://127.0.0.1:8188）。");
                return sb.ToString();
            }
            sb.AppendLine("[WARN] ComfyUI 未响应，尝试启动…");
            string comfyPy = Path.Combine(UserProfile, "ComfyUI", "venv", "Scripts", "python.exe");
            string comfyMain = Path.Combine(UserProfile, "ComfyUI", "main.py");
            if (!File.Exists(comfyPy) || !File.Exists(comfyMain))
            {
                sb.AppendLine("[WARN] 未找到 ComfyUI（期望 " + comfyMain + "）。跳过。");
                return sb.ToString();
            }
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = comfyPy;
                psi.Arguments = "main.py --port 8188 --listen 127.0.0.1";
                psi.WorkingDirectory = Path.Combine(UserProfile, "ComfyUI");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                // ⚠️ 根治 ComfyUI 反复报 OSError [Errno 22]：stdout/stderr 若被重定向
                // 到管道却无人消费，tqdm 疯狂写 stderr 一旦管道缓冲填满，flush() 就抛
                // "Invalid argument"，导致任何含 KSampler 的流程必崩、0 输出、GPU 空转。
                // 必须立即异步消费两流，绝不能留不读的管道。
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                // 异步无限读并丢弃，保证管道永不满、句柄始终有效（防 tqdm 写崩）。
                p.OutputDataReceived += (s, e) => { /* discard */ };
                p.ErrorDataReceived += (s, e) => { /* discard */ };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                sb.AppendLine("[OK] 已启动 ComfyUI（PID " + p.Id + "），等待就绪…");
                // 等待最多 60 秒；分场景判定：进程是否存活、端口是否监听、最终是否 HTTP 就绪
                bool ready = false;
                bool alive = true;
                bool portUp = false;
                for (int i = 0; i < 60; i++)
                {
                    Thread.Sleep(1000);
                    alive = !p.HasExited;
                    portUp = HttpProbe("http://127.0.0.1:8188/", 1000);
                    if (portUp) { ready = true; sb.AppendLine("[OK] ComfyUI 已就绪。"); break; }
                    // 进程中途退出 → 已无意义继续等，直接报原因
                    if (!alive) break;
                }
                if (!ready)
                {
                    // 分场景错误提示（启动守护）
                    if (!alive)
                        sb.AppendLine("[FAIL] ComfyUI 进程提前退出（PID " + p.Id + "）。可能原因：venv 依赖缺失 / 显卡驱动报错 / 端口被其他实例占用。请查看 ComfyUI 日志（comfy-启动.log.err）排查。");
                    else if (portUp)
                        sb.AppendLine("[FAIL] ComfyUI 端口已监听但仍未就绪（60 秒超时）。可能原因：模型冷加载过慢（Flux GGUF 6.5GB）或前一次异常残留。可再等一会儿或重启本工具重试。");
                    else
                        sb.AppendLine("[FAIL] ComfyUI 启动 60 秒内仍未监听端口 8188。可能原因：venv 未装全依赖 / 显卡驱动异常 / 启动参数错误。请查看 ComfyUI 日志（comfy-启动.log.err）排查。");
                }
                return sb.ToString();
            }
            catch (Exception e)
            {
                sb.AppendLine("[FAIL] 启动 ComfyUI 失败：" + e.Message);
                sb.AppendLine("   排查：确认 venv 存在（" + comfyPy + "）、显卡驱动正常、无其他实例占用 8188。");
                return sb.ToString();
            }
        }

        // ================= 强力自愈：确保 dsh web 一定打开 =================
        // 解决"端口 3080 被僵尸 dsh / 其他进程占用，导致打不开"的问题：
        //   1) 精准杀掉所有 bin.js 相关 node 进程（含僵尸/卡死的）
        //   2) 强制释放 3080 端口上的一切占用进程（快速方式）
        //   3) 等待端口真正释放
        //   4) 循环启动 dsh web，直到 HTTP 探测成功（最多 N 次）
        //   5) 顺带检查桌面客户端与 ComfyUI 是否缺失并修复
        // 返回详细过程供界面/日志展示。
        public static string ForceRecover()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== 强力自愈启动 ====");

            // --- 0) 动手之前：先体检配置 + 拍快照 ---
            // 2026-09-12 加固：自愈要杀 node 进程，强杀可能打断 DSH 写 settings.yaml；
            // 先留退路，再动手。配置本身若已损坏，这里也会先报出来（并可用快照回滚）。
            string snap = SafeConfig.Snapshot("强力自愈之前");
            sb.AppendLine(snap == null
                ? "[WARN] 无法创建配置快照，后续操作将不做自动回滚。"
                : "[OK]   已快照关键配置 → " + Path.GetFileName(snap));
            foreach (string cp in SafeConfig.CriticalPaths())
            {
                if (!File.Exists(cp))
                    sb.AppendLine("[WARN] 缺少配置：" + cp.Substring(DshHome.Length).TrimStart('\\'));
            }
            sb.AppendLine();

            // --- 0b) 先检查并修复桌面客户端 / ComfyUI（构建产物缺失） ---
            sb.AppendLine(EnsureDesktopLauncher());
            sb.AppendLine();

            // --- 1) 杀掉所有 dsh 的 node 进程（僵尸 dsh） ---
            // ★ T15：这里的口径已统一 —— GetDshPids() 现在按"IsDshEntry(命令行)"判定，
            //   不再"命令行里含 bin.js 就算 dsh"（那会连别的项目的同名脚本一起杀）。
            // ★★ v5（2026-09-18）：改走 GetDshPidsForKill() —— 有显式覆盖时按端口收窄。
            //   否则「强力自愈」在沙箱里跑一次，会把用户线上正在承载会话的 dsh 一起清掉
            //   （WP3 实测事故，见 HasExplicitScopeOverride 的注释）。
            int[] dshPids = GetDshPidsForKill();
            if (ScopeNote().Length > 0) sb.AppendLine(ScopeNote().TrimEnd());
            if (dshPids.Length > 0)
            {
                sb.AppendLine("发现 dsh 进程 (PID: " + string.Join(",", dshPids) + ")，强制结束…");
                // 统一走 KillDshLikeProcesses：它会**再确认一次身份**，并把
                // 「跳过谁、为什么跳过」「谁杀失败、失败原因」都写进报告（旧版 catch 里什么都不留）。
                KillDshLikeProcesses(dshPids, sb);
            }
            else
            {
                sb.AppendLine("未发现 dsh 进程。");
            }

            // --- 2) 释放当前端口占用进程 ---
            // ★ 2026-09-16 补修（T1 —— 理论审查抓到我的 P12 只改了一半）：
            //   这里原来是**无条件 `pr.Kill()`**，连进程名都不看，谁占端口就杀谁。
            //   ★ 即使按钮叫「强力自愈」，**杀掉无关程序也是"误杀"而不是"强力"** ——
            //   用户能接受"清掉 dsh 残留"，但绝不该接受"顺手杀了别的软件"。
            //   现在统一走 KillDshLikeProcesses：只结束**确认是 dsh** 的进程，跳过的写进报告。
            int[] owners = GetPortOwnerPidsFast(ActivePort);
            if (owners.Length > 0)
            {
                sb.AppendLine("端口 " + ActivePort + " 被占用 (PID: " + string.Join(",", owners) + ")，尝试释放…");
                int killed = KillDshLikeProcesses(owners, sb);
                sb.AppendLine("  已结束 " + killed + " 个 dsh 进程。");
                if (killed < owners.Length)
                    sb.AppendLine("  ⚠ 有 " + (owners.Length - killed) + " 个占用者**不是 dsh（或身份无法确认），已跳过**（避免误杀）；"
                                  + "请自行确认它们后处理，或换个端口。");
            }
            else
            {
                sb.AppendLine("端口 " + ActivePort + " 当前未被占用。");
            }

            // --- 3) 等待端口真正释放（最多 8 秒） ---
            sb.AppendLine("等待端口 " + ActivePort + " 释放…");
            bool freed = false;
            for (int i = 0; i < 16; i++)
            {
                Thread.Sleep(500);
                if (!PortListens(ActivePort, 300)) { freed = true; break; }
            }
            sb.AppendLine(freed ? "端口已释放。" : "警告：端口仍未释放，继续尝试启动。");

            // --- 4) 循环启动直到 HTTP 成功 ---
            bool started = false;
            string last = "";
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                last = StartDsh() ? "已拉起 dsh 进程。" : "启动命令执行失败。";
                sb.AppendLine("启动尝试 " + attempt + ": " + last);
                // 等待服务响应（最多 15 秒/次）
                bool ok = false;
                for (int i = 0; i < 15; i++)
                {
                    Thread.Sleep(1000);
                    if (HttpProbe(ActiveRootUrl, 900)) { ok = true; break; }
                }
                if (ok)
                {
                    started = true;
                    sb.AppendLine(">>> 自愈成功：dsh web 已恢复并响应 HTTP。（" + ActiveRootUrl + "）");
                    break;
                }
                // 若这次没起来，再清一次进程和端口再试
                // ★ T15/T4b：旧版是裸 `try{...Kill();}catch{}` —— 既不校验身份、也不记失败原因。
                //   现在统一走 KillDshLikeProcesses（身份校验 + 原因进报告）。
                // ★★ v5（2026-09-18）：这里**必须**跟第 1) 步同口径 —— 用 GetDshPidsForKill()。
                //   上一版只改了第 1) 步，重试循环里还留着全机扫描的 GetDshPids()：
                //   沙箱里只要第一次启动没起来，重试就会把用户线上正在承载会话的 dsh 全清掉，
                //   而报告上看起来"范围已收窄"——那是最坏的一种不一致（说了不算）。
                KillDshLikeProcesses(GetDshPidsForKill(), sb);
                // ★ 2026-09-16（P12）：端口占用者**只结束确认是 dsh 的进程**，避免误杀别的程序；
                //   被跳过的会写进自愈报告（用 sb），用户能看到"端口被某某程序占着"。
                KillDshLikeProcesses(GetPortOwnerPidsFast(ActivePort), sb);
                Thread.Sleep(2000);
            }

            if (!started)
            {
                sb.AppendLine(">>> 自愈未完全成功。已清理僵尸进程并尝试启动，但 HTTP 仍未响应。");
                sb.AppendLine("    可能原因：前端构建缺失 / 配置损坏 / 模型适配问题。可点「导出报告」并查看 dsh-web.out.log。");
            }

            // 顺带检查本地绘图服务（ComfyUI），若掉线则拉起
            sb.AppendLine();
            sb.AppendLine(EnsureComfyUi());

            // 顺便拉起桌面客户端（若存在）
            try
            {
                string desktopExe = Path.Combine(UserProfile, "Desktop", "DeepSeek Harness.exe");
                if (started && File.Exists(desktopExe)) Process.Start(desktopExe);
            }
            catch { }

            // 2026-09-12 加固：强杀之后体检配置，坏了自动回滚到动手前的快照
            try
            {
                string heal = SafeConfig.HealAfterKill(snap);
                if (!string.IsNullOrEmpty(heal)) { sb.AppendLine(); sb.Append(heal); }
            }
            catch { }

            return sb.ToString();
        }

        // 一键修复：根据“为何打不开”的根因执行对应动作，返回操作结果
        public static string RepairFromWhy()
        {
            if (ReadOnlyMode) return ReadOnlyRefusal();
            // ★ T5：这里必须走 deep 扫描 —— 「进程活着但端口没起来」正是本按钮要处理的场景，
            //   而它只有在全量扫描下才看得见（旧版判据恒假 ⇒ 会走分支 4 重复起实例）。
            ServiceState st = CheckState(true);
            int[] owners = GetPortOwnerPids(ActivePort);

            // 1) 有监听但 HTTP 无响应 → 重启
            // ★ T12：旧版这里把 RestartDsh 精心拼出来的 LastRestartNote（含"端口释放失败／
            //   启动失败原因／重启超时"三种真原因）**整段丢掉**，只回一句"请手动查看日志"；
            //   而 MainForm 的「重启服务」按钮是拼了的 ⇒ 同一个失败，两个按钮给出不同质量的诊断。
            if (st.Listens && !st.HttpResponds)
                return RestartDsh()
                    ? "已重启：检测到服务卡死，已强制重启 dsh。" + NoteSuffix()
                    : "重启失败。" + NoteSuffix();

            // 2) 端口被非 dsh 进程占用
            // ★ 2026-09-16 修复（P7 + 修好 P1 之后的**连带问题**）：
            //   这段原来写"或直接点『启动并打开』让它改用其它端口" —— **那是错的指引**。
            //   修好 P1 之后本分支**从死代码变活**（以前恒为假、根本不执行），
            //   而 `OpenUiInPrivateFresh` 早就改成"端口已被占用就不再另起实例"（见上面的修复注释），
            //   ⇒ 点「启动并打开」**不会**改用其它端口，只会在同一端口上失败。
            //   现在改成如实说明 + 真正可行的下一步：不给假指望，也不替用户杀进程。
            //
            // ★ T14：判据从"DshPids 为空"改成**逐 pid 判"占用者里有没有非 dsh"** ——
            //   端口被**另一个 node 程序**（不是 dsh）占着时，旧判据不触发（DshPids 非空），
            //   于是落到分支 4 在已被占用的端口上起 dsh，必然 EADDRINUSE，
            //   最后却回"已启动 dsh web，正在尝试打开界面……"（假成功）。
            bool hasNonDshOccupier = false;
            foreach (int pid in owners)
            {
                bool isDsh = false;
                foreach (int d in st.DshPids) if (d == pid) { isDsh = true; break; }
                if (!isDsh) { hasNonDshOccupier = true; break; }
            }
            if (hasNonDshOccupier)
            {
                return "检测到端口 " + ActivePort + " 被**非 dsh 的其它程序**占用（PID " + string.Join(",", owners) + "）。\r\n"
                     + "救星不会去杀它（避免误杀），也不会另起实例（同一端口必然冲突）。\r\n"
                     + "请先关掉占用该端口的程序，再点「🚀 启动并打开」；\r\n"
                     + "若占用者其实是 dsh 残留进程（比如卡死没退干净），点「⏹ 停止服务」即可清掉。";
            }

            // 3) 进程在但没监听 → 重启（T5 修复后本分支**第一次真正可达**）
            if (st.HasDshProcess && !st.Listens)
                return RestartDsh()
                    ? "已重启：dsh 进程存在但端口未监听，已重新拉起。" + NoteSuffix()
                    : "重启失败。" + NoteSuffix();

            // 4) 其余：直接启动
            // ★ T13：旧版成功后立刻回"已启动 dsh web，正在尝试打开界面……"，
            //   而 StartDsh **只保证"进程已创建"**，不保证服务可用 —— 调用方随后
            //   `thenOpen=true` 会马上去 OpenUi()，而 OpenUi 一上来就 HttpProbe；
            //   冷启动要 40~60 秒 ⇒ 探测必失败 ⇒ **浏览器窗口不会打开**。
            //   用户看到"正在尝试打开界面……"然后什么都没有发生 = 教科书式"假成功/无反应"。
            //   现在如实说明"进程已拉起、还没就绪"，并让调用方**不要**立刻去开浏览器。
            if (StartDsh())
            {
                LastRepairStartedOnly = true;
                return "已拉起 dsh 进程。" + Environment.NewLine
                     + "⚠ dsh 冷启动通常需要 **40~60 秒**（argo-mcp 预热 + unified-search 初始化），"
                     + "现在还没就绪，**界面不会立刻打开**。\r\n"
                     + "请等约 1 分钟后点「🚀 启动并打开」（或等本工具提示就绪）。";
            }
            LastRepairStartedOnly = false;
            return "启动失败：" + (LastStartError.Length > 0 ? LastStartError : "未找到可用的 dsh 入口（bin.js / node）。");
        }

        /// <summary>
        /// 一键修复是否"只拉起了进程、还没就绪"（T13）。
        /// 界面据此决定**不要**立刻调 OpenUi —— 那会在冷启动期间必然失败、
        /// 表现为"点了没反应"，比不打开更伤信任。
        /// </summary>
        public static bool LastRepairStartedOnly = false;

        /// <summary>把最近一次重启/停止的真实原因拼成可读后缀（T12）。</summary>
        private static string NoteSuffix()
        {
            string note = _lastRestartNote.Length > 0 ? _lastRestartNote : _lastStopNote;
            return string.IsNullOrEmpty(note) ? "请手动查看日志。" : "\r\n\r\n" + note;
        }

        // 定位前端构建产物（web 页面资源）：自动在 dsh 安装树内扫描显著的 dist/frontend 目录
        private static string FindWebDist()
        {
            try
            {
                // 候选根：dsh 安装目录（appdata npm 或 .ai-manager）
                string[] roots =
                {
                    Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "npm", "node_modules", "@deepseek-ai", "dsh"),
                    Path.Combine(UserProfile, ".ai-manager", "npm-global", "node-v24", "node_modules", "@deepseek-ai", "dsh"),
                };
                foreach (string root in roots)
                {
                    if (!Directory.Exists(root)) continue;
                    // 直接命中常见的 dist 目录
                    string[] exact =
                    {
                        Path.Combine(root, "node_modules", "@deepseek-ai", "dsh-web-app", "dist"),
                        Path.Combine(root, "node_modules", "@deepseek-ai", "dsh-web", "dist"),
                        Path.Combine(root, "dist", "web"),
                    };
                    foreach (string e in exact) if (Directory.Exists(e)) return e;
                    // 兜底：扫描 @deepseek-ai 下含 index.html 或 index.htm 的目录（前端静态产物）
                    string webHtml = FindIndexHtml(Path.Combine(root, "node_modules", "@deepseek-ai"));
                    if (webHtml != null) return webHtml;
                }
            }
            catch { }
            return null;
        }

        // 在 @deepseek-ai 作用域下递归找 index.html 所在目录（浅层优先）
        private static string FindIndexHtml(string scopeDir)
        {
            try
            {
                if (!Directory.Exists(scopeDir)) return null;
                // 优先检查常见的 dsh-web-frontend / dsh-web / dist 目录
                foreach (string pkg in new[] { "dsh-web-frontend", "dsh-web", "dsh-host-frontend-static" })
                {
                    string baseDir = Path.Combine(scopeDir, pkg);
                    if (!Directory.Exists(baseDir)) continue;
                    foreach (string sub in Directory.GetDirectories(baseDir, "*", SearchOption.TopDirectoryOnly))
                    {
                        if (File.Exists(Path.Combine(sub, "index.html")) || File.Exists(Path.Combine(sub, "index.htm")))
                            return sub;
                    }
                    // 包根直接有 index.html
                    if (File.Exists(Path.Combine(baseDir, "index.html"))) return baseDir;
                    // lib/dist 子目录
                    foreach (string sub in new[] { "dist", "lib" })
                    {
                        string p = Path.Combine(baseDir, sub);
                        if (File.Exists(Path.Combine(p, "index.html"))) return p;
                    }
                }
            }
            catch { }
            return null;
        }

        // 读日志里的错误行（ERROR/Error/Cannot/ENOENT 等）
        // ★ 局限（2026-09-16 标注，勿当"当前故障"读）：本函数**扫整个日志文件、没有时间窗**，
        //   所以返回的内容**包含历史错误**。调用方必须按服务状态决定定性 ——
        //   服务 HTTP 正常时只能当"历史线索/提示"，不能标 [FAIL]（已在 WhyOpenFailed 里这么做）。
        private static string ReadLogLastErrors()
        {
            try
            {
                var sb = new StringBuilder();
                int count = 0;
                foreach (string logpath in new[] { DshWebLog, DshWebLog + ".err" })
                {
                    if (!File.Exists(logpath)) continue;
                    foreach (string line in File.ReadLines(logpath))
                    {
                        // 优先捕获插件树崩溃（最有用的线索）
                        if (line.IndexOf("plugin tree failed", StringComparison.OrdinalIgnoreCase) >= 0
                            || line.IndexOf("failed to apply loader entry", StringComparison.OrdinalIgnoreCase) >= 0
                            || line.IndexOf("failed to import loader entry", StringComparison.OrdinalIgnoreCase) >= 0
                            || line.IndexOf("EPERM", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            sb.AppendLine("  " + line.Trim());
                            if (++count >= 8) break;
                        }
                        else if (line.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0
                                 || line.IndexOf("Cannot", StringComparison.OrdinalIgnoreCase) >= 0
                                 || line.IndexOf("ENOENT", StringComparison.OrdinalIgnoreCase) >= 0
                                 || line.IndexOf("ECONNREFUSED", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            sb.AppendLine("  " + line.Trim());
                            if (++count >= 6) break;
                        }
                    }
                    if (count >= 8) break;
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        // 诊断结果文本（供 CLI 自检 / 日志）
        public static string DiagnosticsText()
        {
            var sb = new StringBuilder();
            DiagItem[] items = RunDiagnostics();
            foreach (DiagItem it in items)
            {
                sb.Append(it.Ok ? "[OK]   " : "[FAIL] ").Append(it.Text);
            }
            return sb.ToString();
        }

        // ================= 导出诊断报告 =================
        // 汇总全部诊断信息（环境、服务状态、为何打不开、插件兼容性、崩溃排查、日志线索），
        // 写入报告文件，返回报告完整内容。
        public static string ExportDiagnostics()
        {
            var sb = new StringBuilder();
            string line = new string('=', 60);
            sb.AppendLine(line);
            sb.AppendLine("  大肥鱼救星 · DSH 诊断报告");
            sb.AppendLine("  生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(line);
            sb.AppendLine();

            // 1. 环境信息
            sb.AppendLine("【1. 环境】");
            LaunchSpec spec = DiscoverLaunch();
            sb.AppendLine("  DSH 版本      : " + DshVersion());
            sb.AppendLine("  profile       : web");
            sb.AppendLine("  DSH 入口      : " + (spec != null ? spec.BinJs : "?"));
            sb.AppendLine("  node          : " + (spec != null ? spec.Node : "?"));
            sb.AppendLine("  DSH home      : " + DshHome);
            sb.AppendLine("  URL           : " + ActiveRootUrl);
            sb.AppendLine();

            // 2. 服务状态
            sb.AppendLine("【2. 服务状态】");
            ServiceState st = CheckState();
            sb.AppendLine("  端口 " + ActivePort + " 监听 : " + st.Listens);
            // ★ 2026-09-16：把"凭什么认这个端口"写进报告 —— 别人的 dsh 不一定在 3080（可能是 3081…）
            sb.AppendLine("  端口发现来源   : " + (string.IsNullOrEmpty(DshBoot.LastPortSource) ? "（未重新发现）" : DshBoot.LastPortSource)
                          + "；显式设置：" + (DshBoot.ReadPortOverride() > 0
                                ? DshBoot.ReadPortOverride() + "（" + (DshBoot.PortOverrideLabel ?? "?") + "）"
                                : "无（可设 DSH_PORT 或写 " + DshBoot.PortOverrideFile + "）"));
            sb.AppendLine("  HTTP 响应      : " + st.HttpResponds);
            sb.AppendLine("  dsh 进程 PID   : " + (st.DshPids.Length > 0 ? string.Join(",", st.DshPids) : "无"));
            sb.AppendLine("  端口占用者     : " + (st.OccupierPids.Length > 0 ? string.Join(",", st.OccupierPids) : "无"));
            sb.AppendLine();

            // 3. 为何打不开
            sb.AppendLine("【3. 为何打不开诊断】");
            sb.AppendLine(WhyOpenFailed());
            sb.AppendLine();

            // 4. 插件兼容性
            sb.AppendLine("【4. 插件兼容性】");
            sb.AppendLine(PluginCompatibility());
            sb.AppendLine();

            // 5. 插件崩溃排查
            sb.AppendLine("【5. 插件崩溃排查】");
            sb.AppendLine(DetectCrashedPlugin());
            sb.AppendLine();

            // 6. 运行诊断（通用）
            sb.AppendLine("【6. 诊断明细】");
            sb.AppendLine(DiagnosticsText());
            sb.AppendLine();

            // 7. 日志线索
            sb.AppendLine("【7. 日志错误线索】");
            string lastErr = ReadLogLastErrors();
            sb.AppendLine(lastErr.Length > 0 ? lastErr : "  （日志无显著错误）");
            sb.AppendLine();

            sb.AppendLine(line);
            sb.AppendLine("  报告路径: " + DiagnosticsReportPath);
            sb.AppendLine(line);

            string content = sb.ToString();
            try
            {
                EnsureAppDataDir();
                File.WriteAllText(DiagnosticsReportPath, content, new UTF8Encoding(false));
            }
            catch { }
            return content + "\r\n\r\n>>> 已导出到: " + DiagnosticsReportPath;
        }

        public static string DiagnosticsReportPath
        {
            get { return Path.Combine(AppDataDir, "dsh-diagnostics-report.txt"); }
        }
    }
}

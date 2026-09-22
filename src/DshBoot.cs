using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// ============================================================
// DSH 启动链路（2026-09-16 依据《dsh打不开_故障排查与修复_20260916.md》重写）
//
// 文档给的三个根因，本文件逐条对应：
//   ① 【端口漂移】launcher.ini 里 Url=…:3080 但 Arguments 是 --port 3081
//      → 桌面图标探测 3080、服务起在 3081，永远等不到 ready，还每次留一个孤儿进程。
//      救星自己也有同一类病：ActivePort 被硬编码成 3081，而 dsh 实际跑在 3080
//      → 救星"看不见"正在运行的实例，所有按钮都打空。
//      ⇒ DiscoverLivePort() / CheckLauncherIni() / RepairLauncherIni()
//   ② 【冷启动 40~60 秒】argo-mcp 预热 + unified-search 初始化，而旧工具只等 30/40 秒
//      → 稳定报"未能取得认证地址"。⇒ BootWaitMs = 150 秒；CheckArgoDeps() 找出拖慢的原因
//   ③ 【一次性 token】dsh web 根路径不带 token 恒 401，
//      所以"探测根 URL 返回 200 就算就绪"永远不成立。
//      ⇒ FindLiveTokenUrl() / WaitForTokenUrl()（抓 token 后用带 token 的地址校验）
//
// 另修一个用户直接看得见的问题：
//   【PowerShell 窗口乱弹】原来每 4 秒的状态轮询都走 powershell.exe + Get-CimInstance(WMI)，
//   于是 PowerShell 窗口不停地闪，每次最坏还阻塞 8 秒。
//   ⇒ GetListenerPids() 改用 iphlpapi 的 GetExtendedTcpTable（纯 P/Invoke，零进程创建）；
//     轮询路径上不再有任何外部进程。
// ============================================================
namespace BigFatFishRescuer
{
    public static class DshBoot
    {
        /// <summary>冷启动等待上限（毫秒）。与 ~/.dsh/tools/open-dsh.mjs 的 150 秒一致。</summary>
        public const int BootWaitMs = 150000;

        /// <summary>救星默认端口（与可靠启动器 open-dsh.mjs 的起始端口一致）。</summary>
        public const int DefaultPort = 3080;

        /// <summary>端口扫描范围：找"正在跑的 dsh"时在这个区间里挑。</summary>
        public const int PortScanLo = 3080;
        public const int PortScanHi = 3179;

        public static readonly string ToolsDir = Path.Combine(DshCore.DshHome, "tools");
        public static readonly string ToolsOutLog = Path.Combine(ToolsDir, "dsh-web.out.log");
        public static readonly string ToolsErrLog = Path.Combine(ToolsDir, "dsh-web.err.log");
        public static readonly string LastUrlFile = Path.Combine(ToolsDir, "last-url.txt");

        private static readonly Regex UrlRe = new Regex(
            @"http://127\.0\.0\.1:(\d+)/\?token=([A-Za-z0-9_\-\.]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // ============================================================
        // 一、零进程创建地查"谁在监听某端口"
        // ============================================================

        private const int AF_INET = 2;
        private const int TCP_TABLE_OWNER_PID_ALL = 5;
        private const int MIB_TCP_STATE_LISTEN = 2;
        private const int TCP_ROW_SIZE = 24;   // MIB_TCPROW_OWNER_PID

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize,
            bool bOrder, int ulAf, int tableClass, uint reserved);

        /// <summary>
        /// 返回监听 127.0.0.1:<paramref name="port"/>（含 0.0.0.0）的 PID 列表。
        /// 纯 P/Invoke，**不创建任何进程** —— 这是修掉 PowerShell 弹窗的关键。
        /// </summary>
        public static int[] GetListenerPids(int port)
        {
            var result = new List<int>();
            int size = 0;
            try
            {
                GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
                if (size <= 4) return result.ToArray();
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0)
                        return result.ToArray();
                    int count = Marshal.ReadInt32(buf);
                    long baseAddr = buf.ToInt64() + 4;
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr p = new IntPtr(baseAddr + (long)i * TCP_ROW_SIZE);
                        int state = Marshal.ReadInt32(p, 0);
                        int localPortNet = Marshal.ReadInt32(p, 8);
                        int pid = Marshal.ReadInt32(p, 20);
                        // dwLocalPort 低 16 位是网络序端口
                        int lp = ((localPortNet & 0xFF) << 8) | ((localPortNet >> 8) & 0xFF);
                        if (state == MIB_TCP_STATE_LISTEN && lp == port && pid > 0 && !result.Contains(pid))
                            result.Add(pid);
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { }
            return result.ToArray();
        }

        /// <summary>在 [lo,hi] 区间里找出所有监听端口 → PID（同样零进程创建）。</summary>
        public static Dictionary<int, List<int>> ListListenersInRange(int lo, int hi)
        {
            var map = new Dictionary<int, List<int>>();
            int size = 0;
            try
            {
                GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
                if (size <= 4) return map;
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0)
                        return map;
                    int count = Marshal.ReadInt32(buf);
                    long baseAddr = buf.ToInt64() + 4;
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr p = new IntPtr(baseAddr + (long)i * TCP_ROW_SIZE);
                        int state = Marshal.ReadInt32(p, 0);
                        int localPortNet = Marshal.ReadInt32(p, 8);
                        int pid = Marshal.ReadInt32(p, 20);
                        int lp = ((localPortNet & 0xFF) << 8) | ((localPortNet >> 8) & 0xFF);
                        if (state != MIB_TCP_STATE_LISTEN || lp < lo || lp > hi || pid <= 0) continue;
                        if (!map.ContainsKey(lp)) map[lp] = new List<int>();
                        if (!map[lp].Contains(pid)) map[lp].Add(pid);
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { }
            return map;
        }

        /// <summary>托管方式取进程名（不创建进程、不弹窗）。</summary>
        public static bool IsNodeProcess(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                    return p.ProcessName.Equals("node", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static string ProcessNameOf(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid)) return p.ProcessName;
            }
            catch { return "?"; }
        }

        // ============================================================
        // 二、端口发现（不再硬编码 3081）
        // ============================================================

        public static int PortOfUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return 0;
            Match m = UrlRe.Match(url);
            if (m.Success) { int p; if (int.TryParse(m.Groups[1].Value, out p)) return p; }
            return 0;
        }

        // ============================================================
        // 二·〇、端口显式设置（★ 2026-09-16 新增：适配"别人跑在别的端口"）
        //   为什么需要它：dsh 自己默认 3080，但**可靠启动器是从 3080 起找第一个空闲端口**
        //   （3080 被残留 node / 别的程序占住就用 3081、3082…），桌面图标的 launcher.ini
        //   也可能被配成别的端口 ⇒ "有些人 3081"不是特例，是同一套机制的必然结果。
        //   所以：显式设置（环境变量 DSH_PORT → 文件 port.txt）排在发现顺序最前面，
        //   给用户一个"我说了算"的开关；其余情况一律靠**进程身份**认，不靠端口号。
        // ============================================================
        public static readonly string PortOverrideFile = Path.Combine(DshCore.AppDataDir, "port.txt");
        private static int _overridePort = -1;      // -1=还没读 0=未设置 >0=已设置
        private static string _overrideLabel = null;

        public static string PortOverrideLabel { get { return _overrideLabel; } }

        private static bool ValidPort(int p) { return p >= 1 && p <= 65535; }

        /// <summary>
        /// ★ 2026-09-18 新增：把端口**写进 port.txt**（给「一键智能启动」自动换端口用）。
        /// 为什么需要：端口被别的程序占着时，救星不去杀它、而是换一个空闲端口 ——
        /// 但"只换这一次"会让下次启动又回到被占的端口，用户会以为"时好时坏"。
        /// 写下来 ⇒ 下次也用这个端口，桌面图标的地址由调用方同步。
        /// </summary>
        public static bool SetPortOverride(int port, out string err)
        {
            err = null;
            try
            {
                if (port <= 0 || port > 65535) { err = "端口号不合法：" + port; return false; }
                System.IO.Directory.CreateDirectory(DshCore.AppDataDir);
                System.IO.File.WriteAllText(PortOverrideFile,
                    "# 大肥鱼救星：显式指定 dsh web 的端口（改完要重开服务）\r\n" + port + "\r\n",
                    new UTF8Encoding(false));
                ResetPortOverrideCache();
                return true;
            }
            catch (Exception e) { err = e.Message; return false; }
        }

        /// <summary>显式指定的端口（0 = 未指定）。依次读环境变量 DSH_PORT、port.txt。</summary>
        public static int ReadPortOverride()
        {
            if (_overridePort >= 0) return _overridePort;
            _overridePort = 0;
            _overrideLabel = null;
            try
            {
                string env = Environment.GetEnvironmentVariable("DSH_PORT");
                int ep;
                if (!string.IsNullOrEmpty(env) && int.TryParse(env.Trim(), out ep) && ValidPort(ep))
                { _overridePort = ep; _overrideLabel = "环境变量 DSH_PORT"; return _overridePort; }
            }
            catch { }
            try
            {
                if (File.Exists(PortOverrideFile))
                {
                    foreach (string raw in File.ReadAllLines(PortOverrideFile))
                    {
                        string line = (raw ?? "").Trim();
                        if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;   // 支持注释
                        Match m = Regex.Match(line, @"\d+");
                        int fp;
                        if (m.Success && int.TryParse(m.Value, out fp) && ValidPort(fp))
                        { _overridePort = fp; _overrideLabel = "port.txt"; return _overridePort; }
                    }
                }
            }
            catch { }
            return 0;
        }

        /// <summary>上次端口发现的结论来源（给人看的，会写进诊断报告）。</summary>
        public static string LastPortSource { get; private set; }

        /// <summary>只清"显式端口设置"的缓存（供 DshCore.ResetActivePort 调用，避免相互递归）。</summary>
        public static void ResetPortOverrideCache()
        {
            _overridePort = -1; _overrideLabel = null; LastPortSource = null;
        }

        /// <summary>端口设置/环境变了以后调一下：清缓存，下次读 ActivePort 重新发现。</summary>
        public static void ResetPortDiscovery()
        {
            ResetPortOverrideCache();
            DshCore.ResetActivePort();
        }

        /// <summary>
        /// 这个端口能不能算"我们的"：必须有人监听，且（落在默认区间 或 监听者身份= dsh）。
        /// 区间外**必须身份确认**，否则会把别人的服务当成 dsh（T14 的老毛病）。
        /// </summary>
        private static bool PortAliveAndOurs(int p, int[] dshPids)
        {
            if (!ValidPort(p)) return false;
            int[] ls = GetListenerPids(p);
            if (ls.Length == 0) return false;
            if (p >= PortScanLo && p <= PortScanHi) return true;
            if (dshPids != null)
                foreach (int pid in ls)
                    if (Array.IndexOf(dshPids, pid) >= 0) return true;
            return false;
        }

        /// <summary>
        /// 找出"现在真正在跑的 dsh"的端口。顺序：
        ///   0) 显式设置（DSH_PORT / port.txt）—— 用户说了算
        ///   1) 可靠启动器写的 last-url.txt
        ///   2) 各日志里最后一条 dsh web 地址
        ///   3) ★ 身份扫描：任何监听端口，只要监听者判定为 dsh 进程（与端口号无关）
        ///   4) 桌面图标 launcher.ini 里配的 --port（没在跑时"该起在哪"以用户环境为准）
        ///   5) 默认 3080
        /// 全程不创建进程（身份确认走已缓存的 GetDshPids）。
        /// </summary>
        public static int DiscoverLivePort()
        {
            // 0) 显式设置最优先
            int ov = ReadPortOverride();
            if (ov > 0) { LastPortSource = "显式设置（" + (_overrideLabel ?? "?") + "）"; return ov; }

            // dsh 进程清单（懒读一次）
            int[] dshPids = null; bool read = false;
            Func<int[]> ourPids = delegate
            {
                if (!read) { try { dshPids = DshCore.GetDshPids(); } catch { dshPids = null; } read = true; }
                return dshPids;
            };

            // 1) 可靠启动器的产物最可信（它刚启动过）
            int p = PortOfUrl(ReadUrlFromFile(LastUrlFile));
            if (p > 0 && PortAliveAndOurs(p, ourPids()))
            { LastPortSource = "last-url.txt（可靠启动器写入）"; return p; }

            // 2) 各日志里最后一条带 token 的地址
            foreach (Source src in Sources())
            {
                if (src.Kind != "log") continue;
                string u = LastUrlInText(ReadTextShared(src.Path));
                p = PortOfUrl(u);
                if (p > 0 && PortAliveAndOurs(p, ourPids()))
                { LastPortSource = "日志 " + src.Label; return p; }
            }

            // 3) ★ 身份扫描（与端口号无关）：先默认区间，再全端口
            //    ★ 2026-09-16 修复 T14（端口误认）：旧版**只看"是不是 node"就认定是 dsh** ——
            //      若 dsh 没在跑、而区间里恰好有别的 node 服务（开发服务器、另一个工具），
            //      救星会把**它**当成 dsh：ActivePort 指向它 ⇒ 「停止服务」的兜底会去杀它、
            //      「启动并打开」会在它的端口上起 dsh 并失败、整份诊断查的却是别人的端口。
            //      现在必须是 IsDshEntry 判定过的 dsh 进程。
            if (dshPids == null) ourPids();
            if (dshPids != null && dshPids.Length > 0)
            {
                Dictionary<int, List<int>> all = ListListenersInRange(1, 65535);
                var ports = new List<int>(all.Keys);
                ports.Sort();
                foreach (int cand in ports)                       // 3a) 默认区间优先（保持旧行为）
                {
                    if (cand < PortScanLo || cand > PortScanHi) continue;
                    foreach (int pid in all[cand])
                        if (IsNodeProcess(pid) && Array.IndexOf(dshPids, pid) >= 0)
                        { LastPortSource = "身份扫描（" + PortScanLo + "-" + PortScanHi + " 区间内的 dsh 进程）"; return cand; }
                }
                foreach (int cand in ports)                       // 3b) ★ 区间外也认（这一步才是"适配别人"）
                {
                    if (cand >= PortScanLo && cand <= PortScanHi) continue;
                    foreach (int pid in all[cand])
                        if (IsNodeProcess(pid) && Array.IndexOf(dshPids, pid) >= 0)
                        { LastPortSource = "身份扫描（★ 端口 " + cand + " 在默认区间之外，按进程身份认出）"; return cand; }
                }
            }

            // 4) 桌面图标配的端口：没在跑时，"该起在哪"以用户环境为准（免得图标探测 3081、救星却起在 3080）
            try
            {
                LauncherIniInfo ini = InspectLauncherIni();
                if (ini != null && ini.Exists && ValidPort(ini.ArgPort))
                { LastPortSource = "桌面图标 launcher.ini 的 --port"; return ini.ArgPort; }
            }
            catch { }

            // 5) 默认端口
            LastPortSource = "默认值（无显式设置、无实例、launcher.ini 也没写端口）";
            return DefaultPort;
        }

        // ============================================================
        // 三、token 地址发现与就绪等待
        // ============================================================

        private sealed class Source
        {
            public string Path;
            public string Label;
            public string Kind;   // "file" | "log"
        }

        private static List<Source> Sources()
        {
            var list = new List<Source>();
            list.Add(new Source { Path = LastUrlFile, Label = "last-url.txt（可靠启动器写入）", Kind = "file" });

            var logs = new List<string>();
            logs.Add(ToolsOutLog);                  // open-dsh.mjs 的输出
            logs.Add(DshCore.DshWebLog);            // 救星自己启动实例的输出
            try
            {
                foreach (string f in Directory.GetFiles(DshCore.AppDataDir, "*.log"))
                    if (!logs.Contains(f)) logs.Add(f);
            }
            catch { }
            // 最近写过的排前面
            logs.Sort(delegate(string a, string b)
            {
                DateTime ta = SafeMtime(a), tb = SafeMtime(b);
                return tb.CompareTo(ta);
            });
            foreach (string f in logs)
                list.Add(new Source { Path = f, Label = Path.GetFileName(f), Kind = "log" });
            return list;
        }

        private static DateTime SafeMtime(string f)
        {
            try { return File.Exists(f) ? File.GetLastWriteTime(f) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        /// <summary>共享读（dsh 正在写这个文件时也能读）。</summary>
        public static string ReadTextShared(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8, true))
                    return sr.ReadToEnd();
            }
            catch { return ""; }
        }

        public static string ReadUrlFromFile(string path)
        {
            string text = ReadTextShared(path).Trim();
            if (text.Length == 0) return null;
            Match m = UrlRe.Match(text);
            return m.Success ? m.Value : null;
        }

        /// <summary>取文本里**最后**一条带 token 的地址（最新的一次启动）。</summary>
        public static string LastUrlInText(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            MatchCollection ms = UrlRe.Matches(text);
            return ms.Count > 0 ? ms[ms.Count - 1].Value : null;
        }

        /// <summary>
        /// 找出一个**已验证可用**的带 token 地址。找不到返回 null。
        ///
        /// ★ 2026-09-16 修复（「登录要等 316 秒」的根因）：
        ///   旧版对每个来源只调 ReadUrlFromFile —— 那是正则**第一条**匹配。而
        ///   dsh-web.out.log 是**追加**写入的历史文件（实测 15 行 / 跨 7 次启动），
        ///   第一条是早已失效的 3081 老 token ⇒ 永远验证失败 ⇒ 误判「没有活着的实例」
        ///   ⇒ 走「拉起服务」分支 ⇒ 在**已被占用**的端口上另起实例 ⇒ 白等 150 秒 ×2。
        ///
        ///   现在两条改进：
        ///     ① **从最后一条往回试** —— 只有最新的 token 才可能有效；
        ///     ② **跳过死端口** —— 当前无进程监听的端口，其 token 必然失效，
        ///        直接跳过、不发 HTTP 请求（省掉每个 3.5 秒的等待）。
        /// </summary>
        public static string FindLiveTokenUrl(out string label)
        {
            label = null;
            foreach (Source s in Sources())
            {
                string text = ReadTextShared(s.Path);
                if (string.IsNullOrEmpty(text)) continue;
                MatchCollection ms = UrlRe.Matches(text);
                for (int i = ms.Count - 1; i >= 0; i--)
                {
                    string url = ms[i].Value;
                    int port = PortOfUrl(url);
                    if (port <= 0) continue;
                    if (GetListenerPids(port).Length == 0) continue;   // 死端口 → 该 token 必失效
                    try
                    {
                        if (DshCore.UrlAuthOk(url) && DshCore.IsDshPage(url))
                        {
                            label = s.Label;
                            return url;
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>
        /// ★ T20：找出"**当前活着的这个实例**"的日志文件到底是哪一个。
        ///
        /// 为什么需要：本工具自己认**多个**日志/地址来源（`Sources()`），
        /// 但「📄 打开日志」按钮**只开救星目录那一个** —— 若当前实例是"可靠启动器"起的，
        /// 用户打开的就是**另一个实例的日志**，查不到真正要查的故障（本机历史上确实有两条启动路径）。
        /// 判据与 FindLiveTokenUrl 一致：该文件里**最后一条 token 仍然有效**（状态码 OK 且确实是 DSH 页面）。
        /// </summary>
        public static bool TryFindCurrentInstanceLog(out string path, out string why)
        {
            path = null; why = "";
            foreach (Source s in Sources())
            {
                if (s.Kind != "log") continue;
                string text = ReadTextShared(s.Path);
                if (string.IsNullOrEmpty(text)) continue;
                MatchCollection ms = UrlRe.Matches(text);
                for (int i = ms.Count - 1; i >= 0; i--)
                {
                    string url = ms[i].Value;
                    int port = PortOfUrl(url);
                    if (port <= 0) continue;
                    if (GetListenerPids(port).Length == 0) continue;   // 死端口 → 该 token 必失效
                    try
                    {
                        if (DshCore.UrlAuthOk(url) && DshCore.IsDshPage(url))
                        {
                            path = s.Path;
                            why = "该文件里最后一条 token 仍然有效（" + s.Label + "，端口 " + port + "）";
                            return true;
                        }
                    }
                    catch { }
                }
            }
            why = "所有日志来源里都没有仍然有效的 token，无法判定当前实例写在哪个文件。";
            return false;
        }

        /// <summary>
        /// ★ T20：把工具认的全部日志/认证地址来源列出来。
        /// 目的是让"我打开的到底是谁的日志"这件事**可见**，而不是让用户猜。
        /// </summary>
        public static string DescribeSources()
        {
            var sb = new StringBuilder();
            sb.AppendLine("--- 本工具认的日志/认证地址来源（按最近写入排序）---");
            foreach (Source s in Sources())
            {
                string state;
                try
                {
                    if (!File.Exists(s.Path)) state = "不存在";
                    else
                    {
                        var fi = new FileInfo(s.Path);
                        string u = LastUrlInText(ReadTextShared(s.Path));
                        state = fi.Length + " 字节，最后写入 " + fi.LastWriteTime.ToString("MM-dd HH:mm:ss");
                        if (!string.IsNullOrEmpty(u)) state += "，末条 token 端口 " + PortOfUrl(u);
                    }
                }
                catch (Exception e) { state = "读取失败：" + e.Message; }
                sb.AppendLine("  · " + s.Label + "\r\n      " + s.Path + "\r\n      " + state);
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 等到一个可用的带 token 地址。maxMs 默认 150 秒（冷启动 40~60 秒是常态）。
        /// progress 回调用于把"已等待 N 秒"写到界面日志。
        /// </summary>
        public static string WaitForTokenUrl(int maxMs, Action<string> progress, out string label)
        {
            label = null;
            var sw = Stopwatch.StartNew();
            int reported = -1;
            while (sw.ElapsedMilliseconds < maxMs)
            {
                string src;
                string url = FindLiveTokenUrl(out src);
                if (!string.IsNullOrEmpty(url)) { label = src; return url; }

                int sec = (int)(sw.ElapsedMilliseconds / 1000);
                if (progress != null && sec / 10 != reported)
                {
                    reported = sec / 10;
                    progress("已等待 " + sec + " 秒 / 上限 " + (maxMs / 1000) + " 秒（冷启动 40~60 秒属正常，请稍候）");
                }
                Thread.Sleep(500);
            }
            return null;
        }

        // ============================================================
        // 四、launcher.ini 端口一致性检查 / 修复（文档根因①）
        // ============================================================

        public sealed class LauncherIniInfo
        {
            public bool Exists;
            public int UrlPort;
            public int ArgPort;
            public string Url = "";
            public string Arguments = "";
            public string Error = "";
            public bool Mismatch { get { return Exists && UrlPort > 0 && ArgPort > 0 && UrlPort != ArgPort; } }
        }

        public static LauncherIniInfo InspectLauncherIni()
        {
            var info = new LauncherIniInfo();
            try
            {
                if (!File.Exists(DshCore.LauncherIni)) return info;
                info.Exists = true;
                foreach (string raw in File.ReadAllLines(DshCore.LauncherIni))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("Url=", StringComparison.Ordinal))
                    {
                        info.Url = line.Substring(4).Trim();
                        info.UrlPort = PortOfAnyUrl(info.Url);
                    }
                    else if (line.StartsWith("ArgumentsBase64=", StringComparison.Ordinal))
                    {
                        info.Arguments = DshCore.FromB64(line.Substring("ArgumentsBase64=".Length)).Trim();
                        info.ArgPort = PortFromArguments(info.Arguments);
                    }
                }
            }
            catch (Exception e) { info.Error = e.Message; }
            return info;
        }

        /// <summary>从形如 "…bin.js web --no-open --port 3081" 的参数里取端口。</summary>
        public static int PortFromArguments(string args)
        {
            if (string.IsNullOrEmpty(args)) return 0;
            int m = args.IndexOf("--port", StringComparison.OrdinalIgnoreCase);
            if (m < 0) return 0;
            string rest = args.Substring(m + 6).TrimStart();
            int end = 0;
            while (end < rest.Length && rest[end] >= '0' && rest[end] <= '9') end++;
            if (end == 0) return 0;
            int p;
            return int.TryParse(rest.Substring(0, end), NumberStyles.Integer, CultureInfo.InvariantCulture, out p) ? p : 0;
        }

        /// <summary>Url= 里的端口（格式可能是 http://127.0.0.1:3080/ 而不是带 token 的地址）。</summary>
        public static int PortOfAnyUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return 0;
            Match m = Regex.Match(url, @":(\d{2,5})");
            if (m.Success) { int p; if (int.TryParse(m.Groups[1].Value, out p)) return p; }
            return 0;
        }

        /// <summary>
        /// 修 launcher.ini 的端口漂移：以 Arguments 的 --port 为准改 Url 的端口。
        /// 为什么会漂：install.js 生成这个 ini 时，Url 取的是硬编码常量，
        /// 而 Arguments 是当时 dsh 进程 argv 的快照 —— 两者来源不同，必然漂。
        /// 写前备份，保留原编码的 BOM 状态。
        /// </summary>
        public static string RepairLauncherIni()
        {
            LauncherIniInfo info = InspectLauncherIni();
            if (!info.Exists) return "launcher.ini 不存在，无需修：" + DshCore.LauncherIni;
            if (info.Error.Length > 0) return "launcher.ini 读取失败：" + info.Error;
            if (info.ArgPort <= 0) return "launcher.ini 里没找到 --port，无法判断应以哪个端口为准（Url 端口 " + info.UrlPort + "）。";
            if (!info.Mismatch) return "launcher.ini 端口一致（都是 " + info.ArgPort + "），无需修。";

            try
            {
                byte[] raw = File.ReadAllBytes(DshCore.LauncherIni);
                bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
                string text = File.ReadAllText(DshCore.LauncherIni);
                string backup = DshCore.LauncherIni + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.WriteAllBytes(backup, raw);

                string updated = Regex.Replace(text,
                    @"(?m)^Url=http://127\.0\.0\.1:\d+/",
                    "Url=http://127.0.0.1:" + info.ArgPort.ToString(CultureInfo.InvariantCulture) + "/");
                if (updated == text)
                {
                    // 兜底：按行替换
                    var lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
                    for (int i = 0; i < lines.Count; i++)
                        if (lines[i].TrimStart().StartsWith("Url=", StringComparison.Ordinal))
                            lines[i] = "Url=http://127.0.0.1:" + info.ArgPort.ToString(CultureInfo.InvariantCulture) + "/";
                    updated = string.Join("\r\n", lines.ToArray());
                }

                // ★ 2026-09-16 修复 T16：旧版这里用 `File.WriteAllText` —— 正是
                //   `SafeConfig.AtomicWriteText` 注释里**点名禁止**的做法（open-truncate：
                //   先把长度截成 0 再写，途中被杀 / 磁盘满就会留下 **0 字节或半截文件**）。
                //   它虽然先备了 `.bak-<时间戳>`，但那是"手工恢复"级别的退路，**不是原子性**。
                //   而 launcher.ini 坏了 = 桌面图标彻底失效（本轮诊断里正是重点修的对象）。
                //   现在改为原子替换，并**保留原来的 BOM 状态**（BOM 会影响某些读取方）。
                SafeConfig.AtomicWriteText(DshCore.LauncherIni, updated, bom);
                return "已修 launcher.ini 的端口漂移：Url 的端口 " + info.UrlPort + " → " + info.ArgPort +
                       "（备份 " + Path.GetFileName(backup) + "）";
            }
            catch (Exception e)
            {
                return "launcher.ini 修复失败：" + e.Message;
            }
        }

        // ============================================================
        // 五、冷启动变慢的元凶体检（文档根因②）
        // ============================================================

        /// <summary>
        /// argo-search 走 `python` 跑 argo.js；系统 python 缺 pyyaml 时会反复打印
        /// "PyYAML 未安装" 并退回内置默认配置 —— 这正是冷启动要 40~60 秒的主因之一。
        /// </summary>
        public static string CheckArgoDeps()
        {
            var sb = new StringBuilder();
            // 2026-09-16 修正：原来用 where.exe 找 python，但它按 OEM 代码页输出，
            // 中文用户名（含中文用户名时）会被 UTF-8 解码成乱码、路径直接失效；且 `cmd /c ""..."` 套三层
            // 引号会静默失败，把"已装"误报成"缺"。改为原生查路径 + 直接调 python。
            // 见 PatchGuard.SysPython / PyYamlVersion。
            string py = PatchGuard.SysPython();

            if (py == null) { sb.AppendLine("找不到 python（argo-search 的检索后端需要它）。"); return sb.ToString(); }

            string ver = PatchGuard.PyYamlVersion();
            if (ver != null)
                sb.AppendLine("[OK]   pyyaml 已安装（" + ver + "，解释器 " + py + "）");
            else
                sb.AppendLine("[FAIL] 系统 python 缺 pyyaml（" + py + "）→ 每次冷启动都会反复退回默认配置，明显变慢。" +
                              "\r\n       修复：\"" + py + "\" -m pip install pyyaml（或点「🧩 补丁体检」一键装）");

            int warnings = CountInLogs("PyYAML 未安装", 3);
            if (warnings > 0)
                sb.AppendLine("[WARN] 最近的启动日志里出现 " + warnings + " 次「PyYAML 未安装」" +
                              (warnings >= 3 ? "（≥3 次即说明冷启动在被反复拖慢）" : ""));
            return sb.ToString();
        }

        /// <summary>在最近的日志文件里数关键字出现次数（最多看 maxFiles 个文件）。</summary>
        public static int CountInLogs(string keyword, int maxFiles)
        {
            int total = 0, seen = 0;
            foreach (Source s in Sources())
            {
                if (s.Kind != "log") continue;
                if (seen++ >= maxFiles) break;
                string text = ReadTextShared(s.Path);
                if (text.Length == 0) continue;
                int idx = 0;
                while ((idx = text.IndexOf(keyword, idx, StringComparison.Ordinal)) >= 0) { total++; idx += keyword.Length; }
            }
            return total;
        }

        // ============================================================
        // 六、给"诊断"和"一键自检"用的条目
        // ============================================================

        public static DiagItem[] DiagItems()
        {
            var items = new List<DiagItem>();

            // 端口
            int live = DiscoverLivePort();
            int listeners = GetListenerPids(live).Length;
            items.Add(new DiagItem
            {
                Ok = listeners > 0,
                Text = "启动端口：当前判定 dsh 在 " + live + "（监听者 " + (listeners > 0 ? listeners.ToString() : "无") +
                       "）" + (live != DshBoot.DefaultPort ? "；注意：与默认端口 " + DshBoot.DefaultPort + " 不同，说明端口发生过漂移。\r\n" : "\r\n")
            });

            // launcher.ini
            LauncherIniInfo ini = InspectLauncherIni();
            if (!ini.Exists)
                items.Add(new DiagItem { Ok = true, Text = "桌面图标配置 launcher.ini 不存在（未装 whale-desktop-launcher）\r\n" });
            else if (ini.Mismatch)
                items.Add(new DiagItem
                {
                    Ok = false,
                    Text = "桌面图标配置端口打架：Url 探测 " + ini.UrlPort + "，但启动参数是 --port " + ini.ArgPort +
                           "\r\n       → 图标会「探测 A、服务起在 B」，永远等不到就绪，且每点一次留一个孤儿进程。" +
                           "\r\n       点「一键修复」可自动改 Url 的端口。\r\n"
                });
            else
                items.Add(new DiagItem { Ok = true, Text = "桌面图标配置端口一致（" + ini.ArgPort + "）\r\n" });

            // token 可达性
            string label;
            string url = FindLiveTokenUrl(out label);
            if (url != null)
            {
                int p = PortOfUrl(url);
                items.Add(new DiagItem
                {
                    Ok = true,
                    Text = "认证地址可用（来源 " + label + "，端口 " + p + "）→ 双击图标打不开时可直接用它。\r\n"
                });
            }
            else
            {
                items.Add(new DiagItem
                {
                    Ok = false,
                    Text = "找不到可用的认证地址。注意：dsh web 用一次性 token，不带 token 访问根路径恒 401，" +
                           "所以「根 URL 返回 200」这个判据永远不成立——必须抓 token。\r\n"
                });
            }

            // 冷启动元凶
            // ★ 2026-09-16 修复 P6：`CheckArgoDeps()` 的文本**自带** `[OK]`/`[FAIL]` 标记，
            //   而 DiagItem 的消费方（DshCore.DiagnosticsText / Program）会**再加一次**前缀 ⇒
            //   实测打印出 `[OK]   [OK]   pyyaml 已安装…`（两层前缀，看着像坏了）。
            //   这里把内层标记去掉，只保留一层。
            string argoRaw = CheckArgoDeps().Trim();
            if (argoRaw.Length > 0)
            {
                items.Add(new DiagItem
                {
                    // Ok 判据用**带标记的原文**取，Text 用去掉内层标记的版本（否则前缀会印两遍）
                    Ok = argoRaw.IndexOf("[FAIL]") < 0,
                    Text = StripLeadMarker(argoRaw) + "\r\n"
                });
            }

            return items.ToArray();
        }

        /// <summary>
        /// 去掉文本开头的 `[OK]` / `[FAIL]` / `[WARN]` 标记（P6）。
        /// 用于把"自带标记的文本"塞进 DiagItem —— DiagItem 的消费方会再加一层前缀。
        /// </summary>
        private static string StripLeadMarker(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string t = text.TrimStart();
            string[] marks = { "[OK]", "[FAIL]", "[WARN]", "[i]" };
            foreach (string mk in marks)
                if (t.StartsWith(mk, StringComparison.OrdinalIgnoreCase))
                    return t.Substring(mk.Length).TrimStart();
            return t;
        }

        /// <summary>「🚦 启动链路自检」按钮用的一页纸报告。</summary>
        public static string StartupChainCheck()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== 启动链路自检（对照《dsh打不开_故障排查与修复_20260916.md》）====");
            sb.AppendLine();

            // ① 端口
            int live = DiscoverLivePort();
            sb.AppendLine("【① 端口】");
            sb.AppendLine("  现在真正在跑的 dsh 端口：" + live);
            sb.AppendLine("  该端口监听者 PID：" + Join(GetListenerPids(live)));
            sb.AppendLine("  救星当前使用的端口：" + DshCore.ActivePort +
                          (DshCore.ActivePort == live ? "（一致 ✔）" : "（★与实跑端口不一致，已按实跑端口纠正）"));
            Dictionary<int, List<int>> all = ListListenersInRange(PortScanLo, PortScanHi);
            if (all.Count > 0)
            {
                var ports = new List<int>(all.Keys); ports.Sort();
                var parts = new List<string>();
                foreach (int p in ports) parts.Add(p + "(pid " + Join(all[p].ToArray()) + ")");
                sb.AppendLine("  " + PortScanLo + "-" + PortScanHi + " 区间内监听中的端口：" + string.Join("、", parts.ToArray()));
            }
            sb.AppendLine();

            // ② launcher.ini
            sb.AppendLine("【② 桌面图标配置 launcher.ini】");
            LauncherIniInfo ini = InspectLauncherIni();
            if (!ini.Exists) sb.AppendLine("  不存在：" + DshCore.LauncherIni);
            else
            {
                sb.AppendLine("  Url（用来探测就绪）    ：" + ini.Url + "   → 端口 " + ini.UrlPort);
                sb.AppendLine("  Arguments（真正启动）  ：" + ini.Arguments);
                sb.AppendLine("  启动参数里的 --port    ：" + (ini.ArgPort > 0 ? ini.ArgPort.ToString() : "（没有）"));
                sb.AppendLine(ini.Mismatch
                    ? "  ★ 端口打架：探测 " + ini.UrlPort + " 而服务起在 " + ini.ArgPort + " → 图标永远等不到就绪。点「一键修复」可自动改。"
                    : "  端口一致 ✔");
            }
            sb.AppendLine();

            // ③ 一次性 token
            sb.AppendLine("【③ 认证（一次性 token）】");
            string label;
            string url = FindLiveTokenUrl(out label);
            if (url != null)
            {
                sb.AppendLine("  找到可用认证地址（来源：" + label + "）");
                sb.AppendLine("  " + url);
                sb.AppendLine("  （已确认是 DSH 页面：返回 200 且含 id=\"root\"）");
            }
            else
                sb.AppendLine("  暂时没有可用认证地址。不带 token 访问根路径恒 401 —— 这就是「图标探测永远失败」的结构性原因。");
            sb.AppendLine();

            // ④ 冷启动
            sb.AppendLine("【④ 冷启动耗时与元凶】");
            sb.AppendLine("  等待上限已设为 " + (BootWaitMs / 1000) + " 秒（旧版只等 30 秒 → 冷启动 40~60 秒时必然误报失败）");
            sb.Append(CheckArgoDeps());
            sb.AppendLine();

            // ⑤ 结论
            sb.AppendLine("【结论】");
            sb.AppendLine("  · 桌面图标 DeepSeek Harness.exe 用裸请求探测，拿不到 token ⇒ 结构上无法就绪；");
            sb.AppendLine("    请改用桌面上的「DeepSeek Harness（打开）.cmd」，或直接点救星的「🚀 启动并打开 / 🌐 打开无痕」。");
            sb.AppendLine("  · dsh 在写文件时被强杀会留半截配置 —— 救星已在每次强杀前后自动快照 + 体检回滚。");
            return sb.ToString();
        }

        private static string Join(int[] pids)
        {
            if (pids == null || pids.Length == 0) return "无";
            var parts = new string[pids.Length];
            for (int i = 0; i < pids.Length; i++) parts[i] = pids[i].ToString(CultureInfo.InvariantCulture);
            return string.Join(",", parts);
        }
    }
}

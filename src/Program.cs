using System;
using System.Threading;
using System.Windows.Forms;

// ============================================================
// 大肥鱼救星 - 入口
// 用法:
//   大肥鱼救星.exe            打开桌面 GUI
//   大肥鱼救星.exe --selftest  CLI 自检（无窗口，打印结果）
// ============================================================
namespace BigFatFishRescuer
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // ★★ v5（WP5）修：把 CLI 输出编码**统一钉成 UTF-8**。
            //   原来各分支混用：一部分走 `new StreamWriter(Console.OpenStandardOutput(), UTF8)`
            //   （UTF-8），另一部分直接 `Console.Out.WriteLine(res)` —— 后者在中文 Windows 上
            //   用控制台代码页（cp936/GBK）编码。后果：`--classify` 的输出一旦被重定向/管道
            //   捕获就是 GBK，远程排障与跨平台比对时全是乱码，而这**恰好是 WP5 要解决的场景**。
            //   WP3 实测踩到：脚本按 UTF-8 读 `--classify` 的输出，读成乱码后
            //   误判成“没匹配到任何已知故障形态”——工具的结论被编码吃掉。
            //   ★ WP5 复核补记（第一版只设 Console.OutputEncoding 没生效）：
            //   .NET Framework 里 `Console.OutputEncoding = ...` 在「stdout 已被重定向成管道」
            //   或「进程没有控制台句柄」时不保证重建 Console.Out，实测 `--classify` 仍是 GBK 字节。
            //   因此这里**直接换掉 Console.Out 本身**（Console.SetOut 对重定向场景必然生效），
            //   这样后面所有 `Console.Out.Write/WriteLine` 分支（--classify/--corpus-check/...）
            //   就都是 UTF-8 了，跟已经手写 StreamWriter 的分支保持一致。
            //   同时保留 OutputEncoding 赋值（对真控制台场景让 chcp 也跟上）。
            //   ★★ v5（WP5 复核第二轮）修：上面只换了 **stdout**，stderr 漏了。
            //   实测（WP5 量测）：`--headless --classify-only` 的 stdout 是 UTF-8，
            //   但 stderr 那行 `REPORT_FILE=C:\Users\<中文用户名>\...` 是 **GBK 字节**
            //   （用户名那几个汉字按 GBK 编码，按 UTF-8 解码直接抛异常）。
            //   后果：`2>&1` 合并后 / 在 Linux 上读 stderr 全是乱码 —— 而 WP5 的验收
            //   就是「输出一致结论、可逐字比对」，stderr 不一致会直接毁掉这条。
            //   对称地换掉 Console.Error（对重定向场景必然生效）。
            try
            {
                var _u8 = new System.Text.UTF8Encoding(false);
                var _so = new System.IO.StreamWriter(Console.OpenStandardOutput(), _u8, 4096);
                _so.AutoFlush = true;
                Console.SetOut(_so);
                var _se = new System.IO.StreamWriter(Console.OpenStandardError(), _u8, 4096);
                _se.AutoFlush = true;
                Console.SetError(_se);
                Console.OutputEncoding = _u8;
            }
            catch { }

            // ★ WP2②：兼容旧启动器的**显式开关**（默认关）。
            //   命中 --legacy-launcher-ini 才打开 launcher.ini 的 base64 解析 ——
            //   把「解码 base64 → 立刻起进程」这条加载器形状从主路径降级为冷门分支。
            //   （等价写法：环境变量 BFF_LEGACY_LAUNCHER_INI=1，见 DshCore.ResolveLegacyLauncherIni。）
            if (args != null)
            {
                foreach (string _a in args)
                {
                    if (string.Equals(_a, "--legacy-launcher-ini", StringComparison.OrdinalIgnoreCase))
                        DshCore.LegacyLauncherIni = true;
                }
            }

            bool selftest = args != null && args.Length > 0
                && (args[0] == "--selftest" || args[0] == "-t" || args[0] == "/t");

            if (selftest) return RunSelfTest();

            // 无窗口静默自愈模式（供开机自启 / 计划任务）
            bool autorecover = args != null && args.Length > 0
                && (args[0] == "--autorecover" || args[0] == "-r" || args[0] == "/r");
            if (autorecover) return RunAutoRecover();

            // 导出脱敏诊断包（供上游排查）: --diagnose | -d | /d
            bool diagnose = args != null && args.Length > 0
                && (args[0] == "--diagnose" || args[0] == "-d" || args[0] == "/d");
            if (diagnose) return RunDiagnose();

            // 打开无痕/打开 DSH：--open | -o | /o
            bool open = args != null && args.Length > 0
                && (args[0] == "--open" || args[0] == "-o" || args[0] == "/o");
            if (open)
            {
                DshCore.EnsureAppDataDir();
                string result = DshCore.OpenUi();
                Console.Out.WriteLine(result);
                return 0;
            }

            // 配置安全原语的功能自测（在临时目录里跑，不动任何真实配置）
            bool configtest = args != null && args.Length > 0 && args[0] == "--configtest";
            if (configtest) return RunConfigTest();

            // 环境与弹窗体检（只读；对应界面上的「🧩 补丁体检」「🖥 默认终端」「🚦 启动链路自检」）
            bool envcheck = args != null && args.Length > 0 && args[0] == "--envcheck";
            if (envcheck) return RunEnvCheck();

            // 装后体检（v4.5 · A2/A3）：日志来源全景 + 进程退出判据（只读，零外部进程）
            bool plugincheck = args != null && args.Length > 0 && args[0] == "--plugincheck";
            if (plugincheck) return RunPluginCheck();

            // ★ 2026-09-18 新增：`--smart [--dry]` —— 一键智能启动并打开（--dry 只说要做什么，不做）
            if (args != null && args.Length > 0 && args[0] == "--smart")
            {
                bool dry = false;
                foreach (string a2 in args) if (a2 == "--dry") dry = true;
                Console.WriteLine("== 大肥鱼救星 v" + DshCore.AppVersion + " · 智能启动 ==");
                Console.WriteLine(DshCore.SmartStartAndOpen(dry));
                return 0;
            }

            // ★ 2026-09-18 新增：安全总闸与预演
            //   `--readonly on|off|status` —— 开/关"只诊断"（开了就绝不碰 dsh 进程）
            //   `--plan-kill`             —— 预演：列出"现在动手会结束哪些进程"，**一个都不杀**
            if (args != null && args.Length > 0 && args[0] == "--readonly")
            {
                string mode = args.Length >= 2 ? args[1].Trim().ToLowerInvariant() : "status";
                if (mode == "on" || mode == "1" || mode == "true") DshCore.ReadOnlyMode = true;
                else if (mode == "off" || mode == "0" || mode == "false") DshCore.ReadOnlyMode = false;
                Console.WriteLine("== 大肥鱼救星 v" + DshCore.AppVersion + " · 只诊断模式 ==");
                Console.WriteLine("当前：" + (DshCore.ReadOnlyMode ? "已开（绝不结束/启动 dsh 进程）" : "已关（可动手，但每步仍会弹确认框）"));
                Console.WriteLine("开关文件：" + System.IO.Path.Combine(DshCore.AppDataDir, "readonly.txt"));
                return 0;
            }
            if (args != null && args.Length > 0 && args[0] == "--plan-kill")
            {
                Console.WriteLine(DshCore.PlanKill());
                return 0;
            }

            // ★ WP2④（2026-09-20 防误报加固）：`--killtree-plan <PID>` ——
            //   对**指定 PID 那一棵进程树**做预演：走的是和真杀完全相同的
            //   「按 ParentProcessId 逐层收窄 + 杀前重新核对命令行」逻辑，
            //   只是最后一步不动手，把「会动谁 / 不动谁」如实打出来。
            //   用途：① 让人在动手前先看清楚范围；② 让"不拼 taskkill 命令行、
            //   只按 PID 杀"这条有可复核的输出（而不是只能翻源码）。
            if (args != null && args.Length >= 2 && args[0] == "--killtree-plan")
            {
                int _pid = 0;
                if (!int.TryParse(args[1], System.Globalization.NumberStyles.Integer,
                                  System.Globalization.CultureInfo.InvariantCulture, out _pid))
                {
                    Console.WriteLine("用法：大肥鱼救星.exe --killtree-plan <PID>   （PID 必须是整数）");
                    return 2;
                }
                var _note = new System.Text.StringBuilder();
                _note.AppendLine("==== 进程树预演（PID " + _pid + "）· 本次一个都不会杀 ====");
                int _n = DshCore.KillTreeManaged(_pid, _note, true);
                _note.AppendLine();
                _note.AppendLine("预演结果：真杀的话会结束 " + _n + " 个。");
                Console.WriteLine(_note.ToString());
                return 0;
            }

            // ★ WP1 新增：`--conflict`（只读冲突雷达）与 `--conflict-selftest`（判据自检）。
            //   放在单实例互斥体**之前**：GUI 正开着也要能跑（与 --selftest / --plan-kill 同口径）。
            if (args != null && args.Length > 0 && args[0] == "--conflict")
            {
                ConflictRadar.ScanResult sc = ConflictRadar.Scan();
                System.Collections.Generic.List<ConflictRadar.Conflict> cf = ConflictRadar.Judge(sc.All);
                Console.WriteLine(ConflictRadar.Render(sc.All, cf, sc));
                // ★ 退出码按**硬红**走，不按"提醒/unknown"走 —— 报告里写着「提醒不算冲突」，
                //   退出码就得和这句话一致（改判后本机硬红=0、提醒 44，若按总条数会永远 exit 1）。
                return ConflictRadar.CountHard(cf) > 0 ? 1 : 0;
            }
            // ★ WP3 抽样用：`--conflict-rows` 同一把尺子、同一份扫描，只多吐 CR_* 机器行
            //   （纯 ASCII token，供 PowerShell 切分；红项计数与 --conflict 完全一致）。
            if (args != null && args.Length > 0 && args[0] == "--conflict-rows")
            {
                ConflictRadar.ScanResult scr = ConflictRadar.Scan();
                System.Collections.Generic.List<ConflictRadar.Conflict> cfr = ConflictRadar.Judge(scr.All);
                Console.WriteLine(ConflictRadar.MachineRows(scr.All, cfr, scr));
                Console.WriteLine(ConflictRadar.Render(scr.All, cfr, scr));
                return ConflictRadar.CountHard(cfr) > 0 ? 1 : 0;
            }
            if (args != null && args.Length > 0 && args[0] == "--conflict-plan")
            {
                ConflictRadar.ScanResult sc2 = ConflictRadar.Scan();
                System.Collections.Generic.List<ConflictRadar.Conflict> cf2 = ConflictRadar.Judge(sc2.All);
                Console.WriteLine(ConflictPlan.Render(cf2, sc2.All));
                return 0;   // 预演：没有红项失败可言，成败由 --conflict 判
            }
            // ★ WP5：`--conflict-tiers` 只列档位成员（纯静态、不起进程），
            //   所以它恒返回 0 —— "哪档能跑"要由 wp5_tiers.ps1 的真内核实测给。
            if (args != null && args.Length > 0 && args[0] == "--conflict-tiers")
            {
                Console.WriteLine(ConflictTiers.Render());
                return 0;
            }
            if (args != null && args.Length > 0 && args[0] == "--conflict-selftest")
            {
                string rep = ConflictRadar.SelfTest() + ConflictRadarHosts.SelfTest2() + ConflictRadarStatic.SelfTest3();
                Console.WriteLine(rep);
                return rep.IndexOf("[FAIL]", StringComparison.Ordinal) >= 0 ? 1 : 0;
            }
            // ★ WP4 第一条写动作：`--conflict-disable <id>`（禁用冲突条目）
            //   纪律：① 不带 id ⇒ 只列候选、一个字节不写；② id 没参与硬红 ⇒ 拒绝执行、一个字节不写；
            //         ③ 动手前打印「会动的 / 不动的」；④ 动完重扫雷达真复验；
            //         ⑤ 复验不过**不自动回滚**（文件可能合法只是没热生效）⇒ 报告 + 快照路径 + 回滚建议。
            //   退出码：0=成功且复验通过 / 1=拒绝或未改动 / 2=复验未通过（已改但雷达仍红）。
            if (args != null && args.Length > 0 && args[0] == "--conflict-disable")
            {
                if (args.Length < 2)
                {
                    Console.WriteLine(ConflictFix.ListCandidates());
                    return 0;
                }
                int _cdisExit = 0;
                string _cdisRep = ConflictFix.Disable(args[1], out _cdisExit);
                Console.WriteLine(_cdisRep);
                return _cdisExit;
            }

            // ★ 2026-09-18 新增：`--start` —— 命令行启动 dsh web 并**等它真的能响应**（无窗口，便于验证/排障）
            if (args != null && args.Length > 0 && args[0] == "--start")
            {
                DshCore.EnsureAppDataDir();
                bool ok = false;
                try { ok = DshCore.StartDsh(); } catch (Exception e) { Console.WriteLine("启动异常：" + e.Message); }
                Console.WriteLine("== 大肥鱼救星 v" + DshCore.AppVersion + " · 启动服务 ==");
                Console.WriteLine("端口：" + DshCore.ActivePort);
                // ★ WP2 判据可观测性（2026-09-20）：把"这次实际挑中的入口"和"走的哪条启动路"
                //   打到命令行输出里 —— 否则只有起完进程回读命令行的办法，无法在程序自述里自证。
                Console.WriteLine("入口来源：" + DshCore.LastLaunchSource);
                if (DshCore.LastLaunchBinJs.Length > 0)
                    Console.WriteLine("入口 bin.js：" + DshCore.LastLaunchBinJs);
                if (DshCore.LastStartNote.Length > 0)
                    Console.WriteLine("启动方式：" + DshCore.LastStartNote);
                if (ok)
                    Console.WriteLine("结论：OK（端口已有 HTTP 响应"
                                      + (DshCore.LastStartMs > 0 ? "，从启动到响应用了 " + DshCore.LastStartMs + " ms" : "（复用了已在跑的实例）") + "）");
                else
                    Console.WriteLine("结论：★ 没起来\r\n" + DshCore.LastStartError);
                return ok ? 0 : 1;
            }

            // C1 移除悬空引用（v4.5，可逆：先快照 → 只改清单 → 写后校验 → 复验）
            if (args != null && args.Length >= 2 && args[0] == "--fixdangling")
            {
                DshCore.EnsureAppDataDir();
                string res;
                try { res = PluginDiag.RemoveDanglingReference(args[1]); }
                catch (Exception e) { res = "执行异常：" + e.GetType().Name + ": " + e.Message; }
                Console.Out.WriteLine(res);
                return 0;
            }

            // ★★ 2026-09-18 新增：`--classify <日志文件>` —— 把**任意一份 DSH 日志**分类，
            //   给"帮别人看日志"用：对方把日志发来，主人跑一条命令就能拿到"这是哪类故障、该点哪个按钮"。
            //   判据完全按**报错文本**走（不认插件名单）⇒ 对市面上任何插件都成立。
            if (args != null && args.Length >= 2 && args[0] == "--classify")
            {
                var outp = new System.Text.StringBuilder();
                try
                {
                    var finds = PluginDiag.ClassifyFiles(new string[] { args[1] });
                    outp.AppendLine("== 大肥鱼救星 v" + DshCore.AppVersion + " · 日志分类 ==");
                    outp.AppendLine("日志：" + args[1]);
                    outp.AppendLine();
                    if (finds.Count == 0)
                        outp.AppendLine("没有匹配到任何已知故障形态（这份日志里可能没有插件树失败行）。");
                    else
                    {
                        var seen = new System.Collections.Generic.List<string>();
                        foreach (PluginDiag.Finding fd in finds)
                        {
                            if (seen.Contains(fd.Category)) continue;
                            seen.Add(fd.Category);
                            outp.AppendLine("【" + fd.Category + "】");
                            if (!string.IsNullOrEmpty(fd.Culprit)) outp.AppendLine("  涉及：" + fd.Culprit);
                            if (!string.IsNullOrEmpty(fd.Evidence)) outp.AppendLine("  证据：" + fd.Evidence.Trim());
                            if (!string.IsNullOrEmpty(fd.SourcePath)) outp.AppendLine("  来源：" + fd.SourcePath);
                            outp.AppendLine("  建议：" + PluginDiag.AdviseOf(fd.Category));
                            outp.AppendLine();
                        }
                        outp.AppendLine("（命中 " + finds.Count + " 行、" + seen.Count + " 类）");
                    }
                }
                catch (Exception e) { outp.AppendLine("分类失败：" + e.GetType().Name + ": " + e.Message); }
                // ★ 同时落一份 UTF-8（带 BOM）报告：面向 Windows 用户，重定向 stdout 容易变乱码，
                //   而且主人常常要把结论**直接转给别人**，给个文件路径最省事。
                try
                {
                    DshCore.EnsureAppDataDir();
                    string rp = System.IO.Path.Combine(DshCore.AppDataDir, "classify-report.txt");
                    System.IO.File.WriteAllText(rp, outp.ToString(), new System.Text.UTF8Encoding(true));
                    Console.Error.WriteLine("REPORT_FILE=" + rp);
                }
                catch { }
                Console.Out.Write(outp.ToString());
                return 0;
            }

            // ★★ v5 新增（WP5）：`--headless [--log <日志>] [--out <文件>] [--classify-only]`
            //   给"不会装 Windows 程序的人"远程排障用：只读、无窗口、一份可转发报告、退出码可判。
            //   ★ 硬承诺：本次运行**不启动也不结束任何 dsh 进程**，**不修改任何配置**；
            //     只往自己的 AppDataDir 写报告（用的是 SafeConfig 之外的直接写，见下）。
            //   ★ `--classify-only` 是**纯函数模式**：结论只由「输入日志 + 代码」决定，
            //     不含任何本机状态（路径已归一化、时间戳账号都不进正文），
            //     所以同一份日志在任何平台应得到**逐字一致**的结论；报告里给出
            //     CLASSIFY_FINGERPRINT=sha256:…，供跨机比对（WP5 验收项）。
            //   ★ 放在单实例互斥体之前：GUI 正开着也能跑（与 --selftest/--corpus-check 一致）。
            if (args != null && args.Length > 0 && args[0] == "--headless")
            {
                string hlLog = null, hlOut = null;
                bool classifyOnly = false;
                for (int hi = 1; hi < args.Length; hi++)
                {
                    if (args[hi] == "--log" && hi + 1 < args.Length) hlLog = args[hi + 1];
                    else if (args[hi] == "--out" && hi + 1 < args.Length) hlOut = args[hi + 1];
                    else if (args[hi] == "--classify-only") classifyOnly = true;
                }

                int hlRc = 0;
                string hlRep;
                try { hlRep = HeadlessReport(hlLog, classifyOnly, out hlRc); }
                catch (Exception e)
                {
                    hlRc = 1;
                    hlRep = "== --headless 内部异常 ==\r\n" + e.GetType().Name + ": " + e.Message + "\r\n"
                          + (e.StackTrace == null ? "" : e.StackTrace) + "\r\n";
                }

                // 报告一律 UTF-8 **带 BOM**：任务书要求；Windows 记事本/其它平台都能正确识别。
                var hlEnc = new System.Text.UTF8Encoding(true);
                try
                {
                    DshCore.EnsureAppDataDir();
                    string hp = System.IO.Path.Combine(DshCore.AppDataDir, "headless-report.txt");
                    System.IO.File.WriteAllText(hp, hlRep, hlEnc);
                    Console.Error.WriteLine("REPORT_FILE=" + hp);
                }
                catch { }
                if (!string.IsNullOrEmpty(hlOut))
                {
                    try { System.IO.File.WriteAllText(hlOut, hlRep, hlEnc); }
                    catch (Exception e) { Console.Error.WriteLine("OUT_WRITE_FAILED=" + e.Message); hlRc = 1; }
                }
                try
                {
                    using (var w = new System.IO.StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)))
                    { w.Write(hlRep); w.Flush(); }
                }
                catch { }
                return hlRc;
            }

            // ★★ v5 新增（WP1）：`--corpus-check [--corpus <tsv>]` —— 拿**真实语料库**回归分类器。
            //   为什么必须有：这之前"认不认得故障"只能靠几个手工样例，改一条判据无法知道有没有退化。
            //   门槛**写死**在 PluginDiag.CorpusCheck 里；故意退化（删判据 / 删预筛词）必须 exit 1。
            //   ★ 放在单实例互斥体**之前** ⇒ 有 GUI 实例在跑时也能跑（与 --selftest / --classify 一致）。
            if (args != null && args.Length > 0 && args[0] == "--corpus-check")
            {
                string corpusTsv = null;
                for (int ci = 0; ci < args.Length - 1; ci++)
                    if (args[ci] == "--corpus") corpusTsv = args[ci + 1];

                bool corpusPass;
                string corpusReport;
                try { corpusReport = PluginDiag.CorpusCheck(corpusTsv, out corpusPass); }
                catch (Exception e)
                {
                    corpusPass = false;
                    corpusReport = "语料回归异常：" + e.GetType().Name + ": " + e.Message + "\n";
                }
                try
                {
                    DshCore.EnsureAppDataDir();
                    string rp2 = System.IO.Path.Combine(DshCore.AppDataDir, "corpus-check-report.txt");
                    System.IO.File.WriteAllText(rp2, corpusReport, new System.Text.UTF8Encoding(false));
                    Console.Error.WriteLine("REPORT_FILE=" + rp2);
                }
                catch { }
                Console.Out.Write(corpusReport);
                return corpusPass ? 0 : 1;
            }

            // ★★ v5.0 新增：`--scout <包名> [--apply]` / `--scout-selftest`
            //   为什么加：这个功能原来**只有 GUI 按钮** ⇒ 回归脚本判不了它，而它偏偏是
            //   本轮唯一会去改**真实 profile**的新动作。没有判据的写动作等于没验过。
            //   `--scout-selftest` 全在临时沙箱里跑（假 pnpm、不联网、不碰真实 profile）。
            //   放在单实例互斥体**之前**：GUI 正开着也要能跑判据。
            if (args != null && args.Length > 0 && args[0] == "--scout-selftest")
            {
                string scReport;
                int scRc;
                try
                {
                    scReport = PluginScout.SelfTest();
                    scRc = scReport.IndexOf("[FAIL]") >= 0 ? 1 : 0;
                }
                catch (Exception e)
                {
                    scRc = 1;
                    scReport = "== --scout-selftest 内部异常 ==\r\n" + e.GetType().Name + ": " + e.Message + "\r\n"
                             + (e.StackTrace == null ? "" : e.StackTrace) + "\r\n";
                }
                try
                {
                    DshCore.EnsureAppDataDir();
                    string sp = System.IO.Path.Combine(DshCore.AppDataDir, "scout-selftest-report.txt");
                    System.IO.File.WriteAllText(sp, scReport, new System.Text.UTF8Encoding(true));
                    Console.Error.WriteLine("REPORT_FILE=" + sp);
                }
                catch { }
                Console.Out.Write(scReport);
                return scRc;
            }
            if (args != null && args.Length >= 2 && args[0] == "--scout")
            {
                bool scApply = false;
                for (int si = 2; si < args.Length; si++) if (args[si] == "--apply") scApply = true;
                DshCore.EnsureAppDataDir();
                string scProf = System.IO.Path.Combine(DshCore.DshHome, "profiles", "web");
                Console.WriteLine("== 大肥鱼救星 v" + DshCore.AppVersion + " · 研究插件 ==");
                Console.WriteLine("只诊断模式：" + (DshCore.ReadOnlyMode ? "已开（写动作会被拒绝）" : "已关"));
                ScoutResult scr;
                try { scr = PluginScout.Probe(args[1]); }
                catch (Exception e)
                {
                    Console.WriteLine("探测异常：" + e.GetType().Name + ": " + e.Message);
                    return 1;
                }
                Console.WriteLine(PluginScout.PlanText(scr, scProf));
                if (scr.NeedBuild.Count == 0 || !scApply) return 0;
                Console.WriteLine("---- 应用（--apply） ----");
                string scLog;
                bool scOk = PluginScout.Apply(scr, scProf, out scLog);
                Console.WriteLine(scLog);
                Console.WriteLine(scOk ? "结论：已应用并复验通过" : "结论：未应用（已自动回滚或已拒绝）");
                return scOk ? 0 : 1;
            }

            // ★★ v5 新增（WP4）：修复动作的**形式化闭环**。
            //   `--plan-fix [类别|动作id] [--id <条目id>] [--pkg <包名>] [--port <端口>]`
            //       只预演：不写文件、不碰进程，但必须指出**将改哪个文件的哪一行**。
            //   `--fix-apply <动作id> [...]`
            //       真执行：写前快照 → 执行 → 复验；复验不过**自动回滚**。
            //   为什么放在单实例互斥体之前：排障时 GUI 往往正开着，预演命令必须还能跑。
            if (args != null && args.Length > 0 && args[0] == "--plan-fix")
            {
                DshCore.EnsureAppDataDir();
                string pfFilter = args.Length >= 2 ? args[1].Trim() : "all";
                string pfId = null, pfPkg = null; int pfPort = 0;
                for (int i = 1; i < args.Length - 1; i++)
                {
                    if (args[i] == "--id") pfId = args[i + 1];
                    else if (args[i] == "--pkg") pfPkg = args[i + 1];
                    else if (args[i] == "--port") { int.TryParse(args[i + 1], out pfPort); }
                }
                string pfRes;
                try { pfRes = RepairPlan.Report(pfFilter, pfId, pfPkg, pfPort); }
                catch (Exception e) { pfRes = "预演异常：" + e.GetType().Name + ": " + e.Message + "\r\n"; }
                try
                {
                    string rp3 = System.IO.Path.Combine(DshCore.AppDataDir, "plan-fix-report.txt");
                    System.IO.File.WriteAllText(rp3, pfRes, new System.Text.UTF8Encoding(true));
                    Console.Error.WriteLine("REPORT_FILE=" + rp3);
                }
                catch { }
                try
                {
                    using (var w = new System.IO.StreamWriter(System.Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)))
                    { w.Write(pfRes); w.Flush(); }
                }
                catch { }
                return 0;
            }

            if (args != null && args.Length >= 2 && args[0] == "--fix-apply")
            {
                DshCore.EnsureAppDataDir();
                string faId = args[1].Trim();
                string faEntry = null, faPkg = null; int faPort = 0;
                for (int i = 2; i < args.Length - 1; i++)
                {
                    if (args[i] == "--id") faEntry = args[i + 1];
                    else if (args[i] == "--pkg") faPkg = args[i + 1];
                    else if (args[i] == "--port") { int.TryParse(args[i + 1], out faPort); }
                }
                bool faVerified = false;
                string faRes;
                try { faRes = RepairPlan.Apply(faId, faEntry, faPkg, faPort, out faVerified); }
                catch (Exception e)
                {
                    faVerified = false;
                    faRes = "执行异常：" + e.GetType().Name + ": " + e.Message + "\r\n";
                }
                try
                {
                    string rp4 = System.IO.Path.Combine(DshCore.AppDataDir, "fix-apply-report.txt");
                    System.IO.File.WriteAllText(rp4, faRes, new System.Text.UTF8Encoding(true));
                    Console.Error.WriteLine("REPORT_FILE=" + rp4);
                }
                catch { }
                try
                {
                    using (var w = new System.IO.StreamWriter(System.Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)))
                    { w.Write(faRes); w.Flush(); }
                }
                catch { }
                // ★ 退出码就是"有没有真的修好"：0=复验通过，1=未通过（已回滚）
                return faVerified ? 0 : 1;
            }

            // C4 启用/禁用某个条目（v4.5，可逆：先快照 → 只改 disabled 行 → 验 YAML）
            if (args != null && args.Length >= 2 && (args[0] == "--enableentry" || args[0] == "--disableentry"))
            {
                DshCore.EnsureAppDataDir();
                string res;
                // ★★ 2026-09-22 修真 bug：这里原本写的是 `args[0] == "--enableentry"`，
                //   而 SetEntryDisabled 的第二个参数是 **disabled**（true＝禁用）—— 也就是说
                //   `--disableentry` 实际在**启用**、`--enableentry` 实际在**禁用**，两个开关**正好对调**。
                //   危害实例：想临时摘掉出问题的插件（--disableentry whale-desktop-launcher）会把它
                //   的 `disabled: true` 改成 false，等于**亲手把那个会拖垮 DSH 的 launcher 打开**。
                //   抓到它的过程：--conflict-disable 的阳性夹具里 `--disableentry bff-ins` 的写盘结果
                //   与 `--enableentry` 相反（一个字节都没按预期改）。
                //   其它两条同功能路径本来就是对的（UI 的「启用被禁条目」传 false、RepairPlan 的
                //   disableentry 传 true）—— 只有这条命令行线接反了，所以一直没被发现。
                bool wantDisabled = (args[0] == "--disableentry");
                try { res = PluginDiag.SetEntryDisabled(args[1], wantDisabled); }
                catch (Exception e) { res = "执行异常：" + e.GetType().Name + ": " + e.Message; }
                Console.Out.WriteLine(res);
                return 0;
            }

            // C5 为"缺失的服务"启用被禁用条目：--fixpending <条目id> [服务名]
            if (args != null && args.Length >= 2 && args[0] == "--fixpending")
            {
                DshCore.EnsureAppDataDir();
                string res;
                try { res = PluginDiag.EnableEntryForMissingService(args.Length >= 3 ? args[2] : null, args[1]); }
                catch (Exception e) { res = "执行异常：" + e.GetType().Name + ": " + e.Message; }
                Console.Out.WriteLine(res);
                return 0;
            }

            // C6 去重同一文件里的重复 entry id：--dedup <id>
            if (args != null && args.Length >= 2 && args[0] == "--dedup")
            {
                DshCore.EnsureAppDataDir();
                string res;
                try { res = PluginDiag.RemoveDuplicateEntry(args[1]); }
                catch (Exception e) { res = "执行异常：" + e.GetType().Name + ": " + e.Message; }
                Console.Out.WriteLine(res);
                return 0;
            }

            // F2 分层判定：--layercheck [外部日志文件...]（不给文件就只看本机日志）
            if (args != null && args.Length >= 1 && args[0] == "--layercheck")
            {
                DshCore.EnsureAppDataDir();
                var extra = new System.Collections.Generic.List<string>();
                for (int i = 1; i < args.Length; i++) if (!string.IsNullOrWhiteSpace(args[i])) extra.Add(args[i]);
                string res;
                try { res = PluginDiag.LayerReport(extra.ToArray()); }
                catch (Exception e) { res = "执行异常：" + e.GetType().Name + ": " + e.Message; }
                string rp = System.IO.Path.Combine(DshCore.AppDataDir, "layercheck-report.txt");
                try { System.IO.File.WriteAllText(rp, res, new System.Text.UTF8Encoding(false)); } catch { }
                try
                {
                    using (var w = new System.IO.StreamWriter(System.Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)))
                    { w.Write(res); w.Flush(); }
                }
                catch { }
                Console.Error.WriteLine("REPORT_FILE=" + rp);
                return 0;
            }

            // F3 回滚建议：--rollbackadvice
            if (args != null && args.Length >= 1 && args[0] == "--rollbackadvice")
            {
                DshCore.EnsureAppDataDir();
                string res;
                try { res = PluginDiag.RollbackAdvice(); }
                catch (Exception e) { res = "执行异常：" + e.GetType().Name + ": " + e.Message; }
                Console.Out.WriteLine(res);
                return 0;
            }

            // F1 重装插件：--reinstall <包名>
            if (args != null && args.Length >= 2 && args[0] == "--reinstall")
            {
                DshCore.EnsureAppDataDir();
                string res;
                try { res = PluginDiag.ReinstallPlugin(args[1]); }
                catch (Exception e) { res = "执行异常：" + e.GetType().Name + ": " + e.Message; }
                Console.Out.WriteLine(res);
                return 0;
            }

            // ★ 2026-09-17 新增 ①补丁固化体检：--patchlock
            if (args != null && args.Length >= 1 && args[0] == "--patchlock")
            {
                DshCore.EnsureAppDataDir();
                string res;
                try { res = PatchLock.Report(); }
                catch (Exception e) { res = "执行异常：" + e.GetType().Name + ": " + e.Message; }
                string rp = System.IO.Path.Combine(DshCore.AppDataDir, "patchlock-report.txt");
                try { System.IO.File.WriteAllText(rp, res, new System.Text.UTF8Encoding(false)); } catch { }
                try
                {
                    using (var w = new System.IO.StreamWriter(System.Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)))
                    { w.Write(res); w.Flush(); }
                }
                catch { }
                Console.Error.WriteLine("REPORT_FILE=" + rp);
                return 0;
            }

            // ★ 2026-09-17 新增 ②一键固化：--patchlock-fix <补丁id>
            if (args != null && args.Length >= 2 && args[0] == "--patchlock-fix")
            {
                DshCore.EnsureAppDataDir();
                string res;
                try { res = PatchLock.Lock(args[1]); }
                catch (Exception e) { res = "执行异常：" + e.GetType().Name + ": " + e.Message; }
                string rp = System.IO.Path.Combine(DshCore.AppDataDir, "patchlock-fix-report.txt");
                try { System.IO.File.WriteAllText(rp, res, new System.Text.UTF8Encoding(false)); } catch { }
                Console.Out.WriteLine(res);
                Console.Error.WriteLine("REPORT_FILE=" + rp);
                return 0;
            }

            // ★ 2026-09-17 新增 ③白屏盲区扫描：--whitescreen
            if (args != null && args.Length >= 1 && args[0] == "--whitescreen")
            {
                DshCore.EnsureAppDataDir();
                string res;
                try { res = PatchLock.WhiteScreenReport(); }
                catch (Exception e) { res = "执行异常：" + e.GetType().Name + ": " + e.Message; }
                string rp = System.IO.Path.Combine(DshCore.AppDataDir, "whitescreen-report.txt");
                try { System.IO.File.WriteAllText(rp, res, new System.Text.UTF8Encoding(false)); } catch { }
                try
                {
                    using (var w = new System.IO.StreamWriter(System.Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)))
                    { w.Write(res); w.Flush(); }
                }
                catch { }
                Console.Error.WriteLine("REPORT_FILE=" + rp);
                return 0;
            }

            // ★ 2026-09-17 新增 ④错误码翻译：--errtext <原始错误文本>
            if (args != null && args.Length >= 2 && args[0] == "--errtext")
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 1; i < args.Length; i++) sb.Append(args[i]).Append(' ');
                // 用 UTF-8 输出（否则中文在管道/控制台里会变乱码）
                try
                {
                    using (var w = new System.IO.StreamWriter(System.Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)))
                    { w.Write(PatchLock.TranslateModelError(sb.ToString())); w.Flush(); }
                }
                catch { }
                return 0;
            }

            // ★ 2026-09-17 新增 ⑤中文/非 ASCII 路径体检：--pathaudit
            if (args != null && args.Length >= 1 && args[0] == "--pathaudit")
            {
                DshCore.EnsureAppDataDir();
                string res;
                try { res = PathAudit.Report(); }
                catch (Exception e) { res = "执行异常：" + e.GetType().Name + ": " + e.Message; }
                string rp = System.IO.Path.Combine(DshCore.AppDataDir, "pathaudit-report.txt");
                try { System.IO.File.WriteAllText(rp, res, new System.Text.UTF8Encoding(false)); } catch { }
                try
                {
                    using (var w = new System.IO.StreamWriter(System.Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)))
                    { w.Write(res); w.Flush(); }
                }
                catch { }
                Console.Error.WriteLine("REPORT_FILE=" + rp);
                return 0;
            }

            // ★ 2026-09-17 新增 ⑥强力自愈的能力边界：--selfheal-boundary
            if (args != null && args.Length >= 1 && args[0] == "--selfheal-boundary")
            {
                DshCore.EnsureAppDataDir();
                string res;
                try { res = PathAudit.SelfHealBoundary(); }
                catch (Exception e) { res = "执行异常：" + e.GetType().Name + ": " + e.Message; }
                string rp = System.IO.Path.Combine(DshCore.AppDataDir, "selfheal-boundary-report.txt");
                try { System.IO.File.WriteAllText(rp, res, new System.Text.UTF8Encoding(false)); } catch { }
                try
                {
                    using (var w = new System.IO.StreamWriter(System.Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)))
                    { w.Write(res); w.Flush(); }
                }
                catch { }
                Console.Error.WriteLine("REPORT_FILE=" + rp);
                return 0;
            }

            // ★ 2026-09-17 新增 ⑦说明书页布局自检：--manualcheck
            if (args != null && args.Length >= 1 && args[0] == "--manualcheck")
            {
                DshCore.EnsureAppDataDir();
                string res;
                try { res = Manual.LayoutCheck(); }
                catch (Exception e) { res = "执行异常：" + e.GetType().Name + ": " + e.Message; }
                try
                {
                    using (var w = new System.IO.StreamWriter(System.Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)))
                    { w.Write(res); w.Flush(); }
                }
                catch { }
                return 0;
            }

            // ★ 2026-09-17 新增 ⑧皮肤联动报告：--skin
            //   ★ 2026-09-18 扩展：`--skin list` 列可选皮肤；`--skin <id>` 直接切（并存盘，同界面上的「🎨 换皮肤」）。
            if (args != null && args.Length >= 1 && args[0] == "--skin")
            {
                DshCore.EnsureAppDataDir();
                if (args.Length >= 2) return SkinTheme.CliSkin(args[1]);   // list / <id>
                string res;
                // ★ 2026-09-17：自绘标题栏（B 方案）的报告挂在皮肤报告后面
                try
                {
                    res = SkinTheme.Report();
                    try { res = res + "\r\n" + SkinFrame.Report(); } catch { }
                }
                catch (Exception e) { res = "执行异常：" + e.GetType().Name + ": " + e.Message; }
                try
                {
                    using (var w = new System.IO.StreamWriter(System.Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)))
                    { w.Write(res); w.Flush(); }
                }
                catch { }
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // ★ 2026-09-17：B 方案自绘标题栏的**退回开关**。
            //   自绘万一出岔子（某个 Windows 版本上标题栏没扣干净等），带上这个开关
            //   就能整套退回原生窗口，不用回滚代码也不用重编旧版。
            //   注意：不能写死 args[0]（本项目踩过：`--tab 4 --shot x.png` 被静默忽略）。
            try
            {
                string forced = Environment.GetEnvironmentVariable("BFF_NATIVE_FRAME");
                if (!string.IsNullOrEmpty(forced) && forced != "0" && !forced.Equals("false", StringComparison.OrdinalIgnoreCase))
                    SkinFrame.Disable("环境变量 BFF_NATIVE_FRAME=" + forced);
            }
            catch { }

            // ★ 2026-09-17：还原收口的**负对照**开关（供 --maxcycle 自证判据真的抓得住漂移）
            try
            {
                string g = Environment.GetEnvironmentVariable("BFF_RESTORE_GUARD");
                if (!string.IsNullOrEmpty(g) && (g == "0" || g.Equals("false", StringComparison.OrdinalIgnoreCase)))
                    MainForm.RestoreGuardEnabled = false;
            }
            catch { }
            if (args != null)
                for (int ai = 0; ai < args.Length; ai++)
                    if (args[ai] == "--nativeframe") { SkinFrame.Disable("命令行 --nativeframe"); break; }

            // 仅供自检/截图：--tab N 启动时选中第 N 个页签（0 起）
            if (args != null && args.Length >= 2 && args[0] == "--tab")
            {
                int t;
                if (int.TryParse(args[1], out t)) MainForm.InitialTab = t;
            }

            // ★ 2026-09-17 新增：--maxcycle [N] 最大化/还原漂移 回归判据（自动自检，见 MainForm.MaxCycleTest）
            //   扫全部参数，不写死位置（本项目踩过 "--tab 4 --shot x.png" 被静默忽略的坑）。
            int maxCycleRounds = -1;
            if (args != null)
                for (int ai = 0; ai < args.Length; ai++)
                    if (args[ai] == "--maxcycle")
                    {
                        maxCycleRounds = 4;
                        if (ai + 1 < args.Length)
                        {
                            int n;
                            if (int.TryParse(args[ai + 1], out n) && n > 0 && n <= 50) maxCycleRounds = n;
                        }
                        break;
                    }

            // ★ 2026-09-18 新增：--paintbench [N] 头部横幅重绘耗时（默认 200 次），配 BFF_NOCACHE=1 做负对照
            int paintBenchRounds = -1;
            if (args != null)
                for (int ai = 0; ai < args.Length; ai++)
                    if (args[ai] == "--paintbench")
                    {
                        paintBenchRounds = 200;
                        if (ai + 1 < args.Length)
                        {
                            int n;
                            if (int.TryParse(args[ai + 1], out n) && n > 0 && n <= 5000) paintBenchRounds = n;
                        }
                        break;
                    }

            // ★ 2026-09-16 新增（T27b：没有单实例保护）：
            //   桌面图标可以**双开** ⇒ 两套 4 秒轮询、两套对同一批配置做快照与写入、
            //   `run-times.txt` 各自全量覆盖（该写入没有文件锁）⇒
            //   "并发点按钮"在双开下会放大成"两个进程并发写配置"。
            //   这里用全局互斥体保证只有一个 GUI 实例；已有实例时把它的窗口**唤到前台**再退出。
            bool createdNew;
            using (var mutex = new Mutex(true, @"Local\BigFatFishRescuer.SingleInstance.v4", out createdNew))
            {
                if (!createdNew)
                {
                    // ★★ 2026-09-18：这里**必须出声**（原来是一声不响 return 0）。
                    //   后果实测过：机器上还留着一个救星实例时，--selftest / --configtest / --shot /
                    //   --maxcycle / --paintbench **什么都不做、也不报错、也不写报告**，
                    //   命令行看起来"跑了但没输出" ⇒ 我为此白测了一轮（还以为是自己 grep 错了）。
                    //   （--skin 不受影响：它在这段互斥体**之前**就处理掉了。）
                    try
                    {
                        Console.WriteLine("已有「大肥鱼救星」在运行 ⇒ 本次只把那个窗口唤到前台，不执行命令行任务。");
                        Console.WriteLine("提醒：--selftest / --configtest / --envcheck / --shot / --maxcycle / --paintbench");
                        Console.WriteLine("      都需要先关掉已在跑的实例；换皮肤 --skin list|<id> 不受影响。");
                    }
                    catch { }
                    BringExistingToFront();
                    return 0;
                }
                // ★ 2026-09-17 新增：--shot <png路径> 自截图（文档配图/验收用）。
                //   为什么内置：从外面截窗口要抢前台，Windows 不让后台进程抢（实测抢不到，
                //   截回来的是浏览器）；DrawToBitmap 是**由程序自己画自己**，不依赖前台。
                // ★ 2026-09-17 修正：原来写死"--shot 必须是第 1 个参数"，
                //   于是 `--tab 4 --shot x.png` 会被**静默忽略**（既不出图也不报错，实测踩过）
                //   ⇒ 改成扫全部参数。
                if (args != null)
                    for (int ai = 0; ai < args.Length - 1; ai++)
                        if (args[ai] == "--shot") { MainForm.ShotPath = args[ai + 1]; break; }

                // ★ 2026-09-17：--maxcycle 回归判据跑完就退出，不进 GUI 主循环
                //   （放在互斥体之内 ⇒ 有实例在跑时不会静默跑出一个第二实例去碰配置）
                if (maxCycleRounds > 0) return MainForm.MaxCycleTest(maxCycleRounds) == 0 ? 0 : 1;

                // ★ 2026-09-18：--paintbench [N] 量"头部横幅重绘一次多少 ms"（配 BFF_NOCACHE=1 做负对照）
                if (paintBenchRounds > 0) return MainForm.PaintBench(paintBenchRounds) == 0 ? 0 : 1;
                Application.Run(new MainForm());
            }
            return 0;
        }

        /// <summary>把已经在跑的那个救星窗口唤到前台（T27b：双击第二次时用户不该"什么都没发生"）。</summary>
        private static void BringExistingToFront()
        {
            try
            {
                System.Diagnostics.Process me = System.Diagnostics.Process.GetCurrentProcess();
                foreach (System.Diagnostics.Process p in
                         System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
                {
                    if (p.Id == me.Id) continue;
                    IntPtr h = p.MainWindowHandle;
                    if (h == IntPtr.Zero) continue;
                    ShowWindow(h, SW_RESTORE);
                    SetForegroundWindow(h);
                    break;
                }
            }
            catch { }
        }

        private const int SW_RESTORE = 9;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // 静默自愈：不弹窗，直接跑强力自愈，结果写入日志文件并退出（供开机自启）
        private static int RunAutoRecover()
        {
            DshCore.EnsureAppDataDir();
            string result;
            try { result = DshCore.ForceRecover(); }
            catch (Exception e) { result = "自愈异常：" + e.Message; }
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(DshCore.AppDataDir, "autorecover.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" + result,
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
            return 0;
        }

        // 导出脱敏诊断包（供上游排查）：跑完整5步诊断 + 汇总日志/patch/配置，隐去用户名与路径
        private static int RunDiagnose()
        {
            DshCore.EnsureAppDataDir();
            string ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string dir = System.IO.Path.Combine(DshCore.AppDataDir, "diagnose-" + ts);
            try { System.IO.Directory.CreateDirectory(dir); } catch { }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("== 大肥鱼救星 · 脱敏诊断包导出 ==");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("目录: " + dir);
            sb.AppendLine();

            // 1) 完整诊断报告（5步：服务状态/为何打不开/插件兼容/崩溃排查/日志线索）
            try
            {
                string report = DshCore.ExportDiagnostics();
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "dsh-diagnostics-report.txt"),
                    Sanitize(report), new System.Text.UTF8Encoding(false));
                sb.AppendLine("[OK]   dsh-diagnostics-report.txt");
            }
            catch (Exception e) { sb.AppendLine("[FAIL] 诊断报告: " + e.Message); }

            // 2) 错误日志
            string errLog = System.IO.Path.Combine(DshCore.AppDataDir, "dsh-web.out.log.err");
            if (System.IO.File.Exists(errLog))
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "dsh-web.out.log.err"),
                    Sanitize(ReadFileShare(errLog)), new System.Text.UTF8Encoding(false));
                sb.AppendLine("[OK]   dsh-web.out.log.err");
            }
            else sb.AppendLine("[WARN] 未找到 dsh-web.out.log.err");

            // 3) profile patch（插件启用/禁用层）
            string patch = System.IO.Path.Combine(DshCore.DshHome, "profiles", "web", "cordis.patch.yml");
            if (System.IO.File.Exists(patch))
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "cordis.patch.yml"),
                    Sanitize(ReadFileShare(patch)), new System.Text.UTF8Encoding(false));
                sb.AppendLine("[OK]   cordis.patch.yml");
            }
            else sb.AppendLine("[WARN] 未找到 cordis.patch.yml");

            // 4) launcher.ini（启动配置源）
            string ini = System.IO.Path.Combine(DshCore.DshHome, "whale-desktop-launcher", "launcher.ini");
            if (System.IO.File.Exists(ini))
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "launcher.ini"),
                    Sanitize(ReadFileShare(ini)), new System.Text.UTF8Encoding(false));
                sb.AppendLine("[OK]   launcher.ini");
            }
            else sb.AppendLine("[WARN] 未找到 launcher.ini");

            // 5) 环境摘要
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "env.txt"),
                Sanitize("DSH版本: " + DshCore.DshVersion() + "\r\nCLR: " + Environment.Version + "\r\nOS: " + Environment.OSVersion),
                new System.Text.UTF8Encoding(false));
            sb.AppendLine("[OK]   env.txt");

            sb.AppendLine();
            sb.AppendLine("完成。把整个目录发给上游排查即可（已隐去用户名/路径）。");

            string result = sb.ToString();
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "README.txt"), result, new System.Text.UTF8Encoding(false));
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(DshCore.AppDataDir, "diagnose.log"), result, new System.Text.UTF8Encoding(false)); } catch { }
            try { Console.Error.WriteLine("DIAGNOSE_DIR=" + dir); } catch { }
            return 0;
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            // ★★ v5（WP5 复核第二轮）修：原来这里**硬编码**了一个 URL 编码用户名
            //   （`s.Replace("%E9%9D%92%E8%96%87", "USER")`），而紧接着的注释却写着
            //   「不要把某台机器的用户名硬编码进 exe（那是隐私泄漏）」—— 自相矛盾：
            //   那个百分号串就是某个真实用户名的 UTF-8 URL 编码，仍然泄了。
            //   而且它在**别人的机器上失效**（别人日志里的 URL 编码用户名不会被脱敏）。
            //   修法：URL 编码形式**从环境变量现算**，exe 里不留任何人的用户名。
            string uname = Environment.UserName;
            if (!string.IsNullOrEmpty(uname))
            {
                // 只替换「用户名本身」与「它的 URL 编码形式」。
                // ★ 故意**不**做 uname 的小写全局替换：那会把日志里同名的普通单词
                //   （如用户名 john → 正文里的 "john"）一起抹掉，属于过度脱敏，
                //   会改动分类证据、进而改动指纹与判据 ⇒ 不可接受。
                s = s.Replace(uname, "USER");                                  // 明文
                try
                {
                    string enc = Uri.EscapeDataString(uname);                  // URL 编码（大写 hex）
                    if (!string.IsNullOrEmpty(enc) && enc != uname)
                    {
                        s = s.Replace(enc, "USER");
                        s = s.Replace(enc.ToLowerInvariant(), "USER");         // 编码串很独特，无过度脱敏风险
                    }
                }
                catch { }
            }
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home)) s = s.Replace(home, "~");
            return s;
        }

        // 以 FileShare.ReadWrite 读取（dsh 运行中可能锁住日志文件）
        private static string ReadFileShare(string path)
        {
            try
            {
                using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                using (var sr = new System.IO.StreamReader(fs, System.Text.Encoding.UTF8, true))
                {
                    return sr.ReadToEnd();
                }
            }
            catch { return string.Empty; }
        }

        // ------------------------------------------------------------
        // 开机自启
        //
        // ★★ WP2①（2026-09-20 防误报加固）：**只引导、不代写**。
        //   旧版（2026-09-16）用 PowerShell + WScript.Shell 往「启动」文件夹写 .lnk。
        //   那正是杀软机器学习启发式里**权重最高**的"持久化"形状
        //   （见 _救星_防误报研究.md §一.1），也是本次 Defender 判
        //   Trojan:Win32/Bearfoos.B!ml 的头号嫌疑。
        //   ⇒ 本程序现在**一行都不写**：不写「启动」文件夹、不写 Run 键、不写计划任务、
        //     不写 schtasks/RegisterTaskDefinition。只做三件事：
        //       ① 生成一份自愈 .cmd 到**救星自己的目录**（不是启动文件夹），内容可自查；
        //       ② 把"你自己怎么放进去"的步骤讲清楚；
        //       ③ 帮你打开「启动」文件夹（只读浏览，不写入）。
        //     放不放、放哪个、什么时候放，全部由你决定。
        //
        // （历史问题 T21 / T21b / P13 / P16 已在旧版修好；其中 T21「只看 File.Exists
        //   判成功」随着"不再代写"一并失效 —— 没有写入，就不存在"写没写成功"的误报。
        //   P16「重定向两路输出却从不读」的修复保留在 RunPs 里，因为读快捷方式目标还要用。）
        // ------------------------------------------------------------
        public static string StartupShortcutPath
        {
            get
            {
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Startup), "大肥鱼救星-自愈.lnk");
            }
        }

        public static bool StartupShortcutExists()
        {
            try { return System.IO.File.Exists(StartupShortcutPath); }
            catch { return false; }
        }

        /// <summary>
        /// 旧启动项快捷方式指向哪里。
        /// ★ WP2①：这里**不再**用 COM 去读 .lnk 的目标 —— 那需要在程序里放
        ///   「WScript.Shell + CreateShortcut」这类字符串，正是"创建启动项"的典型字眼，
        ///   会被杀软 ML 启发式当成持久化形状（哪怕我们这里只是读）。
        ///   改用文件系统时间做一次**只读**描述，要确认指向哪里请自己在资源管理器里看属性。
        /// </summary>
        public static string StartupShortcutTarget()
        {
            if (!StartupShortcutExists()) return "（不存在）";
            try
            {
                var fi = new System.IO.FileInfo(StartupShortcutPath);
                return "（不再由本程序解析；最后修改时间 " + fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                       + "，大小 " + fi.Length.ToString() + " 字节。"
                       + "想看它指向哪里：在资源管理器里右键它 → 属性 → 目标）";
            }
            catch (Exception e) { return "（读取失败：" + e.Message + "）"; }
        }

        /// <summary>取消开机自启：删除启动项快捷方式，**不动别的任何东西**（P13）。</summary>
        public static string UninstallStartup()
        {
            string startup = StartupShortcutPath;
            if (!System.IO.File.Exists(startup))
                return "本来就没有开机自启项，无需取消。\r\n（期望路径：" + startup + "）";
            try
            {
                // 先备份一份到救星目录，误删了还能找回来
                string bak = System.IO.Path.Combine(DshCore.AppDataDir,
                    "开机自启快捷方式-备份-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".lnk");
                try { DshCore.EnsureAppDataDir(); System.IO.File.Copy(startup, bak, true); } catch { bak = null; }
                System.IO.File.Delete(startup);
                return "已取消开机自启（删除启动项）。\r\n（原文件" + (bak != null ? "备份在：" + bak : "未能备份") + "）";
            }
            catch (Exception e)
            {
                return "取消失败：" + e.Message + "\r\n请手动删除：" + startup;
            }
        }

        /// <summary>静默跑一段 PowerShell 并读回输出（**两路都异步排空**，P16）。</summary>
        private static string RunPs(string script, int timeoutMs)
        {
            var psi = new System.Diagnostics.ProcessStartInfo();
            psi.FileName = "powershell.exe";
            // ★ P16：加 -WindowStyle Hidden 双保险 + 两路**异步排空**
            psi.Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"" + script + "\"";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
            {
                var so = new System.Text.StringBuilder();
                // ★ P16：旧版 RedirectStandardError=true 却**从不读** —— 输出一多就会写满
                //   管道缓冲而**永久阻塞**（表现为"卡住"）。这里两路都异步读掉。
                p.OutputDataReceived += delegate(object s, System.Diagnostics.DataReceivedEventArgs e)
                { if (e.Data != null) { lock (so) so.AppendLine(e.Data); } };
                p.ErrorDataReceived += delegate { /* 丢弃，仅为排空管道 */ };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return null; }
                try { p.WaitForExit(); } catch { }
                lock (so) return so.ToString();
            }
        }

        /// <summary>
        /// 生成一份**用户自用**的开机自愈 .cmd，路径在**救星自己的目录**里（不是启动文件夹）。
        /// 本程序不会替你把它放进「启动」文件夹 —— 那一步由你自己做。
        /// </summary>
        public static string WriteStartupScript()
        {
            try
            {
                DshCore.EnsureAppDataDir();
                string exe = Application.ExecutablePath;
                string path = System.IO.Path.Combine(DshCore.AppDataDir, "开机自愈-需要你自己放进启动文件夹.cmd");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("rem 大肥鱼救星 · 开机自愈（你自己放进「启动」文件夹才会生效）");
                sb.AppendLine("rem 生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("rem 注意：这个文件**不是**救星写进「启动」文件夹的。");
                sb.AppendLine("rem       救星不写任何启动项；放不放、放哪里，由你自己决定。");
                sb.AppendLine("rem 撤销：回到「启动」文件夹把这个 .cmd 删掉即可。");
                sb.AppendLine("start \"\" \"" + exe + "\" --autorecover");
                // 用 ANSI（本机中文 Windows = GBK）写，cmd 与记事本才不会把中文显示成乱码
                System.IO.File.WriteAllText(path, sb.ToString(), System.Text.Encoding.Default);
                return path;
            }
            catch (Exception e)
            {
                return "（生成失败：" + e.Message + "）";
            }
        }

        /// <summary>只是"打开「启动」文件夹"给你看 —— 只读浏览，本程序不往里写任何东西。</summary>
        public static string OpenStartupFolder()
        {
            string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = "explorer.exe";
                psi.Arguments = "\"" + startup + "\"";
                psi.UseShellExecute = false;
                System.Diagnostics.Process.Start(psi);
                return "已打开「启动」文件夹：" + startup + "\r\n（本程序没有往里写任何东西 —— 放什么由你拖进去。）";
            }
            catch (Exception e)
            {
                return "打不开「启动」文件夹：" + e.Message + "\r\n路径是：" + startup;
            }
        }

        /// <summary>
        /// ★ WP2①：开机自启**只引导、不代写**。
        /// 返回一段说明；本函数**不会**写「启动」文件夹 / Run 键 / 计划任务中的任何一个。
        /// </summary>
        public static string StartupGuide()
        {
            string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string exe = "";
            try { exe = Application.ExecutablePath; } catch { exe = "（读不到自身路径）"; }
            string script = WriteStartupScript();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("== 开机自启 · 只引导，不代写 ==");
            sb.AppendLine();
            sb.AppendLine("为什么不再自动创建：");
            sb.AppendLine("  「程序自己往开机启动项里写东西」是杀软机器学习启发式里权重最高的特征之一，");
            sb.AppendLine("  救星这次被 Defender 误报成木马，头号嫌疑就是它。");
            sb.AppendLine("  所以本程序现在**一行都不写**，放不放、放哪个，全部由你决定。");
            sb.AppendLine();
            sb.AppendLine("想让它开机自愈，两步（全程你自己操作）：");
            sb.AppendLine("  1) 按 Win+R，输入 shell:startup 回车 —— 会打开「启动」文件夹：");
            sb.AppendLine("       " + startup);
            sb.AppendLine("  2) 把下面这个 .cmd **拖进**那个文件夹即可（复制也行）：");
            sb.AppendLine("       " + script);
            sb.AppendLine();
            sb.AppendLine("撤销：回到「启动」文件夹把那个 .cmd 删掉就行，用不着我。");
            sb.AppendLine();
            sb.AppendLine("当前状态（只读检查，没有改动任何东西）：");
            sb.AppendLine("  · 启动文件夹里：" + (StartupShortcutExists()
                ? "**有**旧版快捷方式 " + StartupShortcutPath + "（再点一次本按钮可取消它）"
                : "没有救星创建的任何东西"));
            sb.AppendLine("  · 本程序当前路径：" + exe);
            return sb.ToString();
        }

        // 环境与弹窗体检（只读，不改任何东西）—— 供无 GUI 时验证这批新代码
        // ============================================================
        // WP5 · 无 GUI / 跨平台只读诊断（--headless）
        //
        // 为什么这么做：任务书要求"不会装 Windows 程序的人也能远程排障" ——
        // 一个只在 Windows GUI 里能用、还顺手启动/结束进程的工具，在别人的机器上
        // 恰恰是最危险的东西。所以这里把**纯逻辑**（DshCore/PluginDiag/PatchLock/
        // SafeConfig/PathAudit）单独驱动一遍，并且：
        //   · 不启动/不结束任何进程；
        //   · 不写任何配置，只往自己的 AppDataDir 写报告；
        //   · 报告 UTF-8 BOM；退出码 0=全通过 / 1=有失败项（可判）。
        //   · `--classify-only` 输出**只由输入日志决定**（路径已归一化、无时间戳/平台），
        //     因此同一份日志在任何平台应得到逐字一致的结果，并用
        //     CLASSIFY_FINGERPRINT=sha256:… 固化下来供跨机比对。
        // ============================================================
        private static string Sha256Hex(string s)
        {
            byte[] h;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s == null ? "" : s));
            var o = new System.Text.StringBuilder();
            for (int i = 0; i < h.Length; i++)
                o.Append(h[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return o.ToString();
        }

        /// <summary>把机器相关的串归一化，保证跨平台可逐字比对。</summary>
        private static string NormalizeForCompare(string s, string logPath)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Sanitize(s);                                    // 用户名/家目录 → USER / ~
            if (!string.IsNullOrEmpty(logPath))
            {
                s = s.Replace(logPath, "<LOG>");
                try { s = s.Replace(System.IO.Path.GetFullPath(logPath), "<LOG>"); } catch { }
            }
            s = s.Replace("\r\n", "\n").Replace("\r", "\n");
            return s;
        }

        private static string HeadlessReport(string logPath, bool classifyOnly, out int rc)
        {
            var sb = new System.Text.StringBuilder();
            int pass = 0, fail = 0;

            // ---------- 纯函数模式：跨机可逐字比对 ----------
            string classifyBlock = "";
            int nFind = 0;
            var cats = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(logPath))
            {
                if (!System.IO.File.Exists(logPath))
                {
                    classifyBlock = "[FAIL] 日志文件不存在：" + logPath;
                    fail++;
                }
                else
                {
                    try
                    {
                        var finds = PluginDiag.ClassifyFiles(new string[] { logPath });
                        nFind = finds.Count;
                        var ci = new System.Text.StringBuilder();
                        foreach (var f in finds)
                        {
                            if (cats.IndexOf(f.Category) < 0) cats.Add(f.Category);
                            ci.AppendLine("[" + f.Category + "] "
                                + (string.IsNullOrEmpty(f.Culprit) ? "" : "涉及=" + NormalizeForCompare(f.Culprit, logPath) + " | ")
                                + "证据=" + NormalizeForCompare(f.Evidence, logPath));
                        }
                        classifyBlock = ci.ToString();
                        if (nFind == 0) fail++; else pass++;
                    }
                    catch (Exception e)
                    {
                        classifyBlock = "[FAIL] 分类异常：" + e.GetType().Name + ": " + e.Message;
                        fail++;
                    }
                }
            }

            if (classifyOnly)
            {
                // ★★ v5（WP5 复核第二轮）修：**跨平台逐字比对必须与行尾无关**。
                //   旧写法用 sb.AppendLine 输出、而指纹又直接算在 classifyBlock 上，
                //   两处都走 Environment.NewLine（Windows=CRLF / Linux=LF）
                //   ⇒ 同一份日志在两个平台上**指纹必然不同**，
                //     WP5 的验收判据（"两个平台输出一致结论、可逐字比对"）当场作废。
                //   本机即可复现：同一段分类块，CRLF 版 sha256 = 2e6d…（旧 exe 打印的）
                //   ≠ LF 版 sha256 = 见 README-HEADLESS.md。
                //   修法：输出与指纹**都钉死用 "\n"**，不再依赖平台默认行尾。
                string fpSrc = classifyBlock.Replace("\r\n", "\n").Replace("\r", "\n");
                var o = new System.Text.StringBuilder();
                o.Append("== 大肥鱼救星 · --headless --classify-only（纯函数模式）==\n");
                o.Append("输入日志: " + (string.IsNullOrEmpty(logPath) ? "(未给 --log)" : "<LOG>") + "\n");
                o.Append("命中片段: " + nFind + " 条；涉及类别: " + cats.Count + " 类"
                         + (cats.Count > 0 ? "（" + string.Join("、", cats.ToArray()) + "）" : "") + "\n");
                o.Append("\n");
                if (fpSrc.Length > 0) o.Append(fpSrc);
                o.Append("\n");
                o.Append("CLASSIFY_FINGERPRINT=sha256:" + Sha256Hex(fpSrc) + "\n");
                o.Append("（本模式不含时间/平台/机器路径，且行尾已统一为 LF ⇒ 同一份日志在任何平台得到相同指纹）\n");
                rc = (fail > 0 && !string.IsNullOrEmpty(logPath)) ? 1 : 0;
                if (string.IsNullOrEmpty(logPath)) rc = 1;
                return o.ToString();
            }

            // ---------- 全量只读体检 ----------
            sb.AppendLine("== 大肥鱼救星 · 无 GUI 只读诊断（--headless）==");
            sb.AppendLine("程序: " + (System.Reflection.Assembly.GetExecutingAssembly().Location));
            sb.AppendLine("平台: " + System.Environment.OSVersion.VersionString
                          + " / CLR " + System.Environment.Version.ToString());
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("DshHome: " + DshCore.DshHome);
            sb.AppendLine("只读承诺: 本次运行不启动/不结束任何 dsh 进程，不修改任何配置（只写本报告）");
            sb.AppendLine();

            Action<string, Func<string>> body = delegate(string t, Func<string> f)
            {
                sb.AppendLine();
                sb.AppendLine("---- " + t + " ----");
                try
                {
                    string x = f();
                    sb.AppendLine(string.IsNullOrEmpty(x) ? "(无输出)" : x.TrimEnd());
                    pass++;
                }
                catch (Exception e)
                {
                    sb.AppendLine("(异常) " + e.GetType().Name + ": " + e.Message);
                    fail++;
                }
            };
            Action<string, Func<string>> local = delegate(string t, Func<string> f)
            {
                sb.AppendLine();
                sb.AppendLine("---- " + t + "（本机状态，跨机比对时请忽略）----");
                try
                {
                    string x = f();
                    sb.AppendLine(string.IsNullOrEmpty(x) ? "(无输出)" : x.TrimEnd());
                }
                catch (Exception e) { sb.AppendLine("(异常) " + e.GetType().Name + ": " + e.Message); }
            };

            sb.AppendLine("######## A. 代码能力（与机器无关，计数进退出码） ########");
            body("补丁层状态 PatchGuard.CheckPatches", delegate { return PatchGuard.CheckPatches(); });
            body("终端环境 PatchGuard.TerminalCheck", delegate { return PatchGuard.TerminalCheck(); });
            body("启动链 DshBoot.StartupChainCheck", delegate { return DshBoot.StartupChainCheck(); });
            body("插件全景 PluginDiag.PanoramaReport", delegate { return PluginDiag.PanoramaReport(); });
            body("根因分析 PluginDiag.RootCauseReport", delegate { return PluginDiag.RootCauseReport(); });
            body("重复条目 PluginDiag.DuplicateReport", delegate { return PluginDiag.DuplicateReport(); });
            body("插件分层 PluginDiag.LayerReport", delegate { return PluginDiag.LayerReport(new string[0]); });
            body("补丁层锁 PatchLock.Report", delegate { return PatchLock.Report(); });
            body("白屏排查 PatchLock.WhiteScreenReport", delegate { return PatchLock.WhiteScreenReport(); });
            body("路径审计 PathAudit.Report", delegate { return PathAudit.Report(); });
            body("自愈能力边界 PathAudit.SelfHealBoundary", delegate { return PathAudit.SelfHealBoundary(); });
            body("已知修法复检 PluginDiag.RecheckKnownFixes", delegate { return PluginDiag.RecheckKnownFixes(); });
            body("回滚建议 PluginDiag.RollbackAdvice", delegate { return PluginDiag.RollbackAdvice(); });
            body("保留端口 PluginDiag.ReservedPortReport", delegate { return PluginDiag.ReservedPortReport(); });
            body("修复动作预演 RepairPlan.Report（只预演，不执行）",
                 delegate { return RepairPlan.Report("all", null, null, 0); });

            // 配置只读体检：非空输出 = 真的有问题 ⇒ 明确判 FAIL（不当"能力项"混过去）
            sb.AppendLine();
            sb.AppendLine("---- 真实配置只读体检 SafeConfig.HealAfterKill ----");
            try
            {
                string heal = SafeConfig.HealAfterKill(null);
                if (string.IsNullOrEmpty(heal)) { sb.AppendLine("(配置全部合法)"); pass++; }
                else { sb.AppendLine(heal.TrimEnd()); sb.AppendLine("[FAIL] 配置体检发现问题（上方已列出）"); fail++; }
            }
            catch (Exception e) { sb.AppendLine("(异常) " + e.GetType().Name + ": " + e.Message); fail++; }

            sb.AppendLine();
            sb.AppendLine("######## B. 本机状态（不计数） ########");
            local("插件存活性 PluginDiag.LivenessReport", delegate { return PluginDiag.LivenessReport(null); });
            local("会话健康 PluginDiag.SessionHealthReport", delegate { return PluginDiag.SessionHealthReport(); });

            if (!string.IsNullOrEmpty(logPath))
            {
                sb.AppendLine();
                sb.AppendLine("######## C. 外部日志分类（与上面同一套判据） ########");
                sb.AppendLine("命中片段: " + nFind + " 条；涉及类别: " + cats.Count + " 类");
                sb.AppendLine();
                sb.Append(classifyBlock);
            }

            sb.AppendLine();
            sb.AppendLine("==================================================");
            sb.AppendLine("结论: 通过 " + pass + " 项 / 失败 " + fail + " 项   ⇒ exit " + (fail > 0 ? 1 : 0));
            sb.AppendLine("==================================================");
            rc = fail > 0 ? 1 : 0;
            return sb.ToString();
        }

        private static int RunEnvCheck()
        {
            DshCore.EnsureAppDataDir();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("== 环境与弹窗体检 ==");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();
            try { sb.AppendLine(PatchGuard.CheckPatches()); } catch (Exception e) { sb.AppendLine("CheckPatches 异常: " + e.Message); }
            sb.AppendLine();
            try { sb.AppendLine(PatchGuard.TerminalCheck()); } catch (Exception e) { sb.AppendLine("TerminalCheck 异常: " + e.Message); }
            sb.AppendLine();
            try { sb.AppendLine(DshBoot.StartupChainCheck()); } catch (Exception e) { sb.AppendLine("StartupChainCheck 异常: " + e.Message); }

            string report = sb.ToString();
            string path = System.IO.Path.Combine(DshCore.AppDataDir, "envcheck-report.txt");
            try { System.IO.File.WriteAllText(path, report, new System.Text.UTF8Encoding(false)); } catch { }
            try
            {
                using (var w = new System.IO.StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)))
                {
                    w.Write(report);
                    w.Flush();
                }
            }
            catch { }
            Console.Error.WriteLine("REPORT_FILE=" + path);
            return 0;
        }

        // 配置安全原语功能自测（--configtest）
        // 只在临时目录里读写，绝不触碰真实的 ~/.dsh 配置（第 7 项只读体检）。
        private static int RunConfigTest()
        {
            var sb = new System.Text.StringBuilder();
            int pass = 0, fail = 0;
            sb.AppendLine("== 配置安全原语自测 ==");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            string tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "bffr-configtest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(tmpDir);

            Action<string, bool> check = delegate(string name, bool ok)
            {
                if (ok) { pass++; sb.AppendLine("[OK]   " + name); }
                else { fail++; sb.AppendLine("[FAIL] " + name); }
            };

            try
            {
                string err;
                // ---- 1) JSON 合法性校验 ----
                check("JSON: 合法对象", SafeConfig.JsonWellFormed("{\"a\":1,\"b\":[1,2,{\"c\":null}]}", out err));
                check("JSON: 带 BOM", SafeConfig.JsonWellFormed("\uFEFF{\"a\":1}", out err));
                check("JSON: 截断的 bundles 片段应判非法",
                    !SafeConfig.JsonWellFormed("      \"bundles\": [\r\n        \"x\"\r\n      ],", out err));
                check("JSON: 尾部多余内容应判非法", !SafeConfig.JsonWellFormed("{\"a\":1} trailing", out err));
                check("JSON: 两个对象拼接应判非法", !SafeConfig.JsonWellFormed("{}{}", out err));
                check("JSON: 空内容应判非法", !SafeConfig.JsonWellFormed("", out err));
                check("JSON: 尾随逗号应判非法", !SafeConfig.JsonWellFormed("{\"a\":1,}", out err));

                // ---- 2) YAML 结构校验 ----
                string y1 = "# --- dsh-skin managed ---\r\n- id: a\r\n  disabled: false\r\n# --- end ---\r\n";
                check("YAML: 正常补丁文件", SafeConfig.YamlLooksLikeSequence(y1, out err));
                check("YAML: 注释+空数组模板", SafeConfig.YamlLooksLikeSequence("# 说明\r\n[]\r\n", out err));
                check("YAML: 同一块重复应判非法",
                    !SafeConfig.YamlLooksLikeSequence("- id: a\r\n  disabled: false\r\n- id: a\r\n  disabled: false\r\n", out err));
                check("YAML: 顶层非序列行应判非法",
                    !SafeConfig.YamlLooksLikeSequence("localCfg: 7\r\nlocalSteps: 12\r\n", out err));

                // ---- 3) 原子写入 ----
                string f = System.IO.Path.Combine(tmpDir, "cfg.json");
                SafeConfig.AtomicWriteText(f, "{\"v\":1}");
                check("原子写: 首次写入内容正确", System.IO.File.ReadAllText(f) == "{\"v\":1}");
                SafeConfig.AtomicWriteText(f, "{\"v\":2}");
                check("原子写: 覆盖后内容正确", System.IO.File.ReadAllText(f) == "{\"v\":2}");
                check("原子写: 覆盖时留有带时间戳备份",
                    System.IO.Directory.GetFiles(tmpDir, "cfg.json.bak-*").Length >= 1);
                check("原子写: 未残留临时文件",
                    System.IO.Directory.GetFiles(tmpDir, "*.tmp-*").Length == 0);

                // ---- 4) 备份不覆盖 ----
                string b1 = SafeConfig.BackupFile(f);
                string b2 = SafeConfig.BackupFile(f);
                check("备份: 同一秒内两次备份互不覆盖", b1 != null && b2 != null && b1 != b2);

                // ---- 5) 写后校验失败要能回滚 ----
                string g = System.IO.Path.Combine(tmpDir, "rollback.json");
                SafeConfig.AtomicWriteText(g, "{\"good\":true}");
                string before = System.IO.File.ReadAllText(g);
                bool threw = false;
                try
                {
                    SafeConfig.AtomicWriteText(g, "{\"bad\": tru");
                    SafeConfig.VerifyAfterWrite(g, true, false);
                }
                catch { threw = true; }
                check("写后校验: 非法 JSON 会抛错", threw);
                check("写后校验: 已回滚到上一个合法版本", System.IO.File.ReadAllText(g) == before);

                // ---- 6) 快照 ----
                string snap = SafeConfig.Snapshot("自测（--configtest）");
                check("快照: 生成快照目录与 MANIFEST",
                    snap != null && System.IO.File.Exists(System.IO.Path.Combine(snap, "MANIFEST.txt")));
                check("快照: 能列出快照", SafeConfig.ListSnapshots().Count >= 1);

                // ---- 7) 真实配置体检（只读，不写盘） ----
                string heal = SafeConfig.HealAfterKill(null);
                check("真实配置: 关键文件全部合法（若有损坏会在此列出）", heal.Length == 0);
                if (heal.Length > 0) sb.AppendLine(heal);
            }
            finally
            {
                try { System.IO.Directory.Delete(tmpDir, true); } catch { }
            }

            sb.AppendLine();
            sb.AppendLine("通过 " + pass + " / 失败 " + fail);
            string report = sb.ToString();
            string reportPath = System.IO.Path.Combine(DshCore.AppDataDir, "configtest-report.txt");
            try
            {
                DshCore.EnsureAppDataDir();
                System.IO.File.WriteAllText(reportPath, report, new System.Text.UTF8Encoding(false));
            }
            catch { }
            try
            {
                using (var w = new System.IO.StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)))
                {
                    w.Write(report);
                    w.Flush();
                }
            }
            catch { }
            Console.Error.WriteLine("REPORT_FILE=" + reportPath);
            return fail == 0 ? 0 : 2;
        }

        // 装后体检（v4.5 · A2/A3）：日志来源全景 + 进程退出判据
        // 为什么单独一个入口：旧版「排查崩溃插件」只读救星自己那两份日志，
        // 凡是被桌面图标/可靠启动器/工坊桌面端拉起的实例，它都读不到 ⇒ 永远「未能定位」。
        // 这一节先把"该读的日志读到没读到"如实摊开，后面的根因分类都依赖它。
        private static int RunPluginCheck()
        {
            DshCore.EnsureAppDataDir();
            string report;
            try
            {
                report = PluginDiag.PanoramaReport() + "\r\n"
                       + PluginDiag.RootCauseReport() + "\r\n"
                       + PluginDiag.LivenessReport() + "\r\n"
                       + PluginDiag.AlignmentReport() + "\r\n"
                       + PluginDiag.DuplicateReport() + "\r\n"
                       + PluginDiag.AllPatchLayersReport() + "\r\n"
                       + PluginDiag.SkinSystemReport() + "\r\n"
                       + PluginDiag.LayerReport(null) + "\r\n"
                       + PluginDiag.ReservedPortReport() + "\r\n"
                       + PluginDiag.RollbackAdvice() + "\r\n"
                       + PluginDiag.SessionHealthReport() + "\r\n"
                       + PluginDiag.RecheckKnownFixes() + "\r\n"
                       + PluginDiag.ModelPathReport() + "\r\n"
                       + PluginDiag.ExternalToolsHint();
            }
            catch (Exception e) { report = "装后体检异常：" + e.GetType().Name + ": " + e.Message; }

            string reportPath = System.IO.Path.Combine(DshCore.AppDataDir, "plugincheck-report.txt");
            try { System.IO.File.WriteAllText(reportPath, report, new System.Text.UTF8Encoding(false)); } catch { }
            try
            {
                using (var writer = new System.IO.StreamWriter(System.Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)))
                {
                    writer.Write(report);
                    writer.Flush();
                }
            }
            catch { }
            Console.Error.WriteLine("REPORT_FILE=" + reportPath);
            return 0;
        }

        // CLI 自检：探测、诊断、写入报告并退出（供无 GUI 环境验证）
        private static int RunSelfTest()        {            DshCore.EnsureAppDataDir();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("== 大肥鱼救星 v" + DshCore.AppVersion + " 自检 ==");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            DshCore.LaunchSpec ls = DshCore.DiscoverLaunch();
            sb.AppendLine("-- 启动配置 --");
            if (ls == null || ls.BinJs == null)
            {
                sb.AppendLine("[FAIL] 未发现 dsh 入口");
            }
            else
            {
                sb.AppendLine("[OK]   bin.js : " + ls.BinJs);
                sb.AppendLine("[OK]   node   : " + ls.Node);
                sb.AppendLine("[OK]   来源   : " + ls.Source);
                sb.AppendLine("[OK]   URL    : " + DshCore.ActiveRootUrl);
            }
            sb.AppendLine();

            sb.AppendLine("-- 服务状态 --");
            ServiceState st = DshCore.CheckState();
            sb.AppendLine("监听 " + DshCore.ActivePort + " : " + st.Listens);
            sb.AppendLine("HTTP 响应    : " + st.HttpResponds);
            sb.AppendLine("dsh 进程     : " + (st.HasDshProcess ? string.Join(",", st.DshPids) : "无"));
            sb.AppendLine(DshCore.ActivePort + " 占用者 : " + (st.OccupierPids.Length > 0 ? string.Join(",", st.OccupierPids) : "无"));
            sb.AppendLine();

            sb.AppendLine("-- 诊断 --");
            DiagItem[] items = DshCore.RunDiagnostics();
            int fails = 0;
            foreach (DiagItem it in items)
            {
                sb.AppendLine((it.Ok ? "[OK]   " : "[FAIL] ") + it.Text.TrimEnd());
                if (!it.Ok) fails++;
            }
            sb.AppendLine();
            sb.AppendLine("失败项: " + fails + " / " + items.Length);

            string report = sb.ToString();
            // GUI 子系统 exe 无有效控制台：始终落盘报告文件
            string reportPath = System.IO.Path.Combine(DshCore.AppDataDir, "selftest-report.txt");
            try
            {
                System.IO.File.WriteAllText(reportPath, report, new System.Text.UTF8Encoding(false));
            }
            catch { }
            // 尽力输出（在 Start-Process 重定向下可能失败，忽略）
            try
            {
                using (var writer = new System.IO.StreamWriter(System.Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)))
                {
                    writer.Write(report);
                    writer.Flush();
                }
            }
            catch { }
            Console.Error.WriteLine("REPORT_FILE=" + reportPath);
            return fails == 0 ? 0 : 2;
        }
    }
}

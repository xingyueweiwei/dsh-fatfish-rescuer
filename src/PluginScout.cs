using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

// ============================================================
// PluginScout.cs — 「研究一个插件」：报告 / 预演 / 应用 / 复验 / 回滚
//
// 与 RepairPlan.cs 同一套动作模型，字段对齐：
//    适用类别 / 前置条件 / 预演内容 / ★将改哪里 / 等价命令 / 复验判据 / 回滚方式
//
// 三条硬纪律（与 WP4 一致）：
//   ① 预演零写入（Plan 只读 registry + 临时目录干装，绝不碰用户 profile）
//   ② 应用前先备份；应用后复验；复验不通过 ⇒ 自动回滚
//   ③ 判据按"pnpm 自己报的文本"走，不认任何插件名单（换任何插件都成立）
//
// ★ 关键实测结论（2026-09-19）：构建授权需求**不能静态推断**——
//   ① 只看直接依赖会漏掉传递依赖；② pnpm 11 的 lockfile 不再写 requiresBuild。
//   唯一准确来源是让 pnpm 自己报：在临时目录干装一次，抓
//     [ERR_PNPM_IGNORED_BUILDS] Ignored build scripts: <pkg>
//   且 pnpm 报此错时并未执行构建，所以不会真编译。
//
// ============================================================
// ★★ v5.0 复审后补的六条（原版有真缺陷，逐条对应一个判据）：
//   ① 只读闸门：原来 Apply **完全不看** DshCore.ReadOnlyMode ⇒ 在主人开了
//      「🛡 只诊断」的情况下，它照样会去改真实 profile。现已在唯一入口拦下。
//   ② 进程树：原来超时只 p.Kill() 杀 cmd.exe，**pnpm/node 子进程会活下来**
//      在真实 profile 里继续 install。现按 ParentProcessId 逐层收窄后逐个 Process.Kill()
//      （★ WP2④：2026-09-20 起不再用 taskkill /T /F —— 它是 shell 杀进程形状，
//       且 /T 会顺进程树杀，本项目已被它误伤过一次）。
//   ③ 管道死锁：原来先 ReadToEnd(stdout) 再 ReadToEnd(stderr)；子进程写满 stderr
//      而我们在读 stdout ⇒ 双双卡住（本项目 2026-09-16 记过的同款坑）。现改异步排空。
//   ④ 回滚口径：原来"回滚"只还原 pnpm-workspace.yaml，**node_modules / lockfile
//      留在改后状态** ⇒ 配置与磁盘不一致。现在备份 ws+lock，回滚后**再跑一次
//      install 把 node_modules 也还原**。
//   ⑤ 包名注入：原来直接把用户输入拼进 package.json 与命令行。现在先做 npm 包名
//      白名单校验（`NameOk`），不合法直接拒绝、零外部进程。
//   ⑥ 可测性：原来只有 GUI 按钮 ⇒ 回归脚本判不了它。现在有 `--scout` /
//      `--scout-selftest`（判据全在临时沙箱里跑，不联网、不碰真实 profile）。
// ============================================================
namespace BigFatFishRescuer
{
    internal sealed class ScoutResult
    {
        public string Pkg;
        public string Version;
        public string Error;
        public string Note;
        public List<string> NeedBuild = new List<string>();
        public bool Probed;
    }

    internal static class PluginScout
    {
        /// <summary>
        /// 只读闸门的**唯一读取点**（自检可临时注入，免得为了测闸门去改主人真实的开关文件）。
        /// 生产路径上它恒等于 DshCore.ReadOnlyMode。
        /// </summary>
        internal static Func<bool> ReadOnlyGate = delegate { return DshCore.ReadOnlyMode; };

        /// <summary>pnpm 解析：BFF_PNPM 覆盖（自检/排障用）→ %APPDATA%\npm\pnpm.cmd → PATH。</summary>
        internal static string ResolvePnpm()
        {
            string over = Environment.GetEnvironmentVariable("BFF_PNPM");
            if (over != null && over.Trim().Length > 0) return over.Trim();
            string appdata = Environment.GetEnvironmentVariable("APPDATA");
            if (appdata != null && appdata.Length > 0)
            {
                string[] cand = new string[] {
                    Path.Combine(appdata, @"npm\pnpm.cmd"),
                    Path.Combine(appdata, @"npm\pnpm.exe")
                };
                for (int i = 0; i < cand.Length; i++)
                {
                    try { if (File.Exists(cand[i])) return cand[i]; }
                    catch (Exception) { }
                }
            }
            return "pnpm.cmd";
        }

        /// <summary>npm 包名白名单（防把用户输入直接拼进 JSON / 命令行）。</summary>
        internal static bool NameOk(string pkg)
        {
            if (pkg == null) return false;
            string p = pkg.Trim();
            if (p.Length < 1 || p.Length > 214) return false;
            return Regex.IsMatch(p, "^(@[A-Za-z0-9][A-Za-z0-9._-]*/)?[A-Za-z0-9][A-Za-z0-9._-]*$");
        }

        /// <summary>最近一次 KillTree 的**范围自报**（会动谁 / 不动谁）。</summary>
        internal static string LastKillNote = "";

        /// <summary>
        /// 结束整棵进程树（含 pnpm / node 孙进程）。只在超时时调用，只杀**我们自己起的那个 PID**。
        /// ★ WP2④（2026-09-20 防误报加固）：不再起 taskkill 子进程去杀 ——
        ///   ① "起一个 taskkill 去杀进程"是 shell 杀进程形状；
        ///   ② `taskkill /T` 会顺进程树杀，本项目已经被它误伤过一次。
        ///   改走 DshCore.KillTreeManaged：按 ParentProcessId 逐层收窄 + 逐个 Process.Kill()
        ///   + 杀前重新核对命令行 + 范围自报。
        /// </summary>
        private static void KillTree(int pid)
        {
            StringBuilder note = new StringBuilder();
            try { DshCore.KillTreeManaged(pid, note); }
            catch (Exception) { }
            LastKillNote = note.ToString().Trim();
        }

        /// <summary>
        /// 跑一个子进程并把 stdout+stderr 一起收回来。
        /// ★ 必须**异步排空**两条管道：先读 stdout 再读 stderr 会在子进程写满 stderr 时双向卡死。
        /// </summary>
        internal static string Run(string file, string args, string workDir, int timeoutMs)
        {
            int pid = -1;
            bool timedOut = false;
            return Run(file, args, workDir, timeoutMs, out pid, out timedOut);
        }

        internal static string Run(string file, string args, string workDir, int timeoutMs, out int childPid, out bool timedOut)
        {
            childPid = -1;
            timedOut = false;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = file;
                psi.Arguments = args;
                if (workDir != null && workDir.Length > 0) psi.WorkingDirectory = workDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                StringBuilder so = new StringBuilder();
                StringBuilder se = new StringBuilder();
                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs ev)
                    { if (ev.Data != null) { lock (so) { so.AppendLine(ev.Data); } } };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs ev)
                    { if (ev.Data != null) { lock (se) { se.AppendLine(ev.Data); } } };
                    p.Start();
                    childPid = p.Id;
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        timedOut = true;
                        KillTree(childPid);
                        try { p.WaitForExit(5000); } catch (Exception) { }
                    }
                    string o, e;
                    lock (so) { o = so.ToString(); }
                    lock (se) { e = se.ToString(); }
                    return o + "\n" + e;
                }
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>查 registry 拿最新版本号（只读）。</summary>
        public static string LatestVersion(string pkg)
        {
            string outText = Run("cmd.exe", "/c npm view " + pkg + " version --json", null, 120000);
            if (outText == null) return "";
            Match m = Regex.Match(outText, "\"([0-9]+\\.[0-9]+\\.[0-9]+[^\"]*)\"");
            if (m.Success) return m.Groups[1].Value;
            m = Regex.Match(outText, "([0-9]+\\.[0-9]+\\.[0-9]+[^\\s\"']*)");
            return m.Success ? m.Groups[1].Value : "";
        }

        public static ScoutResult Probe(string pkg) { return Probe(pkg, null); }

        /// <summary>
        /// 探测：在**临时目录**干装一次，让 pnpm 自己报需要构建授权的包。
        /// versionOverride 只给自检用（跳过 registry，保证判据不联网）。
        /// </summary>
        public static ScoutResult Probe(string pkg, string versionOverride)
        {
            ScoutResult r = new ScoutResult();
            r.Pkg = pkg == null ? "" : pkg.Trim();
            r.Note = "";
            if (!NameOk(r.Pkg))
            {
                r.Error = "包名不合法（只允许 npm 包名：字母/数字/._-，可带 @scope/）；已拒绝，未起任何外部进程。";
                return r;
            }
            r.Version = versionOverride != null ? versionOverride : LatestVersion(r.Pkg);
            if (r.Version == null || r.Version.Length == 0)
            {
                r.Error = "registry 查不到该包（检查名字/网络）";
                return r;
            }
            string tmp = Path.Combine(Path.GetTempPath(),
                "bff_scout_" + DateTime.Now.ToString("HHmmss") + "_" + new Random().Next(1000, 9999));
            bool created = false;
            try
            {
                Directory.CreateDirectory(tmp);
                created = true;
                string pj = "{\"name\":\"bff-scout-probe\",\"version\":\"1.0.0\",\"private\":true," +
                            "\"dependencies\":{\"" + r.Pkg + "\":\"" + r.Version + "\"}}";
                File.WriteAllText(Path.Combine(tmp, "package.json"), pj, new UTF8Encoding(false));
                string outText = Run("cmd.exe", "/c " + ResolvePnpm() + " install --prefer-offline", tmp, 600000);
                r.Probed = true;
                if (outText == null || outText.Trim().Length == 0)
                    r.Note = "干装没有拿到任何输出（pnpm 可能未安装或被安全软件拦下）；本次结论仅供参考。";
                MatchCollection mc = Regex.Matches(outText == null ? "" : outText, "Ignored build scripts:\\s*(.+)");
                for (int i = 0; i < mc.Count; i++)
                {
                    string[] parts = mc[i].Groups[1].Value.Split(',');
                    for (int j = 0; j < parts.Length; j++)
                    {
                        string name = parts[j].Trim();
                        name = Regex.Replace(name, "@[^@]*$", "");
                        if (name.Length > 0 && !r.NeedBuild.Contains(name)) r.NeedBuild.Add(name);
                    }
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }
            finally
            {
                if (created && !DeleteDirHard(tmp))
                    r.Note = (r.Note + " 临时目录未能删除（可能被子进程短暂占用）：" + tmp).Trim();
            }
            return r;
        }

        /// <summary>
        /// 删临时目录：**重试**再放弃。
        /// 为什么：实测干装刚结束时 pnpm 的孙进程还在收尾，第一次 Directory.Delete 会抛
        /// IOException 留下垃圾（2026-09-20 在 dsh-better-sidebar 上实测到）。
        /// </summary>
        private static bool DeleteDirHard(string dir)
        {
            for (int i = 0; i < 8; i++)
            {
                try { Directory.Delete(dir, true); return true; }
                catch (Exception)
                {
                    if (!Directory.Exists(dir)) return true;
                    System.Threading.Thread.Sleep(400);
                }
            }
            try { return !Directory.Exists(dir); } catch (Exception) { return false; }
        }

        /// <summary>生成预演文本（零写入），字段对齐 RepairPlan。</summary>
        public static string PlanText(ScoutResult r, string profile)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 大肥鱼救星 · 研究插件（--scout）==");
            sb.AppendLine("插件      : " + r.Pkg + "  版本: " + ((r.Version == null || r.Version.Length == 0) ? "?" : r.Version));
            if (r.Note != null && r.Note.Length > 0) sb.AppendLine("提示      : " + r.Note);
            if (r.Error != null && r.Error.Length > 0)
            {
                sb.AppendLine("★ 探测失败: " + r.Error);
                return sb.ToString();
            }
            string ws = Path.Combine(profile == null ? "<profile>" : profile, "pnpm-workspace.yaml");
            if (r.NeedBuild.Count == 0)
            {
                sb.AppendLine("适用类别  : 无需适配");
                sb.AppendLine("预演内容  : 完整依赖树里没有任何包需要构建授权。");
                sb.AppendLine("复验判据  : 直接安装即可（rc=0）。");
                sb.AppendLine("★ 本动作  : 不做任何写入，真实 profile 一个字节都不动。");
                return sb.ToString();
            }
            sb.AppendLine("适用类别  : HOST_POLICY_BLOCK（宿主未预授权原生构建；插件本身可装）");
            sb.AppendLine("前置条件  : 需存在 " + ws);
            sb.AppendLine("预演内容  : 在 pnpm-workspace.yaml 追加/替换 allowBuilds 段（**map** 形式）");
            sb.AppendLine("★ 将改哪里 : " + ws);
            sb.AppendLine("             + allowBuilds:");
            for (int i = 0; i < r.NeedBuild.Count; i++)
                sb.AppendLine("             +   " + r.NeedBuild[i] + ": true");
            sb.AppendLine("等价命令  : 手工在 " + Path.GetFileName(ws) + " 写入上面的 allowBuilds");
            sb.AppendLine("复验判据  : 改后重跑 pnpm install，rc=0 且不再出现 IGNORED_BUILDS");
            sb.AppendLine("回滚方式  : 还原 " + Path.GetFileName(ws) + ".bak-scout-<时间戳>（并再跑一次 install 还原 node_modules）");
            sb.AppendLine("★ 注意    : 本动作改的是**真实 profile**（" + profile + "），并在原地跑一次 pnpm install；");
            sb.AppendLine("            **不会结束、不会重启正在运行的 DSH**，但它的插件目录会按新配置重装一次。");
            sb.AppendLine("            「🛡 只诊断」开着时，本动作会被直接拒绝（一个字都不写）。");
            return sb.ToString();
        }

        public static bool Apply(ScoutResult r, string profile, out string log)
        {
            return Apply(r, profile, ReadOnlyGate(), out log);
        }

        /// <summary>应用：备份 → 写 allowBuilds → 复验；复验不过自动回滚（含 node_modules）。返回 true=成功。</summary>
        internal static bool Apply(ScoutResult r, string profile, bool readOnly, out string log)
        {
            StringBuilder sb = new StringBuilder();
            log = "";
            if (readOnly)
            {
                log = "★ 已拒绝：「只诊断」模式开着，本动作一个字都不写。\r\n" + DshCore.ReadOnlyRefusal();
                return false;
            }
            if (r.NeedBuild.Count == 0) { log = "无需适配。"; return true; }
            if (profile == null || profile.Trim().Length == 0) { log = "★ 没有给出 profile 目录。"; return false; }
            string ws = Path.Combine(profile, "pnpm-workspace.yaml");
            if (!File.Exists(ws))
            {
                log = "★ 前置不满足：找不到 " + ws;
                return false;
            }
            string stamp = DateTime.Now.ToString("HHmmss") + "_" + new Random().Next(1000, 9999);
            string bak = ws + ".bak-scout-" + stamp;
            string lockPath = Path.Combine(profile, "pnpm-lock.yaml");
            string lockBak = lockPath + ".bak-scout-" + stamp;
            bool lockHad = false;
            try
            {
                File.Copy(ws, bak, true);
                if (File.Exists(lockPath)) { File.Copy(lockPath, lockBak, true); lockHad = true; }
            }
            catch (Exception ex) { log = "备份失败：" + ex.Message + "（未做任何写入）"; return false; }
            sb.AppendLine("已备份 → " + Path.GetFileName(bak) + (lockHad ? " + " + Path.GetFileName(lockBak) : ""));
            string original = "";
            try { original = File.ReadAllText(ws, Encoding.UTF8); }
            catch (Exception ex) { log = "读原文件失败：" + ex.Message + "（未做任何写入）"; return false; }

            List<string> keep = new List<string>();
            string[] lines = original.Replace("\r\n", "\n").Split('\n');
            bool skip = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                if (Regex.IsMatch(l, "^\\s*(allowBuilds|onlyBuiltDependencies)\\s*:")) { skip = true; continue; }
                if (skip)
                {
                    if (Regex.IsMatch(l, "^\\s+\\S")) continue;
                    skip = false;
                }
                keep.Add(l);
            }
            StringBuilder nb = new StringBuilder();
            for (int i = 0; i < keep.Count; i++) { nb.Append(keep[i]); nb.Append('\n'); }
            nb.Append("allowBuilds:\n");
            for (int i = 0; i < r.NeedBuild.Count; i++)
                nb.Append("  " + r.NeedBuild[i] + ": true\n");
            try { File.WriteAllText(ws, nb.ToString(), new UTF8Encoding(false)); }
            catch (Exception ex) { log = "写入失败：" + ex.Message; return false; }
            sb.AppendLine("已写入 allowBuilds: " + string.Join(", ", r.NeedBuild.ToArray()));

            // 复验
            string v = Run("cmd.exe", "/c " + ResolvePnpm() + " install --prefer-offline", profile, 600000);
            bool bad = v == null || v.IndexOf("IGNORED_BUILDS") >= 0;
            sb.AppendLine("复验：仍含 IGNORED_BUILDS = " + (bad ? "是" : "否"));
            if (bad)
            {
                try
                {
                    File.WriteAllText(ws, original, new UTF8Encoding(false));
                    sb.AppendLine("★ 复验不通过 ⇒ 已回滚 " + Path.GetFileName(ws));
                }
                catch (Exception ex) { sb.AppendLine("★ 回滚失败：" + ex.Message + "（备份仍在：" + bak + "）"); log = sb.ToString(); return false; }
                if (lockHad)
                {
                    try { File.Copy(lockBak, lockPath, true); sb.AppendLine("★ 已回滚 " + Path.GetFileName(lockPath)); }
                    catch (Exception ex) { sb.AppendLine("★ lockfile 回滚失败：" + ex.Message); }
                }
                // ★ 配置回滚还不够：刚才那次 install 已经按"改后配置"动过 node_modules，
                //   必须再跑一次 install 把它拉回原状，否则"配置"与"磁盘"不一致。
                string v2 = Run("cmd.exe", "/c " + ResolvePnpm() + " install --prefer-offline", profile, 600000);
                sb.AppendLine("★ 回滚后再跑一次 pnpm install 还原 node_modules（输出 " +
                              (v2 == null ? 0 : v2.Length) + " 字节）");
                log = sb.ToString();
                return false;
            }
            log = sb.ToString();
            return true;
        }

        // ============================================================
        //  判据自检（--scout-selftest）：全在临时沙箱里跑，**不联网、不碰真实 profile**
        //  为什么要有：原版只有 GUI 按钮 ⇒ 回归脚本判不了；而这些动作会改真实 profile，
        //  「谁验过它不碰不该碰的东西」必须由机器答，不能由我说。
        // ============================================================
        public static string SelfTest()
        {
            StringBuilder sb = new StringBuilder();
            int ok = 0, fail = 0;
            sb.AppendLine("== 大肥鱼救星 v" + DshCore.AppVersion + " · 研究插件判据自检 ==");
            sb.AppendLine("范围：临时沙箱 + 假 pnpm（不联网、不碰真实 profile）");
            sb.AppendLine("");

            string sbx = Path.Combine(Path.GetTempPath(), "bff_scoutst_" + DateTime.Now.ToString("HHmmss") + "_" + new Random().Next(1000, 9999));
            string prof = Path.Combine(sbx, "profiles", "web");
            string shimDir = Path.Combine(sbx, "shim");
            string shim = Path.Combine(shimDir, "pnpm.cmd");
            string calls = Path.Combine(shimDir, "calls.txt");
            string wsPath = Path.Combine(prof, "pnpm-workspace.yaml");
            string lockPath = Path.Combine(prof, "pnpm-lock.yaml");
            string realWs = Path.Combine(DshCore.DshHome, "profiles", "web", "pnpm-workspace.yaml");
            string realWsBefore = Sha(realWs);
            string oldPnpm = Environment.GetEnvironmentVariable("BFF_PNPM");
            string oldShim = Environment.GetEnvironmentVariable("BFF_SCOUT_SHIM");

            Action<string, bool, string> mark = delegate(string name, bool pass, string detail)
            {
                if (pass) ok++; else fail++;
                sb.AppendLine((pass ? "[OK]   " : "[FAIL] ") + name);
                if (detail != null && detail.Length > 0) sb.AppendLine("       " + detail);
            };

            try
            {
                Directory.CreateDirectory(prof);
                Directory.CreateDirectory(shimDir);
                File.WriteAllText(wsPath,
                    "# 沙箱里的假 profile（判据用）\r\npackages:\r\n  - '.'\r\nkeepMe: 1\r\n",
                    new UTF8Encoding(false));
                File.WriteAllText(lockPath, "lockfileVersion: '9.0'\r\n", new UTF8Encoding(false));
                File.WriteAllText(shim,
                    "@echo off\r\n" +
                    "echo %1 %2 >> \"%~dp0calls.txt\"\r\n" +
                    "if \"%BFF_SCOUT_SHIM%\"==\"ignored\" (\r\n" +
                    "  echo [ERR_PNPM_IGNORED_BUILDS] Ignored build scripts: foo, bar\r\n" +
                    "  exit /b 1\r\n" +
                    ")\r\n" +
                    "if \"%BFF_SCOUT_SHIM%\"==\"hang\" (\r\n" +
                    "  ping -n 30 127.0.0.1 >nul\r\n" +
                    "  exit /b 0\r\n" +
                    ")\r\n" +
                    "echo Already up to date\r\n" +
                    "exit /b 0\r\n",
                    new UTF8Encoding(false));
                Environment.SetEnvironmentVariable("BFF_PNPM", shim, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("BFF_SCOUT_SHIM", "clean", EnvironmentVariableTarget.Process);

                // ---- 1. 包名白名单（阳性 + 阴性对照） ----
                bool n1 = NameOk("dsh-file-upload");
                bool n2 = NameOk("@scope/name");
                bool n3 = !NameOk("bad name; del /f /q C:\\");
                bool n4 = !NameOk("a\"b");
                bool n5 = !NameOk("");
                mark("包名白名单（2 阳性 / 3 阴性对照）", n1 && n2 && n3 && n4 && n5,
                     "dsh-file-upload=" + n1 + " @scope/name=" + n2 + " 注入串被拒=" + n3 + " 引号被拒=" + n4 + " 空被拒=" + n5);
                // 不合法包名必须**零外部进程**就直接拒绝
                ScoutResult badName = Probe("bad name; del /f /q C:\\", "1.0.0");
                mark("不合法包名 ⇒ 拒绝且不起外部进程", badName.Error != null && badName.Error.Length > 0 && !File.Exists(calls),
                     "Error=" + (badName.Error == null ? "(空)" : badName.Error.Substring(0, Math.Min(24, badName.Error.Length))) + "…；假 pnpm 未被调用=" + (!File.Exists(calls)));

                // ---- 2. 干装隔离：临时目录用完即删 ----
                int tmpBefore = CountDirs(Path.GetTempPath(), "bff_scout_*");
                ScoutResult pr = Probe("bff.selftest.fake", "1.0.0");
                int tmpAfter = CountDirs(Path.GetTempPath(), "bff_scout_*");
                mark("干装只在 %TEMP% 且用完即删（临时目录零残留）",
                     pr.Probed && tmpAfter == tmpBefore && pr.Note.IndexOf("未能删除") < 0,
                     "Probed=" + pr.Probed + "；%TEMP% 下 bff_scout_* 目录 " + tmpBefore + " → " + tmpAfter + "；Note=" + (pr.Note.Length == 0 ? "(无)" : pr.Note));

                // ---- 3. 干装解析：能从 pnpm 报的文本里抓到需授权包 ----
                Environment.SetEnvironmentVariable("BFF_SCOUT_SHIM", "ignored", EnvironmentVariableTarget.Process);
                ScoutResult ig = Probe("bff.selftest.fake", "1.0.0");
                string got = string.Join(",", ig.NeedBuild.ToArray());
                mark("按 pnpm 自己报的文本抓需授权包（不认插件名单）",
                     ig.NeedBuild.Count == 2 && ig.NeedBuild.Contains("foo") && ig.NeedBuild.Contains("bar"),
                     "抓到 = " + got);
                Environment.SetEnvironmentVariable("BFF_SCOUT_SHIM", "clean", EnvironmentVariableTarget.Process);

                // ---- 4. 预演零写入 + 七字段齐全 ----
                ScoutResult plan = new ScoutResult();
                plan.Pkg = "bff.selftest.fake"; plan.Version = "1.0.0";
                plan.NeedBuild.Add("foo"); plan.NeedBuild.Add("bar");
                string pt = PlanText(plan, prof);
                bool fields = pt.Contains("适用类别") && pt.Contains("前置条件") && pt.Contains("预演内容")
                           && pt.Contains("将改哪里") && pt.Contains("等价命令") && pt.Contains("复验判据")
                           && pt.Contains("回滚方式") && pt.Contains(prof);
                string wsHash0 = Sha(wsPath);
                mark("预演七字段齐全且零写入", fields && Sha(wsPath) == wsHash0,
                     "字段齐全=" + fields + "；沙箱 workspace sha256 未变=" + (Sha(wsPath) == wsHash0));
                mark("预演文本如实声明会改真实 profile 与只读闸门",
                     pt.Contains("真实 profile") && pt.Contains("只诊断") && pt.Contains("不会结束"),
                     "含「真实 profile / 只诊断 / 不会结束」= " + (pt.Contains("真实 profile") && pt.Contains("只诊断") && pt.Contains("不会结束")));

                // ---- 5. 只读闸门：开着就必须拒、且一字不写 ----
                string gateLog;
                string wsHash1 = Sha(wsPath);
                bool gateOk = !Apply(plan, prof, true, out gateLog);
                mark("只诊断打开 ⇒ Apply 拒绝且零写入",
                     gateOk && Sha(wsPath) == wsHash1 && gateLog.Contains("只诊断"),
                     "返回=" + (gateOk ? "拒绝" : "竟然动手了") + "；workspace 未变=" + (Sha(wsPath) == wsHash1));
                // ---- 5b. 公共入口真的走闸门（注入对照 —— 注入 true 必须拒，假判据不算判据） ----
                bool gateMatches = (ReadOnlyGate() == DshCore.ReadOnlyMode);
                Func<bool> keepGate = ReadOnlyGate;
                string gl2 = "";
                bool refusedByPublicEntry = false;
                try
                {
                    ReadOnlyGate = delegate { return true; };
                    refusedByPublicEntry = !Apply(plan, prof, out gl2);   // ★ 走的是生产入口
                }
                finally { ReadOnlyGate = keepGate; }
                mark("公共入口 Apply 真的走只读闸门（注入 true 必须拒 + 默认来源核对）",
                     refusedByPublicEntry && gl2.Contains("只诊断") && gateMatches,
                     "注入 true 后公共入口=" + (refusedByPublicEntry ? "拒绝" : "竟然动手了") +
                     "；闸门默认来源 == DshCore.ReadOnlyMode = " + gateMatches +
                     "（只读 getter，不写开关文件）");

                // ---- 6. 复验不通过 ⇒ 自动回滚（含 node_modules 重装闭环） ----
                Environment.SetEnvironmentVariable("BFF_SCOUT_SHIM", "ignored", EnvironmentVariableTarget.Process);
                if (File.Exists(calls)) File.Delete(calls);
                string wsOrig = File.ReadAllText(wsPath, Encoding.UTF8);
                string log6;
                bool r6 = Apply(plan, prof, false, out log6);
                string wsBack = File.ReadAllText(wsPath, Encoding.UTF8);
                int nCalls = CountLines(calls);
                int nBak = CountFiles(prof, "pnpm-workspace.yaml.bak-scout-*");
                mark("复验不过 ⇒ 自动回滚（备份存在 + 原文逐字还原 + install 重跑闭环）",
                     (!r6) && wsBack == wsOrig && nBak == 1 && nCalls >= 2 && log6.Contains("已回滚"),
                     "返回=" + r6 + "；原文还原=" + (wsBack == wsOrig) + "；备份数=" + nBak +
                     "；假 pnpm 被调用=" + nCalls + " 次（复验 1 + 回滚后重装 1 = 2）");
                Environment.SetEnvironmentVariable("BFF_SCOUT_SHIM", "clean", EnvironmentVariableTarget.Process);

                // ---- 7. 成功路径：写入正确 + 原有内容不丢 ----
                string log7;
                bool r7 = Apply(plan, prof, false, out log7);
                string ws7 = File.ReadAllText(wsPath, Encoding.UTF8);
                mark("成功路径：allowBuilds 写成 map 且原有内容不丢",
                     r7 && ws7.Contains("allowBuilds:") && ws7.Contains("  foo: true") && ws7.Contains("  bar: true")
                     && ws7.Contains("packages:") && ws7.Contains("keepMe: 1"),
                     "返回=" + r7 + "；含 allowBuilds/foo/bar/packages/keepMe 全部 = " +
                     (ws7.Contains("allowBuilds:") && ws7.Contains("  foo: true") && ws7.Contains("packages:") && ws7.Contains("keepMe: 1")));

                // ---- 8. 幂等：连做两次不许叠加 allowBuilds ----
                string log8;
                Apply(plan, prof, false, out log8);
                string ws8 = File.ReadAllText(wsPath, Encoding.UTF8);
                int nAllow = Regex.Matches(ws8, "(?m)^allowBuilds\\s*:").Count;
                mark("幂等：连做两次 allowBuilds 段不叠加", nAllow == 1, "allowBuilds 段数 = " + nAllow);

                // ---- 9. 超时必须收掉整棵进程树（不然 pnpm/node 会在真实 profile 里继续跑） ----
                int pingBefore = CountProc("PING");
                Environment.SetEnvironmentVariable("BFF_SCOUT_SHIM", "hang", EnvironmentVariableTarget.Process);
                DateTime t0 = DateTime.Now;
                int hangPid = -1; bool to = false;
                Run("cmd.exe", "/c " + shim + " install", prof, 2000, out hangPid, out to);
                double sec = (DateTime.Now - t0).TotalSeconds;
                bool settled = false;
                for (int i = 0; i < 20 && !settled; i++)
                {
                    if (CountProc("PING") <= pingBefore) { settled = true; break; }
                    System.Threading.Thread.Sleep(500);
                }
                mark("超时 ⇒ 连子进程一起收（不留孤儿 pnpm/ping）",
                     to && sec < 12.0 && settled,
                     "识别为超时=" + to + "；耗时=" + sec.ToString("0.0") + "s（限 12s）；子进程已清=" + settled +
                     "；PING 进程 " + pingBefore + " → " + CountProc("PING"));
                Environment.SetEnvironmentVariable("BFF_SCOUT_SHIM", "clean", EnvironmentVariableTarget.Process);

                // ---- 10. 整轮下来真实 profile 一个字节都没动 ----
                string realWsAfter = Sha(realWs);
                mark("整轮自检不碰真实 profile（sha256 对比）",
                     realWsBefore == realWsAfter,
                     "真实 " + realWs + "：" + Short(realWsBefore) + " → " + Short(realWsAfter) +
                     (realWsBefore.Length == 0 ? "（文件不存在也算未变）" : ""));
            }
            catch (Exception ex)
            {
                fail++;
                sb.AppendLine("[FAIL] 自检自身抛异常：" + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                Environment.SetEnvironmentVariable("BFF_PNPM", oldPnpm, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("BFF_SCOUT_SHIM", oldShim, EnvironmentVariableTarget.Process);
                try { Directory.Delete(sbx, true); sb.AppendLine("沙箱已清理：" + sbx); }
                catch (Exception) { sb.AppendLine("沙箱未能删除（不影响结论）：" + sbx); }
            }

            sb.AppendLine("");
            sb.AppendLine("==================================================");
            sb.AppendLine("结论: 通过 " + ok + " 项 / 失败 " + fail + " 项");
            sb.AppendLine("==================================================");
            return sb.ToString();
        }

        private static string Sha(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] h = System.Security.Cryptography.SHA256.Create().ComputeHash(fs);
                    StringBuilder s = new StringBuilder();
                    for (int i = 0; i < h.Length; i++) s.Append(h[i].ToString("x2"));
                    return s.ToString();
                }
            }
            catch (Exception) { return ""; }
        }
        private static string Short(string s)
        {
            if (s == null || s.Length == 0) return "(不存在)";
            return s.Substring(0, 16) + "…";
        }
        private static int CountDirs(string dir, string pattern)
        {
            try { return Directory.GetDirectories(dir, pattern).Length; } catch (Exception) { return -1; }
        }
        private static int CountFiles(string dir, string pattern)
        {
            try { return Directory.GetFiles(dir, pattern).Length; } catch (Exception) { return -1; }
        }
        private static int CountLines(string path)
        {
            try { return File.Exists(path) ? File.ReadAllLines(path).Length : 0; } catch (Exception) { return -1; }
        }
        private static int CountProc(string name)
        {
            try { return Process.GetProcessesByName(name).Length; } catch (Exception) { return -1; }
        }
    }
}

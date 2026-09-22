using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace BigFatFishRescuer
{
    // ============================================================
    //  补丁固化 · 白屏盲区 · 模型错误码翻译（2026-09-17 新增）
    //
    //  一、为什么需要「固化」
    //    我们给 node_modules 打的补丁（记忆花括号转义、控制台闪窗）
    //    会被 `pnpm install` / 插件升级**静默丢弃**。以前只能"报警 + 一键重打"，
    //    治不了根 —— 因为重打出来的东西下一次安装还是会被冲掉。
    //
    //    pnpm 官方机制能治本：
    //      ① `pnpm patch <包>@<版本> --edit-dir <目录>` 取**上游原件**到临时目录
    //      ② 在临时目录里改（我们做**锚点定点替换**，找不到锚点就不改）
    //      ③ `pnpm patch-commit <目录>` → 生成 `patches/<包>@<版本>.patch`
    //         并自动登记到 `pnpm-workspace.yaml` 的 `patchedDependencies`
    //      ④ 以后每次 `pnpm install`，pnpm **自己**把补丁重新应用一遍
    //    ⇒ 从"记得重打"变成"装也装不掉"。
    //    本机实测（2026-09-17，临时工程 js-yaml@4.3.2）：
    //      生成 patches/js-yaml@4.3.2.patch（2242 字节，标准 diff --git 格式）
    //      + pnpm-workspace.yaml 里出现 patchedDependencies 登记
    //      + 再 `pnpm install` 后 node_modules 里仍带着我们的改动 ⇒ 机制成立。
    //
    //  二、本机两处补丁的命运不同（必须如实区分）
    //      · `dsh-memory-evolve`（转义）装在 **profile 的 pnpm 工程**里
    //        （~/.dsh/profiles/web 有 pnpm-workspace.yaml + node_modules/.pnpm）
    //        ⇒ **可以真固化**。
    //      · `@deepseek-ai/dsh-win32-process`（控制台不闪窗）装在 **npm 全局安装树**
    //        （npm-global/node-v24/node_modules/@deepseek-ai/dsh/node_modules/…）
    //        那棵树**不是 pnpm 工程**（无 package.json/lockfile/.pnpm）
    //        ⇒ pnpm patch 管不到，只能保留「锚点重打 + 体检」这一层。
    //        这是环境事实，不是偷懒 —— 本文件会把它标成 [N/A] 并说明原因。
    // ============================================================
    public static class PatchLock
    {
        // ---------- 声明式补丁清单 ----------

        public class FileEdit
        {
            public string RelPath;   // 相对包目录
            public string Marker;    // 判据串：在岗就应含它
            public string Scope;     // 作用域：只在它**之后**找锚点（防同款行多处命中）
            public string Anchor;    // 定点替换锚点（在 Scope 之后必须唯一命中）
            public string Replacement;
        }

        public class Target
        {
            public string Id;
            public string Title;
            public string PkgName;
            public string PkgDir;        // 包安装目录
            public string ProjectDir;    // 所属工程的根（用于跑 pnpm）
            public string Reason;        // 该补丁为什么存在（给人看）
            public List<FileEdit> Edits = new List<FileEdit>();

            /// <summary>pnpm 工程才能固化；否则只能锚点重打。</summary>
            public bool PnpmCapable
            {
                get { return IsPnpmProject(ProjectDir); }
            }
        }

        private static string ProfileDir()
        {
            string p = Environment.GetEnvironmentVariable("DSH_PROFILE");
            if (string.IsNullOrEmpty(p)) p = "web";
            return Path.Combine(DshCore.DshHome, "profiles", p.Trim());
        }

        /// <summary>本机已知的、会被安装/升级冲掉的补丁清单。</summary>
        public static List<Target> Targets()
        {
            var list = new List<Target>();

            // ① 记忆花括号转义（唯一一类会让 DSH 直接起不来的补丁）
            var me = new Target();
            me.Id = "memory-evolve-brace";
            me.Title = "记忆花括号转义（memory-evolve）";
            me.PkgName = "dsh-memory-evolve";
            me.ProjectDir = ProfileDir();
            me.PkgDir = Path.Combine(ProfileDir(), "node_modules", "dsh-memory-evolve");
            me.Reason = "KEY 记忆里的 {{…}} 字面量会被 dsh-system-prompt 当变量插值并抛错 ⇒ DSH 起不来";
            me.Edits.Add(new FileEdit
            {
                RelPath = Path.Combine("lib", "index.js"),
                Marker = "replaceAll('{{', '{ {')",
                Scope = "export function renderSnapshot(",
                Anchor = "\n  return parts.join('\\n\\n')",
                Replacement = "\n  // Literal {{...}} in memory text must not be read as a prompt variable by\n" +
                              "  // dsh-system-prompt (it throws on unknown references); escape the pair.\n" +
                              "  return parts.map((s) => s.replaceAll('{{', '{ {')).join('\\n\\n')"
            });
            me.Edits.Add(new FileEdit
            {
                RelPath = Path.Combine("lib", "prompts.js"),
                Marker = "replaceAll('{{', '{ {')",
                Scope = "function renderInjectionSnapshot(",
                Anchor = "\n  return lines.join('\\n')",
                Replacement = "\n  // Same escape as renderSnapshot: user rules must reach the model verbatim.\n" +
                              "  return lines.map((line) => line.replaceAll('{{', '{ {')).join('\\n')"
            });
            list.Add(me);

            // ② 控制台不闪窗（改的是 DSH 本体安装树，不是 pnpm 工程）
            var wp = new Target();
            wp.Id = "win32-process-no-window";
            wp.Title = "控制台不闪窗（dsh-win32-process）";
            wp.PkgName = "@deepseek-ai/dsh-win32-process";
            wp.PkgDir = FindWin32ProcessDir();
            wp.ProjectDir = "";
            wp.Reason = "DSH 无控制台，起 powershell 时若不带 CREATE_NO_WINDOW，Windows 会每条命令弹一个窗口";
            wp.Edits.Add(new FileEdit
            {
                RelPath = Path.Combine("lib", "index.js"),
                Marker = "CREATE_NO_WINDOW",
                Anchor = "function createRestrictedProcess(api, options, commandLine, creationFlags, startupInfo, processInfo) {",
                Replacement = "const CREATE_NO_WINDOW = 0x08000000;\r\n" +
                              "function createRestrictedProcess(api, options, commandLine, creationFlags, startupInfo, processInfo) {"
            });
            wp.ProjectDir = ProjectRootOf(wp.PkgDir);
            list.Add(wp);

            return list;
        }

        private static string FindWin32ProcessDir()
        {
            // DSH 本体可能装在几处；逐个试（找不到就返回空，报告里如实标 [?]）
            var roots = new List<string>();
            roots.Add(Path.Combine(DshCore.UserProfile, ".ai-manager", "npm-global", "node-v24", "node_modules", "@deepseek-ai", "dsh"));
            string local = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            roots.Add(Path.Combine(local, "npm", "node_modules", "@deepseek-ai", "dsh"));
            roots.Add(Path.Combine(local, "npm", "node_modules", "@deepseek-ai", "dsh", "node_modules", "@deepseek-ai", "dsh"));
            foreach (var r in roots)
            {
                string p = Path.Combine(r, "node_modules", "@deepseek-ai", "dsh-win32-process");
                if (Directory.Exists(p)) return p;
            }
            return "";
        }

        /// <summary>从包目录往上找最近的、含 node_modules 的那一层，作为"工程根"。</summary>
        private static string ProjectRootOf(string pkgDir)
        {
            if (string.IsNullOrEmpty(pkgDir)) return "";
            var d = new DirectoryInfo(pkgDir);
            while (d != null)
            {
                if (d.Name == "node_modules" && d.Parent != null) return d.Parent.FullName;
                d = d.Parent;
            }
            return "";
        }

        /// <summary>pnpm 工程判据：有 node_modules，且（有 lockfile 或有 .pnpm 或有 workspace 文件）。</summary>
        public static bool IsPnpmProject(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            if (!Directory.Exists(Path.Combine(dir, "node_modules"))) return false;
            return File.Exists(Path.Combine(dir, "pnpm-lock.yaml"))
                || Directory.Exists(Path.Combine(dir, "node_modules", ".pnpm"))
                || File.Exists(Path.Combine(dir, "pnpm-workspace.yaml"));
        }

        // ---------- pnpm 工具链探针 ----------

        public static string PnpmExe()
        {
            var cands = new List<string>();
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            cands.Add(Path.Combine(appData, "npm", "pnpm.cmd"));
            cands.Add(Path.Combine(appData, "npm", "pnpm.exe"));
            cands.Add(Path.Combine(appData, "npm", "pnpm"));
            foreach (var c in cands) if (File.Exists(c)) return c;
            // 退路：PATH 上找
            try
            {
                var psi = new ProcessStartInfo("where.exe", "pnpm");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(4000);
                    foreach (var line in o.Split('\n'))
                    {
                        string s = line.Trim();
                        if (s.Length > 0 && File.Exists(s)) return s;
                    }
                }
            }
            catch { }
            return "";
        }

        private static string _pnpmVer;
        public static string PnpmVersion()
        {
            if (_pnpmVer != null) return _pnpmVer;
            string exe = PnpmExe();
            if (exe.Length == 0) { _pnpmVer = ""; return _pnpmVer; }
            int code; string outp;
            Run(exe, "--version", null, out code, out outp);
            _pnpmVer = code == 0 ? outp.Trim() : "";
            return _pnpmVer;
        }

        private static string Run(string exe, string args, string workDir, out int exitCode, out string output)
        {
            exitCode = -1;
            output = "";
            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                if (!string.IsNullOrEmpty(workDir)) psi.WorkingDirectory = workDir;
                // ★ 2026-09-17 实测教训：github 源依赖的 `pnpm patch` 要去 codeload.github.com 取原件，
                //   本机直连会被 ECONNRESET 重置并**长时间重试（看起来像卡死）**。
                //   有代理就用代理（环境变量里已有就沿用，也可用 DSH_PATCHLOCK_PROXY 显式指定）。
                string proxy = Environment.GetEnvironmentVariable("DSH_PATCHLOCK_PROXY");
                if (string.IsNullOrEmpty(proxy)) proxy = Environment.GetEnvironmentVariable("HTTPS_PROXY");
                if (string.IsNullOrEmpty(proxy)) proxy = Environment.GetEnvironmentVariable("https_proxy");
                if (!string.IsNullOrEmpty(proxy))
                {
                    psi.EnvironmentVariables["HTTPS_PROXY"] = proxy;
                    psi.EnvironmentVariables["HTTP_PROXY"] = proxy;
                    psi.EnvironmentVariables["npm_config_proxy"] = proxy;
                    psi.EnvironmentVariables["npm_config_https_proxy"] = proxy;
                }
                // 顺手禁掉交互式确认（避免"等你敲 y"看起来像卡死）
                psi.EnvironmentVariables["npm_config_confirm_modules_purge"] = "false";
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    string e = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(240000))
                    {
                        // ★ 超时必须**真的杀掉**，否则会留下一个永远挂着的 pnpm 进程（实测踩过）
                        try { p.Kill(); } catch { }
                        // ★ WP2④（2026-09-20 防误报加固）：不再起 `taskkill /T /F /PID …` ——
                        //   ① 起一个 taskkill 去杀进程 = shell 杀进程形状；
                        //   ② `/T` 顺进程树杀，本项目已被它误伤过一次。
                        //   改为按 ParentProcessId 逐层收窄 + 逐个 Process.Kill()，范围如实写进输出。
                        var kn = new System.Text.StringBuilder();
                        try { DshCore.KillTreeManaged(p.Id, kn); } catch { }
                        exitCode = -1;
                        output = (o + "\n" + e + "\n[超时] 240 秒未结束，已强制结束我们起的那个进程及其子进程。\n"
                                  + kn.ToString()
                                  + "若是 github 源依赖，请先配代理：HTTPS_PROXY=http://127.0.0.1:7890").Trim();
                        return output;
                    }
                    exitCode = p.ExitCode;
                    output = (o + "\n" + e).Trim();
                }
            }
            catch (Exception ex)
            {
                output = "执行失败：" + ex.Message;
            }
            return output;
        }

        // ---------- 三层状态报告 ----------

        /// <summary>
        /// 固化体检。三层分开报，任何一层不成立都**不影响**另一层的结论：
        ///   L1 补丁在岗？（锚点替换过的内容还在不在）
        ///   L2 已固化？（patchedDependencies 登记 + patches/ 文件）
        ///   L3 工具链？（pnpm 能不能用、这棵树是不是 pnpm 工程）
        /// </summary>
        public static string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("== 补丁固化体检 ==");
            sb.AppendLine("（目的：让补丁**装也装不掉**；以前只能重打，重打完下次安装又没了）");
            sb.AppendLine();

            // --- L3 工具链 ---
            string pnpm = PnpmExe();
            string ver = PnpmVersion();
            sb.AppendLine("-- L3 工具链 --");
            if (pnpm.Length == 0)
                sb.AppendLine("[FAIL] 没找到 pnpm ⇒ 固化做不了（可先 `npm i -g pnpm`）。");
            else
                sb.AppendLine("[OK]   pnpm：" + pnpm + (ver.Length > 0 ? "（版本 " + ver + "）" : ""));
            sb.AppendLine();

            // --- 逐个补丁 ---
            var targets = Targets();
            int lockable = 0, locked = 0;
            foreach (var t in targets)
            {
                sb.AppendLine("-- " + t.Title + " --");
                sb.AppendLine("   它为什么存在：" + t.Reason);

                // L1 在岗
                bool pkgThere = !string.IsNullOrEmpty(t.PkgDir) && Directory.Exists(t.PkgDir);
                if (!pkgThere)
                {
                    sb.AppendLine("[?]    L1 未装该包（找不到 " + (t.PkgDir.Length == 0 ? "目录" : t.PkgDir) + "）⇒ 跳过。");
                    sb.AppendLine();
                    continue;
                }
                int onDuty = 0;
                foreach (var ed in t.Edits)
                {
                    string f = Path.Combine(t.PkgDir, ed.RelPath);
                    if (!File.Exists(f)) continue;
                    string txt = ReadText(f);
                    if (txt.IndexOf(ed.Marker, StringComparison.Ordinal) >= 0) onDuty++;
                }
                sb.AppendLine(onDuty == t.Edits.Count
                    ? "[OK]   L1 补丁在岗（" + onDuty + "/" + t.Edits.Count + " 处判据命中）。"
                    : "[WARN] L1 补丁**不在岗**（" + onDuty + "/" + t.Edits.Count + "）：请先点「🧩 补丁体检」重打。");

                // L3' 这棵树能不能固化
                if (!t.PnpmCapable)
                {
                    sb.AppendLine("[N/A]  L2 无法用 pnpm 固化：该包所在安装树不是 pnpm 工程"
                                + (t.ProjectDir.Length > 0 ? "（" + t.ProjectDir + "）" : "")
                                + " ⇒ 只能保留「锚点重打 + 每次体检」这一层。");
                    sb.AppendLine("        · 这是环境事实：npm 全局安装树没有 lockfile/.pnpm，pnpm 管不到它。");
                }
                else
                {
                    var st = LockState(t);
                    if (st.Locked)
                    {
                        locked++;
                        sb.AppendLine("[OK]   L2 已固化：登记 " + st.Where + "；补丁文件 " + st.PatchFile);
                        sb.AppendLine("       ⇒ 以后 `pnpm install` / 插件升级都会**自动重新应用**，不会再被静默丢弃。");
                    }
                    else
                    {
                        lockable++;
                        sb.AppendLine("[WARN] L2 未固化：pnpm 工程（" + t.ProjectDir + "）里没有 patchedDependencies 登记。");
                        sb.AppendLine("       ⇒ 现在这层保护**下次安装就会掉**。点「🔒 固化补丁」可以一次做掉。");
                    }
                }
                sb.AppendLine();
            }

            sb.AppendLine("-- 结论 --");
            if (locked > 0) sb.AppendLine("[OK]   已固化 " + locked + " 个补丁。");
            if (lockable > 0) sb.AppendLine("[WARN] 还有 " + lockable + " 个可固化的补丁没固化（点「🔒 固化补丁」）。");
            if (locked == 0 && lockable == 0) sb.AppendLine("[?]    没有可固化的补丁。");
            sb.AppendLine("说明：已装好的环境**不用**为了固化而卸载重装 —— pnpm 会在下一次安装时应用补丁。");
            return sb.ToString();
        }

        public class PatchLockState
        {
            public bool Locked;
            public string Where = "";
            public string PatchFile = "";
        }

        /// <summary>读工程里的 patchedDependencies 登记（pnpm 11 写在 pnpm-workspace.yaml，旧版写在 package.json）。</summary>
        public static PatchLockState LockState(Target t)
        {
            var st = new PatchLockState();
            if (string.IsNullOrEmpty(t.ProjectDir)) return st;
            string[] cfgFiles = new string[]
            {
                Path.Combine(t.ProjectDir, "pnpm-workspace.yaml"),
                Path.Combine(t.ProjectDir, "package.json")
            };
            foreach (var cf in cfgFiles)
            {
                if (!File.Exists(cf)) continue;
                string txt = ReadText(cf);
                if (txt.IndexOf("patchedDependencies", StringComparison.Ordinal) < 0) continue;
                if (txt.IndexOf(t.PkgName, StringComparison.Ordinal) < 0) continue;
                st.Locked = true;
                st.Where = Path.GetFileName(cf);
                break;
            }
            string pdir = Path.Combine(t.ProjectDir, "patches");
            if (Directory.Exists(pdir))
            {
                foreach (var f in Directory.GetFiles(pdir, "*.patch"))
                {
                    if (Path.GetFileName(f).StartsWith(t.PkgName.Replace("@", "").Replace("/", "+"), StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(f).StartsWith(t.PkgName, StringComparison.OrdinalIgnoreCase))
                    {
                        st.PatchFile = Path.GetFileName(f);
                        break;
                    }
                }
            }
            if (st.PatchFile.Length == 0 && st.Locked)
            {
                foreach (var f in Directory.GetFiles(pdir, "*.patch"))
                {
                    if (Path.GetFileName(f).IndexOf(PkgLeaf(t.PkgName), StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        st.PatchFile = Path.GetFileName(f);
                        break;
                    }
                }
            }
            return st;
        }

        private static string PkgLeaf(string pkgName)
        {
            int i = pkgName.LastIndexOf('/');
            return i >= 0 ? pkgName.Substring(i + 1) : pkgName;
        }

        // ---------- 一键固化（fail-closed） ----------

        /// <summary>
        /// 把某个补丁固化成 pnpm 官方补丁。步骤与判据：
        ///   ① 备份配置文件（package.json / pnpm-workspace.yaml / pnpm-lock.yaml）
        ///   ② pnpm patch &lt;包&gt;@&lt;版本&gt; --edit-dir &lt;临时目录&gt;   ← 取上游原件
        ///   ③ 对每个文件做锚点定点替换（**锚点必须唯一命中**，否则中止、什么都不写）
        ///   ④ pnpm patch-commit &lt;临时目录&gt;                       ← 生成 patches/ + 登记
        ///   ⑤ 复验：patches/ 里有补丁文件 且 配置里有 patchedDependencies
        /// 任何一步失败 ⇒ 立即返回，**不改动**任何工程文件（fail-closed）。
        /// </summary>
        public static string Lock(string id)
        {
            var sb = new StringBuilder();
            Target t = null;
            foreach (var x in Targets()) if (x.Id == id) { t = x; break; }
            if (t == null) return "[FAIL] 没有这个补丁：" + id;

            sb.AppendLine("== 固化补丁：" + t.Title + " ==");
            if (!Directory.Exists(t.PkgDir)) return "[FAIL] 包没装：" + t.PkgDir;
            if (!t.PnpmCapable)
                return "[N/A]  该包所在安装树不是 pnpm 工程，pnpm 固化不适用：\n       " + t.ProjectDir
                     + "\n       请改用「🧩 补丁体检」里的重打（本机这条补丁只能这样守）。";

            string pnpm = PnpmExe();
            if (pnpm.Length == 0) return "[FAIL] 没找到 pnpm，无法固化（可先 `npm i -g pnpm`）。";

            var st0 = LockState(t);
            if (st0.Locked)
                return "[OK]   已经固化过了（登记在 " + st0.Where + "，补丁 " + st0.PatchFile + "），无需重复。";

            // 版本：从包自己的 package.json 读
            string ver = PkgVersion(t.PkgDir);
            string spec = t.PkgName + (ver.Length > 0 ? "@" + ver : "");

            // ① 备份配置文件
            string bakDir = Path.Combine(DshCore.AppDataDir, "patchlock-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            try
            {
                Directory.CreateDirectory(bakDir);
                foreach (var f in new string[] { "package.json", "pnpm-workspace.yaml", "pnpm-lock.yaml" })
                {
                    string src = Path.Combine(t.ProjectDir, f);
                    if (File.Exists(src)) File.Copy(src, Path.Combine(bakDir, f), true);
                }
                sb.AppendLine("[OK]   ① 已备份配置文件 → " + bakDir);
            }
            catch (Exception ex)
            {
                return "[FAIL] 备份失败，为安全起见中止：" + ex.Message;
            }

            // ② 取上游原件
            string editDir = Path.Combine(Path.GetTempPath(), "bffr-patch-" + DateTime.Now.ToString("HHmmss"));
            try { if (Directory.Exists(editDir)) Directory.Delete(editDir, true); Directory.CreateDirectory(editDir); }
            catch { }
            int code; string outp;
            outp = Run(pnpm, "patch " + spec + " --edit-dir \"" + editDir + "\"", t.ProjectDir, out code, out outp);
            sb.AppendLine("② pnpm patch " + spec + " --edit-dir …  → 退出码 " + code);
            if (code != 0)
            {
                sb.AppendLine(Tail(outp, 12));
                sb.AppendLine("[FAIL] 取上游原件失败。常见原因：还没在本工程跑过 `pnpm install`（模块目录没就绪）。");
                sb.AppendLine("       已中止，**没有改动任何文件**。");
                return sb.ToString();
            }

            // ③ 锚点定点替换（唯一命中才改）
            var done = new List<string>();
            foreach (var ed in t.Edits)
            {
                string f = Path.Combine(editDir, ed.RelPath);
                if (!File.Exists(f))
                {
                    sb.AppendLine("[FAIL] 原件里没有 " + ed.RelPath + " ⇒ 中止（没有改动任何文件）。");
                    return sb.ToString();
                }
                string txt = ReadText(f);
                if (txt.IndexOf(ed.Marker, StringComparison.Ordinal) >= 0)
                {
                    sb.AppendLine("[?]    " + ed.RelPath + " 原件里已经带了目标内容（上游已采纳？）⇒ 跳过这处。");
                    continue;
                }
                int n = CountOccurScoped(txt, ed.Scope, ed.Anchor);
                if (n != 1)
                {
                    sb.AppendLine("[FAIL] " + ed.RelPath + " 的锚点命中 " + n + " 次（要求恰好 1 次）⇒ 中止，**绝不瞎改**。");
                    sb.AppendLine("       锚点：" + Short(ed.Anchor));
                    if (!string.IsNullOrEmpty(ed.Scope))
                        sb.AppendLine("       作用域：" + Short(ed.Scope) + "（只在它之后找锚点）");
                    return sb.ToString();
                }
                int at = IndexOfScoped(txt, ed.Scope, ed.Anchor);
                string patched = txt.Substring(0, at) + ed.Replacement + txt.Substring(at + ed.Anchor.Length);
                WriteText(f, patched);
                done.Add(ed.RelPath);
                sb.AppendLine("[OK]   ③ 已改 " + ed.RelPath + "（锚点唯一命中）");
            }
            if (done.Count == 0)
            {
                sb.AppendLine("[?]    没有需要改的地方（上游可能已自带该修复）⇒ 不生成补丁。");
                return sb.ToString();
            }

            // ④ patch-commit
            outp = Run(pnpm, "patch-commit \"" + editDir + "\"", t.ProjectDir, out code, out outp);
            sb.AppendLine("④ pnpm patch-commit …  → 退出码 " + code);
            sb.AppendLine("   " + Tail(outp, 10).Replace("\n", "\n   "));
            if (code != 0)
            {
                sb.AppendLine("[FAIL] 生成补丁失败。工程文件已备份在 " + bakDir + "，可从中恢复。");
                return sb.ToString();
            }

            // ⑤ 复验
            var st = LockState(t);
            sb.AppendLine("⑤ 复验：");
            sb.AppendLine(st.PatchFile.Length > 0
                ? "[OK]   补丁文件已生成：patches\\" + st.PatchFile
                : "[FAIL] 没看到 patches\\ 下的补丁文件。");
            sb.AppendLine(st.Locked
                ? "[OK]   已登记 " + st.Where + " 的 patchedDependencies ⇒ **pnpm 以后会自己重新应用它**。"
                : "[FAIL] 配置里没有 patchedDependencies 登记。");
            if (st.PatchFile.Length > 0 && st.Locked)
                sb.AppendLine("[OK]   ★ 固化成功：这层保护从此不再依赖「记得重打」。");
            sb.AppendLine("（当前已装好的 node_modules 不用动；下次 `pnpm install` 时 pnpm 会应用补丁。）");
            return sb.ToString();
        }

        private static string PkgVersion(string pkgDir)
        {
            try
            {
                string pj = Path.Combine(pkgDir, "package.json");
                if (!File.Exists(pj)) return "";
                string txt = ReadText(pj);
                int i = txt.IndexOf("\"version\"", StringComparison.Ordinal);
                if (i < 0) return "";
                int c = txt.IndexOf(':', i);
                int q1 = txt.IndexOf('"', c + 1);
                int q2 = txt.IndexOf('"', q1 + 1);
                if (q1 < 0 || q2 < 0) return "";
                return txt.Substring(q1 + 1, q2 - q1 - 1);
            }
            catch { return ""; }
        }

        // ---------- 小工具 ----------

        private static string ReadText(string path)
        {
            try { return File.ReadAllText(path, new UTF8Encoding(false)); }
            catch { return ""; }
        }

        private static void WriteText(string path, string text)
        {
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        private static int CountOccur(string hay, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return 0;
            int n = 0, i = 0;
            while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        /// <summary>
        /// 作用域＝**函数体范围**：从 Scope 那行起，到下一个 `export function` 之前。
        /// 为什么这样做：上游 index.js 里同款 `return parts.join('\n\n')` 有两处（一处在本函数、一处
        /// 在别的函数），只按"缩进"区分不出来；而多行锚点又会因 **CRLF/LF 差异**匹配不上（实测两次：
        /// 一次命中 2 次、加了注释行后又变 0 次）。按函数体切范围与换行符无关，最稳。
        /// </summary>
        private static bool FunctionExtent(string hay, string scope, out int start, out int end)
        {
            start = 0; end = 0;
            if (string.IsNullOrEmpty(scope)) { start = 0; end = hay.Length; return true; }
            int s = hay.IndexOf(scope, StringComparison.Ordinal);
            if (s < 0) return false;
            int e = hay.IndexOf("\nexport function", s + scope.Length, StringComparison.Ordinal);
            start = s;
            end = e < 0 ? hay.Length : e;
            return true;
        }

        /// <summary>只统计函数体范围内的锚点命中数。</summary>
        private static int CountOccurScoped(string hay, string scope, string anchor)
        {
            if (string.IsNullOrEmpty(anchor)) return 0;
            int s, e;
            if (!FunctionExtent(hay, scope, out s, out e)) return 0;
            return CountOccur(hay.Substring(s, e - s), anchor);
        }

        private static int IndexOfScoped(string hay, string scope, string anchor)
        {
            int s, e;
            if (!FunctionExtent(hay, scope, out s, out e)) return -1;
            string body = hay.Substring(s, e - s);
            int i = body.IndexOf(anchor, StringComparison.Ordinal);
            return i < 0 ? -1 : s + i;
        }

        private static string Short(string s)
        {
            s = s.Replace("\r", "").Replace("\n", "\\n");
            return s.Length > 90 ? s.Substring(0, 90) + "…" : s;
        }

        private static string Tail(string s, int lines)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var all = s.Replace("\r", "").Split('\n');
            int start = all.Length > lines ? all.Length - lines : 0;
            var sb = new StringBuilder();
            for (int i = start; i < all.Length; i++) if (all[i].Trim().Length > 0) sb.AppendLine("   " + all[i]);
            return sb.ToString().TrimEnd();
        }

        // ============================================================
        //  白屏盲区：客户端插件的"还没跑起来就注定失败"静态扫描
        //
        //  为什么单独做：客户端插件崩的时候**服务端日志往往是干净的**
        //  （页面白屏发生在浏览器里），所以从服务端看不出任何东西。
        //  能静态查的（也就是本方法查的）：
        //    · client 入口文件缺失 / 0 字节  ← 本机皮肤包实测 lib\index.js = 0 字节
        //    · 同一个插件被装了两份（node_modules 与 plugins 各一份）
        //    · 声明了 dsh.client 却指向不存在的文件 / 平台不是 web
        //    · 登记在 profile bundles 里但目录根本不在
        //  查不了的（要如实说）：两个插件在**运行期**抢同一个服务名 —— 需要页面侧证据。
        // ============================================================
        public static string WhiteScreenReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("== 白屏盲区扫描（客户端插件静态体检）==");
            sb.AppendLine("（服务端日志看不见白屏；这里查的是「还没执行就注定失败」的那类错）");
            sb.AppendLine();

            string prof = ProfileDir();
            var seen = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            int nClient = 0, bad = 0;

            foreach (var dir in PluginDirs(prof))
            {
                string pj = Path.Combine(dir, "package.json");
                if (!File.Exists(pj)) continue;
                string name = JsonStr(ReadText(pj), "\"name\"");
                if (name.Length == 0) continue;
                string dshClient = JsonSection(ReadText(pj), "\"client\"");
                bool isClient = dshClient.Length > 0;
                if (isClient) nClient++;

                if (!seen.ContainsKey(name)) seen[name] = new List<string>();
                seen[name].Add(dir);

                if (!isClient) continue;

                // client 入口
                string entry = JsonStr(dshClient, "\"entry\"");
                if (entry.Length == 0)
                {
                    string exports = JsonSection(ReadText(pj), "\"exports\"");
                    entry = JsonStr(exports, "\"./client\"");
                }
                if (entry.Length == 0) entry = Path.Combine("lib", "client.js");
                string epath = Path.Combine(dir, entry.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(epath))
                {
                    bad++;
                    sb.AppendLine("[FAIL] " + name + "：client 入口不存在 → " + entry);
                    continue;
                }
                long len = new FileInfo(epath).Length;
                if (len == 0)
                {
                    bad++;
                    sb.AppendLine("[FAIL] " + name + "：client 入口是 **0 字节** → " + entry + "（页面必然白屏）");
                    continue;
                }

                // main 声明了却空文件
                string main = JsonStr(ReadText(pj), "\"main\"");
                if (main.Length > 0)
                {
                    string mp = Path.Combine(dir, main.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(mp) && new FileInfo(mp).Length == 0)
                        sb.AppendLine("[WARN] " + name + "：main 指向的文件是 0 字节（" + main + "）——若 DSH 走 main 就会白屏");
                }

                // 平台
                string plat = JsonStr(dshClient, "\"platform\"");
                if (plat.Length > 0 && !plat.Equals("web", StringComparison.OrdinalIgnoreCase))
                {
                    bad++;
                    sb.AppendLine("[FAIL] " + name + "：dsh.client.platform = " + plat + "（不是 web，页面不会加载它）");
                }
            }

            // 重复安装
            sb.AppendLine();
            sb.AppendLine("-- 重复安装（同名插件装了两份：加载哪份不确定，容易白屏）--");
            bool dup = false;
            foreach (var kv in seen)
            {
                if (kv.Value.Count > 1)
                {
                    dup = true;
                    sb.AppendLine("[WARN] " + kv.Key + " 有 " + kv.Value.Count + " 份：");
                    foreach (var d in kv.Value) sb.AppendLine("        · " + d);
                }
            }
            if (!dup) sb.AppendLine("[OK]   没有同名重复安装。");

            // bundles 登记的 client 插件是否真的在
            sb.AppendLine();
            sb.AppendLine("-- profile 登记的客户端插件是否都在盘上 --");
            string pjProf = Path.Combine(prof, "package.json");
            if (File.Exists(pjProf))
            {
                string txt = ReadText(pjProf);
                int miss = 0;
                foreach (var nm in JsonArray(JsonSection(txt, "\"bundles\"")))
                {
                    string p = Path.Combine(prof, "node_modules", nm.Replace('/', Path.DirectorySeparatorChar));
                    string p2 = Path.Combine(prof, "plugins", nm.Replace('/', Path.DirectorySeparatorChar));
                    // ★ 官方内置 bundle（@deepseek-ai/dsh-base、dsh-web-app 等）装在 DSH 安装树里，
                    //   不在 profile 下 —— 只查 profile 会把它们误报成「盘上没有」（实测踩过）。
                    string dshTree = Path.Combine(DshCore.UserProfile, ".ai-manager", "npm-global", "node-v24", "node_modules");
                    string p3 = Path.Combine(dshTree, nm.Replace('/', Path.DirectorySeparatorChar));
                    string p4 = Path.Combine(dshTree, "@deepseek-ai", "dsh", "node_modules", nm.Replace('/', Path.DirectorySeparatorChar));
                    if (!Directory.Exists(p) && !Directory.Exists(p2) && !Directory.Exists(p3) && !Directory.Exists(p4))
                    {
                        miss++;
                        sb.AppendLine("[WARN] 登记了但盘上没有：" + nm);
                    }
                }
                if (miss == 0) sb.AppendLine("[OK]   bundles 里 " + JsonArray(JsonSection(txt, "\"bundles\"")).Count + " 项都在盘上。");
            }

            sb.AppendLine();
            sb.AppendLine("-- 结论 --");
            sb.AppendLine("客户端插件 " + nClient + " 个，静态硬故障 " + bad + " 个。");
            sb.AppendLine("★ 静态扫描**查不到**运行期「两个插件抢同一个服务名」这类冲突 —— 那需要页面侧证据：");
            sb.AppendLine("  白屏时先看浏览器控制台第一条报错，再到「🩺诊断 → 导出报告」把那份日志交给救星。");
            return sb.ToString();
        }

        private static List<string> PluginDirs(string prof)
        {
            var list = new List<string>();
            AddDirChildren(list, Path.Combine(prof, "node_modules"));
            AddDirChildren(list, Path.Combine(prof, "node_modules", "@dsh-external"));
            AddDirChildren(list, Path.Combine(prof, "node_modules", "@deepseek-ai"));
            AddDirChildren(list, Path.Combine(prof, "node_modules", "@local"));
            AddDirChildren(list, Path.Combine(prof, "plugins"));
            return list;
        }

        private static void AddDirChildren(List<string> list, string root)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                foreach (var d in Directory.GetDirectories(root))
                {
                    string n = Path.GetFileName(d);
                    if (n.StartsWith(".", StringComparison.Ordinal)) continue;
                    if (n.StartsWith("_bak", StringComparison.OrdinalIgnoreCase)) continue;   // 备份目录不算"装了两份"
                    if (n.IndexOf("-bak", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (n.IndexOf(".bak", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (n.StartsWith("@", StringComparison.Ordinal)) continue;   // 作用域目录，已单独枚举
                    list.Add(d);
                }
            }
            catch { }
        }

        // 极简 JSON 取值（不引依赖：只处理我们自己读得懂的形态）
        private static string JsonStr(string txt, string key)
        {
            if (string.IsNullOrEmpty(txt)) return "";
            int i = txt.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return "";
            int c = txt.IndexOf(':', i + key.Length);
            if (c < 0) return "";
            int k = c + 1;
            while (k < txt.Length && char.IsWhiteSpace(txt[k])) k++;
            // ★ 只有「值真的是字符串」才算命中 —— 否则（值是对象/数组，如 exports 里的
            //   "./client": { types, import }）会把**子键名**当成值返回（实测踩过：
            //   dsh-image-gen 被误报成「client 入口不存在 → types」）。
            if (k >= txt.Length || txt[k] != '"') return "";
            int q1 = k;
            int q2 = txt.IndexOf('"', q1 + 1);
            if (q2 < 0) return "";
            return txt.Substring(q1 + 1, q2 - q1 - 1);
        }

        /// <summary>取一个对象/数组片段（用于在里面继续找键）。</summary>
        private static string JsonSection(string txt, string key)
        {
            if (string.IsNullOrEmpty(txt)) return "";
            int i = txt.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return "";
            int c = txt.IndexOf(':', i + key.Length);
            if (c < 0) return "";
            int s = c + 1;
            while (s < txt.Length && char.IsWhiteSpace(txt[s])) s++;
            if (s >= txt.Length) return "";
            char open = txt[s];
            char close = open == '{' ? '}' : (open == '[' ? ']' : '\0');
            if (close == '\0') return "";
            int depth = 0;
            for (int k = s; k < txt.Length; k++)
            {
                if (txt[k] == open) depth++;
                else if (txt[k] == close)
                {
                    depth--;
                    if (depth == 0) return txt.Substring(s, k - s + 1);
                }
            }
            return txt.Substring(s);
        }

        private static List<string> JsonArray(string section)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(section)) return list;
            int i = 0;
            while (true)
            {
                int q1 = section.IndexOf('"', i);
                if (q1 < 0) break;
                int q2 = section.IndexOf('"', q1 + 1);
                if (q2 < 0) break;
                list.Add(section.Substring(q1 + 1, q2 - q1 - 1));
                i = q2 + 1;
            }
            return list;
        }

        // ============================================================
        //  模型错误码翻译：把 DSH 抛的机器码翻成人话
        //  （原先只报原文，用户看不懂该干嘛）
        // ============================================================
        public class ErrText
        {
            public string Code;
            public string Meaning;
            public string Action;
        }

        private static readonly ErrText[] ErrTable = new ErrText[]
        {
            new ErrText { Code = "MISSING_CREDENTIAL",
                Meaning = "这个模型没配密钥（或密钥被清掉了）。",
                Action = "去设置里给对应 provider 填 API Key；填完点「🩺装后体检」确认模型通路。" },
            new ErrText { Code = "UNKNOWN_MODEL",
                Meaning = "选的模型名在当前 provider 上不存在（多半是升级/改名了）。",
                Action = "在模型选择里换一个已启用的模型；或点「🩺装后体检」看「模型通路」那段列出的可用模型。" },
            new ErrText { Code = "DUPLICATE_ADAPTER",
                Meaning = "同一个 provider 被注册了两次（插件装重了或两处都配了）。",
                Action = "用「🧩 插件与皮肤 → 去重重复条目」查重复登记；或用白屏扫描看是不是同名装了两份。" },
            new ErrText { Code = "DUPLICATE_DISCOVERY",
                Meaning = "同一个设置命名空间的模型发现被登记了两次。",
                Action = "同上：先查重复插件，再看 profile 的 bundles 是否有重复项。" },
            new ErrText { Code = "INVALID_REQUEST",
                Meaning = "请求被服务端拒了（参数/内容不合规）。若消息里带 Content Exists Risk，是内容审核拒的。",
                Action = "换个说法重发；不要在对话里粘贴订阅链接/密钥一类敏感原文（进了历史会持续触发审核）。" },
            new ErrText { Code = "Content Exists Risk",
                Meaning = "内容审核拒了整个请求体（注意：是**整段历史**，不是最后那句）。",
                Action = "该会话已被污染，建议新开会话，只带一页结论（脱敏）。" },
            new ErrText { Code = "gateway/internal",
                Meaning = "浏览器**连不上本地服务**（是页面→本机的通路断了，不是模型或皮肤的问题）。",
                Action = "点「🚑 急救」里的启动/重启；再用「🩺 装后体检」确认端口与 HTTP 是否正常。" },
            new ErrText { Code = "EADDRINUSE",
                Meaning = "端口被占了（常见：上一次的 DSH 没退干净）。",
                Action = "点「🚑 急救」重启；或换端口（DSH_PORT 环境变量 / port.txt）。" },
            new ErrText { Code = "EACCES",
                Meaning = "这个端口被系统**保留**了（Windows 的 Hyper-V/WSL2 会整段保留端口）。",
                Action = "换端口即可；点「🩺 装后体检」会列出本机被保留的端口段。" },
            new ErrText { Code = "ENOENT",
                Meaning = "要读的文件不存在（插件缺文件是典型）。",
                Action = "看「🩺 装后体检」的悬空引用/入口缺失那几项；必要时「🧩 重装插件」。" },
            new ErrText { Code = "unknown prompt variable",
                Meaning = "记忆里写了 {{…}} 字面量，被当成提示词变量插值 ⇒ **DSH 起不来**。",
                Action = "点「🧩 补丁体检」重打记忆花括号转义；再点「🔒 固化补丁」让它装也装不掉。" },
            new ErrText { Code = "duplicate loader entry id",
                Meaning = "插件树的补丁文件里同一个 id 被插入了两次 ⇒ 整棵树加载失败、DSH 打不开。",
                Action = "点「🧩 插件与皮肤 → 去重重复条目」（救星会把它改成合法的 id 定点补丁，保住你的配置）。" },
            new ErrText { Code = "pending (waiting for services",
                Meaning = "某个插件要的服务没人提供 ⇒ 一直挂着（表现为起不来或卡住）。",
                Action = "点「🧩 启用被禁条目」按缺失服务自动启用；或用「🧭 分层判定」看是哪一层的问题。" },
            new ErrText { Code = "429",
                Meaning = "请求太频繁/额度用尽。",
                Action = "等一会儿；或换模型。注意 peak 时段（工作日 9-12、14-18）价格是平时的两倍。" },
            new ErrText { Code = "ETIMEDOUT",
                Meaning = "超时（网络或对端没响应）。",
                Action = "检查代理/网络；重试一次。" }
        };

        /// <summary>把一段原始错误文本翻成人话；认不出来就如实说认不出来，不硬编。</summary>
        public static string TranslateModelError(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var hits = new List<ErrText>();
            foreach (var e in ErrTable)
                if (raw.IndexOf(e.Code, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(e);
            if (hits.Count == 0)
                return "[?]   这条错误我不认识（原样保留，不上纲上线）：" + Short(raw);
            var sb = new StringBuilder();
            foreach (var e in hits)
            {
                sb.AppendLine("[!]    " + e.Code + "：" + e.Meaning);
                sb.AppendLine("       怎么办：" + e.Action);
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>给人看的错误码对照表（写进交付文档时用）。</summary>
        public static string ErrorTable()
        {
            var sb = new StringBuilder();
            sb.AppendLine("| 错误码/关键词 | 说人话 | 该怎么办 |");
            sb.AppendLine("|---|---|---|");
            foreach (var e in ErrTable)
                sb.AppendLine("| " + e.Code + " | " + e.Meaning + " | " + e.Action + " |");
            return sb.ToString();
        }

        // ---------- 自检（把纯函数都验一遍，正负样本都要有） ----------
        public static List<string> Selftest()
        {
            var res = new List<string>();

            // 1) pnpm 工程判据：正样本=真 profile；负样本=不存在的目录 / 普通目录
            bool pos = IsPnpmProject(ProfileDir());
            res.Add("pnpm 工程判据（正样本 profile）: " + (pos ? "OK" : "FAIL"));
            res.Add("pnpm 工程判据（负样本 不存在目录）: " + (!IsPnpmProject(Path.Combine(DshCore.DshHome, "no-such-dir-xyz")) ? "OK" : "FAIL"));
            res.Add("pnpm 工程判据（负样本 空串）: " + (!IsPnpmProject("") ? "OK" : "FAIL"));

            // 2) 锚点唯一命中计数
            res.Add("CountOccur 唯一命中: " + (CountOccur("abcXYZdef", "XYZ") == 1 ? "OK" : "FAIL"));
            res.Add("CountOccur 零命中: " + (CountOccur("abcdef", "XYZ") == 0 ? "OK" : "FAIL"));
            res.Add("CountOccur 两次命中: " + (CountOccur("aXbXc", "X") == 2 ? "OK" : "FAIL"));
            res.Add("CountOccur 空针=0: " + (CountOccur("abc", "") == 0 ? "OK" : "FAIL"));

            // 3) 错误码翻译：正样本必须命中；负样本必须**不**命中
            string t1 = TranslateModelError("{\"code\":\"MISSING_CREDENTIAL\"}");
            res.Add("错误码翻译 正样本 MISSING_CREDENTIAL: " + (t1.IndexOf("没配密钥", StringComparison.Ordinal) >= 0 ? "OK" : "FAIL"));
            string t2 = TranslateModelError("Error: UNKNOWN_MODEL at provider x");
            res.Add("错误码翻译 正样本 UNKNOWN_MODEL: " + (t2.IndexOf("不存在", StringComparison.Ordinal) >= 0 ? "OK" : "FAIL"));
            string t3 = TranslateModelError("something totally unrelated happened");
            res.Add("错误码翻译 负样本 不硬编: " + (t3.IndexOf("不认识", StringComparison.Ordinal) >= 0 ? "OK" : "FAIL"));
            res.Add("错误码翻译 空输入=空: " + (TranslateModelError("") == "" ? "OK" : "FAIL"));
            res.Add("错误码对照表 行数>10: " + (ErrTable.Length > 10 ? "OK" : "FAIL"));

            // 4) JSON 片段抽取（白屏扫描靠它）
            string js = "{\"name\":\"pkg-a\",\"dsh\":{\"client\":{\"platform\":\"web\"}},\"exports\":{\"./client\":\"./lib/client.js\"}}";
            res.Add("JsonStr name: " + (JsonStr(js, "\"name\"") == "pkg-a" ? "OK" : "FAIL"));
            string sec = JsonSection(js, "\"client\"");
            res.Add("JsonSection client: " + (sec.StartsWith("{") && sec.EndsWith("}") ? "OK" : "FAIL"));
            res.Add("JsonStr platform（嵌套内）: " + (JsonStr(sec, "\"platform\"") == "web" ? "OK" : "FAIL"));
            var arr = JsonArray("[\"a\",\"b\",\"c\"]");
            res.Add("JsonArray 3 项: " + (arr.Count == 3 ? "OK" : "FAIL"));
            res.Add("JsonArray 空: " + (JsonArray("").Count == 0 ? "OK" : "FAIL"));
            res.Add("JsonStr 缺键=空: " + (JsonStr(js, "\"nope\"") == "" ? "OK" : "FAIL"));

            // 5) 补丁清单自身
            var ts = Targets();
            res.Add("补丁清单 至少 2 条: " + (ts.Count >= 2 ? "OK" : "FAIL"));
            var meT = ts[0];
            res.Add("清单 memory-evolve 有 2 处编辑: " + (meT.Edits.Count == 2 ? "OK" : "FAIL"));
            res.Add("清单 每条都有判据串与锚点: " +
                (AllHave(ts) ? "OK" : "FAIL"));

            // 6) 报告能出文本且不抛
            try
            {
                string r = Report();
                res.Add("固化体检可生成: " + (r.IndexOf("L1", StringComparison.Ordinal) >= 0 && r.IndexOf("L2", StringComparison.Ordinal) >= 0 ? "OK" : "FAIL"));
            }
            catch (Exception ex) { res.Add("固化体检抛异常: FAIL " + ex.Message); }
            try
            {
                string w = WhiteScreenReport();
                res.Add("白屏扫描可生成: " + (w.IndexOf("客户端插件", StringComparison.Ordinal) >= 0 ? "OK" : "FAIL"));
            }
            catch (Exception ex) { res.Add("白屏扫描抛异常: FAIL " + ex.Message); }

            return res;
        }

        private static bool AllHave(List<Target> ts)
        {
            foreach (var t in ts)
            {
                if (t.Edits.Count == 0) return false;
                foreach (var e in t.Edits)
                    if (string.IsNullOrEmpty(e.Marker) || string.IsNullOrEmpty(e.Anchor) || string.IsNullOrEmpty(e.Replacement)) return false;
            }
            return true;
        }
    }
}

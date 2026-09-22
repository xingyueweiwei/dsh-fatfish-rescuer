using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

// ============================================================
// 环境与弹窗体检（2026-09-16 新增，来自当天实战教训）
//
// 为什么需要这一组：
//   当天为了查"一直弹 PowerShell 窗口"，绕了三小时，最后发现是四件事叠加：
//     ① 救星旧版每 4 秒用 powershell+WMI 查进程
//     ② DSH 核心 dsh-win32-process 的 CreateProcessAsUserW 漏了 CREATE_NO_WINDOW
//     ③ argo 插件 spawn(python) 漏了 windowsHide
//     ④ 系统把「默认终端应用程序」设成了 Windows Terminal，
//        于是任何新控制台都被接管成一个写着 Windows PowerShell 的大终端窗口
//   ①②③ 都是**代码补丁**，而 DSH 一升级 ② 就会被覆盖 → 弹窗复发。
//   ④ 是"视觉放大器"：真正让人看见窗口的是它。
//
// 本文件把这三件事变成救星能一键检查/修复的能力：
//   CheckPatches()  / RepairPatches()   —— 补丁与依赖体检 + 重打
//   PopupProbe(n)                       —— 采样 n 秒，报出"谁在新建控制台"
//   TerminalCheck() / SetTerminalToConhost() —— 默认终端体检与降噪
// ============================================================
namespace BigFatFishRescuer
{
    public static class PatchGuard
    {
        // ---- 默认终端应用程序的两个 CLSID（注册表 HKCU\Console\%%Startup）----
        private const string GuidConhost = "{B23D10C0-E52E-411E-9D5B-C09FDF709C7D}";
        private const string GuidWindowsTerminal = "{2EACA947-7F5F-4CFA-BA87-8F7FBEEFBE69}";
        private const string GuidLetWindowsDecide = "{00000000-0000-0000-0000-000000000000}";

        // ============================================================
        // 一、补丁与依赖体检
        // ============================================================

        private static string DshWin32ProcPath()
        {
            return Path.Combine(DshCore.UserProfile, ".ai-manager", "npm-global", "node-v24",
                "node_modules", "@deepseek-ai", "dsh", "node_modules", "@deepseek-ai",
                "dsh-win32-process", "lib", "index.js");
        }

        private static string ArgoJsPath()
        {
            return Path.Combine(DshCore.DshHome, "profiles", "web", "node_modules",
                "argo-search", "bin", "argo.js");
        }

        // ============================================================
        // ④ prompt 组装的「字面量花括号」防线（2026-09-16 新增）
        //
        // 为什么要有这一项：这是**唯一一类会让 DSH 直接起不来**的补丁。
        //   机制：`@deepseek-ai/dsh-system-prompt` 把每个 prompt 段/运行时上下文都当模板插值，
        //         `{{prompt}}` 这种字面量会被当成**变量引用**，而注册表里只有 provider/model/cwd
        //         ⇒ 抛 `unknown prompt variable` ⇒ **DSH 启动失败（AI 起不来）**。
        //         而 `dsh-memory-evolve` 会把用户写的 KEY 记忆**原文**注入 `memory:snapshot` 上下文。
        //   两层防线：
        //     ① 上游转义：memory-evolve 的 renderSnapshot / renderInjectionSnapshot 把 `{{` 换成 `{ {`
        //        ⚠ 改的是 node_modules ⇒ `pnpm install` / 插件更新会**静默丢弃**它
        //     ② 治本守卫：本地插件 dsh-prompt-guard 挂在 system-prompt/assemble 瀑布上兜底
        //        ⚠ 它的登记行在 profile 的 cordis.patch.yml 里，可能被别的写者覆盖掉
        //   实测状态（2026-09-16）：①在岗、②的登记行**曾丢失**（guard 自带 --check 报 MISS insert row）
        //   ⇒ 只剩"会掉的那层"，插件一升级就会起不来。所以必须能一键体检 + 重打。
        // ============================================================

        private static string ProfileDir()
        {
            string p = Environment.GetEnvironmentVariable("DSH_PROFILE");
            if (string.IsNullOrEmpty(p)) p = "web";
            return Path.Combine(DshCore.DshHome, "profiles", p.Trim());
        }

        private static string MemoryEvolveLibDir()
        {
            return Path.Combine(ProfileDir(), "node_modules", "dsh-memory-evolve", "lib");
        }

        private static string PromptGuardPluginDir()
        {
            return Path.Combine(ProfileDir(), "plugins", "dsh-prompt-guard");
        }

        private static string ProfilePatchPath()
        {
            return Path.Combine(ProfileDir(), "cordis.patch.yml");
        }

        /// <summary>memory-evolve 两处转义的判据串（与上游 repatch 脚本逐字一致）。</summary>
        private const string BraceEscapeToken = "replaceAll('{{', '{ {')";

        /// <summary>guard 的 patch 登记块（与 dsh-prompt-guard/install.mjs 的 INSERT_BLOCK 逐字一致）。</summary>
        private const string GuardInsertBlock =
            "# --- dsh-prompt-guard: keep literal double-brace text in prompt contributions from aborting an assembly ---\r\n" +
            "# written by dsh-prompt-guard/install.mjs; re-running the installer is a no-op\r\n" +
            "- insert:\r\n" +
            "    - id: prompt-guard\r\n" +
            "      name: './plugins/dsh-prompt-guard/lib/index.js'\r\n";

        private static bool HasBraceEscape(string path)
        {
            try { return File.Exists(path) && File.ReadAllText(path).IndexOf(BraceEscapeToken, StringComparison.Ordinal) >= 0; }
            catch { return false; }
        }

        private static bool GuardWired(string patchPath)
        {
            try { return File.Exists(patchPath) && File.ReadAllText(patchPath).IndexOf("dsh-prompt-guard", StringComparison.Ordinal) >= 0; }
            catch { return false; }
        }

        /// <summary>
        /// 在 `marker` 之后、`anchor` 处做定点替换（与上游 repatch-memory-evolve.mjs 的
        /// replaceAfter 同语义）。**找不到锚点就返回 false，绝不乱改。**
        /// </summary>
        private static bool TryPatchAfter(string path, string marker, string anchor, string replacement, out string err)
        {
            err = null;
            try
            {
                string src = File.ReadAllText(path);
                if (src.IndexOf(BraceEscapeToken, StringComparison.Ordinal) >= 0) return true; // 已打过
                int m = src.IndexOf(marker, StringComparison.Ordinal);
                if (m < 0) { err = "找不到函数锚点 `" + marker + "`（上游可能重构过）——**未改动**，需手工打"; return false; }
                int a = src.IndexOf(anchor, m, StringComparison.Ordinal);
                if (a < 0) { err = "函数内找不到语句锚点 —— **未改动**，需手工打"; return false; }
                string patched = src.Substring(0, a) + replacement + src.Substring(a + anchor.Length);
                SafeConfig.BackupFile(path);
                SafeConfig.AtomicWriteText(path, patched);
                return true;
            }
            catch (Exception e) { err = e.Message; return false; }
        }

        /// <summary>统计"会被注入 system prompt 的记忆文件"里的字面量 `{{` 处数（风险提示用）。</summary>
        private static int CountInjectableLiteralBraces(out List<string> where)
        {
            where = new List<string>();
            int total = 0;
            var files = new List<string>();
            try
            {
                files.Add(Path.Combine(DshCore.DshHome, "memories", "MEMORY.md"));   // 全局记忆（会注入）
                files.Add(Path.Combine(DshCore.DshHome, "memories", "USER.md"));     // 用户档案（会注入）
                string proj = Path.Combine(DshCore.DshHome, "memories", "projects");
                if (Directory.Exists(proj))
                    foreach (string d in Directory.GetDirectories(proj))
                        files.Add(Path.Combine(d, "KEY.md"));                        // 项目关键记忆（会注入）
                // 注：projects/*/MEMORY.md 与 daily/*.md **不会被注入**（上游源码注释明确），故不列入
            }
            catch { }
            foreach (string f in files)
            {
                try
                {
                    if (!File.Exists(f)) continue;
                    int n = Regex.Matches(File.ReadAllText(f), @"\{\{").Count;
                    if (n > 0) { total += n; where.Add(Path.GetFileName(Path.GetDirectoryName(f)) + "/" + Path.GetFileName(f) + " ×" + n); }
                }
                catch { }
            }
            return total;
        }

        /// <summary>
        /// 找系统 python。
        /// ★ 不用 where.exe：它按 OEM 代码页（本机 GBK）输出，中文用户名会被 UTF-8 解码成乱码
        ///   （实测 "用户名" 变 "��ޱ"，路径直接失效）。改为原生查常见位置 + PATH。
        /// </summary>
        public static string SysPython()
        {
            var cands = new List<string>();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (string v in new[] { "Python313", "Python312", "Python311", "Python310" })
                cands.Add(Path.Combine(local, "Programs", "Python", v, "python.exe"));
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (string v in new[] { "Python313", "Python312", "Python311" })
                cands.Add(Path.Combine(pf, v, "python.exe"));

            string path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                foreach (string d in path.Split(';'))
                {
                    string t = d.Trim().Trim('"');
                    if (t.Length == 0) continue;
                    try
                    {
                        string c = Path.Combine(t, "python.exe");
                        if (File.Exists(c) && !cands.Contains(c)) cands.Add(c);
                    }
                    catch { }
                }
            }
            foreach (string c in cands) { try { if (File.Exists(c)) return c; } catch { } }
            return null;
        }

        /// <summary>返回 pyyaml 版本；未安装返回 null。直接调 python，不经 cmd（避免引号与编码坑）。</summary>
        public static string PyYamlVersion()
        {
            string py = SysPython();
            if (py == null) return null;
            string v = DshCore.RunHidden(py, "-c \"import yaml;print(yaml.__version__)\"", 20000);
            if (string.IsNullOrEmpty(v)) return null;
            v = v.Trim();
            if (v.IndexOf("Traceback", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (v.IndexOf("No module named", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (v.Length == 0) return null;
            // 只取最后一行（防止 python 把警告也打出来）
            string[] lines = v.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length > 0 ? lines[lines.Length - 1].Trim() : null;
        }

        /// <summary>三个补丁 + pyyaml 的体检报告。</summary>
        public static string CheckPatches()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== 环境补丁体检 ====");
            sb.AppendLine("（这些补丁都在 node_modules 里，**DSH 升级后会被覆盖**，届时弹窗等老毛病会复发）");
            sb.AppendLine();

            // ① DSH CREATE_NO_WINDOW
            string dsh = DshWin32ProcPath();
            if (!File.Exists(dsh))
            {
                sb.AppendLine("[?]    找不到 dsh-win32-process（DSH 装法不同或未安装）：" + dsh);
            }
            else
            {
                string t = SafeRead(dsh);
                bool has = t.IndexOf("CREATE_NO_WINDOW", StringComparison.Ordinal) >= 0;
                bool two = Regex.Matches(t, "CREATE_NO_WINDOW").Count >= 3;   // 声明 + 两处调用
                sb.AppendLine(has
                    ? (two ? "[OK]   ① DSH 子进程不弹控制台（CREATE_NO_WINDOW，两处调用都在）"
                           : "[WARN] ① CREATE_NO_WINDOW 只在部分位置，建议点「补丁体检/重打」修一遍")
                    : "[FAIL] ① DSH 缺少 CREATE_NO_WINDOW —— 每执行一条命令都可能弹一个控制台窗口");
            }

            // ② argo windowsHide
            string argo = ArgoJsPath();
            if (!File.Exists(argo))
                sb.AppendLine("[?]    找不到 argo-search（可能没装该插件）");
            else
            {
                string t = SafeRead(argo);
                int n = Regex.Matches(t, "windowsHide").Count;
                sb.AppendLine(n >= 2
                    ? "[OK]   ② argo-search 子进程不弹控制台（windowsHide 两处都在）"
                    : "[FAIL] ② argo-search 缺 windowsHide（" + n + "/2 处）—— 检索时会弹控制台（可一键重打）");
            }

            // ③ pyyaml
            string py = SysPython();
            if (py == null)
                sb.AppendLine("[WARN] ③ 找不到 python（argo 后端需要它）");
            else
            {
                string v = PyYamlVersion();
                sb.AppendLine(v != null
                    ? "[OK]   ③ pyyaml 已装（" + v + "）→ 冷启动不会反复退回默认配置"
                    : "[FAIL] ③ 系统 python 缺 pyyaml → argo 反复失败会拖慢冷启动（可一键装）");
            }

            // ④ prompt 组装的「字面量花括号」防线（唯一一类会让 DSH 起不来的补丁）
            {
                string lib = MemoryEvolveLibDir();
                string fIndex = Path.Combine(lib, "index.js");
                string fPrompts = Path.Combine(lib, "prompts.js");
                bool memInstalled = File.Exists(fIndex) || File.Exists(fPrompts);
                if (!memInstalled)
                {
                    sb.AppendLine("[?]    ④ 未装 dsh-memory-evolve，跳过花括号防线");
                }
                else
                {
                    bool a1 = HasBraceEscape(fIndex);
                    bool a2 = HasBraceEscape(fPrompts);
                    bool guard = File.Exists(Path.Combine(PromptGuardPluginDir(), "lib", "index.js"));
                    bool wired = GuardWired(ProfilePatchPath());
                    List<string> where;
                    int braces = CountInjectableLiteralBraces(out where);

                    if (a1 && a2 && guard && wired)
                        sb.AppendLine("[OK]   ④ prompt 组装的字节防线完整（上游转义两处 + 守卫已装并已登记）");
                    else
                    {
                        sb.AppendLine("[FAIL] ④ prompt 组装防线不完整 —— **这会让 DSH 直接起不来**（不是弹窗问题）");
                        sb.AppendLine("         上游转义 index.js  : " + (a1 ? "在" : "**缺**"));
                        sb.AppendLine("         上游转义 prompts.js: " + (a2 ? "在" : "**缺**"));
                        sb.AppendLine("         守卫插件已安装      : " + (guard ? "在" : "**缺**"));
                        sb.AppendLine("         守卫已登记到 patch  : " + (wired ? "在" : "**缺**（登记行可能被别的写者覆盖）"));
                        sb.AppendLine("         → 点「补丁体检/重打」可补齐前两项与登记行（守卫本体需另有安装包）");
                    }
                    if (braces > 0)
                    {
                        sb.AppendLine("         [i] 可注入记忆里还有字面量 {{ 共 " + braces + " 处（" + string.Join("、", where.ToArray()) + "）");
                        sb.AppendLine("             安全的前提是上面两层齐全；建议顺手把它们改成 `{ {`（治本，不再依赖补丁）");
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine("结论：点下面同一个按钮即可全部重打/补齐。补丁生效**需要重启 DSH**。");
            return sb.ToString();
        }

        /// <summary>重打补丁：DSH CREATE_NO_WINDOW + argo windowsHide + pyyaml。</summary>
        public static string RepairPatches()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== 环境补丁重打 ====");
            sb.AppendLine();

            // ① DSH
            string dsh = DshWin32ProcPath();
            if (File.Exists(dsh))
            {
                string t = SafeRead(dsh);
                bool declared = t.IndexOf("const CREATE_NO_WINDOW = 0x08000000;", StringComparison.Ordinal) >= 0;
                int calls = Regex.Matches(t, @"buildCommandLine\(options\.command, options\.args\), (0|4), startupInfo").Count;
                if (declared && calls == 0)
                {
                    sb.AppendLine("[OK]   ① DSH 补丁已就位，无需重打。");
                }
                else
                {
                    string anchor = "function createRestrictedProcess(api, options, commandLine, creationFlags, startupInfo, processInfo) {";
                    if (t.IndexOf(anchor, StringComparison.Ordinal) < 0)
                    {
                        sb.AppendLine("[FAIL] ① DSH 源码结构变了（找不到 createRestrictedProcess），未改动。");
                    }
                    else
                    {
                        try
                        {
                            SafeConfig.BackupFile(dsh);
                            string r = t;
                            if (!declared)
                                r = r.Replace(anchor,
                                    "/** Local patch (by " + DshCore.AppName + "): 子进程不分配控制台窗口。 */\r\n" +
                                    "const CREATE_NO_WINDOW = 0x08000000;\r\n" + anchor);
                            r = Regex.Replace(r,
                                @"buildCommandLine\(options\.command, options\.args\), 0, startupInfo",
                                "buildCommandLine(options.command, options.args), CREATE_NO_WINDOW, startupInfo");
                            r = Regex.Replace(r,
                                @"buildCommandLine\(options\.command, options\.args\), 4, startupInfo",
                                "buildCommandLine(options.command, options.args), 4 | CREATE_NO_WINDOW, startupInfo");
                            SafeConfig.AtomicWriteText(dsh, r);
                            sb.AppendLine("[OK]   ① 已重打 DSH 的 CREATE_NO_WINDOW（备份见同目录 .bak-*）。**需重启 DSH 生效**。");
                        }
                        catch (Exception e) { sb.AppendLine("[FAIL] ① 重打失败：" + e.Message); }
                    }
                }
            }
            else sb.AppendLine("[?]    ① 找不到 dsh-win32-process，跳过。");

            // ② argo
            string argo = ArgoJsPath();
            if (File.Exists(argo))
            {
                string t = SafeRead(argo);
                if (Regex.Matches(t, "windowsHide").Count >= 2)
                {
                    sb.AppendLine("[OK]   ② argo 补丁已就位，无需重打。");
                }
                else
                {
                    try
                    {
                        SafeConfig.BackupFile(argo);
                        string r = t;
                        r = r.Replace("    stdio: 'inherit',\r\n    env,\r\n  });",
                                      "    stdio: 'inherit',\r\n    env,\r\n    windowsHide: true,\r\n  });");
                        r = r.Replace("    stdio: ['pipe', 'pipe', 'inherit'],\r\n    env,\r\n  });",
                                      "    stdio: ['pipe', 'pipe', 'inherit'],\r\n    env,\r\n    windowsHide: true,\r\n  });");
                        // 兼容 LF 行尾
                        r = r.Replace("    stdio: 'inherit',\n    env,\n  });",
                                      "    stdio: 'inherit',\n    env,\n    windowsHide: true,\n  });");
                        r = r.Replace("    stdio: ['pipe', 'pipe', 'inherit'],\n    env,\n  });",
                                      "    stdio: ['pipe', 'pipe', 'inherit'],\n    env,\n    windowsHide: true,\n  });");
                        int now = Regex.Matches(r, "windowsHide").Count;
                        if (now >= 2)
                        {
                            SafeConfig.AtomicWriteText(argo, r);
                            sb.AppendLine("[OK]   ② 已重打 argo 的 windowsHide。杀掉 argo 的 MCP 进程即可生效（救星不必重启）。");
                        }
                        else
                            sb.AppendLine("[WARN] ② argo 源码结构与预期不符（windowsHide 仍 " + now + "/2），未写入。");
                    }
                    catch (Exception e) { sb.AppendLine("[FAIL] ② 重打失败：" + e.Message); }
                }
            }
            else sb.AppendLine("[?]    ② 找不到 argo-search，跳过。");

            // ③ pyyaml
            string py = SysPython();
            if (py == null) sb.AppendLine("[WARN] ③ 找不到 python，跳过。");
            else
            {
                string v = PyYamlVersion();
                if (v != null)
                    sb.AppendLine("[OK]   ③ pyyaml 已在（" + v + "）。");
                else
                {
                    sb.AppendLine("       正在安装 pyyaml…（" + py + "）");
                    DshCore.RunHidden(py, "-m pip install pyyaml --quiet --disable-pip-version-check", 180000);
                    string v2 = PyYamlVersion();
                    sb.AppendLine(v2 != null
                        ? "[OK]   ③ pyyaml 安装成功（" + v2 + "）。"
                        : "[FAIL] ③ pyyaml 安装后仍不可用，请手动执行：\"" + py + "\" -m pip install pyyaml");
                }
            }

            // ④ prompt 组装的「字面量花括号」防线
            {
                string lib = MemoryEvolveLibDir();
                string fIndex = Path.Combine(lib, "index.js");
                string fPrompts = Path.Combine(lib, "prompts.js");
                bool any = File.Exists(fIndex) || File.Exists(fPrompts);
                if (!any) sb.AppendLine("[?]    ④ 未装 dsh-memory-evolve，跳过。");
                else
                {
                    // (a) 上游转义：锚点与上游 repatch-memory-evolve.mjs 逐字一致，找不到就**不改**
                    if (!File.Exists(fIndex))
                        sb.AppendLine("[?]    ④ prompts.js/index.js 不全，跳过对应项。");
                    else
                    {
                        if (HasBraceEscape(fIndex))
                            sb.AppendLine("[OK]   ④-a memory-evolve index.js 转义已就位。");
                        else
                        {
                            string err;
                            bool ok = TryPatchAfter(fIndex,
                                "export function renderSnapshot(",
                                "\n  return parts.join('\\n\\n')",
                                "\n  // Literal {{...}} in memory text must not be read as a prompt variable by\n" +
                                "  // dsh-system-prompt (it throws on unknown references); escape the pair.\n" +
                                "  return parts.map((s) => s.replaceAll('{{', '{ {')).join('\\n\\n')",
                                out err);
                            sb.AppendLine(ok
                                ? "[OK]   ④-a 已重打 memory-evolve index.js 的转义（备份 .bak-*）。**需重启 DSH 生效**。"
                                : "[FAIL] ④-a 重打失败：" + err);
                        }
                    }
                    if (File.Exists(fPrompts))
                    {
                        if (HasBraceEscape(fPrompts))
                            sb.AppendLine("[OK]   ④-b memory-evolve prompts.js 转义已就位。");
                        else
                        {
                            string err;
                            bool ok = TryPatchAfter(fPrompts,
                                "function renderInjectionSnapshot(",
                                "\n  return lines.join('\\n')",
                                "\n  // Same escape as renderSnapshot: user rules must reach the model verbatim.\n" +
                                "  return lines.map((line) => line.replaceAll('{{', '{ {')).join('\\n')",
                                out err);
                            sb.AppendLine(ok
                                ? "[OK]   ④-b 已重打 memory-evolve prompts.js 的转义（备份 .bak-*）。**需重启 DSH 生效**。"
                                : "[FAIL] ④-b 重打失败：" + err);
                        }
                    }

                    // (c) 守卫登记行（守卫本体若没装，只能如实提示）
                    string patch = ProfilePatchPath();
                    bool guard = File.Exists(Path.Combine(PromptGuardPluginDir(), "lib", "index.js"));
                    if (GuardWired(patch))
                        sb.AppendLine("[OK]   ④-c 守卫已登记在 cordis.patch.yml。");
                    else if (!guard)
                        sb.AppendLine("[WARN] ④-c 守卫本体没装（缺 " + PromptGuardPluginDir() + "），无法自动补登记行。");
                    else
                    {
                        try
                        {
                            SafeConfig.BackupFile(patch);
                            string cur = File.Exists(patch) ? File.ReadAllText(patch) : "";
                            string added = (cur.Length > 0 && !cur.EndsWith("\n") ? cur + "\r\n" : cur) +
                                           (cur.Length > 0 ? "\r\n" : "") + GuardInsertBlock;
                            SafeConfig.AtomicWriteText(patch, added);
                            sb.AppendLine("[OK]   ④-c 已把守卫的登记行补回 cordis.patch.yml（备份 .bak-*）。**热生效**（patchReload: live）。");
                        }
                        catch (Exception e) { sb.AppendLine("[FAIL] ④-c 补登记行失败：" + e.Message); }
                    }
                }
            }

            sb.AppendLine();
            return sb.ToString();
        }

        // ============================================================
        // 二、弹窗体检：采样 N 秒，报出"谁在新建控制台"
        // ============================================================

        public static string PopupProbe(int seconds)
        {
            if (seconds < 3) seconds = 3;
            if (seconds > 30) seconds = 30;

            var sb = new StringBuilder();
            sb.AppendLine("==== 弹窗体检（采样 " + seconds + " 秒）====");
            sb.AppendLine("原理：控制台窗口只在「某个进程新建了控制台程序」时出现。");
            sb.AppendLine("      所以盯住这段时间里新建的 powershell/conhost/WindowsTerminal，看它们的父进程是谁。");
            sb.AppendLine();
            sb.Append(TerminalCheck());
            sb.AppendLine();
            sb.AppendLine("── 采样中，请稍候（这期间请不要点其它按钮）──");

            var psi = new StringBuilder();
            psi.Append("$ErrorActionPreference='SilentlyContinue';");
            psi.Append("$seen=@{};");
            psi.Append("$t0=Get-Date;");
            psi.Append("while(((Get-Date)-$t0).TotalSeconds -lt " + seconds.ToString(CultureInfo.InvariantCulture) + "){");
            psi.Append(" Get-CimInstance Win32_Process -Filter \"Name='powershell.exe' OR Name='conhost.exe' OR Name='OpenConsole.exe' OR Name='WindowsTerminal.exe' OR Name='cmd.exe' OR Name='python.exe' OR Name='wscript.exe' OR Name='cscript.exe'\" |");
            psi.Append("   ForEach-Object { if(-not $seen.ContainsKey($_.ProcessId)){ $seen[$_.ProcessId]=$_.ParentProcessId } };");
            psi.Append(" Start-Sleep -Milliseconds 200 };");
            psi.Append("$all=@{}; Get-CimInstance Win32_Process | ForEach-Object { $all[[int]$_.ProcessId]=$_.Name };");
            psi.Append("if($seen.Count -eq 0){ '(采样期间没有任何新控制台进程 —— 很干净)' } else {");
            psi.Append(" foreach($k in $seen.Keys){ '{0,-20} PID {1,-7} 父={2}' -f $all[[int]$k], $k, $all[[int]$seen[$k]] } };");
            psi.Append("'---';");
            psi.Append("'WindowsTerminal 进程数: ' + (Get-Process WindowsTerminal -ErrorAction SilentlyContinue | Measure-Object).Count");

            string outp = DshCore.RunPsEncoded(psi.ToString(), (seconds + 25) * 1000);
            sb.AppendLine();
            if (string.IsNullOrEmpty(outp))
                sb.AppendLine("[FAIL] 采样失败（脚本超时或被拦）。");
            else
            {
                sb.AppendLine("── 新建的控制台进程及其父进程 ──");
                sb.AppendLine(outp.TrimEnd());
                sb.AppendLine();
                sb.AppendLine("判读方法：");
                sb.AppendLine("  · 父=node.exe            → DSH 在跑命令；若频繁出现且你并没在操作，是有插件在轮询");
                sb.AppendLine("  · 父=asus_*/ArmouryCrate → 华硕软件（本机实测每 30 秒一次，属它的正常行为）");
                sb.AppendLine("  · 父=explorer/svchost    → 系统或商店应用激活");
                sb.AppendLine("  · 只要 WindowsTerminal 进程数 > 0，新控制台就会变成大终端窗口 → 见上方「默认终端」建议");
            }
            return sb.ToString();
        }

        // ============================================================
        // 三、默认终端体检 / 降噪
        // ============================================================

        public static string TerminalCheck()
        {
            var sb = new StringBuilder();
            sb.AppendLine("── 默认终端应用程序（决定新控制台长什么样）──");
            string console = ReadDelegation("DelegationConsole");
            string terminal = ReadDelegation("DelegationTerminal");
            if (console == null && terminal == null)
            {
                sb.AppendLine("[?]   读不到 HKCU\\Console\\%%Startup（尚未设置过，由 Windows 决定）");
                return sb.ToString();
            }
            sb.AppendLine("  DelegationConsole  = " + Describe(console));
            sb.AppendLine("  DelegationTerminal = " + Describe(terminal));

            int wt = 0;
            try { wt = System.Diagnostics.Process.GetProcessesByName("WindowsTerminal").Length; } catch { }
            sb.AppendLine("  WindowsTerminal 正在运行的实例数：" + wt);

            bool wtSelected = IsGuid(terminal, GuidWindowsTerminal) || IsGuid(console, GuidWindowsTerminal);
            bool decide = IsGuid(terminal, GuidLetWindowsDecide) && IsGuid(console, GuidLetWindowsDecide);

            if (wtSelected || wt > 0)
            {
                sb.AppendLine();
                sb.AppendLine("  ★ 结论：新控制台会被交给 Windows Terminal → 每次都是一整个大终端窗口弹出来。");
                sb.AppendLine("     这**不是问题本身**（控制台该建还是会建），但它把「看不见的小动作」放大成「看得见的大窗口」。");
                sb.AppendLine("     想彻底安静：点这个按钮把它改成「Windows 控制台主机」，或在 Windows Terminal");
                sb.AppendLine("     设置 → 启动 → 默认终端应用程序 里手动改。改完仍可用 Terminal 正常开终端。");
            }
            else if (decide)
            {
                sb.AppendLine("  （由 Windows 决定；若 WindowsTerminal 实例数为 0，通常不会被接管）");
            }
            else
            {
                sb.AppendLine("  ✅ 已是 Windows 控制台主机（新控制台不会变成大终端窗口）");
            }
            return sb.ToString();
        }

        /// <summary>把默认终端应用程序设为「Windows 控制台主机」（消除大终端窗口弹出）。</summary>
        public static string SetTerminalToConhost()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Console\%%Startup", true))
                {
                    if (k == null)
                        return "改不了：HKCU\\Console\\%%Startup 不存在（可先手动开一次「设置 → 默认终端应用程序」）。";
                    k.SetValue("DelegationConsole", GuidConhost, RegistryValueKind.String);
                    k.SetValue("DelegationTerminal", GuidConhost, RegistryValueKind.String);
                }
                return "✅ 已把默认终端应用程序改为「Windows 控制台主机」。\r\n" +
                       "   以后任何程序新建控制台都不会再变成 Windows Terminal 大窗口。\r\n" +
                       "   想改回去：Windows Terminal 设置 → 启动 → 默认终端应用程序 → 选 Windows Terminal。\r\n" +
                       "   注意：DSH「打开无痕/打开界面」仍照常可用，不受影响。";
            }
            catch (Exception e)
            {
                return "修改失败：" + e.Message;
            }
        }

        // ---- 小工具 ----
        private static string SafeRead(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8, true))
                    return sr.ReadToEnd();
            }
            catch { return ""; }
        }

        private static string ReadDelegation(string name)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Console\%%Startup", false))
                {
                    if (k == null) return null;
                    object v = k.GetValue(name);
                    return v == null ? null : v.ToString();
                }
            }
            catch { return null; }
        }

        private static bool IsGuid(string value, string expected)
        {
            return value != null && value.Trim().Equals(expected, StringComparison.OrdinalIgnoreCase);
        }

        private static string Describe(string guid)
        {
            if (guid == null) return "(未设置)";
            string g = guid.Trim();
            if (IsGuid(g, GuidConhost)) return g + "  = Windows 控制台主机（安静）";
            if (IsGuid(g, GuidWindowsTerminal)) return g + "  = Windows Terminal（会弹大窗口）";
            if (IsGuid(g, GuidLetWindowsDecide)) return g + "  = 让 Windows 决定";
            return g;
        }
    }
}

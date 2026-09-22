using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

// ============================================================
// RepairPlan.cs — WP4「自动修复闭环形式化」
//
// 目标：把原来散落在各处的修复动作（去重 / 清悬空 / 启用禁用条目 / 重装插件 /
//       清残留 / 换端口 / 修补丁防线）统一成**一个可预演、可复验、可回滚的模型**：
//
//   动作 = { id, 名称, 适用类别, 前置条件, 预演(将改哪个文件的哪一行), 执行, 复验判据, 回滚方式 }
//
// 三条硬纪律（对应任务书 WP4 验收）：
//   ① `--plan-fix` **只预演不执行**，且必须指出**具体文件 + 行号 + 该行原文**；
//   ② 执行走 `--fix-apply`，**先快照 → 执行 → 复验**；复验不通过 ⇒ **自动回滚**；
//   ③ 判据按**报错文本/结构**走，不认任何插件名单（换任何插件都成立）。
//
// C#5 约束：不用字符串插值、不用 ?. 、不用表达式体成员。
// ============================================================
namespace BigFatFishRescuer
{
    internal sealed class RepairAction
    {
        public string Id;
        public string Title;
        public string Categories;      // 适用类别（| 分隔；空=通用）
        public string Preconditions;   // 前置条件（人话）
        public string DryHint;         // 预演要看什么
        public string VerifyCriteria;  // 复验判据
        public string Rollback;        // 回滚方式
        public bool NeedsId;
        public bool NeedsPkg;
        public bool NeedsPort;
    }

    internal static class RepairPlan
    {
        // ---------- 动作表（这就是「模型」的落地） ----------
        public static RepairAction[] All()
        {
            List<RepairAction> l = new List<RepairAction>();

            RepairAction a1 = new RepairAction();
            a1.Id = "dedup";
            a1.Title = "去掉同一文件里重复的 loader entry id";
            a1.Categories = "插件树加载失败|重复条目";
            a1.Preconditions = "某个 id 在同一份 cordis.patch.yml 里出现 ≥2 次（DSH 会抛 duplicate loader entry id 并秒退）";
            a1.DryHint = "列出重复 id 及每一处所在文件与行号；不改文件";
            // ★ 口径修正（WP4 端到端实测踩到）：产品去重的做法是把重复的 `- insert:` 子项
            //   转成**顶层 id-targeted patch**（既去重、又保住它原本带的 config）。
            //   所以字面 `- id: <x>` 仍会保留；旧判据「只出现 1 次」会导致**明明修好了却判失败并自动回滚**。
            a1.VerifyCriteria = "改后该 id 在 `- insert:` 块内**不再重复**（会真崩的重复 = 0）；"
                              + "顶层 `- id:` 形式的 id-patch 允许保留；且 YAML 仍能解析";
            a1.Rollback = "写前自动快照到 AppDataDir\\config-snapshots；失败即回滚原文件";
            a1.NeedsId = true;
            l.Add(a1);

            RepairAction a2 = new RepairAction();
            a2.Id = "fixdangling";
            a2.Title = "移除悬空引用（bundles/dependencies 指向没装的包）";
            a2.Categories = "插件树加载失败|依赖解析不到|包管理器/构建链失败（pnpm）";
            a2.Preconditions = "profiles/web/package.json 里列了某包，但 profiles/web/node_modules 与 profiles/node_modules 里都找不到它";
            a2.DryHint = "指出悬空包名 + 它出现在 package.json 的第几行；不改文件";
            a2.VerifyCriteria = "改后 package.json 里不再出现该包名，且 JSON 仍然合法";
            a2.Rollback = "写前快照；JSON 校验失败即回滚";
            a2.NeedsPkg = true;
            l.Add(a2);

            RepairAction a3 = new RepairAction();
            a3.Id = "disableentry";
            a3.Title = "禁用某个 loader entry（临时摘掉出问题的插件）";
            a3.Categories = "插件树加载失败|依赖解析不到|前端报错";
            a3.Preconditions = "该 id 存在于 profile 或 home 的 cordis.patch.yml";
            a3.DryHint = "指出将把哪一行改成 disabled，并列出改后该条目的样子";
            a3.VerifyCriteria = "改后 YAML 可解析，且该条目 disabled 为真";
            a3.Rollback = "写前快照；YAML 校验失败即回滚";
            a3.NeedsId = true;
            l.Add(a3);

            RepairAction a4 = new RepairAction();
            a4.Id = "enableentry";
            a4.Title = "重新启用某个被禁用的 entry";
            a4.Categories = "服务等待中（pending）|依赖解析不到";
            a4.Preconditions = "该 id 在 patch 层里被 disabled: true";
            a4.DryHint = "指出将把哪一行 disabled 去掉";
            a4.VerifyCriteria = "改后 YAML 可解析，且该条目不再 disabled";
            a4.Rollback = "写前快照；失败即回滚";
            a4.NeedsId = true;
            l.Add(a4);

            RepairAction a5 = new RepairAction();
            a5.Id = "reinstall";
            a5.Title = "重装某个插件包（半装 / main 文件缺失 / 装了一半）";
            a5.Categories = "插件树加载失败|依赖解析不到|包管理器/构建链失败（pnpm）";
            a5.Preconditions = "该包已在 profile dependencies 里，或能解析到 npm 上的实体";
            a5.DryHint = "列出将要执行的命令与目标目录；不执行";
            a5.VerifyCriteria = "重装后该包的入口文件（package.json 的 main/module）**真实存在**";
            a5.Rollback = "重装是覆盖式操作；失败时报出原始 pnpm 输出，配置不动";
            a5.NeedsPkg = true;
            l.Add(a5);

            RepairAction a6 = new RepairAction();
            a6.Id = "stopleftover";
            a6.Title = "清掉残留 / 半死的 dsh 进程（含占着端口的）";
            a6.Categories = "端口被占|半死实例|残留进程";
            a6.Preconditions = "存在命令行指向 dsh bin.js 的 node 进程，或当前端口被这类进程占用";
            a6.DryHint = "列出将被结束的 PID + 身份判据结论；一个都不杀（等价 --plan-kill）";
            a6.VerifyCriteria = "动作后端口不再被 dsh 占用，且能重新拉起并响应 HTTP";
            a6.Rollback = "结束进程不可逆；但**非 dsh 进程一律跳过**（A1），不会误杀";
            l.Add(a6);

            RepairAction a7 = new RepairAction();
            a7.Id = "switchport";
            a7.Title = "换端口（端口被**非 dsh** 程序占着时，不去杀它，改自己换）";
            a7.Categories = "端口被占";
            a7.Preconditions = "目标端口被占用，且占用者**不是** dsh（否则应该清残留而不是换端口）";
            a7.DryHint = "指出将写入 port.txt 的路径与内容（单行端口号）";
            a7.VerifyCriteria = "改后 ReadPortOverride() 返回新端口，且该端口空闲";
            a7.Rollback = "删除/还原 port.txt 即回到自动发现";
            a7.NeedsPort = true;
            l.Add(a7);

            RepairAction a8 = new RepairAction();
            a8.Id = "repairpatch";
            a8.Title = "重打「弹窗/终端/防线」补丁（DSH 升级后补丁被覆盖）";
            a8.Categories = "前端报错|补丁失效";
            a8.Preconditions = "PatchGuard.CheckPatches() 报告有缺失补丁";
            a8.DryHint = "列出缺哪些补丁、各自要写进哪个 node_modules 文件的第几处";
            a8.VerifyCriteria = "重打后 CheckPatches() 全绿";
            a8.Rollback = "补丁目标在 node_modules 内，DSH 升级本就会覆盖；PatchGuard 自带备份";
            l.Add(a8);

            return l.ToArray();
        }

        // ---------- 小工具 ----------

        private static string ProfileDir()
        {
            return Path.Combine(DshCore.DshHome, "profiles", "web");
        }

        /// <summary>在文件里找所有含 needle 的行，返回 "  行号: 原文" 列表。</summary>
        public static List<string> FindLines(string file, string needle)
        {
            List<string> res = new List<string>();
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return res;
            try
            {
                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                        res.Add("  " + Path.GetFileName(file) + " 第 " + (i + 1) + " 行: " + lines[i].Trim());
                }
            }
            catch { }
            return res;
        }

        private static string[] PatchFiles()
        {
            return new string[]
            {
                Path.Combine(ProfileDir(), "cordis.patch.yml"),
                Path.Combine(DshCore.DshHome, "cordis.patch.yml"),
            };
        }

        /// <summary>扫出所有重复出现的 entry id（同一文件里 ≥2 次）。</summary>
        public static Dictionary<string, List<string>> DuplicateIds()
        {
            Dictionary<string, List<string>> res = new Dictionary<string, List<string>>();
            foreach (string f in PatchFiles())
            {
                if (!File.Exists(f)) continue;
                Dictionary<string, int> cnt = new Dictionary<string, int>();
                List<string> loc = new List<string>();
                try
                {
                    string[] lines = File.ReadAllLines(f, Encoding.UTF8);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        Match m = Regex.Match(lines[i], @"^\s*-?\s*id\s*:\s*([A-Za-z0-9@_\-/\.]+)\s*$");
                        if (!m.Success) continue;
                        string id = m.Groups[1].Value;
                        int c;
                        cnt.TryGetValue(id, out c);
                        cnt[id] = c + 1;
                        loc.Add(id + "\t" + Path.GetFileName(f) + " 第 " + (i + 1) + " 行: " + lines[i].Trim());
                    }
                }
                catch { continue; }
                foreach (KeyValuePair<string, int> kv in cnt)
                {
                    if (kv.Value < 2) continue;
                    if (!res.ContainsKey(kv.Key)) res[kv.Key] = new List<string>();
                    foreach (string s in loc)
                        if (s.StartsWith(kv.Key + "\t")) res[kv.Key].Add(s.Substring(kv.Key.Length + 1));
                }
            }
            return res;
        }

        /// <summary>
        /// 只数**会真崩**的那种重复：同一个 id 在 `- insert:` 块里出现 ≥2 次。
        /// ★ 为什么必须与 DuplicateIds() 分开（2026-09-18 WP4 端到端验收抓到的真缺陷）：
        ///   dedup 的修法是「把重复的 `- insert:` 子项转成顶层 `- id:` 补丁」——
        ///   这正是 DSH 的语义：`- insert:` 是**创建** row，同 id 建两次才抛
        ///   `duplicate loader entry id`；顶层 `- id:` 是**按 id 修改已存在 row**，
        ///   目标缺失只 warn。可是旧复验用的是 DuplicateIds()，它把**所有** `id:` 行都算进去
        ///   ⇒ 修好之后 id 仍然出现 2 次（一次在 insert 里、一次在顶层）⇒ 复验误判失败
        ///   ⇒ 触发自动回滚，**把刚修好的又撤回去了**。
        ///   实测：`--fix-apply dedup --id dshmarket` 永远报「复验未通过」，文件字节数不变。
        ///   ⇒ **扫描**与**复验**必须是两个口径：扫描看"哪里值得注意"，
        ///     复验只认"会不会真崩"。
        /// </summary>
        public static Dictionary<string, List<string>> DuplicateInsertIds()
        {
            Dictionary<string, List<string>> res = new Dictionary<string, List<string>>();
            foreach (string f in PatchFiles())
            {
                if (!File.Exists(f)) continue;
                Dictionary<string, int> cnt = new Dictionary<string, int>();
                List<string> loc = new List<string>();
                try
                {
                    string[] lines = File.ReadAllLines(f, Encoding.UTF8);
                    bool inBlock = false;
                    int blockIndent = -1;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string raw = lines[i];
                        string t = raw.Trim();
                        if (t.Length == 0 || t.StartsWith("#")) continue;
                        int ind = raw.Length - raw.TrimStart().Length;
                        if (t.StartsWith("- insert:"))
                        { inBlock = true; blockIndent = ind; continue; }
                        if (inBlock && ind <= blockIndent && t.StartsWith("- "))
                        { inBlock = false; }                 // 缩进回到块外 ⇒ 离开 insert 块
                        if (!inBlock) continue;
                        Match m = Regex.Match(t, @"^-?\s*id\s*:\s*([A-Za-z0-9@_\-/\.]+)\s*$");
                        if (!m.Success) continue;
                        string id2 = m.Groups[1].Value;
                        int c;
                        cnt.TryGetValue(id2, out c);
                        cnt[id2] = c + 1;
                        loc.Add(id2 + "\t" + Path.GetFileName(f) + " 第 " + (i + 1) + " 行: " + t);
                    }
                }
                catch { continue; }
                foreach (KeyValuePair<string, int> kv in cnt)
                {
                    if (kv.Value < 2) continue;
                    if (!res.ContainsKey(kv.Key)) res[kv.Key] = new List<string>();
                    foreach (string s in loc)
                        if (s.StartsWith(kv.Key + "\t")) res[kv.Key].Add(s.Substring(kv.Key.Length + 1));
                }
            }
            return res;
        }

        /// <summary>扫出 package.json 里列了、但 node_modules 找不到的包（悬空引用）。</summary>
        public static List<string> DanglingPackages(out List<string> lines)
        {
            lines = new List<string>();
            List<string> res = new List<string>();
            string pj = Path.Combine(ProfileDir(), "package.json");
            if (!File.Exists(pj)) return res;
            string text = "";
            try { text = File.ReadAllText(pj, Encoding.UTF8); } catch { return res; }
            string[] nmRoots = new string[]
            {
                Path.Combine(ProfileDir(), "node_modules"),
                Path.Combine(DshCore.DshHome, "profiles", "node_modules"),
                Path.Combine(DshCore.DshHome, "node_modules"),
            };
            MatchCollection ms = Regex.Matches(text, "\"([@A-Za-z0-9_\\-\\./]+)\"\\s*:\\s*\"\\^?[0-9]");
            List<string> seen = new List<string>();
            foreach (Match m in ms)
            {
                string pkg = m.Groups[1].Value;
                if (pkg == "name") continue;
                if (seen.Contains(pkg)) continue;
                seen.Add(pkg);
                bool found = false;
                foreach (string r in nmRoots)
                {
                    try { if (Directory.Exists(Path.Combine(r, pkg.Replace('/', Path.DirectorySeparatorChar)))) { found = true; break; } }
                    catch { }
                }
                if (found) continue;
                // bundles 里的裸名（不带版本号）也可能悬空
                res.Add(pkg);
                lines.AddRange(FindLines(pj, "\"" + pkg + "\""));
            }
            // bundles 数组里的成员（没有版本号，上面的正则抓不到）
            Match mb = Regex.Match(text, "\"bundles\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
            if (mb.Success)
            {
                foreach (Match m2 in Regex.Matches(mb.Groups[1].Value, "\"([^\"]+)\""))
                {
                    string pkg = m2.Groups[1].Value;
                    if (seen.Contains(pkg)) continue;
                    seen.Add(pkg);
                    bool found = false;
                    foreach (string r in nmRoots)
                    {
                        try { if (Directory.Exists(Path.Combine(r, pkg.Replace('/', Path.DirectorySeparatorChar)))) { found = true; break; } }
                        catch { }
                    }
                    if (!found) { res.Add(pkg); lines.AddRange(FindLines(pj, "\"" + pkg + "\"")); }
                }
            }
            return res;
        }

        /// <summary>扫出被禁用的条目。</summary>
        public static List<string> DisabledEntries()
        {
            List<string> res = new List<string>();
            foreach (string f in PatchFiles())
            {
                if (!File.Exists(f)) continue;
                try
                {
                    string[] lines = File.ReadAllLines(f, Encoding.UTF8);
                    for (int i = 0; i < lines.Length; i++)
                        if (Regex.IsMatch(lines[i], @"disabled\s*:\s*true"))
                            res.Add(Path.GetFileName(f) + " 第 " + (i + 1) + " 行: " + lines[i].Trim());
                }
                catch { }
            }
            return res;
        }

        // ---------- 预演 ----------

        public static string Report(string filter, string id, string pkg, int port)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 大肥鱼救星 v" + DshCore.AppVersion + " · 修复动作预演（--plan-fix）==");
            sb.AppendLine("★ 本命令**只预演，不执行**：不写任何文件、不碰任何进程。");
            sb.AppendLine("DSH home：" + DshCore.DshHome);
            sb.AppendLine("筛选：" + (string.IsNullOrEmpty(filter) ? "(全部)" : filter));
            sb.AppendLine();

            // 先给一份「当前状态扫描」，这样"该修什么"不是靠猜
            sb.AppendLine("---- 当前状态扫描 ----");
            // ★ 2026-09-18：扫描输出也要区分两种"重复"，否则用户修好了还会看到"重复 entry id：1 个"
            //   （顶层 `- id:` 是 DSH 官方允许的 id-targeted patch，只 warn、不崩）。
            Dictionary<string, List<string>> dins = DuplicateInsertIds();
            if (dins.Count == 0) sb.AppendLine("  ★ 会真崩的重复 insert id：无");
            else
            {
                sb.AppendLine("  ★ 会真崩的重复 insert id：" + dins.Count
                              + " 个（DSH 会抛 duplicate loader entry id 并秒退）");
                foreach (KeyValuePair<string, List<string>> kv in dins)
                {
                    sb.AppendLine("    · " + kv.Key + "（" + kv.Value.Count + " 处）");
                    foreach (string s in kv.Value) sb.AppendLine("        " + s);
                }
            }
            Dictionary<string, List<string>> dups = DuplicateIds();
            if (dups.Count == 0) sb.AppendLine("  同 id 出现多次（含顶层 id-patch，官方允许、只 warn）：无");
            else
            {
                sb.AppendLine("  同 id 出现多次（含顶层 id-patch，官方允许、只 warn）：" + dups.Count + " 个");
                foreach (KeyValuePair<string, List<string>> kv in dups)
                {
                    sb.AppendLine("    · " + kv.Key + "（" + kv.Value.Count + " 处）");
                    foreach (string s in kv.Value) sb.AppendLine("        " + s);
                }
            }
            List<string> dangLines;
            List<string> dang = DanglingPackages(out dangLines);
            if (dang.Count == 0) sb.AppendLine("  悬空引用：无");
            else
            {
                sb.AppendLine("  悬空引用：" + dang.Count + " 个 -> " + string.Join(", ", dang.ToArray()));
                foreach (string s in dangLines) sb.AppendLine("        " + s);
            }
            List<string> dis = DisabledEntries();
            sb.AppendLine("  被禁用条目：" + (dis.Count == 0 ? "无" : dis.Count + " 处"));
            foreach (string s in dis) sb.AppendLine("        " + s);
            sb.AppendLine("  当前端口：" + DshCore.ActivePort
                          + "（显式覆盖：" + (DshBoot.ReadPortOverride() > 0 ? DshBoot.ReadPortOverride().ToString() : "无") + "）");
            int[] dp = DshCore.GetDshPids();
            int[] ow = DshCore.GetPortOwnerPidsFast(DshCore.ActivePort);
            sb.AppendLine("  dsh 进程：" + (dp.Length == 0 ? "无" : string.Join(",", ToStr(dp))));
            sb.AppendLine("  端口占用者：" + (ow.Length == 0 ? "无" : string.Join(",", ToStr(ow))));
            sb.AppendLine();

            sb.AppendLine("---- 动作模型 ----");
            int n = 0;
            foreach (RepairAction a in All())
            {
                if (!string.IsNullOrEmpty(filter) && filter != "all" && filter != "*")
                {
                    bool match = a.Id == filter
                        || a.Categories.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                        || a.Title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!match) continue;
                }
                n++;
                sb.AppendLine();
                sb.AppendLine("[" + n + "] " + a.Id + " — " + a.Title);
                sb.AppendLine("    适用类别：" + (string.IsNullOrEmpty(a.Categories) ? "(通用)" : a.Categories));
                sb.AppendLine("    前置条件：" + a.Preconditions);
                sb.AppendLine("    预演内容：" + a.DryHint);
                sb.AppendLine("    ★ 将改哪里：");
                foreach (string s in LocateLines(a, id, pkg, port)) sb.AppendLine("        " + s);
                sb.AppendLine("    等价命令：" + CmdFor(a, id, pkg, port));
                sb.AppendLine("    复验判据：" + a.VerifyCriteria);
                sb.AppendLine("    回滚方式：" + a.Rollback);
            }
            if (n == 0)
            {
                sb.AppendLine();
                sb.AppendLine("  （没有匹配 '" + filter + "' 的动作。可用 id：" +
                              string.Join(" / ", Ids()) + "）");
            }
            sb.AppendLine();
            sb.AppendLine("---- 怎么真正执行 ----");
            sb.AppendLine("  大肥鱼救星.exe --fix-apply <动作id> [--id <条目id>] [--pkg <包名>] [--port <端口>]");
            sb.AppendLine("  执行流程：写前快照 → 执行 → 复验；复验不通过会**自动回滚**并把原因打出来。");
            sb.AppendLine();
            sb.AppendLine("★ 纪律：本命令的输出里不含任何令牌/密钥；也不读取你的对话内容。");
            return sb.ToString();
        }

        private static string CmdFor(RepairAction a, string id, string pkg, int port)
        {
            if (a.Id == "dedup") return "大肥鱼救星.exe --dedup " + Arg(id, "<条目id>");
            if (a.Id == "fixdangling") return "大肥鱼救星.exe --fixdangling " + Arg(pkg, "<包名>");
            if (a.Id == "disableentry") return "大肥鱼救星.exe --disableentry " + Arg(id, "<条目id>");
            if (a.Id == "enableentry") return "大肥鱼救星.exe --enableentry " + Arg(id, "<条目id>");
            if (a.Id == "reinstall") return "大肥鱼救星.exe --reinstall " + Arg(pkg, "<包名>");
            if (a.Id == "stopleftover") return "大肥鱼救星.exe --plan-kill（预演）/ --fix-apply stopleftover（真停）";
            if (a.Id == "switchport") return "大肥鱼救星.exe --fix-apply switchport --port " + (port > 0 ? port.ToString() : "<端口>");
            if (a.Id == "repairpatch") return "大肥鱼救星.exe --fix-apply repairpatch";
            return "（未接入）";
        }

        private static string Arg(string v, string placeholder)
        {
            return string.IsNullOrEmpty(v) ? placeholder : v;
        }

        private static string[] Ids()
        {
            List<string> l = new List<string>();
            foreach (RepairAction a in All()) l.Add(a.Id);
            return l.ToArray();
        }

        /// <summary>★ 核心：算出「将改哪个文件的哪一行」，并给出该行原文。</summary>
        private static List<string> LocateLines(RepairAction a, string id, string pkg, int port)
        {
            List<string> res = new List<string>();
            string pDir = ProfileDir();
            string pPatch = Path.Combine(pDir, "cordis.patch.yml");
            string hPatch = Path.Combine(DshCore.DshHome, "cordis.patch.yml");
            string pPkg = Path.Combine(pDir, "package.json");

            if (a.Id == "dedup")
            {
                Dictionary<string, List<string>> dups = DuplicateIds();
                if (!string.IsNullOrEmpty(id) && dups.ContainsKey(id))
                {
                    foreach (string s in dups[id]) res.Add("删掉重复块 —— " + s);
                }
                else if (dups.Count > 0)
                {
                    foreach (KeyValuePair<string, List<string>> kv in dups)
                        foreach (string s in kv.Value) res.Add("(" + kv.Key + ") " + s);
                }
                else res.Add("当前没有重复 id；若指定了 --id 而它并不重复，执行会被前置条件挡住。");
            }
            else if (a.Id == "fixdangling")
            {
                List<string> dLines;
                List<string> d = DanglingPackages(out dLines);
                if (!string.IsNullOrEmpty(pkg))
                {
                    res.AddRange(FindLines(pPkg, "\"" + pkg + "\""));
                    if (res.Count == 0) res.Add(pPkg + " 里找不到 \"" + pkg + "\"（不是悬空引用，会拒绝执行）");
                }
                else if (d.Count > 0)
                {
                    foreach (string s in dLines) res.Add("删掉这一行 —— " + s);
                }
                else res.Add("当前没有悬空引用。");
            }
            else if (a.Id == "disableentry" || a.Id == "enableentry")
            {
                string needle = string.IsNullOrEmpty(id) ? "id:" : "id: " + id;
                res.AddRange(FindLines(pPatch, needle));
                res.AddRange(FindLines(hPatch, needle));
                if (res.Count == 0) res.Add("两份 patch 里都没有 " + needle + "（找不到就改不了，会明说）");
                else res.Add("改法：" + (a.Id == "disableentry"
                    ? "在该条目块内加/改 `disabled: true`"
                    : "把该条目块内的 `disabled: true` 去掉或改成 false"));
            }
            else if (a.Id == "reinstall")
            {
                // ★ 2026-09-18 修（WP4 端到端验收抓到）：旧版这一行**无条件**把占位符
                //   "<包名>" 塞进 Path.Combine ⇒ .NET 的 Path.Combine 会校验非法路径字符，
                //   `<` / `>` 直接抛 ArgumentException: 路径中具有非法字符。
                //   后果：`--plan-fix all`（不带 --pkg）整个预演崩掉、一条有用信息都看不到。
                //   ★ 上一轮只修了下面那个 Path.Combine、漏了这一行 ⇒ 所以这次两处都堵上，
                //     并且改成"占位符只在拼**人看的提示文本**时出现，绝不进 Path.Combine"。
                if (!string.IsNullOrEmpty(pkg))
                {
                    res.Add("目标目录：" + Path.Combine(pDir, "node_modules", pkg));
                    res.Add("复验点（入口文件）："
                            + Path.Combine(pDir, "node_modules", pkg, "package.json")
                            + " 的 main/module 指向的文件必须存在");
                }
                else
                {
                    res.Add("目标目录：<profile 的 node_modules>\\<包名>（缺 --pkg，算不出具体路径）");
                }
                res.Add("等价命令：dsh plugin --profile web add " + (pkg ?? "<包名>"));
            }
            else if (a.Id == "stopleftover")
            {
                // ★ 与真正执行（DshCore.StopDsh）同口径：用**收窄后**的清单。
                //   旧版这里用全机扫描的 GetDshPids()，于是沙箱里预演会列出用户线上实例，
                //   而执行时其实不会碰它 —— 预演与执行不一致，是比"不预演"更坏的东西。
                int[] dp = DshCore.GetDshPidsForKill();
                if (DshCore.HasExplicitScopeOverride())
                    res.Add("★ 已按 ActivePort 收窄范围（BFF_DSH_HOME/DSH_PORT 有显式覆盖）");
                if (dp.Length == 0) res.Add("当前没有 dsh 进程可清。");
                foreach (int pid in dp) res.Add("会结束 dsh 进程 pid=" + pid);
                int[] ow = DshCore.GetPortOwnerPidsFast(DshCore.ActivePort);
                foreach (int pid in ow)
                {
                    bool already = false;
                    foreach (int d in dp) if (d == pid) { already = true; break; }
                    if (already) continue;
                    res.Add("端口 " + DshCore.ActivePort + " 占用者 pid=" + pid
                            + "（会在执行时**再验一次身份**，非 dsh 一律跳过）");
                }
            }
            else if (a.Id == "switchport")
            {
                int np = port;
                if (np <= 0) np = DshCore.FindFreePort(3081);
                res.Add("将写入：" + DshBoot.PortOverrideFile);
                res.Add("写入内容：第 1 行 = \"" + np + "\"（单行端口号）");
                res.Add("当前文件状态：" + (File.Exists(DshBoot.PortOverrideFile)
                        ? "已存在，内容会被覆盖（原内容会在快照里）" : "不存在，将新建"));
                res.Add("目标端口 " + np + " 现在：" + (DshCore.PortListens(np, 400) ? "★仍被占用（会提示换一个）" : "空闲"));
            }
            else if (a.Id == "repairpatch")
            {
                res.Add("PatchGuard.CheckPatches() 会列出缺哪些补丁及其目标文件与行；");
                res.Add("重打目标是 node_modules 内的文件（DSH 升级会覆盖，属预期）。");
            }
            return res;
        }

        // ---------- 执行 + 复验 + 回滚 ----------

        public static string Apply(string actionId, string id, string pkg, int port, out bool verified)
        {
            verified = false;
            StringBuilder sb = new StringBuilder();
            RepairAction act = null;
            foreach (RepairAction a in All()) if (a.Id == actionId) act = a;
            if (act == null) return "未知动作 id：" + actionId + "（可用：" + string.Join(" / ", Ids()) + "）";

            sb.AppendLine("== 大肥鱼救星 · 执行修复动作 " + act.Id + " ==");
            sb.AppendLine("动作：" + act.Title);

            // 前置条件
            List<string> pre = new List<string>();
            if (act.NeedsId && string.IsNullOrEmpty(id)) pre.Add("缺少 --id <条目id>");
            if (act.NeedsPkg && string.IsNullOrEmpty(pkg)) pre.Add("缺少 --pkg <包名>");
            if (act.NeedsPort && port <= 0) pre.Add("缺少 --port <端口>");
            // ★ 2026-09-18 补（WP4 端到端验收）：换端口的目的就是"躲开被占的端口"，
            //   所以**目标端口本身必须空闲**。旧版不查这一条 ⇒ 先写进去、复验失败、再回滚，
            //   绕一大圈才报错（而且旧版回滚还漏掉了 port.txt）。现在前置就挡住，一个字节都不写。
            if (act.Id == "switchport" && port > 0 && DshCore.PortListens(port, 400))
                pre.Add("目标端口 " + port + " **现在被占用** —— 换端口是为了躲开被占的端口，请换一个空闲的");
            if (pre.Count > 0)
            {
                sb.AppendLine("★ 前置条件不满足，已拒绝执行：");
                foreach (string s in pre) sb.AppendLine("   - " + s);
                sb.AppendLine("（先跑 `--plan-fix " + act.Id + "` 看预演。）");
                return sb.ToString();
            }

            // ① 写前快照（回滚用）
            List<string> targets = new List<string>();
            string pDir = ProfileDir();
            targets.Add(Path.Combine(pDir, "cordis.patch.yml"));
            targets.Add(Path.Combine(DshCore.DshHome, "cordis.patch.yml"));
            targets.Add(Path.Combine(pDir, "package.json"));
            targets.Add(DshBoot.PortOverrideFile);
            string snapDir = Path.Combine(DshCore.AppDataDir,
                "repairplan-snap-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Dictionary<string, string> snap = new Dictionary<string, string>();
            // ★ 2026-09-18 修（WP4 端到端验收抓到）：快照必须**连"原本不存在"这件事一起记下来**。
            //   旧版只快照存在的文件 ⇒ 动作**新建**的文件在回滚时没人管。
            //   实测：`--fix-apply switchport --port <被占端口>` 复验失败后，
            //   回滚只还原了 2 个补丁文件，**port.txt 被留成了那个被占用的端口**
            //   ⇒ 下次启动直接 EADDRINUSE。这违反 A5（改配置失败必须回滚）与 A3（不许留下打不开的状态）。
            List<string> snapMissing = new List<string>();
            try
            {
                Directory.CreateDirectory(snapDir);
                int k = 0;
                foreach (string t in targets)
                {
                    if (!File.Exists(t)) { snapMissing.Add(t); continue; }   // ★ 记下"原本没有"
                    string dst = Path.Combine(snapDir, k + "_" + Path.GetFileName(t));
                    File.Copy(t, dst, true);
                    snap[t] = dst + "|" + File.ReadAllText(t, Encoding.UTF8);
                    k++;
                }
                sb.AppendLine("[OK] 已快照 " + snap.Count + " 个文件（另有 " + snapMissing.Count
                              + " 个原本就不存在）→ " + snapDir);
            }
            catch (Exception e) { sb.AppendLine("[WARN] 快照失败：" + e.Message + "（仍继续，但不保证能回滚）"); }

            // ② 执行
            string res = "";
            try
            {
                if (act.Id == "dedup") res = PluginDiag.RemoveDuplicateEntry(id);
                else if (act.Id == "fixdangling") res = PluginDiag.RemoveDanglingReference(pkg);
                else if (act.Id == "disableentry") res = PluginDiag.SetEntryDisabled(id, true);
                else if (act.Id == "enableentry") res = PluginDiag.SetEntryDisabled(id, false);
                else if (act.Id == "reinstall") res = PluginDiag.ReinstallPlugin(pkg);
                else if (act.Id == "stopleftover")
                {
                    bool ok = DshCore.StopDsh();
                    res = (ok ? "已结束残留 dsh 进程。" : "没有结束任何进程。") + "\r\n" + DshCore.LastStopHead;
                }
                else if (act.Id == "switchport")
                {
                    string err;
                    bool ok = DshBoot.SetPortOverride(port, out err);
                    DshCore.ResetActivePort();
                    res = ok ? ("已把端口覆盖写为 " + port + "\r\n文件：" + DshBoot.PortOverrideFile)
                             : ("写端口失败：" + err);
                }
                else if (act.Id == "repairpatch") res = PatchGuard.RepairPatches();
            }
            catch (Exception e)
            {
                res = "执行异常：" + e.GetType().Name + ": " + e.Message;
            }
            sb.AppendLine();
            sb.AppendLine("---- 执行输出 ----");
            sb.AppendLine(res);

            // ③ 复验（按动作各自的判据）
            string vwhy = "";
            bool ok2 = Verify(act, id, pkg, port, out vwhy);
            sb.AppendLine();
            sb.AppendLine("---- 复验 ----");
            sb.AppendLine((ok2 ? "[OK]   复验通过：" : "[FAIL] 复验未通过：") + vwhy);

            // ④ 复验不过 ⇒ 自动回滚
            if (!ok2)
            {
                sb.AppendLine();
                sb.AppendLine("---- 自动回滚 ----");
                int rb = 0;
                foreach (KeyValuePair<string, string> kv in snap)
                {
                    try
                    {
                        string content = kv.Value.Substring(kv.Value.IndexOf('|') + 1);
                        File.WriteAllText(kv.Key, content, new UTF8Encoding(false));
                        rb++;
                        sb.AppendLine("  已还原：" + kv.Key);
                    }
                    catch (Exception e) { sb.AppendLine("  ★ 还原失败 " + kv.Key + "：" + e.Message); }
                }
                // ★ 动作**新建**的文件也必须清掉（否则会留下一个指向坏状态的配置，
                //   例如一个指向"被占用端口"的 port.txt ⇒ 下次启动 EADDRINUSE）。见快照处注释。
                foreach (string t in snapMissing)
                {
                    try
                    {
                        if (File.Exists(t))
                        {
                            File.Delete(t);
                            rb++;
                            sb.AppendLine("  已删除（动作新建、回滚要一并撤掉）：" + t);
                        }
                    }
                    catch (Exception e) { sb.AppendLine("  ★ 删除失败 " + t + "：" + e.Message); }
                }
                sb.AppendLine("（回滚了 " + rb + " 个文件）");
                verified = false;
            }
            else verified = true;
            sb.AppendLine();
            sb.AppendLine("结论：" + (verified ? "动作成功且已复验" : "动作未通过复验，已回滚到执行前状态"));
            return sb.ToString();
        }

        private static string ReadAllSafe(string f)
        {
            try { return File.Exists(f) ? File.ReadAllText(f, Encoding.UTF8) : ""; }
            catch { return ""; }
        }

        /// <summary>
        /// 纯函数：某份补丁文本里 id 对应条目的 disabled 状态。
        /// 判据：找到 `- id: &lt;id&gt;` 那一行，往后最多 6 行内找 `disabled:`（遇到下一条 `- ` 就停）。
        /// 找不到该 id ⇒ 返回 false（调用方据此判"根本没改到东西"）。
        /// </summary>
        public static bool DisabledStateOf(string text, string id, out bool isDisabled)
        {
            isDisabled = false;
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(id)) return false;
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                string v;
                if (t.StartsWith("- id:")) v = t.Substring(5);
                else if (t.StartsWith("id:")) v = t.Substring(3);
                else continue;
                v = v.Trim().Trim('\'', '"');
                if (!string.Equals(v, id, StringComparison.Ordinal)) continue;
                for (int j = i + 1; j < lines.Length && j <= i + 6; j++)
                {
                    string u = lines[j].Trim();
                    if (u.StartsWith("- ")) break;                 // 进入下一条
                    if (u.StartsWith("disabled:"))
                    {
                        string w = u.Substring(9).Trim().Trim('\'', '"');
                        isDisabled = string.Equals(w, "true", StringComparison.OrdinalIgnoreCase);
                        return true;
                    }
                }
                return true;      // 找到条目但没写 disabled ⇒ 视为启用
            }
            return false;
        }

        private static bool Verify(RepairAction act, string id, string pkg, int port, out string why)
        {
            why = "";
            string pDir = ProfileDir();
            if (act.Id == "dedup")
            {
                // ★ 复验口径必须与修法一致：只认「`- insert:` 子项重复」这一种**会真崩**的形态。
                //   旧版用 DuplicateIds()（把所有 `id:` 行都算进去）⇒ 修好了也被判失败并自动回滚
                //   （详见 DuplicateInsertIds() 的注释 —— 这是 WP4 端到端验收抓出来的真缺陷）。
                Dictionary<string, List<string>> dups = DuplicateInsertIds();
                bool still = !string.IsNullOrEmpty(id) && dups.ContainsKey(id);
                why = still
                    ? ("id " + id + " 在 `- insert:` 块里仍然重复（" + dups[id].Count + " 处；DSH 会抛 duplicate loader entry id）")
                    : ("会真崩的重复 insert id 已为 0（「" + id + "」只剩 1 个 insert 子项）");
                return !still;
            }
            if (act.Id == "fixdangling")
            {
                List<string> l;
                List<string> d = DanglingPackages(out l);
                bool still = d.Contains(pkg);
                why = still ? ("仍能扫到悬空包 " + pkg) : ("package.json 里已无悬空包 " + pkg);
                return !still;
            }
            if (act.Id == "disableentry" || act.Id == "enableentry")
            {
                // ★ 2026-09-18 修（WP4 端到端验收抓到）：这两条的复验原来是**永远返回 true 的占位实现**
                //   （disableentry 那个 foreach 只是把 ok 反复赋 true、压根没判；enableentry 直接 return true）
                //   ⇒ 执行失败也会被判"成功"，正是 WP4 要消灭的东西。现在做真复验：
                //   读回补丁，判「该 id 的条目现在 disabled 是什么状态」。
                bool want = (act.Id == "disableentry");
                bool found = false, state = false;
                string where = null, foundText = null;
                string[] pf = PatchFiles();
                for (int i = 0; i < pf.Length; i++)
                {
                    string txt = ReadAllSafe(pf[i]);
                    if (txt.Length == 0) continue;
                    bool st;
                    if (!DisabledStateOf(txt, id, out st)) continue;
                    found = true; state = st; where = Path.GetFileName(pf[i]); foundText = txt;
                    break;
                }
                if (!found)
                {
                    why = "两层补丁里都找不到条目「" + id + "」⇒ 没改到任何东西";
                    return false;
                }
                if (state != want)
                {
                    why = where + " 里「" + id + "」的 disabled = " + state + "，与期望的 " + want + " 不符";
                    return false;
                }
                string yerr;
                bool seq = SafeConfig.YamlLooksLikeSequence(foundText, out yerr);
                why = where + " 里「" + id + "」的 disabled = " + state + "（与期望一致）；"
                      + "该文件 YAML 顶层仍是序列 = " + seq + (seq ? "" : "（" + yerr + "）");
                return seq;
            }
            if (act.Id == "reinstall")
            {
                // ★ v5 修：pkg 为空时旧版会 `Path.Combine(pDir, "node_modules", null, "package.json")`
                //   抛 ArgumentNullException —— 复验本该报"没修好"，却变成一条异常。
                if (string.IsNullOrEmpty(pkg))
                {
                    why = "未提供包名（--pkg）⇒ 无法复验。这不是修复失败，是调用姿势不全。";
                    return false;
                }
                string mj = Path.Combine(pDir, "node_modules", pkg, "package.json");
                if (!File.Exists(mj)) { why = "重装后仍找不到 " + mj; return false; }
                try
                {
                    string t = File.ReadAllText(mj, Encoding.UTF8);
                    Match m = Regex.Match(t, "\"(main|module)\"\\s*:\\s*\"([^\"]+)\"");
                    if (!m.Success) { why = "包内 package.json 没有 main/module 字段"; return false; }
                    string entry = Path.Combine(Path.GetDirectoryName(mj), m.Groups[2].Value.Replace('/', Path.DirectorySeparatorChar));
                    bool ok = File.Exists(entry);
                    why = ok ? ("入口文件存在：" + m.Groups[2].Value) : ("入口文件仍缺失：" + entry);
                    return ok;
                }
                catch (Exception e) { why = "读包内 package.json 失败：" + e.Message; return false; }
            }
            if (act.Id == "stopleftover")
            {
                // ★ 复验必须跟执行同口径。旧版用全机扫描的 GetDshPids()：在收窄模式下，
                //   "全机 dsh 进程数 == 0" 永远不成立（用户线上实例还活着）⇒ 复验必然 FAIL，
                //   明明清干净了也判成没修好。改成问"收窄范围内还有没有候选"。
                int[] dp = DshCore.GetDshPidsForKill();
                bool ok = dp.Length == 0 && !DshCore.PortListens(DshCore.ActivePort, 600);
                why = ok ? ("已无候选 dsh 进程，端口已释放"
                            + (DshCore.HasExplicitScopeOverride() ? "（范围已按 ActivePort 收窄）" : ""))
                         : ("仍有候选 dsh 进程 " + string.Join(",", ToStr(dp))
                            + "；端口仍监听=" + DshCore.PortListens(DshCore.ActivePort, 400));
                return ok;
            }
            if (act.Id == "switchport")
            {
                // ★ 2026-09-18 修（WP4 端到端验收）：旧版只报"读回 56359，与请求 56358 不一致"，
                //   完全看不出**为什么**（真因是那个端口被占了、工具自动顺延到下一个空闲端口）。
                int got = DshBoot.ReadPortOverride();
                bool busy = DshCore.PortListens(port, 400);
                bool ok = (got == port) && !busy;
                if (busy)
                    why = "请求的端口 " + port + " **现在被占用** ⇒ 不能改到它（读回 " + got
                          + "，说明工具已自动顺延到下一个空闲端口）。请换一个空闲端口。";
                else
                    why = ok ? ("读回端口覆盖 = " + got + "（与请求一致，且该端口空闲）")
                             : ("读回 " + got + "，与请求 " + port + " 不一致");
                // 注意：改端口不会把 DSH 拉起来（那需要 --start），所以这里只复验"覆盖值写对了"
                return ok;
            }
            if (act.Id == "repairpatch")
            {
                string rep = PatchGuard.CheckPatches();
                bool bad = rep.IndexOf("[FAIL]", StringComparison.Ordinal) >= 0
                        || rep.IndexOf("[WARN]", StringComparison.Ordinal) >= 0;
                why = bad ? "CheckPatches 仍有未通过项" : "CheckPatches 全绿";
                return !bad;
            }
            why = "（该动作没有写复验判据——这是缺陷，需补）";
            return false;
        }

        private static string PatchFilesText()
        {
            List<string> l = new List<string>();
            foreach (string f in PatchFiles()) if (File.Exists(f)) l.Add(Path.GetFileName(f));
            return l.Count == 0 ? "（两份 patch 都不存在）" : string.Join(" + ", l.ToArray());
        }

        private static string[] ToStr(int[] a)
        {
            if (a == null) return new string[0];
            string[] r = new string[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = a[i].ToString();
            return r;
        }
    }
}

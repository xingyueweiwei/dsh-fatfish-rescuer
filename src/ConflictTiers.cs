using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BigFatFishRescuer
{
    /// <summary>
    /// WP5 · 能跑的组合表（**纯静态，不起进程、不写文件**）。
    ///
    /// 它只回答一个问题：**每一档里到底有哪些条目**，让"全部/核心/最小"这三档有可核对的定义。
    /// 档位成员全部从覆盖层文件里现读（块头 `# --- 写者 ---`、条目 `- id:`、入口 `name:`），
    /// 不写死任何插件名/目录（任务书 §2.5）。
    ///
    /// ★ 为什么这里**不印**"哪档能跑"：能不能跑是运行时事实，只能由 L1 真内核实测得出
    ///   （v5/conflict/wp5_tiers.ps1 起内核跑三档）。本命令不起进程 ⇒ 所以它明确把
    ///   "运行时结论未在本命令内产生"写出来，避免把静态推断冒充成实测结论。
    /// </summary>
    public static class ConflictTiers
    {
        sealed class Blk
        {
            public string Writer = "";
            public string Id = "";
            public string Name = "";
            public string Enabled = "";
            public bool LocalPlugin;
            public int Line;
            public string Source = "";
        }

        public static string Render()
        {
            StringBuilder sb = new StringBuilder();
            List<string> files = new List<string>();
            List<Blk> blocks = new List<Blk>();
            int unread = 0;

            string homePatch = Path.Combine(DshCore.DshHome, "cordis.patch.yml");
            string profPatch = Path.Combine(ProfDir(), "cordis.patch.yml");
            Read(homePatch, "home", blocks, files, ref unread);
            Read(profPatch, "profile", blocks, files, ref unread);

            sb.AppendLine("== 大肥鱼救星 v" + DshCore.AppVersion + " · 冲突档位表（--conflict-tiers，**只读、不起进程**） ==");
            sb.AppendLine("DSH home：" + DshCore.DshHome + (DshCore.HasExplicitScopeOverride() ? "（沙箱/显式覆盖模式）" : ""));
            if (blocks.Count == 0)
            {
                sb.AppendLine("★ 两层覆盖文件里一条顶层条目都没读到 ⇒ 没有可列的档位（ unread=" + unread + "）");
                sb.AppendLine("TIERS_COVERAGE files=" + files.Count.ToString(CultureInfo.InvariantCulture) +
                              " blocks=0 unread=" + unread.ToString(CultureInfo.InvariantCulture));
                return sb.ToString();
            }

            List<Blk> core = new List<Blk>();
            List<Blk> dropped = new List<Blk>();
            for (int i = 0; i < blocks.Count; i++)
            {
                if (blocks[i].LocalPlugin) dropped.Add(blocks[i]);
                else core.Add(blocks[i]);
            }
            List<Blk> min = new List<Blk>();
            min.Add(blocks[0]);

            sb.AppendLine("档位定义（可核对，全部现读）：");
            sb.AppendLine("  all      = 两层覆盖文件里的全部顶层条目");
            sb.AppendLine("  core     = all 减去「入口指向 ./plugins/ 的本地插件块」");
            sb.AppendLine("  minimal  = 只保留 all 的第一条（最小可用面的下界探针）");
            sb.AppendLine("  口径说明：本表数的是 **home + profile 两层**的顶层条目；" +
                          "wp5_tiers.ps1 的 entries= 只数 profile 一层 ⇒ 两边数字天然差 home 层那几条，不是矛盾。");
            sb.AppendLine();
            // ★ 反直觉但重要：被砍掉的块里若有 `disabled: true`，那它是**抑制块**——
            //   砍掉它等于把某个插件**放出来**（今晚 launcher.ini 被写穿就是这条路径：
            //   覆盖层空了 ⇒ 抑制失效 ⇒ 插件加载并写真实文件）。所以"越薄越安全"是错的。
            int suppressDropped = 0;
            for (int i = 0; i < dropped.Count; i++) if (string.Equals(dropped[i].Enabled, "true", StringComparison.OrdinalIgnoreCase)) suppressDropped++;
            Emit(sb, "all", blocks, blocks.Count - core.Count);
            Emit(sb, "core", core, dropped.Count);
            Emit(sb, "minimal", min, blocks.Count - 1);
            sb.AppendLine();
            int minSuppress = 0;
            for (int i = 1; i < blocks.Count; i++) if (string.Equals(blocks[i].Enabled, "true", StringComparison.OrdinalIgnoreCase)) minSuppress++;
            sb.AppendLine("★ 档位风险提示（现读，不是模板话）：");
            sb.AppendLine("  core 砍掉 " + dropped.Count.ToString(CultureInfo.InvariantCulture) + " 块，其中抑制块（disabled: true）" +
                          suppressDropped.ToString(CultureInfo.InvariantCulture) + " 条；" +
                          "minimal 砍掉 " + (blocks.Count - 1).ToString(CultureInfo.InvariantCulture) + " 块，其中抑制块 " +
                          minSuppress.ToString(CultureInfo.InvariantCulture) + " 条。");
            sb.AppendLine("  抑制块被砍 = 对应插件**从禁用变成加载** ⇒ 变薄不等于变安全；" +
                          "实测过一条真实路径：覆盖层为空时 whale-desktop-launcher 被放出来，并把真实 launcher.ini 改写成沙箱端口。");
            sb.AppendLine("★ 本命令**没有**起过任何 dsh 进程 ⇒ 不产出「哪档能跑」的结论。");
            sb.AppendLine("  运行时档位实测：v5\\conflict\\wp5_tiers.ps1 -OwnerApproved -SandboxRoot <L1 沙箱>");
            sb.AppendLine("  它每档都走与 §11.3 五条自证同一把尺子（LISTEN + 日志静默 + 进程存活 + 真 HTTP 应答）。");
            sb.AppendLine("不动的：一个字节都不写；真实实例与端口不参与；不读 node_modules 内容。");
            sb.AppendLine("TIERS_COVERAGE files=" + files.Count.ToString(CultureInfo.InvariantCulture) +
                          " blocks=" + blocks.Count.ToString(CultureInfo.InvariantCulture) +
                          " local_plugin_blocks=" + dropped.Count.ToString(CultureInfo.InvariantCulture) +
                          " unread=" + unread.ToString(CultureInfo.InvariantCulture) +
                          " booted_here=0");
            return sb.ToString();
        }

        static void Emit(StringBuilder sb, string tier, List<Blk> l, int minus)
        {
            StringBuilder ids = new StringBuilder();
            for (int i = 0; i < l.Count && i < 12; i++)
            {
                if (ids.Length > 0) ids.Append(",");
                ids.Append(l[i].Id.Length > 0 ? l[i].Id : "(无id)");
                if (l[i].Enabled.Length > 0) ids.Append("!" + l[i].Enabled);
            }
            sb.AppendLine("TIER name=" + tier + " blocks=" + l.Count.ToString(CultureInfo.InvariantCulture) +
                          " minus=" + minus.ToString(CultureInfo.InvariantCulture) +
                          " ids=" + ids.ToString() + (l.Count > 12 ? " …" : ""));
            for (int i = 0; i < l.Count && i < 12; i++)
            {
                sb.AppendLine("    · " + l[i].Id + "  ← " + l[i].Source + ":" +
                              l[i].Line.ToString(CultureInfo.InvariantCulture) +
                              (l[i].Writer.Length > 0 ? ("  写者=" + l[i].Writer) : "") +
                              (l[i].Enabled.Length > 0 ? ("  disabled=" + (l[i].Enabled == "false" ? "false" : l[i].Enabled)) : "") +
                              (l[i].Name.Length > 0 ? ("  name=" + l[i].Name) : ""));
            }
        }

        static string ProfDir()
        {
            try { return Path.Combine(DshCore.DshHome, "profiles", "web"); }
            catch { return DshCore.DshHome; }
        }

        /// <summary>读一份覆盖层文件，切出顶层条目块。文件不存在 ⇒ 记 unread（不当"没有冲突"）。</summary>
        static void Read(string path, string source, List<Blk> blocks, List<string> files, ref int unread)
        {
            if (!File.Exists(path)) { unread++; return; }
            string text;
            try { text = File.ReadAllText(path); }
            catch { unread++; return; }
            files.Add(path);
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            Blk cur = null;
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("# ---"))
                {
                    string body = line.TrimStart('#').Trim();
                    body = body.TrimStart('-').Trim();
                    if (body.StartsWith("end ")) { if (cur != null) { blocks.Add(cur); cur = null; } continue; }
                    if (cur != null) { blocks.Add(cur); cur = null; }
                    cur = new Blk();
                    cur.Source = source;
                    int cut = body.IndexOfAny(new char[] { '(', ',', ':', '\t' });
                    if (cut > 0) body = body.Substring(0, cut);
                    cur.Writer = body.Trim();
                    continue;
                }
                if (line.StartsWith("- "))
                {
                    if (cur != null && (cur.Id.Length > 0 || cur.Name.Length > 0)) blocks.Add(cur);
                    Blk nb = new Blk();
                    nb.Source = source;
                    nb.Line = i + 1;
                    cur = nb;
                }
                if (cur == null) continue;
                if (line.StartsWith("- id:") || line.StartsWith("id:"))
                {
                    string v = Norm(line.Substring(line.IndexOf(':') + 1));
                    if (cur.Id.Length == 0) cur.Id = v;
                    continue;
                }
                if (line.StartsWith("name:"))
                {
                    cur.Name = Norm(line.Substring("name:".Length));
                    string n = cur.Name.Replace((char)92, '/');
                    cur.LocalPlugin = n.Contains("plugins/");
                    continue;
                }
                if (line.StartsWith("disabled:"))
                {
                    if (cur.Enabled.Length == 0) cur.Enabled = Norm(line.Substring("disabled:".Length));
                    continue;
                }
            }
            if (cur != null && (cur.Id.Length > 0 || cur.Name.Length > 0)) blocks.Add(cur);
        }

        static string Norm(string s)
        {
            if (s == null) return "";
            string r = s.Trim();
            r = r.Trim('\'', '"');
            int c = r.IndexOf(" #");
            if (c > 0) r = r.Substring(0, c);
            return r.Trim();
        }
    }
}

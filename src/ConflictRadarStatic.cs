using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BigFatFishRescuer
{
    /// <summary>
    /// WP2 · 静态判据（只读）：把"已知会打架的形状"从**配置与清单**里判出来。
    /// 每个检查都收**根目录当参数**，这样自检能在临时目录造一正一负样本，
    /// 绝不在真实 profile 上做实验（任务书 §4 红线 2）。
    ///
    /// 现在覆盖两类：
    ///  A) 补丁登记一致性：pnpm-workspace.yaml 的 patchedDependencies ↔ patches/ 里的实文件。
    ///     登记了却没有文件 ⇒ 装完就坏（真红）；有文件却没登记 ⇒ 提醒（重装/升级后极易静默丢）。
    ///  B) 提示词字面量：记忆里出现「双花括号被空格拆开」这种会被模板引擎当变量的文本。
    ///     本机实测确实存在（memories/MEMORY.md 与两个 daily 文件），而它曾经让 DSH 直接起不来。
    ///     但**静态判不了这文本到底会不会被插值**（取决于哪个插件把它塞进模板、有没有人中和它），
    ///     所以只报提醒 + 待 WP3 对照，不装懂判红。
    /// </summary>
    public static class ConflictRadarStatic
    {
        // ---------------- A) 补丁登记一致性 ----------------
        public static void CheckPatchRegistry(string profileDir, List<ConflictRadar.Occ> all, ConflictRadar.ScanResult r)
        {
            string ws = Path.Combine(profileDir, "pnpm-workspace.yaml");
            string patchesDir = Path.Combine(profileDir, "patches");
            bool hasWs = File.Exists(ws);
            r.FilesScanned.Add(hasWs ? ws : ws + " (不存在)");
            if (!hasWs) return;

            List<string> listed = new List<string>();
            try
            {
                string text = File.ReadAllText(ws);
                int k = text.IndexOf("patchedDependencies", StringComparison.Ordinal);
                if (k >= 0)
                {
                    string[] lines = text.Substring(k).Split(new char[] { (char)13, (char)10 }, StringSplitOptions.None);
                    for (int i = 1; i < lines.Length; i++)
                    {
                        string ln = lines[i];
                        if (ln.TrimStart().Length == 0) continue;
                        // 只有缩进的 `key: value` 才算登记行；顶格说明这一段结束了
                        if (!char.IsWhiteSpace(ln[0])) break;
                        int c = ln.IndexOf(':');
                        if (c <= 0) continue;
                        string name = ln.Substring(c + 1).Trim();
                        if (name.Length > 0) listed.Add(name);
                        all.Add(Make("补丁登记", "登记:" + ln.Substring(0, c).Trim(), "pnpm-workspace.yaml", ws, i));
                    }
                }
            }
            catch { return; }

            // 登记的补丁文件必须存在
            for (int i = 0; i < listed.Count; i++)
            {
                string p = listed[i];
                string abs = p;
                try { if (!Path.IsPathRooted(abs)) abs = Path.Combine(profileDir, p); } catch { }
                bool ok = false;
                try { ok = File.Exists(abs); } catch { ok = false; }
                ConflictRadar.Occ o = Make("补丁登记", p, "pnpm-workspace.yaml", ws, 0);
                o.Note = ok ? "文件在" : "★登记的补丁文件不存在";
                all.Add(o);
            }
            // patches/ 里没被登记的实文件
            if (Directory.Exists(patchesDir))
            {
                string[] files;
                try { files = Directory.GetFiles(patchesDir, "*.patch"); } catch { files = new string[0]; }
                for (int i = 0; i < files.Length; i++)
                {
                    string f = Path.GetFileName(files[i]);
                    bool found = false;
                    for (int j = 0; j < listed.Count; j++)
                    {
                        string lp = listed[j].Replace((char)92, '/');
                        if (lp.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) { found = true; break; }
                    }
                    if (found) continue;
                    ConflictRadar.Occ o = Make("补丁登记", "孤儿:" + f, "patches目录", patchesDir, 0);
                    o.Note = "补丁文件在，但 patchedDependencies 里没登记 ⇒ 下次 install/升级会静默丢弃";
                    all.Add(o);
                }
            }
        }

        // ---------------- B) 提示词字面量 ----------------
        /// <summary>
        /// 只报**文件名与条数**，绝不把记忆内容打印进报告（那是用户的对话内容）。
        /// </summary>
        public static void CheckPromptLiterals(string memoriesDir, List<ConflictRadar.Occ> all, ConflictRadar.ScanResult r)
        {
            if (!Directory.Exists(memoriesDir)) { r.FilesMissing.Add(memoriesDir + " (不存在)"); return; }
            r.FilesScanned.Add(memoriesDir);
            List<string> queue = new List<string>();
            queue.Add(memoriesDir);
            int files = 0, hits = 0;
            for (int qi = 0; qi < queue.Count && qi < 400; qi++)
            {
                string dir = queue[qi];
                string[] subs;
                try { subs = Directory.GetDirectories(dir); } catch { continue; }
                for (int s = 0; s < subs.Length; s++)
                {
                    try
                    {
                        // 绝不进入 junction / 符号链接（NTFS reparse point）—— 遍历穿透过真实目录是本工程的老坑
                        FileAttributes at = new DirectoryInfo(subs[s]).Attributes;
                        if ((at & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint) continue;
                    }
                    catch { continue; }
                    queue.Add(subs[s]);
                }
                string[] md;
                try { md = Directory.GetFiles(dir, "*.md"); } catch { md = new string[0]; }
                for (int i = 0; i < md.Length; i++)
                {
                    files++;
                    int n = CountLiteral(md[i]);
                    if (n <= 0) continue;
                    hits += n;
                    string rel = md[i];
                    try { if (rel.StartsWith(memoriesDir, StringComparison.OrdinalIgnoreCase)) rel = rel.Substring(memoriesDir.Length).TrimStart((char)92); } catch { }
                    ConflictRadar.Occ o = Make("提示词字面量", rel, "记忆文件", md[i], 0);
                    o.Note = n.ToString(CultureInfo.InvariantCulture) + " 处双花括号字面量";
                    all.Add(o);
                }
            }
            r.MemoryFiles = files;
            r.MemoryLiteralHits = hits;
        }

        static int CountLiteral(string path)
        {
            try
            {
                string text = File.ReadAllText(path);
                // 「左花括号 + 空白 + 左花括号」= 会被模板引擎当变量起点的那种写法
                string needle = ((char)123).ToString() + " " + ((char)123).ToString();
                int n = 0, from = 0;
                while (true)
                {
                    int k = text.IndexOf(needle, from, StringComparison.Ordinal);
                    if (k < 0) break;
                    n++; from = k + 1;
                    if (n > 50) break;      // 单个文件封顶，防止计数失控
                }
                return n;
            }
            catch { return 0; }
        }

        static ConflictRadar.Occ Make(string kind, string key, string writer, string file, int line)
        {
            ConflictRadar.Occ o = new ConflictRadar.Occ();
            o.Kind = kind; o.Key = key; o.Writer = writer; o.File = file; o.Line = line;
            return o;
        }

        // ---------------- 判红/判提醒 ----------------
        public static List<ConflictRadar.Conflict> Extras(List<ConflictRadar.Occ> all)
        {
            List<ConflictRadar.Conflict> outp = new List<ConflictRadar.Conflict>();
            for (int i = 0; i < all.Count; i++)
            {
                ConflictRadar.Occ o = all[i];
                string note = o.Note ?? "";
                if (o.Kind == "补丁登记" && note.IndexOf("登记的补丁文件不存在", StringComparison.Ordinal) >= 0)
                {
                    ConflictRadar.Conflict c = new ConflictRadar.Conflict();
                    c.Kind = o.Kind; c.Key = o.Key;
                    c.Why = "patchedDependencies 登记了补丁，但文件不在 ⇒ pnpm 直接报错或静默不装";
                    c.Occs.Add(o);
                    outp.Add(c);
                    continue;
                }
                if (o.Kind == "补丁登记" && note.IndexOf("静默丢弃", StringComparison.Ordinal) >= 0)
                {
                    ConflictRadar.Conflict c = new ConflictRadar.Conflict();
                    c.Kind = o.Kind; c.Key = o.Key;
                    c.Why = note + "（不确定是否有意为之 ⇒ 提醒，不判红）";
                    c.Soft = true;
                    c.Occs.Add(o);
                    outp.Add(c);
                    continue;
                }
                if (o.Kind == "提示词字面量")
                {
                    ConflictRadar.Conflict c = new ConflictRadar.Conflict();
                    c.Kind = o.Kind; c.Key = o.Key;
                    c.Why = "该记忆文本里有 " + o.Note + "，若被塞进提示词模板会被当变量（曾致 DSH 起不来）；"
                          + "但静态无法确定它是否真会进插值路径 ⇒ 提醒 + 待 WP3 对照";
                    c.Soft = true;
                    c.Occs.Add(o);
                    outp.Add(c);
                }
            }
            return outp;
        }

        // ---------------- 自检（一正一负，全在临时目录） ----------------
        public static string SelfTest3()
        {
            int pass = 0, fail = 0;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("---- WP2 静态判据自检（临时目录，不碰真实 profile） ----");
            string dir = Path.Combine(Path.GetTempPath(), "bff_wp2_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            try { Directory.CreateDirectory(Path.Combine(dir, "patches")); } catch { }

            // A 阳性：登记了却不存在的补丁
            try
            {
                File.WriteAllText(Path.Combine(dir, "pnpm-workspace.yaml"),
                    "packages:\n  - .\n\npatchedDependencies:\n  demo@1.0.0: patches/demo@1.0.0.patch\n");
            }
            catch { }
            ConflictRadar.ScanResult r1 = new ConflictRadar.ScanResult();
            List<ConflictRadar.Occ> a1 = new List<ConflictRadar.Occ>();
            CheckPatchRegistry(dir, a1, r1);
            Check(sb, ref pass, ref fail, "W1 阳性：登记的补丁文件不存在 ⇒ 必须报红",
                HasRed(Extras(a1), "补丁登记", "demo@1.0.0", false));

            // A 阴性：补丁登记与文件齐备 ⇒ 不该报红
            try { File.WriteAllText(Path.Combine(dir, "patches", "demo@1.0.0.patch"), "--- a\n+++ b\n"); } catch { }
            ConflictRadar.ScanResult r2 = new ConflictRadar.ScanResult();
            List<ConflictRadar.Occ> a2 = new List<ConflictRadar.Occ>();
            CheckPatchRegistry(dir, a2, r2);
            Check(sb, ref pass, ref fail, "W1n 阴性：登记与实文件齐备 ⇒ 不该报红",
                !HasRed(Extras(a2), "补丁登记", "demo@1.0.0", false));

            // A2 阳性对照的对照：孤儿补丁（文件在、没登记）⇒ 只提醒不判红
            try { File.WriteAllText(Path.Combine(dir, "patches", "orphan@9.9.9.patch"), "--- a\n+++ b\n"); } catch { }
            ConflictRadar.ScanResult r3 = new ConflictRadar.ScanResult();
            List<ConflictRadar.Occ> a3 = new List<ConflictRadar.Occ>();
            CheckPatchRegistry(dir, a3, r3);
            Check(sb, ref pass, ref fail, "W2 阳性：孤儿补丁被识别为提醒（且**不算红**）",
                HasRed(Extras(a3), "补丁登记", "orphan", true));

            // B 阳性/阴性：提示词字面量
            string mem = Path.Combine(dir, "mem");
            try { Directory.CreateDirectory(Path.Combine(mem, "daily")); } catch { }
            string lb = ((char)123).ToString() + " " + ((char)123).ToString() + "prompt" + ((char)125).ToString() + ((char)125).ToString();
            try { File.WriteAllText(Path.Combine(mem, "daily", "has.md"), "正文 " + lb + " 结尾\n"); } catch { }
            try { File.WriteAllText(Path.Combine(mem, "clean.md"), "正文 {{already}}" + ((char)10) + "没有拆开写" + ((char)10)); } catch { }
            ConflictRadar.ScanResult r4 = new ConflictRadar.ScanResult();
            List<ConflictRadar.Occ> a4 = new List<ConflictRadar.Occ>();
            CheckPromptLiterals(mem, a4, r4);
            Check(sb, ref pass, ref fail,
                "W3 覆盖面：字面量扫描必须数到 2 个文件、只在 has.md 命中（实测 files=" + r4.MemoryFiles +
                " hits=" + r4.MemoryLiteralHits + "）",
                r4.MemoryFiles == 2 && r4.MemoryLiteralHits >= 1 && a4.Count >= 1);
            Check(sb, ref pass, ref fail, "W3n 阴性：未拆开写的双花括号不该被算成字面量命中",
                a4.Count == 1 && a4[0].Key.Replace((char)92, '/').EndsWith("daily/has.md"));
            Check(sb, ref pass, ref fail, "W4 保守性：字面量只出提醒，不出真红（静态判不了是否进插值）",
                HasRed(Extras(a4), "提示词字面量", "has.md", true));
            Check(sb, ref pass, ref fail, "W5 隐私：报告键里只有相对路径，不含记忆正文",
                a4.Count >= 1 && a4[0].Note.IndexOf("prompt") < 0);

            sb.AppendLine("WP2 自检结论：通过 " + pass.ToString(CultureInfo.InvariantCulture) +
                          " 项 / 失败 " + fail.ToString(CultureInfo.InvariantCulture) + " 项");
            return sb.ToString();
        }

        static bool HasRed(List<ConflictRadar.Conflict> cs, string kind, string keyPart, bool wantSoft)
        {
            for (int i = 0; i < cs.Count; i++)
            {
                if (!string.Equals(cs[i].Kind, kind, StringComparison.Ordinal)) continue;
                if ((cs[i].Key ?? "").IndexOf(keyPart, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (cs[i].Soft != wantSoft) continue;
                return true;
            }
            return false;
        }
        static void Check(StringBuilder sb, ref int pass, ref int fail, string name, bool ok)
        {
            if (ok) { pass++; sb.AppendLine("[OK]   " + name); }
            else { fail++; sb.AppendLine("[FAIL] " + name); }
        }
    }
}

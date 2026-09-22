using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

// ============================================================
// 配置文件保险箱 & 原子写入（2026-09-12 事故后的加固）
//
// 事故回顾：三个活动配置文件（~/.dsh/profiles/web/package.json、
// ~/.dsh/settings.yaml、~/.dsh/profiles/web/cordis.patch.yml）在同一秒被
// 写入垃圾内容，JSON 解析失败、DSH 起不来。
//
// 教训与对策（本文件就是对策）：
//   1) 写配置文件绝不能直接 File.WriteAllText（open-truncate）。
//      写入中途进程被杀 / 磁盘满 / 被占用，都会留下空文件或半截文件。
//      → AtomicWriteText：先写同目录临时文件 + Flush(true) 落盘，再原子替换。
//   2) 备份绝不能是「固定文件名、每次覆盖」的单槽 .bak。
//      如果源文件已经坏了，下一次备份就把唯一的好副本覆盖掉了。
//      → BackupFile：带时间戳 + 自动避让重名，永不覆盖已有备份。
//   3) 危险操作前要先有无损快照，且快照本身要能一键回滚。
//      → Snapshot / ListSnapshots / RestoreSnapshot（带轮换）。
//   4) 写完要立刻读回校验；不合法就拒绝写入并回滚。
//      → JsonWellFormed / YamlLooksLikeSequence / VerifyAfterWrite。
// ============================================================
namespace BigFatFishRescuer
{
    public static class SafeConfig
    {
        public static readonly string SnapshotRoot =
            Path.Combine(DshCore.AppDataDir, "config-snapshots");

        private const int KeepSnapshots = 40;

        // 关键配置文件（相对 DshHome）——快照与自检都针对这些
        private static readonly string[] Critical = new string[]
        {
            @"settings.yaml",
            @"cordis.patch.yml",
            @"profiles\web\package.json",
            @"profiles\web\cordis.patch.yml",
            @"profiles\web\cordis.yml",
            @"memories\coi\config.json",
        };

        public static string[] CriticalPaths()
        {
            var list = new List<string>();
            foreach (string rel in Critical) list.Add(Path.Combine(DshCore.DshHome, rel));
            return list.ToArray();
        }

        // ------------------------------------------------------------
        // 备份
        // ------------------------------------------------------------

        /// <summary>给单个文件做一个带时间戳的备份。永不覆盖已有备份。返回备份路径，失败返回 null。</summary>
        public static string BackupFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string bak = path + ".bak-" + stamp;
                int n = 1;
                while (File.Exists(bak))
                {
                    bak = path + ".bak-" + stamp + "-" + n;
                    n++;
                    if (n > 99) return null;
                }
                File.Copy(path, bak, false);
                return bak;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------
        // 原子写入
        // ------------------------------------------------------------

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true,
            CharSet = System.Runtime.InteropServices.CharSet.Unicode, BestFitMapping = false)]
        private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, int dwFlags);

        private const int MOVEFILE_REPLACE_EXISTING = 0x1;
        private const int MOVEFILE_WRITE_THROUGH = 0x8;

        /// <summary>
        /// 原子写文本：同目录临时文件 → Flush(true) 落盘 → 原子替换。
        ///
        /// 为什么不能用 File.WriteAllText：它是 open-truncate，先把文件长度截成 0 再写。
        /// 写入途中进程被杀（救星的"重启服务/停止服务/强力自愈"就会 Kill node 进程）、
        /// 磁盘满、被占用，都会留下 0 字节或半截文件 —— 这正是配置被写坏的形态之一。
        ///
        /// 替换用 MoveFileEx(MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)：
        /// 这是 Windows 上真正的原子替换语义。临时文件与目标同目录 → 必然同卷，
        /// 不会退化成 CopyFile+DeleteFile（那会丢失原子性）。
        /// 备份失败则中止写入（宁可不动，也不能在没有退路的情况下改配置）。
        /// </summary>
        public static void AtomicWriteText(string path, string content)
        {
            AtomicWriteText(path, content, false);
        }

        /// <summary>
        /// 原子写文本，可指定是否带 UTF-8 BOM（T16）。
        /// launcher.ini 这类**第三方读取的文件**必须保留原来的 BOM 状态：
        /// 某些读取方按 ANSI 解析无 BOM 的 UTF-8，BOM 一变就可能读成乱码。
        /// </summary>
        public static void AtomicWriteText(string path, string content, bool withBom)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tmp = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir,
                                      "." + Path.GetFileName(path) + ".tmp-" +
                                      Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = new UTF8Encoding(withBom).GetBytes(content);
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);        // 强制刷到磁盘，确保数据先于改名落地
                }

                if (File.Exists(path))
                {
                    // 备份失败 = 没有退路 → 中止（Ansible 式的"备份失败即终止"）
                    string bak = BackupFile(path);
                    if (bak == null)
                        throw new IOException("无法为 " + path + " 创建备份，已中止写入（防止在无退路时改动配置）。");
                }

                if (MoveFileEx(tmp, path, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
                    return;

                int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                try
                {
                    // 退路：File.Replace 也是原子替换，并会做元数据合并
                    File.Replace(tmp, path, null, true);
                    return;
                }
                catch (Exception e2)
                {
                    throw new IOException("原子替换失败（MoveFileEx 错误码 " + err + "，" +
                                          e2.Message + "）。原文件未被改动，临时文件保留在：" + tmp);
                }
            }
            catch
            {
                // 失败时保留现场，方便取证；不要静默删除半成品
                throw;
            }
            finally
            {
                if (File.Exists(tmp) && File.Exists(path))
                {
                    // 替换成功才会走到这里（tmp 已不存在）；若 tmp 仍在说明失败了
                    // —— 上面已抛出并保留了它，这里不再删除，交给人工/日志查看。
                }
            }
        }

        // ------------------------------------------------------------
        // 写后校验
        // ------------------------------------------------------------

        /// <summary>轻量 JSON 合法性校验（递归下降，不依赖任何外部程序集）。</summary>
        public static bool JsonWellFormed(string text, out string error)
        {
            error = null;
            if (text == null) { error = "内容为 null"; return false; }
            int i = 0;
            // 跳过 BOM
            if (text.Length > 0 && text[0] == '\uFEFF') i = 1;
            try
            {
                SkipWs(text, ref i);
                ParseValue(text, ref i, 0);
                SkipWs(text, ref i);
                if (i != text.Length)
                {
                    error = "第 " + (i + 1) + " 字符处有 JSON 之外的多余内容：" +
                            Preview(text, i);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string Preview(string s, int i)
        {
            int len = Math.Min(40, s.Length - i);
            if (len <= 0) return "(结尾)";
            return "\"" + s.Substring(i, len).Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
                break;
            }
        }

        private static void ParseValue(string s, ref int i, int depth)
        {
            if (depth > 200) throw new Exception("JSON 嵌套过深");
            if (i >= s.Length) throw new Exception("JSON 在第 " + (i + 1) + " 字符处意外结束");
            char c = s[i];
            if (c == '{') { ParseObject(s, ref i, depth); return; }
            if (c == '[') { ParseArray(s, ref i, depth); return; }
            if (c == '"') { ParseString(s, ref i); return; }
            if (c == 't') { Expect(s, ref i, "true"); return; }
            if (c == 'f') { Expect(s, ref i, "false"); return; }
            if (c == 'n') { Expect(s, ref i, "null"); return; }
            if (c == '-' || (c >= '0' && c <= '9')) { ParseNumber(s, ref i); return; }
            throw new Exception("第 " + (i + 1) + " 字符处不是合法 JSON 值：" + Preview(s, i));
        }

        private static void Expect(string s, ref int i, string lit)
        {
            if (i + lit.Length > s.Length || s.Substring(i, lit.Length) != lit)
                throw new Exception("第 " + (i + 1) + " 字符处期望 " + lit);
            i += lit.Length;
        }

        private static void ParseObject(string s, ref int i, int depth)
        {
            i++;                       // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"')
                    throw new Exception("第 " + (i + 1) + " 字符处期望对象键（字符串）");
                ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':')
                    throw new Exception("第 " + (i + 1) + " 字符处期望 ':'");
                i++;
                SkipWs(s, ref i);
                ParseValue(s, ref i, depth + 1);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return; }
                throw new Exception("第 " + (i + 1) + " 字符处期望 ',' 或 '}'：" + Preview(s, i));
            }
        }

        private static void ParseArray(string s, ref int i, int depth)
        {
            i++;                       // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return; }
            while (true)
            {
                SkipWs(s, ref i);
                ParseValue(s, ref i, depth + 1);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return; }
                throw new Exception("第 " + (i + 1) + " 字符处期望 ',' 或 ']'：" + Preview(s, i));
            }
        }

        private static void ParseString(string s, ref int i)
        {
            i++;                       // "
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }
                if (c == '"') { i++; return; }
                if (c == '\n' || c == '\r')
                    throw new Exception("第 " + (i + 1) + " 字符处字符串未闭合（出现换行）");
                i++;
            }
            throw new Exception("JSON 字符串未闭合");
        }

        private static void ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && s[i] == '-') i++;
            while (i < s.Length && ((s[i] >= '0' && s[i] <= '9') || s[i] == '.' ||
                                    s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-')) i++;
            if (i == start) throw new Exception("第 " + (i + 1) + " 字符处不是合法数字");
        }

        /// <summary>
        /// YAML 结构自检（轻量）：用于 cordis.patch.yml 这类「一层序列」补丁文件。
        /// 判据：非空、无重复的垃圾块、每个顶层项必须是 "- " 开头或以注释/[] 出现。
        /// 这能抓住"把日志片段拼进 YAML"这一类真实事故。
        /// </summary>
        public static bool YamlLooksLikeSequence(string text, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
            {
                error = "内容为空";
                return false;
            }
            string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var seen = new Dictionary<string, int>();
            int dup = 0;
            foreach (string raw in lines)
            {
                string line = raw.TrimEnd();
                if (line.Trim().Length == 0) continue;
                if (line.TrimStart().StartsWith("#")) continue;     // 注释
                if (line.Trim() == "[]") continue;                  // 空列表
                if (line.StartsWith(" ") || line.StartsWith("\t")) continue;  // 续行
                if (!line.StartsWith("- "))                          // 顶层必须是序列项
                {
                    error = "顶层出现非序列行：" + Preview(line, 0);
                    return false;
                }
                string key = line.Trim();
                // ★ 2026-09-16 修复（假阳性，实战暴露）：
                //   `- insert:` 是**结构行**，不是"条目身份" —— 一个合法的补丁文件里
                //   本来就可以有多条 `- insert:`（每条插一个自己的插件）。
                //   旧判据按**整行文本**去重 ⇒ 出现第二条 insert 就被误报成
                //   "完全重复的顶层条目（疑似拼接/追加失控）"，还会连累「✅ 配置自检」报 FAIL
                //   （实测：本机为 dsh-prompt-guard 补上登记行后，`--configtest` 从 21/0 掉到 20/1，
                //    而文件其实是**完全合法**的 YAML —— 差点让「体检」把这次正确的改动回滚掉）。
                //   现在只对**带 id 的条目行**去重：真出现"整个块被复制两遍"时，
                //   `- id: xxx` 仍会重复 ⇒ 原意图（抓拼接事故）不受影响。
                if (key.Equals("- insert:", StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.ContainsKey(key)) dup++;
                else seen[key] = 1;
            }
            if (dup > 0)
            {
                error = "检测到 " + dup + " 行完全重复的顶层条目（疑似拼接/追加失控）";
                return false;
            }
            return true;
        }

        /// <summary>
        /// YAML 结构自检（映射型，用于 settings.yaml）。
        /// settings.yaml 是「顶层配置段 = 键」的映射，不是序列。
        /// 关键判据：顶层键必须唯一 —— YAML 规范要求 mapping key 唯一，
        /// 而"同一 5 键块重复 3 次"正是 2026-09-12 事故里 settings.yaml 的损坏形态。
        /// </summary>
        public static bool YamlLooksLikeMapping(string text, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
            {
                error = "内容为空";
                return false;
            }
            string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var keys = new Dictionary<string, int>();
            var dups = new List<string>();
            foreach (string raw in lines)
            {
                string line = raw.TrimEnd();
                if (line.Trim().Length == 0) continue;
                if (line.TrimStart().StartsWith("#")) continue;
                if (line.Trim() == "---") continue;
                if (line.StartsWith(" ") || line.StartsWith("\t")) continue;   // 续行 / 嵌套内容
                if (line.TrimStart().StartsWith("- "))
                {
                    error = "顶层出现序列项（本文件应为映射）：" + Preview(line, 0);
                    return false;
                }
                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    error = "顶层出现非 \"键:\" 行：" + Preview(line, 0);
                    return false;
                }
                string key = line.Substring(0, colon).Trim();
                if (key.Length == 0)
                {
                    error = "顶层出现空键：" + Preview(line, 0);
                    return false;
                }
                if (keys.ContainsKey(key))
                {
                    if (!dups.Contains(key)) dups.Add(key);
                }
                else keys[key] = 1;
            }
            if (keys.Count == 0)
            {
                error = "没有解析到任何顶层配置段";
                return false;
            }
            if (dups.Count > 0)
            {
                error = "发现重复的顶层配置段（YAML 要求键唯一）：" + string.Join("、", dups.ToArray());
                return false;
            }
            return true;
        }

        /// <summary>写入后立刻读回校验；不合法则回滚到备份并抛异常。</summary>
        public static void VerifyAfterWrite(string path, bool asJson, bool asYaml)
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            string err;
            if (asJson && !JsonWellFormed(text, out err))
            {
                Rollback(path);
                throw new Exception("写后校验失败（JSON）：" + err + " —— 已回滚。");
            }
            if (asYaml && !YamlLooksLikeSequence(text, out err))
            {
                Rollback(path);
                throw new Exception("写后校验失败（YAML）：" + err + " —— 已回滚。");
            }
        }

        private static void Rollback(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                string pat = Path.GetFileName(path) + ".bak-*";
                var cands = new List<string>(Directory.GetFiles(dir, pat));
                if (cands.Count == 0) return;
                cands.Sort(StringComparer.OrdinalIgnoreCase);
                File.Copy(cands[cands.Count - 1], path, true);
            }
            catch { }
        }

        // ------------------------------------------------------------
        // 快照 / 回滚
        // ------------------------------------------------------------

        /// <summary>对所有关键配置文件做一次无损快照。返回快照目录（失败返回 null）。</summary>
        public static string Snapshot(string reason)
        {
            try
            {
                Directory.CreateDirectory(SnapshotRoot);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string dir = Path.Combine(SnapshotRoot, stamp);
                int n = 1;
                while (Directory.Exists(dir))
                {
                    dir = Path.Combine(SnapshotRoot, stamp + "-" + n);
                    n++;
                }
                Directory.CreateDirectory(Path.Combine(dir, "files"));

                var sb = new StringBuilder();
                sb.AppendLine("# 大肥鱼救星 · 配置文件快照");
                sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("原因: " + (reason == null ? "" : reason));
                sb.AppendLine("DSH home: " + DshCore.DshHome);
                sb.AppendLine();
                sb.AppendLine("状态  字节  改动时间              sha256(前16)  文件");

                foreach (string p in CriticalPaths())
                {
                    string rel = p.Substring(DshCore.DshHome.Length).TrimStart('\\');
                    if (!File.Exists(p))
                    {
                        sb.AppendLine("缺失  -     -                     -             " + rel);
                        continue;
                    }
                    string dst = Path.Combine(dir, "files", rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(p, dst, true);
                    var fi = new FileInfo(p);
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "OK    {0,-5} {1,-21} {2,-13} {3}",
                        fi.Length, fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        Hash16(p), rel));
                }
                File.WriteAllText(Path.Combine(dir, "MANIFEST.txt"), sb.ToString(), new UTF8Encoding(false));
                Rotate();
                return dir;
            }
            catch { return null; }
        }

        private static string Hash16(string path)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(path))
                {
                    byte[] h = sha.ComputeHash(fs);
                    var sb = new StringBuilder();
                    for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return "?"; }
        }

        private static void Rotate()
        {
            try
            {
                var dirs = new List<string>(Directory.GetDirectories(SnapshotRoot));
                if (dirs.Count <= KeepSnapshots) return;
                dirs.Sort(StringComparer.OrdinalIgnoreCase);        // 时间戳命名 → 字典序即时间序
                for (int i = 0; i < dirs.Count - KeepSnapshots; i++)
                {
                    try { Directory.Delete(dirs[i], true); } catch { }
                }
            }
            catch { }
        }

        /// <summary>列出快照名（最新在前）。</summary>
        public static List<string> ListSnapshots()
        {
            var list = new List<string>();
            try
            {
                if (!Directory.Exists(SnapshotRoot)) return list;
                list.AddRange(Directory.GetDirectories(SnapshotRoot));
                list.Sort(StringComparer.OrdinalIgnoreCase);
                list.Reverse();
            }
            catch { }
            return list;
        }

        /// <summary>
        /// 从快照恢复。恢复前会先给当前状态再做一次快照（所以恢复本身也可撤销）。
        /// </summary>
        public static string RestoreSnapshot(string name)
        {
            var sb = new StringBuilder();
            List<string> all = ListSnapshots();
            if (all.Count == 0) return "还没有任何快照，无法恢复。请先点「备份配置」。";
            if (string.IsNullOrEmpty(name)) name = Path.GetFileName(all[0]);
            string dir = Path.Combine(SnapshotRoot, name);
            if (!Directory.Exists(dir)) return "找不到快照：" + name;

            // ★ T23：快照的 MANIFEST.txt 里明明记着 `DSH home:`，但旧版**从不读它、也不校验** ⇒
            //   从**另一台机器 / 另一个 DSH home** 手工拷过来的快照，可以直接覆盖本机活动配置，**零警告**。
            //   这里读出来比对，不一致就**明确警告**（不中止：用户可能是故意迁移，但必须知情）。
            string foreignWarn = "";
            try
            {
                string manifest = Path.Combine(dir, "MANIFEST.txt");
                if (File.Exists(manifest))
                {
                    foreach (string line in File.ReadLines(manifest))
                    {
                        if (!line.StartsWith("DSH home:", StringComparison.Ordinal)) continue;
                        string home = line.Substring("DSH home:".Length).Trim();
                        if (home.Length > 0 && !string.Equals(home.TrimEnd('\\'), DshCore.DshHome.TrimEnd('\\'),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            foreignWarn = "⚠ **这份快照来自另一个 DSH home**：\r\n    快照记录：" + home +
                                          "\r\n    本机实际：" + DshCore.DshHome +
                                          "\r\n    直接恢复可能把**别的机器/别的用户的配置**覆盖到本机。请确认你确实要这么做。\r\n";
                        }
                        break;
                    }
                }
                else
                {
                    foreignWarn = "⚠ 这份快照里没有 MANIFEST.txt，无法确认它是否来自本机（可能是手工拷贝的）。\r\n";
                }
            }
            catch (Exception e) { foreignWarn = "⚠ 读取快照 MANIFEST 失败：" + e.Message + "\r\n"; }

            // ★ T24：恢复前那次"自动快照当前状态"如果失败，旧版**不中止**，
            //   只在正文里少打一行 —— 于是**在没有退路的情况下覆盖 6 个活动配置文件**。
            //   而同一个工具里「修复插件」的口径是"快照失败即中止"（SafeConfig 的使用者）。
            //   这里统一成**中止**：恢复配置是"往回退"的动作，本身极容易被用来救火，
            //   在没有退路时执行它，一旦选错快照就再也回不来了。
            string pre = Snapshot("恢复快照 " + name + " 之前的自动备份");
            if (pre == null)
            {
                return "==== 从快照恢复：" + name + " ====\r\n" +
                       ">>> **已中止**：无法创建配置快照（" + SnapshotRoot + "）。\r\n" +
                       "    恢复配置会覆盖活动配置文件，属于「往后退」的不可逆动作；\r\n" +
                       "    在无法先给当前状态留一份退路的情况下，本工具**不执行**。\r\n" +
                       "    请检查该目录是否可写（磁盘是否已满 / 权限），然后重试。\r\n\r\n" + foreignWarn;
            }

            sb.AppendLine("==== 从快照恢复：" + name + " ====");
            sb.AppendLine("（恢复前已自动快照当前状态 → " + Path.GetFileName(pre) + "，可再退回）");
            if (foreignWarn.Length > 0) { sb.AppendLine(); sb.AppendLine(foreignWarn.TrimEnd()); }
            sb.AppendLine();

            // ★ T27：说清"恢复的到底是什么范围" —— 旧版名字叫「恢复配置」，
            //   实际只覆盖 CriticalPaths 那 6 个文件；插件产物、memories 其它内容、
            //   profiles/web/node_modules 都**不在范围内**，恢复后可能出现
            //   "配置文件回退了、但插件目录还是新的"这种半套状态，而界面上一个字都没写。
            string[] crit = CriticalPaths();
            sb.AppendLine("范围说明：本次只覆盖下列 " + crit.Length + " 个关键配置文件；");
            sb.AppendLine("          插件目录、node_modules、memories 的其它内容**不在范围内**，不会被回退。");
            foreach (string p in crit)
                sb.AppendLine("            · " + p.Substring(DshCore.DshHome.Length).TrimStart('\\'));
            sb.AppendLine();

            int ok = 0, skip = 0;
            foreach (string p in crit)
            {
                string rel = p.Substring(DshCore.DshHome.Length).TrimStart('\\');
                string src = Path.Combine(dir, "files", rel);
                if (!File.Exists(src)) { sb.AppendLine("  · 快照中无此文件，跳过  " + rel); skip++; continue; }
                try
                {
                    string bak = BackupFile(p);
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                    File.Copy(src, p, true);
                    sb.AppendLine("  ✔ 已恢复 " + rel + (bak == null ? "" : "（原文件备份 " + Path.GetFileName(bak) + "）"));
                    ok++;
                }
                catch (Exception e)
                {
                    sb.AppendLine("  ✘ 恢复失败 " + rel + " : " + e.Message);
                }
            }
            sb.AppendLine();
            sb.AppendLine("恢复 " + ok + " 个 / 跳过 " + skip + " 个。");
            // ★ T22：光写文件**不算生效** —— DSH 把配置读在内存里跑，之后任何一次
            //   设置变更/插件操作都会重新写盘，把刚恢复的旧值**静默覆盖回去**（假成功）。
            sb.AppendLine("⚠ **必须重启 DSH 才生效**：DSH 把配置读在内存里跑，");
            sb.AppendLine("  不重启的话，它下一次写配置就会把刚恢复的旧值覆盖回去。");
            return sb.ToString();
        }

        // ------------------------------------------------------------
        // 一键自检
        // ------------------------------------------------------------

        /// <summary>检查单个关键文件是否合法。返回 null 表示合法，否则返回原因。</summary>
        private static string CheckOne(string p)
        {
            if (!File.Exists(p)) return "文件不存在";
            string text;
            try { text = File.ReadAllText(p, Encoding.UTF8); }
            catch (Exception e) { return "读取失败：" + e.Message; }

            string err = null;
            string name = Path.GetFileName(p);
            if (p.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                if (!JsonWellFormed(text, out err)) return err;
            }
            else if (p.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                     p.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                // settings.yaml 是映射（顶层配置段）；其他 patch 类 yml 是序列
                bool isMapping = name.Equals("settings.yaml", StringComparison.OrdinalIgnoreCase) ||
                                 name.Equals("settings.yml", StringComparison.OrdinalIgnoreCase);
                if (isMapping)
                {
                    if (!YamlLooksLikeMapping(text, out err)) return err;
                }
                else
                {
                    if (!YamlLooksLikeSequence(text, out err)) return err;
                }
            }
            return null;
        }

        /// <summary>
        /// 强杀 dsh 进程之后的自动体检 + 自愈。
        ///
        /// 为什么需要：DSH 自己也会写 settings.yaml（设置面板 / 插件配置）。
        /// 如果我们在它写到一半时把它 Kill 掉（Windows 的 TerminateProcess 无法被捕获），
        /// 就会留下半截文件。强杀是不可避免的（没有优雅退出的公开接口），
        /// 所以对策是**杀完之后立刻体检，坏了就从杀之前的快照回滚**。
        /// </summary>
        public static string HealAfterKill(string snapshotDir)
        {
            var sb = new StringBuilder();
            int broke = 0, healed = 0;
            foreach (string p in CriticalPaths())
            {
                string why = CheckOne(p);
                if (why == null) continue;
                broke++;
                string rel = p.Substring(DshCore.DshHome.Length).TrimStart('\\');
                sb.AppendLine("  [发现异常] " + rel + " —— " + why);

                string src = null;
                if (!string.IsNullOrEmpty(snapshotDir))
                {
                    string cand = Path.Combine(snapshotDir, "files", rel);
                    if (File.Exists(cand)) src = cand;
                }
                if (src == null)
                {
                    sb.AppendLine("             快照中没有可用副本，未自动处理（请从 .bak-* 备份人工恢复）。");
                    continue;
                }
                try
                {
                    BackupFile(p);            // 坏内容也留个底，便于取证
                    File.Copy(src, p, true);
                    if (CheckOne(p) == null)
                    {
                        healed++;
                        sb.AppendLine("             ✔ 已从强杀前快照回滚成功。");
                    }
                    else
                    {
                        sb.AppendLine("             ✘ 回滚后仍不合法，请人工检查。");
                    }
                }
                catch (Exception e)
                {
                    sb.AppendLine("             ✘ 回滚失败：" + e.Message);
                }
            }

            if (broke == 0) return "";
            sb.Insert(0, ">>> 关键配置体检：发现 " + broke + " 个异常，已自动回滚 " + healed + " 个：\r\n");
            return sb.ToString();
        }

        public static string SelfCheck()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== 关键配置文件自检 ====");
            sb.AppendLine("DSH home: " + DshCore.DshHome);
            sb.AppendLine();
            int bad = 0;
            foreach (string p in CriticalPaths())
            {
                string rel = p.Substring(DshCore.DshHome.Length).TrimStart('\\');
                if (!File.Exists(p))
                {
                    sb.AppendLine("[缺失] " + rel);
                    bad++;
                    continue;
                }
                string text;
                try { text = File.ReadAllText(p, Encoding.UTF8); }
                catch (Exception e) { sb.AppendLine("[读失败] " + rel + " : " + e.Message); bad++; continue; }

                string err = CheckOne(p);
                bool ok = (err == null);

                var fi = new FileInfo(p);
                if (ok)
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "[OK]   {0}  ({1} 字节, 改动 {2})", rel, fi.Length,
                        fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")));
                else
                {
                    bad++;
                    sb.AppendLine("[损坏] " + rel + "  (" + fi.Length + " 字节)");
                    sb.AppendLine("        " + err);
                }
            }
            sb.AppendLine();
            sb.AppendLine(bad == 0 ? ">>> 结论：关键配置全部合法。" 
                                   : ">>> 结论：" + bad + " 个文件有问题。可点「恢复配置」退回最近一次快照。");
            var snaps = ListSnapshots();
            sb.AppendLine("现有快照 " + snaps.Count + " 份" +
                (snaps.Count > 0 ? "（最新 " + Path.GetFileName(snaps[0]) + "）" : "，建议先点「备份配置」。"));
            return sb.ToString();
        }

        public static string SnapshotManual()
        {
            string dir = Snapshot("手动备份（用户点「备份配置」）");
            if (dir == null) return "备份失败：无法写入 " + SnapshotRoot;
            var sb = new StringBuilder();
            sb.AppendLine("==== 已备份关键配置 ====");
            sb.AppendLine("快照目录：" + dir);
            sb.AppendLine(File.ReadAllText(Path.Combine(dir, "MANIFEST.txt"), Encoding.UTF8));
            sb.AppendLine("（快照保留最近 " + KeepSnapshots + " 份，自动轮换）");
            return sb.ToString();
        }

        /// <summary>列出快照清单（给界面用）。</summary>
        public static string SnapshotListText()
        {
            var sb = new StringBuilder();
            var list = ListSnapshots();
            sb.AppendLine("==== 配置快照（最新在前）====");
            if (list.Count == 0)
            {
                sb.AppendLine("（还没有快照。点「🛡 备份配置」立即存一份。）");
                return sb.ToString();
            }
            int i = 0;
            foreach (string d in list)
            {
                string man = Path.Combine(d, "MANIFEST.txt");
                string reason = "";
                if (File.Exists(man))
                {
                    foreach (string ln in File.ReadLines(man))
                        if (ln.StartsWith("原因: ")) { reason = ln.Substring(4).Trim(); break; }
                }
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0}. {1}  {2}",
                    ++i, Path.GetFileName(d), reason));
                if (i >= 20) { sb.AppendLine("  …（只显示最近 20 份）"); break; }
            }
            // ★ P15/P14：把"会不会被自动删掉""为什么这里看到的比恢复对话框多"讲清楚 ——
            //   用户很容易以为「备份」是只增不减的。
            sb.AppendLine();
            sb.AppendLine("共 " + list.Count + " 份；本工具只保留最近 " + KeepSnapshots + " 份，**超出的会自动删除最旧的**。");
            if (list.Count > 10)
                sb.AppendLine("（「♻ 恢复配置」的对话框里只列最新 10 份；更早的仍在这里可见，");
            if (list.Count > 10)
                sb.AppendLine("  如需恢复到更早的那一份，请告诉 AI 或手动指定快照目录名。）");
            return sb.ToString();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BigFatFishRescuer
{
    /// <summary>
    /// WP1 · 冲突雷达（**只读**）：把「谁占了哪件共享物」摊成一张表，同一件共享物出现
    /// 两个以上写者 ⇒ 报红并附证据（文件 + 行号 + 写者名）。
    ///
    /// 判据不写死任何插件名/目录/端口（任务书 §2.5）：条目 id、配置键、入口路径、bundle 名
    /// 全部从被扫文件里读出来；认不出来的东西一律标 unknown 并保守地**不报红**。
    ///
    /// 为什么写者身份要单独解析：cordis.patch.yml 里同一 id 常被多个来源写 ——
    /// 插件安装器写的 `# --- xxx managed ---` 块、人手加的块、皮肤管理器自己的块。
    /// 真正会打架的不是"这个 id 存在"，而是"两个写者都主张对它的控制权"。
    /// </summary>
    public static class ConflictRadar
    {
        // ---------------- 数据模型 ----------------
        public sealed class Occ
        {
            public string Kind = "";     // 共享物类别
            public string Key = "";      // 被占的东西（id / id.configKey / 路径 / 端口…）
            public string Writer = "";   // 写者（归属块；认不出⇒unknown）
            public string File = "";     // 证据来源
            public int Line;
            public string Note = "";
            public string Enabled = "";  // true / false / 空
            /// <summary>相对路径的解析基准目录。bundle patch 里的 name 是**相对该插件包**的，
            /// 按 profile 根解析会把好端端的入口全判成不存在（实测 194 条假红就是这么来的）。</summary>
            public string Base = "";
            /// <summary>声明者位置：loaded=在装载集闭包里；diskonly=磁盘上有这个包但没被登记加载；
            /// n_a=写者不是包名（人手块名之类）。判红只该吃 loaded，见 BuildLoadedSet 注释。</summary>
            public string Scope = "";
        }

        public sealed class Row
        {
            public string Kind = "";
            public string Key = "";
            public List<Occ> Occs = new List<Occ>();

            // ★ 自身登记（bundle patch 里插件登记自己的 id）**不计入写者数**：
            //   那是"这个条目存在"的来源，不是"另一个人来抢"。把它算成竞争者会把
            //   两层覆盖（同一个写者）抬成 2 个写者 ⇒ 满屏假红。
            public int DistinctWriters()
            {
                List<string> w = new List<string>();
                for (int i = 0; i < Occs.Count; i++)
                {
                    if (IsSelfRegistration(Occs[i])) continue;
                    string s = Occs[i].Writer ?? "";
                    if (s.Length == 0) s = "unknown";
                    if (!Contains(w, s)) w.Add(s);
                }
                return w.Count;
            }
            public int SelfRegistrations()
            {
                int c = 0;
                for (int i = 0; i < Occs.Count; i++) if (IsSelfRegistration(Occs[i])) c++;
                return c;
            }
            static bool IsSelfRegistration(Occ o)
            {
                if (o == null) return false;
                if ((o.Note ?? "") == "self") return true;
                string f = o.File ?? "";
                if (f.IndexOf("bundle:", StringComparison.OrdinalIgnoreCase) < 0) return false;
                string w = o.Writer ?? "";
                if (w.Length == 0) return false;
                // ★ 两边都要先把反斜杠归一成 '/'：证据改成**绝对路径**之后，包名里的
                //   "@scope/pkg" 在 Windows 路径里是 "@scope\pkg"，直接 IndexOf 会全部落空
                //   ⇒ 自登记识别失效，29 条假硬红当场回来（本机实测）。
                string fn = f.Replace((char)92, '/');
                if (fn.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                // 磁盘目录有时用不带 scope 的名字（dsh-skin vs @scope/dsh-skin）
                string leaf = w;
                int sl = w.LastIndexOf('/');
                if (sl >= 0 && sl + 1 < w.Length) leaf = w.Substring(sl + 1);
                return leaf.Length > 3 && fn.IndexOf(leaf, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            public bool AnyUnknownWriter()
            {
                for (int i = 0; i < Occs.Count; i++)
                {
                    string s = Occs[i].Writer ?? "";
                    if (s.Length == 0 || s == "unknown") return true;
                }
                return false;
            }
            public int OccurrencesInSameFile()
            {
                for (int i = 0; i < Occs.Count; i++)
                {
                    for (int j = i + 1; j < Occs.Count; j++)
                    {
                        if (string.Equals(Occs[i].File, Occs[j].File, StringComparison.OrdinalIgnoreCase)) return 2;
                    }
                }
                return 1;
            }
        }

        /// <summary>raw 里的「包目录」是否存在（scoped 取前两段，非 scoped 取第一段）。
        /// 只判这一层是**故意保守**：子路径要靠包 exports 才能解析，静态判不了就别装懂。</summary>
        public static bool PackageDirExists(string raw)
        {
            try
            {
                string s = (raw ?? "").Replace((char)92, '/').TrimStart('.');
                s = s.TrimStart('/');
                if (s.Length == 0) return false;
                string[] seg = s.Split('/');
                string pkg = seg[0].StartsWith("@") && seg.Length > 1 ? seg[0] + "/" + seg[1] : seg[0];
                string[] roots = new string[] {
                    Path.Combine(DshCore.DshHome, "profiles", "web", "node_modules"),
                    Path.Combine(DshCore.DshHome, "profiles", "node_modules") };
                for (int i = 0; i < roots.Length; i++)
                    if (Directory.Exists(Path.Combine(roots[i], pkg.Replace('/', Path.DirectorySeparatorChar)))) return true;
                return false;
            }
            catch { return false; }
        }

        public static int CountHard(List<Conflict> cs)
        {
            int n = 0;
            for (int i = 0; i < cs.Count; i++) if (!cs[i].Soft) n++;
            return n;
        }
        /// <summary>自检用：某一类里有没有**硬红**（提醒不算，提醒是"看见了但不判"）。</summary>
        static bool HasHard(List<Conflict> cs, string kind, string keyPart)
        {
            for (int i = 0; i < cs.Count; i++)
            {
                if (!string.Equals(cs[i].Kind, kind, StringComparison.Ordinal)) continue;
                if ((cs[i].Key ?? "").IndexOf(keyPart, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!cs[i].Soft) return true;
            }
            return false;
        }
        /// <summary>自检用：这一类里到底有没有出现过这个键（防止"保守降级"变成"根本没报"）。</summary>
        static bool HasAny(List<Conflict> cs, string kind, string keyPart)
        {
            for (int i = 0; i < cs.Count; i++)
            {
                if (!string.Equals(cs[i].Kind, kind, StringComparison.Ordinal)) continue;
                if ((cs[i].Key ?? "").IndexOf(keyPart, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
        static int CountSoft(List<Conflict> cs)
        {
            int n = 0;
            for (int i = 0; i < cs.Count; i++) if (cs[i].Soft) n++;
            return n;
        }

        /// <summary>入口声明能否解析：路径（文件或目录）或 node_modules 里的包目录。</summary>
        public static bool EntryResolvable(string raw, string asPath)
        {
            try
            {
                if (File.Exists(asPath) || Directory.Exists(asPath)) return true;
                string[] roots = new string[] {
                    Path.Combine(DshCore.DshHome, "profiles", "web", "node_modules"),
                    Path.Combine(DshCore.DshHome, "profiles", "node_modules"),
                    Path.Combine(DshCore.DshHome, "plugins") };
                for (int i = 0; i < roots.Length; i++)
                {
                    string cand = Path.Combine(roots[i], raw);
                    if (Directory.Exists(cand) || File.Exists(cand)) return true;
                }
                return false;
            }
            catch { return true; }   // 判不了就别报红（保守），交给未扫说明
        }

        static bool Contains(List<string> list, string s)
        {
            for (int i = 0; i < list.Count; i++) if (string.Equals(list[i], s, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Trim().Trim('"', '\'');
        }

        // ---------------- 扫描入口 ----------------
        public sealed class ScanResult
        {
            public List<Occ> All = new List<Occ>();
            public List<string> FilesScanned = new List<string>();
            public List<string> FilesMissing = new List<string>();
            public List<string> NotScanned = new List<string>();
            public List<string> LoadedNames = new List<string>();   // 装载集闭包
            public int PluginDirs;
            public int Bundles;
            public int MemoryFiles;        // WP2：扫了多少个记忆文件
            public int MemoryLiteralHits;  // 里头的字面量条数（只数条数，不外泄内容）
        }

        // ---------------- 装载集：谁**真的**会被加载 ----------------
        // 为什么必须有这一层：第三层扫描是"把三棵 node_modules 里所有带 dsh 字段的清单都读一遍"，
        // 本机实测磁盘上 495 个包目录、而从 13 个 bundle 出发闭包只有 160 个名字。
        // 拿磁盘全集当"有人在抢"，就会把**根本没加载**的包算进冲突 ⇒ 第六类假红的形状。
        // 这里不写死任何包名：种子全部来自前三层已经读到的 bundle 名与 insert 的 name:。
        public static void BuildLoadedSet(ScanResult r)
        {
            List<string> seeds = new List<string>();
            for (int i = 0; i < r.All.Count; i++)
            {
                Occ o = r.All[i];
                if (o.Kind == "bundle" && o.Key.Length > 0) AddName(seeds, o.Key);
                if (o.Kind != "入口路径" || o.Key.Length == 0) continue;
                // ★ 种子只能来自**权威层**（home/profile 覆盖层 + package.json 的 bundle 名单）。
                //   原来这里把所有「入口路径」都当种子 ⇒ 每个磁盘上的 bundle 自带 patch 都把自己
                //   登记进闭包（dsh-headless 的 patch 第 27 行就写着 name: '@deepseek-ai/dsh-headless'），
                //   闭包被吹成 168 个名字，"没加载的包"因此被当成加载了 —— 那 102 条 diskonly
                //   记录里有一大块是这么洗白的。自登记不等于被装载。
                string fs = o.File ?? "";
                if (!fs.StartsWith("home:", StringComparison.OrdinalIgnoreCase) &&
                    !fs.StartsWith("profile:", StringComparison.OrdinalIgnoreCase)) continue;
                int ar = o.Key.IndexOf("⇒", StringComparison.Ordinal);
                if (ar > 0) AddName(seeds, o.Key.Substring(ar + 2));
                else AddName(seeds, o.Key);
            }
            HashSet<string> done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Queue<string> q = new Queue<string>();
            for (int i = 0; i < seeds.Count; i++) q.Enqueue(seeds[i]);
            int guard = 0;
            while (q.Count > 0 && guard < 4000)
            {
                guard++;
                string n = NormName(q.Dequeue());
                if (n.Length == 0 || done.Contains(n)) continue;
                done.Add(n);
                string dir = PackageDirOf(n);
                if (dir.Length == 0) continue;
                string text = "";
                try { text = File.ReadAllText(Path.Combine(dir, "package.json")); } catch { continue; }
                string patchRel = ConflictRadarHosts.BundlePatchOf(text);
                if (patchRel.Length == 0) continue;
                string pp = patchRel;
                try { if (!Path.IsPathRooted(pp)) pp = Path.Combine(dir, pp); } catch { continue; }
                string yml = "";
                try { if (File.Exists(pp)) yml = File.ReadAllText(pp); } catch { continue; }
                // bundle 自带 patch 里的 name: ⇒ 又一层被登记进来的模块
                System.Text.RegularExpressions.MatchCollection ms;
                try
                {
                    ms = System.Text.RegularExpressions.Regex.Matches(
                        yml, "(?m)^\\s*name:\\s*'?([^'\\r\\n]+)'?\\s*$");
                }
                catch { continue; }
                for (int i = 0; i < ms.Count; i++)
                {
                    string v = Norm(ms[i].Groups[1].Value);
                    if (v.Length == 0) continue;
                    if (!done.Contains(NormName(v))) q.Enqueue(v);
                }
            }
            r.LoadedNames = new List<string>(done);
            r.LoadedNames.Sort(StringComparer.OrdinalIgnoreCase);
            // 标 scope：声明者本身是不是包 ⇒ 是包就在/不在装载集里二选一；不是包（人手块名）⇒ n_a。
            for (int i = 0; i < r.All.Count; i++)
            {
                Occ o = r.All[i];
                string w = NormName(o.Writer ?? "");
                if (w.Length == 0) { o.Scope = "n_a"; continue; }
                if (IsLoaded(r, w)) { o.Scope = "loaded"; continue; }
                o.Scope = PackageDirExists(o.Writer) ? "diskonly" : "n_a";
            }
        }
        /// <summary>「入口路径」的 Key 是 `id ⇒ name`；取 id 那半（同 name 不同 id 才是要判红的形状）。</summary>
        static string EntryIdOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            int ar = key.IndexOf("⇒", StringComparison.Ordinal);
            if (ar < 0) return key.Trim();
            return key.Substring(0, ar).Trim();
        }
        /// <summary>「入口路径」记录的 Key 是 `id ⇒ name`；这里取 name 那半（聚合按它，不按 id）。</summary>
        static string EntryNameOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            int ar = key.IndexOf("⇒", StringComparison.Ordinal);
            if (ar < 0) return "";
            return key.Substring(ar + 2).Trim().Trim('\'', '"');
        }
        static bool IsLoaded(ScanResult r, string name)
        {
            string n = NormName(name);
            if (n.Length == 0) return false;
            for (int i = 0; i < r.LoadedNames.Count; i++)
                if (string.Equals(r.LoadedNames[i], n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        /// <summary>前三层登记了多少个不同的 bundle 名（自检用它验"每个种子都必须在闭包里"）。</summary>
        static int BundlesSeeded(ScanResult r)
        {
            List<string> s = new List<string>();
            for (int i = 0; i < r.All.Count; i++)
            {
                if (r.All[i].Kind != "bundle") continue;
                string n = NormName(r.All[i].Key);
                if (n.Length == 0) continue;
                bool dup = false;
                for (int j = 0; j < s.Count; j++) if (string.Equals(s[j], n, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                if (!dup) s.Add(n);
            }
            return s.Count;
        }
        static void AddName(List<string> l, string v)
        {
            string n = NormName(v);
            if (n.Length > 0) l.Add(n);
        }
        /// <summary>`@scope/pkg`、`@scope/pkg/sub`、`./plugins/x/lib/index.js` → 归一成"包/插件目录名"。</summary>
        static string NormName(string raw)
        {
            string s = (raw ?? "").Replace((char)92, '/').Trim().Trim('\'', '"');
            if (s.Length == 0) return "";
            if (s.StartsWith("./") || s.StartsWith("../") || s.StartsWith("/") || Path.IsPathRooted(s))
            {
                s = s.Replace((char)92, '/');
                string[] seg = s.Split('/');
                for (int i = 0; i + 1 < seg.Length; i++)
                {
                    if (seg[i].Equals("plugins", StringComparison.OrdinalIgnoreCase) ||
                        seg[i].Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                        return seg[i + 1];
                }
                return seg.Length > 0 ? seg[seg.Length - 1] : "";
            }
            string[] ps = s.Split('/');
            if (ps[0].StartsWith("@") && ps.Length > 1) return ps[0] + "/" + ps[1];
            return ps[0];
        }
        /// <summary>包名 → 磁盘目录（两棵 profile node_modules 根）；找不到返回空串。</summary>
        public static string PackageDirOf(string raw)
        {
            try
            {
                string pkg = NormName(raw);
                if (pkg.Length == 0) return "";
                string[] roots = new string[] {
                    Path.Combine(DshCore.DshHome, "profiles", "web", "node_modules"),
                    Path.Combine(DshCore.DshHome, "profiles", "node_modules") };
                for (int i = 0; i < roots.Length; i++)
                {
                    string p = Path.Combine(roots[i], pkg.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(Path.Combine(p, "package.json"))) return p;
                }
                string pl = Path.Combine(ProfileDir, "plugins", pkg, "package.json");
                if (File.Exists(pl)) return Path.GetDirectoryName(pl);
            }
            catch { }
            return "";
        }

        static string ProfileDir { get { return Path.Combine(DshCore.DshHome, "profiles", "web"); } }

        public static ScanResult Scan()
        {
            ScanResult r = new ScanResult();

            string homePatch = Path.Combine(DshCore.DshHome, "cordis.patch.yml");
            string homeRoot = Path.Combine(DshCore.DshHome, "cordis.yml");
            string profPatch = Path.Combine(ProfileDir, "cordis.patch.yml");
            string profRoot = Path.Combine(ProfileDir, "cordis.yml");
            string profPkg = Path.Combine(ProfileDir, "package.json");
            string pluginsDir = Path.Combine(ProfileDir, "plugins");

            ReadManifest(homeRoot, "home:cordis.yml", r);
            ReadManifest(homePatch, "home:cordis.patch.yml", r);
            ReadManifest(profRoot, "profile:cordis.yml", r);
            ReadManifest(profPatch, "profile:cordis.patch.yml", r);
            ReadBundles(profPkg, r);
            ReadPluginDirs(pluginsDir, r);
            // ★ 第三层（bundle 自带 patch）与宿主注入点：见 ConflictRadarHosts 的注释，
            //   不读它就会把「占用表残缺」当成「没冲突」。
            ConflictRadarHosts.Reset();
            ConflictRadarHosts.Scan(r);
            // ★ 装载集闭包：必须在第三层扫完之后、判红之前算 —— 判红只该吃"真的会被加载"的那批。
            BuildLoadedSet(r);
            // WP2 静态判据：补丁登记一致性 + 提示词字面量（只读；只报文件名与条数）
            ConflictRadarStatic.CheckPatchRegistry(ProfileDir, r.All, r);
            ConflictRadarStatic.CheckPromptLiterals(Path.Combine(DshCore.DshHome, "memories"), r.All, r);
            ReadPorts(r);

            r.NotScanned.Add("服务名（provider/consumer）：本机 15 个插件清单里**没有** cordis 声明字段（实测只有 dsh.client / dsh.bundle），服务在运行时代码里注册 ⇒ 静态判不了，要靠日志里的 pending (waiting for service …) 与 WP3 对照");
            r.NotScanned.Add("依赖解析链（插件在 profile 树外）：需要跑一次真实 import，属 WP3 沙箱实验 ⇒ 本版只报位置");
            r.NotScanned.Add("构建授权 allowBuilds：已有 PluginScout 负责，本雷达不重复判 ⇒ 交给 --scout");
            r.NotScanned.Add("补丁文件与 patchedDependencies 一致性：属 patched-node-modules 类判据 ⇒ 未扫");
            r.NotScanned.Add("提示词段/变量（memories 的 KEY/USER/MEMORY）：未扫");
            r.NotScanned.Add("UI 控件级挂载点（具体改了哪批控件）：清单里只有 dsh.client.inject 这一层声明（已扫），真正的挂载调用在代码里 ⇒ 控件级抢占仍需 WP3 对照");
            return r;
        }

        static void NoteFile(ScanResult r, string path, bool exists)
        {
            if (exists) r.FilesScanned.Add(path);
            else r.FilesMissing.Add(path);
        }

        /// <summary>
        /// 读 cordis 清单（yml  lite）。识别：`- id:`、`disabled:`、`insert:` 下的 `- id:`/`name:`、
        /// `config:` 下的缩进键，以及 `# --- 写者块 ---` 归属注释。
        /// </summary>
        public static void ReadManifest(string path, string tag, ScanResult r)
        {
            bool exists = File.Exists(path);
            NoteFile(r, path, exists);
            if (!exists) return;
            string text;
            try { text = File.ReadAllText(path); }
            catch { r.FilesMissing.Add(path + " (读不到)"); return; }

            string writer = "";                 // 当前归属块
            string curId = "";                  // 当前条目 id
            bool inInsert = false;
            int insertIndent = -1;
            bool inConfig = false;
            int configIndent = -1;
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
            for (int idx = 0; idx < lines.Length; idx++)
            {
                string raw = lines[idx];
                string line = raw.Trim();
                int indent = raw.Length - raw.TrimStart().Length;
                if (line.Length == 0) continue;

                // 归属注释：# --- xxx (managed|manual|…) ---  → 写者名取第一段非路径文字
                if (line.StartsWith("#"))
                {
                    string body = line.TrimStart('#', ' ').Trim();
                    if (body.StartsWith("---"))
                    {
                        body = body.TrimStart('-').Trim();
                        int cut = body.IndexOfAny(new char[] { '(', ',', ':', '\t' });
                        if (cut > 0) body = body.Substring(0, cut);
                        body = body.Replace("managed", "").Replace("auto-generated", "").Trim();
                        writer = body.Length > 0 ? body : "";
                        curId = ""; inInsert = false; inConfig = false;
                    }
                    continue;
                }

                if (line.StartsWith("disabled:") && curId.Length > 0)
                {
                    // 记一条"被禁"痕迹到该 id 上，供上面的「装与禁并存」判据使用
                    for (int z = r.All.Count - 1; z >= 0; z--)
                    {
                        if (r.All[z].Kind == "加载器条目" && r.All[z].Key == curId && r.All[z].File == tag)
                        {
                            r.All[z].Note = (r.All[z].Note ?? "") + " disabled:" + Norm(line.Substring("disabled:".Length));
                            break;
                        }
                    }
                }

                if (line.StartsWith("- insert:"))
                {
                    inInsert = true; inConfig = false;
                    insertIndent = indent;
                    continue;
                }
                if (line.StartsWith("- id:") || line.StartsWith("id:"))
                {
                    string v = Norm(line.Substring(line.IndexOf(':') + 1));
                    if (v.Length > 0)
                    {
                        curId = v;
                        Occ o = new Occ();
                        o.Kind = "加载器条目";
                        o.Key = v;
                        o.Writer = writer.Length > 0 ? writer : "unknown";
                        o.File = tag; o.Line = idx + 1;
                        o.Note = inInsert ? "insert" : "override";
                        r.All.Add(o);
                    }
                    inConfig = false;
                    continue;
                }
                if (line.StartsWith("disabled:"))
                {
                    string v = Norm(line.Substring("disabled:".Length));
                    if (curId.Length > 0)
                    {
                        Occ o = new Occ();
                        o.Kind = "启用开关";
                        o.Key = curId;
                        o.Writer = writer.Length > 0 ? writer : "unknown";
                        o.File = tag; o.Line = idx + 1;
                        o.Enabled = v;
                        r.All.Add(o);
                    }
                    continue;
                }
                if (line.StartsWith("name:"))
                {
                    string v = Norm(line.Substring("name:".Length));
                    if (v.Length > 0 && curId.Length > 0)
                    {
                        Occ o = new Occ();
                        o.Kind = "入口路径";
                        o.Key = curId + " ⇒ " + v;
                        o.Writer = writer.Length > 0 ? writer : "unknown";
                        o.File = tag; o.Line = idx + 1;
                        o.Note = Path.IsPathRooted(v) ? "abs" : "rel";
                        r.All.Add(o);
                    }
                    continue;
                }
                if (line.StartsWith("config:"))
                {
                    inConfig = true;
                    configIndent = indent;
                    continue;
                }
                if (inConfig)
                {
                    if (indent <= configIndent) { inConfig = false; }
                    else
                    {
                        int c = line.IndexOf(':');
                        if (c > 0)
                        {
                            string k = line.Substring(0, c).Trim();
                            if (k.Length > 0 && !k.StartsWith("-") && curId.Length > 0)
                            {
                                Occ o = new Occ();
                                o.Kind = "配置键";
                                o.Key = curId + "." + k;
                                o.Writer = writer.Length > 0 ? writer : "unknown";
                                o.File = tag; o.Line = idx + 1;
                                o.Note = Norm(line.Substring(c + 1));
                                r.All.Add(o);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>从 profile 的 package.json 里读 dsh.profile.bundles（不引 JSON 库，按括号切片）。</summary>
        public static void ReadBundles(string pkgPath, ScanResult r)
        {
            bool exists = File.Exists(pkgPath);
            NoteFile(r, pkgPath, exists);
            if (!exists) return;
            string text;
            try { text = File.ReadAllText(pkgPath); } catch { return; }
            int b = text.IndexOf("\"bundles\"", StringComparison.Ordinal);
            if (b < 0) return;
            int lb = text.IndexOf('[', b);
            int rb = lb < 0 ? -1 : text.IndexOf(']', lb);
            if (lb < 0 || rb < 0 || rb <= lb) return;
            string inner = text.Substring(lb + 1, rb - lb - 1);
            string[] parts = inner.Split('"');
            for (int i = 0; i < parts.Length; i++)
            {
                string name = parts[i].Trim();
                if (name.Length == 0 || name == "," ) continue;
                if (name.IndexOf(':') >= 0 && name.IndexOf('/') < 0) continue;   // 键名而非值
                Occ o = new Occ();
                o.Kind = "bundle";
                o.Key = name;
                o.Writer = "package.json";
                o.File = "profile:package.json";
                o.Line = 0;
                r.All.Add(o);
                r.Bundles++;
            }
        }

        /// <summary>
        /// 本地插件目录：**判"装没装"必须先看这里**（它们既不在 node_modules 也不在 package.json
        /// 的 dependencies —— 这是本项目踩过的一次误判根源）。
        /// </summary>
        public static void ReadPluginDirs(string pluginsDir, ScanResult r)
        {
            if (!Directory.Exists(pluginsDir)) { r.FilesMissing.Add(pluginsDir + " (不存在)"); return; }
            r.FilesScanned.Add(pluginsDir);
            string[] dirs;
            try { dirs = Directory.GetDirectories(pluginsDir); } catch { return; }
            for (int i = 0; i < dirs.Length; i++)
            {
                string name = Path.GetFileName(dirs[i]);
                if (name.Length == 0) continue;
                if (name.StartsWith("_") || name.IndexOf(".bak", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                Occ o = new Occ();
                o.Kind = "本地插件目录";
                o.Key = name;
                o.Writer = "fs";
                o.File = "profile:plugins/" + name;
                o.Note = Directory.Exists(Path.Combine(dirs[i], "lib")) ? "有 lib/" : "无 lib/";
                r.All.Add(o);
                r.PluginDirs++;
            }
        }

        /// <summary>端口/实例占用（复用 DshCore 的 TCP 表与进程身份判据；只读，绝不动手）。</summary>
        public static void ReadPorts(ScanResult r)
        {
            int[] pids;
            try { pids = DshCore.GetDshPids(); } catch { pids = new int[0]; }
            if (pids == null) pids = new int[0];
            for (int i = 0; i < pids.Length; i++)
            {
                int pid = pids[i];
                int[] owned;
                try { owned = DshCore.GetPortOwnerPidsFast(DshCore.ActivePort); } catch { owned = new int[0]; }
                bool onActive = false;
                if (owned != null) for (int k = 0; k < owned.Length; k++) if (owned[k] == pid) onActive = true;
                Occ o = new Occ();
                o.Kind = "端口";
                o.Key = onActive ? DshCore.ActivePort.ToString(CultureInfo.InvariantCulture) : "未知(不在当前端口)";
                o.Writer = "pid:" + pid.ToString(CultureInfo.InvariantCulture);
                o.File = "本机 TCP 表";
                o.Note = onActive ? "监听当前端口" : "是 dsh 但没监听当前端口";
                r.All.Add(o);
            }
        }

        // ---------------- 判红 ----------------
        public sealed class Conflict { public string Kind; public string Key; public string Why; public bool Soft; public List<Occ> Occs = new List<Occ>(); }

        public static List<Conflict> Judge(List<Occ> allIn)
        {
            // ★ 判红只吃"真的会被加载"的记录：声明者是**仅磁盘存在、没被任何一层登记加载**的包
            //   （diskonly）⇒ 不参与多写者/悬空判红。但明细要留在表里（scope=diskonly），
            //   否则"扫了 495 份清单、其中一大半根本没在跑"这件事就看不见。
            List<Occ> diskOnly = new List<Occ>();
            List<Occ> kept = new List<Occ>();
            for (int i = 0; i < allIn.Count; i++)
            {
                if (allIn[i].Scope == "diskonly") diskOnly.Add(allIn[i]);
                else kept.Add(allIn[i]);
            }
            List<Occ> all = kept;
            List<Conflict> outp = new List<Conflict>();
            Dictionary<string, Row> rows = Group(all);
            foreach (KeyValuePair<string, Row> kv in rows)
            {
                Row row = kv.Value;
                // ★ 「宿主注入声明」不参与多写者判红：inject 是「我要和这个宿主模块一起加载」
                //   的依赖声明，同一个宿主模块被十几个插件注入是 cordis 的正常设计。
                //   它只在「目标模块找不到」时才算问题（见 ConflictRadarHosts.Extras）。
                if (row.Kind == "宿主注入声明") continue;
                // 保守：写者认不出来 ⇒ 不据此报红（判不出来就 unknown）
                if (row.AnyUnknownWriter() && row.Occs.Count > 1)
                {
                    Conflict c = new Conflict();
                    c.Kind = row.Kind; c.Key = row.Key;
                    c.Why = "占同一件东西但**写者身份认不出**（unknown）⇒ 只提醒，不判红";
                    c.Occs = row.Occs;
                    outp.Add(c);
                    continue;
                }
                if (row.Occs.Count >= 1 && row.DistinctWriters() >= 2)
                {
                    Conflict c = new Conflict();
                    c.Kind = row.Kind; c.Key = row.Key;
                    c.Why = "同一件共享物有 " + row.DistinctWriters().ToString(CultureInfo.InvariantCulture) + " 个写者";
                    c.Occs = row.Occs;
                    outp.Add(c);
                    continue;
                }
                if (row.Kind == "加载器条目" && row.OccurrencesInSameFile() >= 2)
                {
                    Conflict c = new Conflict();
                    c.Kind = row.Kind; c.Key = row.Key;
                    c.Why = "同一个文件里登记了两次 ⇒ loader 会报 duplicate loader entry id";
                    c.Occs = row.Occs;
                    outp.Add(c);
                    continue;
                }
            }
            // 同一个 id 既被 insert 登记、又被 disabled:true ⇒ 「一个要装、一个要禁」，
            // 这是真抢占（与写者数无关，常常正是人手一行 vs 插件自己登记）。
            Dictionary<string, Row> rowsSw = Group(all);
            foreach (KeyValuePair<string, Row> kv in rowsSw)
            {
                Row row = kv.Value;
                if (row.Kind != "加载器条目") continue;
                bool hasInsert = false, hasDisable = false;
                for (int i = 0; i < row.Occs.Count; i++)
                {
                    if (row.Occs[i].Note == "insert") hasInsert = true;
                    if ((row.Occs[i].Note ?? "").IndexOf("disabled", StringComparison.OrdinalIgnoreCase) >= 0) hasDisable = true;
                }
                if (hasInsert && hasDisable)
                {
                    Conflict c2 = new Conflict();
                    c2.Kind = "条目存废"; c2.Key = row.Key;
                    // ★ 降级为提醒：`disabled:` 压过 bundle 自己的 `insert:` 很可能就是 cordis 的
                    //   正常语义（主人手动禁 whale-desktop-launcher 正是这个用法）。没验清谁赢之前
                    //   把它算成冲突 = 满屏"假红"，正是任务书 §8 说的"看起来很成功的废品"。
                    //   真要靠 WP3 的 A/B 对照来定，这里只登记候选。
                    c2.Soft = true;
                    c2.Why = "同一个 id 既被 insert 登记又被 disabled ⇒ 谁赢未验证 ⇒ 待 WP3 对照，先不判红";
                    c2.Occs = row.Occs;
                    outp.Add(c2);
                    continue;
                }
                // 只有自身登记、没有别层覆盖 ⇒ 什么都不报。
                // （以前这里也塞进冲突列表，于是「提醒条数」混进了「红条数」，
                //   让 conflicts= 这个数字失去意义。）
            }

            // 入口路径指向的文件不存在 ⇒ 悬空引用（真红，且不需要写者身份）
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Kind != "入口路径") continue;
                string p = all[i].Key;
                int arrow = p.IndexOf("⇒", StringComparison.Ordinal);
                if (arrow > 0) p = p.Substring(arrow + 1).Trim();
                string abs = p;
                try
                {
                    if (!Path.IsPathRooted(p))
                    {
                        string baseDir = (all[i].Base ?? "").Length > 0 ? all[i].Base : ProfileDir;
                        abs = Path.Combine(baseDir, p);
                    }
                }
                catch { }
                // ★ bundle patch 里的 name 经常是**包名**（cordis 走模块解析），不是文件路径。
                //   只按"文件是否存在"判，就会把 194 条正常登记全判成悬空（实测踩过）。
                //   所以三路都试：相对该包目录 / 相对 profile 根 / node_modules 里的包目录。
                if (!EntryResolvable(p, abs))
                {
                    Conflict c = new Conflict();
                    c.Kind = "入口路径"; c.Key = all[i].Key;
                    bool pkgThere = PackageDirExists(p);
                    c.Soft = pkgThere;
                    c.Why = pkgThere
                        ? "包目录在、子路径静态解析不到（要走包 exports 才能定）⇒ unknown，不判红"
                        : "入口的包目录本身就不存在 ⇒ cannot resolve profile bundle（真红）";
                    c.Occs.Add(all[i]);
                    outp.Add(c);
                }
            }
            // ★★ 缺了一把尺子（2026-09-20 由 WP3 实测暴露）：原来的分组按**条目 id**聚合，
            //   而实测会打死冷启动的那个形状是「**同一个入口文件**被两个写者各登记一次
            //   （两个不同的 id）」—— 按 id 分组时它是两条互不相干的记录，雷达**根本看不见**。
            //   现在补上：按 name 聚合，两个以上写者 ⇒ 硬红。硬的理由不是推测，是量出来的：
            //   L1 沙箱 A/B/A+B 里 base0/A/B 全绿、A+B 3.5 秒崩，报 `webserver: duplicate exact route`。
            Dictionary<string, List<Occ>> byName = new Dictionary<string, List<Occ>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Kind != "入口路径") continue;
                string nm = EntryNameOf(all[i].Key);
                if (nm.Length == 0) continue;
                List<Occ> l;
                if (!byName.TryGetValue(nm, out l)) { l = new List<Occ>(); byName[nm] = l; }
                l.Add(all[i]);
            }
            List<string> nk = new List<string>(byName.Keys); nk.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < nk.Count; i++)
            {
                List<Occ> l = byName[nk[i]];
                if (l.Count < 2) continue;
                List<string> ws = new List<string>();
                List<string> ids = new List<string>();
                for (int j = 0; j < l.Count; j++)
                {
                    string w = (l[j].Writer ?? "").Length > 0 ? l[j].Writer : "unknown";
                    bool dup = false;
                    for (int k = 0; k < ws.Count; k++) if (string.Equals(ws[k], w, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                    if (!dup) ws.Add(w);
                    string id = EntryIdOf(l[j].Key);
                    bool dupId = false;
                    for (int k = 0; k < ids.Count; k++) if (string.Equals(ids[k], id, StringComparison.OrdinalIgnoreCase)) { dupId = true; break; }
                    if (!dupId) ids.Add(id);
                }
                if (ws.Count < 2) continue;   // 同一写者登记两次是它自己的事（另有条目重复判据管）
                // ★ 分水岭**不是** id 同不同（我原来这么猜，被实测推翻了：同 id 两个写者
                //   照样 0.7 秒崩，日志里 `duplicate loader entry id` 出现 4 次 ⇒ WP3 exp=dupid）。
                //   真正判不了的是"这两个写者会不会在同一次启动里都生效"：
                //     · 只要有一条来自覆盖层（home:/profile:/plugins 的人手或安装器块）⇒ 一定会生效 ⇒ 硬红
                //     · 全部来自各 bundle 自带的 patch ⇒ 谁生效取决于平台/装载路径，
                //       本机实测这种组合冷启动是绿的（说明并非两个都生效）⇒ 静态判不了 ⇒ unknown，只提醒
                bool anyOverlay = false;
                for (int j = 0; j < l.Count; j++)
                {
                    string fs = l[j].File ?? "";
                    if (fs.StartsWith("bundle:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (fs.StartsWith("manifest:", StringComparison.OrdinalIgnoreCase)) continue;
                    anyOverlay = true;
                }
                Conflict c = new Conflict();
                c.Kind = "入口重复登记"; c.Key = nk[i];
                c.Soft = !anyOverlay;
                c.Why = anyOverlay
                    ? "同一个入口文件被 " + ws.Count.ToString(CultureInfo.InvariantCulture) + " 个写者各登记一次（id 有 " +
                      ids.Count.ToString(CultureInfo.InvariantCulture) + " 个：" + string.Join(", ", ids.ToArray()) +
                      "），且**至少一条写在覆盖层里 ⇒ 一定会生效** ⇒ L1 实测：同 id 0.7 秒崩（duplicate loader entry id），" +
                      "不同 id 3.5 秒崩（webserver: duplicate exact route）"
                    : "同一个入口文件被 " + ws.Count.ToString(CultureInfo.InvariantCulture) +
                      " 个写者各自在自己 bundle 的 patch 里登记 ⇒ 静态无法判定这两个会不会在同一次启动里都生效；" +
                      "本机实测这种组合冷启动是绿的（说明并非两个都生效，多半按平台分流）⇒ unknown，只提醒不判红";
                c.Occs = l;
                outp.Add(c);
            }
            List<Conflict> stc = ConflictRadarStatic.Extras(all);
            for (int i = 0; i < stc.Count; i++) outp.Add(stc[i]);
            // 补充判红：多层启用值不一致、bundle 声明的 patch 文件不存在
            List<Conflict> extra = ConflictRadarHosts.Extras(all);
            for (int i = 0; i < extra.Count; i++) outp.Add(extra[i]);
            // ★ 装载集外的那批：只出**一条汇总提醒**，不出几百条噪声（明细在 --conflict-rows 的 scope= 里）
            if (diskOnly.Count > 0)
            {
                Conflict c = new Conflict();
                c.Kind = "装载集外"; c.Key = "diskonly-records"; c.Soft = true;
                c.Why = diskOnly.Count.ToString(CultureInfo.InvariantCulture) +
                        " 条占用记录来自「磁盘上有包、但没有任何一层登记加载」的清单 ⇒ 不参与判红；" +
                        "要看明细用 --conflict-rows 里 scope=diskonly 的行";
                for (int i = 0; i < diskOnly.Count && i < 5; i++) c.Occs.Add(diskOnly[i]);
                outp.Add(c);
            }
            return outp;
        }

        public static Dictionary<string, Row> Group(List<Occ> all)
        {
            Dictionary<string, Row> rows = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < all.Count; i++)
            {
                Occ o = all[i];
                string gk = o.Kind + "\n" + o.Key;
                Row row;
                if (!rows.TryGetValue(gk, out row))
                {
                    row = new Row(); row.Kind = o.Kind; row.Key = o.Key;
                    rows[gk] = row;
                }
                row.Occs.Add(o);
            }
            return rows;
        }

        // ---------------- 报告 ----------------
        public static string Render(List<Occ> all, List<Conflict> conf, ScanResult sc)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 大肥鱼救星 v" + DshCore.AppVersion + " · 插件打架体检（冲突雷达 · 只读） ==");
            sb.AppendLine("DSH home：" + DshCore.DshHome + (DshCore.HasExplicitScopeOverride() ? "（沙箱/显式覆盖模式）" : ""));
            sb.AppendLine("本次**不会写任何文件**；要动手请另加 --conflict --fix（本版未实现 fix）。");
            sb.AppendLine();

            Dictionary<string, Row> rows = Group(all);
            sb.AppendLine("---- 共享物占用表（" + rows.Count.ToString(CultureInfo.InvariantCulture) + " 件共享物 / " +
                          all.Count.ToString(CultureInfo.InvariantCulture) + " 条占用记录） ----");
            List<string> keys = new List<string>(rows.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < keys.Count; i++)
            {
                Row row = rows[keys[i]];
                if (row.Kind == "端口") continue;
                sb.AppendLine("· " + row.Kind + " | " + row.Key + " | 写者 " + row.DistinctWriters() + " 个：");
                for (int j = 0; j < row.Occs.Count; j++)
                {
                    Occ o = row.Occs[j];
                    string extra = o.Enabled.Length > 0 ? (" enabled=" + o.Enabled) : (o.Note.Length > 0 ? (" " + o.Note) : "");
                    sb.AppendLine("    - " + o.Writer + "  @ " + o.File + (o.Line > 0 ? (":" + o.Line.ToString(CultureInfo.InvariantCulture)) : "") + extra);
                }
            }
            sb.AppendLine();
            sb.AppendLine("---- 报红（" + conf.Count.ToString(CultureInfo.InvariantCulture) + " 条） ----");
            if (conf.Count == 0) sb.AppendLine("（没有多写者抢占，也没有悬空入口）");
            int hard = 0, soft = 0;
            for (int i = 0; i < conf.Count; i++) { if (conf[i].Soft) soft++; else hard++; }
            sb.AppendLine("（其中真红 " + hard + " 条、提醒/unknown " + soft + " 条；提醒不算冲突）");
            for (int i = 0; i < conf.Count; i++)
            {
                Conflict c = conf[i];
                sb.AppendLine((c.Soft ? "[提醒] " : "[红] ") + c.Kind + " | " + c.Key + "  ——  " + c.Why);
                for (int j = 0; j < c.Occs.Count; j++)
                    sb.AppendLine("      证据：" + c.Occs[j].Writer + " @ " + c.Occs[j].File +
                                  (c.Occs[j].Line > 0 ? (":" + c.Occs[j].Line.ToString(CultureInfo.InvariantCulture)) : ""));
            }
            sb.AppendLine();
            sb.AppendLine("---- 覆盖面（自报） ----");
            sb.AppendLine("扫了文件 " + sc.FilesScanned.Count + " 个；缺失/未读 " + sc.FilesMissing.Count + " 个；本地插件目录 " +
                          sc.PluginDirs + " 个；bundle " + sc.Bundles + " 个");
            Dictionary<string, int> perKind = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < all.Count; i++)
            {
                int v; perKind.TryGetValue(all[i].Kind, out v); perKind[all[i].Kind] = v + 1;
            }
            List<string> kk = new List<string>(perKind.Keys); kk.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < kk.Count; i++) sb.AppendLine("  " + kk[i] + " = " + perKind[kk[i]]);
            sb.AppendLine("未扫到的共享物类别（**别当成「没冲突」**）：");
            for (int i = 0; i < sc.NotScanned.Count; i++) sb.AppendLine("  · " + sc.NotScanned[i]);
            sb.AppendLine();
            // ★ 装载集：判红只吃 loaded；diskonly 那部分是"磁盘上有包但根本没登记加载"的清单，
            //   留着当覆盖面，绝不参与判红（第六类假红的形状）。
            int dOnly = 0, lOnly = 0;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Scope == "diskonly") dOnly++;
                else if (all[i].Scope == "loaded") lOnly++;
            }
            sb.AppendLine("装载集闭包：" + sc.LoadedNames.Count + " 个名字（种子=前三层的 bundle 与 insert name:，" +
                          "顺着每个已加载包自带的 bundle patch 迭代到不动点）；" +
                          "占用记录里 在装载集=" + lOnly + " 条、仅磁盘存在=" + dOnly +
                          " 条（后者不参与判红）");
            sb.AppendLine();
            sb.AppendLine("CONFLICT_COVERAGE files=" + sc.FilesScanned.Count.ToString(CultureInfo.InvariantCulture) +
                          " missing=" + sc.FilesMissing.Count.ToString(CultureInfo.InvariantCulture) +
                          " records=" + all.Count.ToString(CultureInfo.InvariantCulture) +
                          " objects=" + rows.Count.ToString(CultureInfo.InvariantCulture) +
                          " conflicts=" + CountHard(conf).ToString(CultureInfo.InvariantCulture) + " soft=" + CountSoft(conf).ToString(CultureInfo.InvariantCulture) +
                          " plugin_dirs=" + sc.PluginDirs.ToString(CultureInfo.InvariantCulture) +
                          " bundles=" + sc.Bundles.ToString(CultureInfo.InvariantCulture) +
                          " manifests=" + ConflictRadarHosts.ManifestsScanned.ToString(CultureInfo.InvariantCulture) +
                          " bundle_patches=" + ConflictRadarHosts.BundlePatchesRead.ToString(CultureInfo.InvariantCulture) +
                          " injections=" + ConflictRadarHosts.InjectionsSeen.ToString(CultureInfo.InvariantCulture) +
                          " mem_files=" + sc.MemoryFiles.ToString(CultureInfo.InvariantCulture) +
                          " mem_literals=" + sc.MemoryLiteralHits.ToString(CultureInfo.InvariantCulture) +
                          " closure=" + sc.LoadedNames.Count.ToString(CultureInfo.InvariantCulture) +
                          " occ_diskonly=" + dOnly.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // ---------------- 机器可读行（WP3 抽样 / WP5 组合表 / WP6 回归都吃这个） ----------------
        /// <summary>
        /// 全部输出**纯 ASCII、空格分隔的 token**：中文写者名转成 ~XXXX~（可逆），路径/键里的
        /// 空格转 %20。为什么不直接把给人看的中文报告喂给实验脚本：那是会变的（今晚
        /// 「锚定 files=0 子串」把自己绊倒过一次，同一类错误）。
        /// 每行前缀：CR_WRITER / CR_FILE / CR_OCC / CR_ROW / CR_RED / CR_SUMMARY。
        /// </summary>
        public static string MachineRows(List<Occ> all, List<Conflict> conf, ScanResult sc)
        {
            StringBuilder sb = new StringBuilder();
            Dictionary<string, int> widx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> fidx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<string> wlist = new List<string>();
            List<string> flist = new List<string>();
            for (int i = 0; i < all.Count; i++)
            {
                string w = all[i].Writer ?? ""; if (w.Length == 0) w = "unknown";
                if (!widx.ContainsKey(w)) { widx[w] = wlist.Count + 1; wlist.Add(w); }
                string f = all[i].File ?? "";
                if (f.Length > 0 && !fidx.ContainsKey(f)) { fidx[f] = flist.Count + 1; flist.Add(f); }
            }
            for (int i = 0; i < wlist.Count; i++) sb.AppendLine("CR_WRITER " + I(i + 1) + " " + Tok(wlist[i]));
            for (int i = 0; i < flist.Count; i++) sb.AppendLine("CR_FILE " + I(i + 1) + " " + Tok(flist[i]));
            for (int i = 0; i < all.Count; i++)
            {
                Occ o = all[i];
                string w = (o.Writer ?? "").Length > 0 ? o.Writer : "unknown";
                int wi = 0; widx.TryGetValue(w, out wi);
                int fi = 0; if (!string.IsNullOrEmpty(o.File)) fidx.TryGetValue(o.File, out fi);
                string en = (o.Enabled ?? "").Length > 0 ? o.Enabled : "-";
                string note = (o.Note ?? "").Length > 0 ? o.Note : "-";
                sb.AppendLine("CR_OCC " + I(i + 1) + " kind=" + KindCode(o.Kind) + " key=" + Tok(o.Key) +
                              " w=" + I(wi) + " f=" + I(fi) + " line=" + I(o.Line) +
                              " en=" + Tok(en) + " note=" + Tok(note) +
                              " scope=" + Tok(string.IsNullOrEmpty(o.Scope) ? "n_a" : o.Scope) +
                              " base=" + Tok(string.IsNullOrEmpty(o.Base) ? "-" : o.Base));
            }
            Dictionary<string, Row> rows = Group(all);
            List<string> rk = new List<string>(rows.Keys); rk.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rk.Count; i++)
            {
                Row r = rows[rk[i]];
                StringBuilder osb = new StringBuilder();
                for (int j = 0; j < all.Count; j++)
                {
                    if (!string.Equals(all[j].Kind, r.Kind, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(all[j].Key, r.Key, StringComparison.OrdinalIgnoreCase)) continue;
                    if (osb.Length > 0) osb.Append(",");
                    osb.Append(I(j + 1));
                }
                sb.AppendLine("CR_ROW " + I(i + 1) + " kind=" + KindCode(r.Kind) + " key=" + Tok(r.Key) +
                              " writers=" + I(r.DistinctWriters()) + " self=" + I(r.SelfRegistrations()) +
                              " occs=" + osb.ToString());
            }
            for (int i = 0; i < conf.Count; i++)
            {
                Conflict c = conf[i];
                StringBuilder osb = new StringBuilder();
                for (int j = 0; j < c.Occs.Count; j++)
                {
                    int oi = IndexOfOcc(all, c.Occs[j]);
                    if (oi < 0) continue;
                    if (osb.Length > 0) osb.Append(",");
                    osb.Append(I(oi));
                }
                sb.AppendLine("CR_RED " + I(i + 1) + " kind=" + KindCode(c.Kind) + " key=" + Tok(c.Key) +
                              " soft=" + I(c.Soft ? 1 : 0) + " why=" + Tok(c.Why) + " occs=" + osb.ToString());
            }
            int nLoaded = 0, nDiskOnly = 0, nNa = 0;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Scope == "loaded") nLoaded++;
                else if (all[i].Scope == "diskonly") nDiskOnly++;
                else nNa++;
            }
            int closureN = (sc == null || sc.LoadedNames == null) ? 0 : sc.LoadedNames.Count;
            // 闭包本身也要可核对：逐名吐出来，否则"closure=168"只是一个我说的数字
            if (sc != null && sc.LoadedNames != null)
                for (int i = 0; i < sc.LoadedNames.Count; i++)
                    sb.AppendLine("CR_LOADED " + I(i + 1) + " " + Tok(sc.LoadedNames[i]));
            sb.AppendLine("CR_SUMMARY records=" + I(all.Count) + " rows=" + I(rows.Count) +
                          " hard=" + I(CountHard(conf)) + " soft=" + I(CountSoft(conf)) +
                          " writers=" + I(wlist.Count) + " files=" + I(flist.Count) +
                          " closure=" + I(closureN) + " occ_loaded=" + I(nLoaded) +
                          " occ_diskonly=" + I(nDiskOnly) + " occ_na=" + I(nNa));
            return sb.ToString();
        }

        static int IndexOfOcc(List<Occ> all, Occ o)
        {
            for (int i = 0; i < all.Count; i++) { if (all[i] == o) return i + 1; }
            return -1;
        }
        static string I(int v) { return v.ToString(CultureInfo.InvariantCulture); }

        /// <summary>共享物类别 → 稳定的 ASCII 码。加新类别时在这里补一行，别改中文显示名。</summary>
        public static string KindCode(string k)
        {
            if (k == null) return "other";
            if (k == "加载器条目") return "loader";
            if (k == "启用开关") return "enable";
            if (k == "入口路径") return "entry";
            if (k == "配置键") return "cfgkey";
            if (k == "bundle") return "bundle";
            if (k == "本地插件目录") return "plugdir";
            if (k == "端口") return "port";
            if (k == "条目存废") return "toggle";
            if (k == "bundle patch 文件") return "patchfile";
            if (k == "宿主注入声明") return "inject";
            if (k == "装载集外") return "offtree";
            if (k == "入口重复登记") return "dupentry";
            return "other";
        }

        /// <summary>把值压成不含空格/等号/逗号的 ASCII token（可逆：%XX 十六进制，中文走 ~XXXX~）。</summary>
        static string Tok(string s)
        {
            if (string.IsNullOrEmpty(s)) return "-";
            StringBuilder o = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == ' ') { o.Append("%20"); }
                else if (ch == '=') { o.Append("%3D"); }
                else if (ch == ',') { o.Append("%2C"); }
                else if (ch == '\r') { o.Append("%0D"); }
                else if (ch == '\n') { o.Append("%0A"); }
                else if (ch >= '!' && ch <= '~') { o.Append(ch); }
                else { o.Append("~").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture)).Append("~"); }
            }
            string r = o.ToString();
            return r.Length > 0 ? r : "-";
        }

        // ---------------- 判据自检（WP1：能报红 + 有阴性对照 + 自报覆盖面） ----------------
        /// <summary>
        /// 全部在**临时目录**里造样本，绝不碰真实 profile：
        /// 阳性=该报红的必须报红；阴性=干净样本必须 0 红（否则这条判据是"永远红"的空转尺子）。
        /// </summary>
        public static string SelfTest()
        {
            int pass = 0, fail = 0;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 大肥鱼救星 v" + DshCore.AppVersion + " · 冲突雷达判据自检（临时目录） ==");
            string dir = Path.Combine(Path.GetTempPath(), "bff_conflict_selftest_" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            try { Directory.CreateDirectory(dir); } catch { }

            // S1 阳性：同一 id 被两个写者主张
            string s1 = WriteYaml(dir, "dup_two_writers.yml",
                "# --- alpha managed ---\n- id: shared-one\n  disabled: false\n" +
                "# --- beta managed ---\n- id: shared-one\n  disabled: true\n");
            // S2 阴性：同一 id 只有一个写者
            string s2 = WriteYaml(dir, "single_writer.yml",
                "# --- alpha managed ---\n- id: shared-one\n  disabled: false\n");
            // S3 阳性：同一个文件里登记两次
            string s3 = WriteYaml(dir, "dup_same_file.yml",
                "# --- alpha managed ---\n- id: shared-one\n- id: shared-one\n");
            // S4 阳性：入口文件不存在（悬空）
            string s4 = WriteYaml(dir, "dangling.yml",
                "# --- alpha managed ---\n- insert:\n    - id: nope\n      name: './plugins/nope/lib/index.js'\n");
            // S5 阴性：干净清单（入口文件真的存在）
            string realEntry = Path.Combine(dir, "exists_entry.js");
            try { File.WriteAllText(realEntry, "//\n"); } catch { }
            string s5 = WriteYaml(dir, "clean.yml",
                "# --- alpha managed ---\n- insert:\n    - id: ok-one\n      name: '" +
                realEntry.Replace('\\', '/') + "'\n");

            Check(sb, ref pass, ref fail, "S1 阳性：同 id 两个写者 ⇒ 必须报红",
                HasConflict(s1, "加载器条目", "shared-one", "2 个写者"));
            Check(sb, ref pass, ref fail, "S2 阴性：同 id 单写者 ⇒ 不该报红",
                !HasConflict(s2, "加载器条目", "shared-one", "个写者"));
            Check(sb, ref pass, ref fail, "S3 阳性：同文件重复登记 ⇒ 必须报红",
                HasConflict(s3, "加载器条目", "shared-one", "duplicate"));
            Check(sb, ref pass, ref fail, "S4 阳性：入口文件缺失 ⇒ 必须报红",
                HasConflict(s4, "入口路径", "nope", "不存在"));
            Check(sb, ref pass, ref fail, "S5 阴性：干净清单 ⇒ 0 红",
                CountConflicts(s5) == 0);
            Check(sb, ref pass, ref fail, "S6 覆盖面：扫描必须自报文件数与记录数（不能是空值）",
                CoverageNonEmpty(s5));

            // S8 覆盖面：装载集闭包必须在真 profile 上非空，且**不能凭空多出不存在的名字**
            //   （闭包算错 ⇒ 要么把没加载的包当有加载去判红，要么把在跑的当没跑而漏判）
            ScanResult live = Scan();
            int seedHit = 0;
            for (int i = 0; i < live.All.Count; i++)
            {
                if (live.All[i].Kind != "bundle") continue;
                if (IsLoaded(live, live.All[i].Key)) seedHit++;
            }
            int diskOnly = 0;
            for (int i = 0; i < live.All.Count; i++) if (live.All[i].Scope == "diskonly") diskOnly++;
            Check(sb, ref pass, ref fail, "S8 覆盖面：装载集闭包非空、种子全部自洽（本机 闭包=" +
                  live.LoadedNames.Count + " 种子命中=" + seedHit + " 仅磁盘记录=" + diskOnly + "）",
                live.LoadedNames.Count >= 20 && seedHit >= BundlesSeeded(live) && BundlesSeeded(live) > 0);
            Check(sb, ref pass, ref fail, "S8n 阴性：凭空名字不许进装载集（判据不能什么都说在跑）",
                !IsLoaded(live, "@no-such-scope/bff-not-real"));

            // S13 阳性：同一个入口文件被两个写者在**覆盖层**各登记一次 ⇒ 必须报硬红。
            //   这条判据的来源是 WP3 实测（exp=dup 不同 id 3.5s 崩、exp=dupid 同 id 0.7s 崩），
            //   不是"看起来像重复"；没有这两个实验，本来根本不会有这条判据。
            List<Occ> dupE = new List<Occ>();
            Occ e1 = new Occ();
            e1.Kind = "入口路径"; e1.Key = "bff-a ⇒ ./plugins/bff-x/lib/index.js";
            e1.Writer = "bffwriterA"; e1.File = "profile:cordis.patch.yml"; e1.Line = 3;
            Occ e2 = new Occ();
            e2.Kind = "入口路径"; e2.Key = "bff-b ⇒ ./plugins/bff-x/lib/index.js";
            e2.Writer = "bffwriterB"; e2.File = "profile:cordis.patch.yml"; e2.Line = 9;
            dupE.Add(e1); dupE.Add(e2);
            Check(sb, ref pass, ref fail, "S13 阳性：同一入口文件两个写者（覆盖层）⇒ 必须报硬红",
                HasHard(Judge(dupE), "入口重复登记", "bff-x"));
            // S13b 阳性：同 id 同 name 两个写者也必须报（我一度猜"同 id 会被去重"，实测 0.7 秒崩）
            Occ e3 = new Occ();
            e3.Kind = "入口路径"; e3.Key = "bff-a ⇒ ./plugins/bff-x/lib/index.js";
            e3.Writer = "bffwriterB"; e3.File = "profile:cordis.patch.yml"; e3.Line = 12;
            dupE.Add(e3);
            Check(sb, ref pass, ref fail, "S13b 阳性：同 id 同入口两个写者 ⇒ 同样必须报（不许假设被去重）",
                HasHard(Judge(dupE), "入口重复登记", "bff-x"));
            // S13n 阴性：只有一个写者登记 ⇒ 不该出这一类红
            List<Occ> oneE = new List<Occ>();
            oneE.Add(e1);
            Check(sb, ref pass, ref fail, "S13n 阴性：单写者登记同一入口 ⇒ 不该报重复登记",
                !HasHard(Judge(oneE), "入口重复登记", "bff-x"));
            // S13u 保守：两条都来自各自 bundle 自带 patch ⇒ 静态判不了谁生效 ⇒ 只能提醒
            List<Occ> bu = new List<Occ>();
            Occ b1 = new Occ();
            b1.Kind = "入口路径"; b1.Key = "bff-a ⇒ ./plugins/bff-y/lib/index.js";
            b1.Writer = "pkg-one"; b1.File = "bundle:pkg-one"; b1.Line = 2;
            Occ b2 = new Occ();
            b2.Kind = "入口路径"; b2.Key = "bff-b ⇒ ./plugins/bff-y/lib/index.js";
            b2.Writer = "pkg-two"; b2.File = "bundle:pkg-two"; b2.Line = 2;
            bu.Add(b1); bu.Add(b2);
            Check(sb, ref pass, ref fail, "S13u 保守：两条都来自各自 bundle patch ⇒ 只提醒不判红",
                !HasHard(Judge(bu), "入口重复登记", "bff-y") && HasAny(Judge(bu), "入口重复登记", "bff-y"));

            // S14 自登记 ≠ 被装载（本机实测：闭包被这一条从 ~40 吹到 168，diskonly 记录被洗白）
            ScanResult t1 = new ScanResult();
            Occ self1 = new Occ();
            self1.Kind = "入口路径"; self1.Key = "bff-a ⇒ @bff/pkg-self";
            self1.Writer = "@bff/pkg-self"; self1.File = "bundle:@bff/pkg-self"; self1.Line = 2;
            t1.All.Add(self1);
            BuildLoadedSet(t1);
            Check(sb, ref pass, ref fail, "S14 阴性：只有自带 patch 里的自登记 ⇒ 不许进装载集",
                !IsLoaded(t1, "@bff/pkg-self"));
            ScanResult t2 = new ScanResult();
            Occ seed1 = new Occ();
            seed1.Kind = "bundle"; seed1.Key = "@bff/pkg-seed"; seed1.Writer = "profile";
            seed1.File = "profile:package.json";
            t2.All.Add(seed1);
            BuildLoadedSet(t2);
            Check(sb, ref pass, ref fail, "S14p 阳性：出现在 package.json bundle 名单里 ⇒ 必须在装载集",
                IsLoaded(t2, "@bff/pkg-seed"));

            sb.AppendLine("临时样本目录：" + dir + "（真实 profile 未被读写）");
            sb.AppendLine("结论：通过 " + pass.ToString(CultureInfo.InvariantCulture) + " 项 / 失败 " +
                          fail.ToString(CultureInfo.InvariantCulture) + " 项");
            return sb.ToString();
        }

        static string WriteYaml(string dir, string name, string body)
        {
            string p = Path.Combine(dir, name);
            try { File.WriteAllText(p, body, new UTF8Encoding(false)); } catch { }
            return p;
        }
        static void Check(StringBuilder sb, ref int pass, ref int fail, string name, bool ok)
        {
            if (ok) { pass++; sb.AppendLine("[OK]   " + name); }
            else { fail++; sb.AppendLine("[FAIL] " + name); }
        }
        static bool HasConflict(string path, string kind, string keyPart, string whyPart)
        {
            ScanResult r = new ScanResult();
            ReadManifest(path, Path.GetFileName(path), r);
            List<Conflict> cs = Judge(r.All);
            for (int i = 0; i < cs.Count; i++)
            {
                if (!string.Equals(cs[i].Kind, kind, StringComparison.Ordinal)) continue;
                if ((cs[i].Key ?? "").IndexOf(keyPart, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if ((cs[i].Why ?? "").IndexOf(whyPart, StringComparison.OrdinalIgnoreCase) < 0) continue;
                return true;
            }
            return false;
        }
        static int CountConflicts(string path)
        {
            ScanResult r = new ScanResult();
            ReadManifest(path, Path.GetFileName(path), r);
            List<Conflict> cs = Judge(r.All);
            return cs.Count;
        }
        static bool CoverageNonEmpty(string path)
        {
            ScanResult r = new ScanResult();
            ReadManifest(path, Path.GetFileName(path), r);
            if (r.FilesScanned.Count < 1) return false;
            int entries = 0;
            for (int i = 0; i < r.All.Count; i++) if (r.All[i].Kind == "加载器条目") entries++;
            if (entries < 1) return false;
            string txt = Render(r.All, Judge(r.All), r);
            int fi = txt.IndexOf("CONFLICT_COVERAGE files=", StringComparison.Ordinal);
            if (fi < 0) return false;
            string tail = txt.Substring(fi);
            // ★ 匹配必须带前导空格锚定：字段名改出 memory_files= 之后，
            //   裸 "files=0" 会命中 "mem_files=0" 的子串 ⇒ 老断言被自己的新字段触发（实测踩过）。
            return tail.IndexOf(" files=0", StringComparison.Ordinal) < 0 &&
                   tail.IndexOf(" records=0", StringComparison.Ordinal) < 0;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BigFatFishRescuer
{
    /// <summary>
    /// WP1 补充扫描：**第三层覆盖清单**与**宿主注入点**（只读）。
    ///
    /// 为什么单独一个类：ConflictRadar.cs 已经被一次外部回滚并成过 844 行、
    /// 十几个成员重复的版本；把新增能力放独立类，结构上就不可能产生重复定义，
    /// 主文件只需要两行钩子。
    ///
    /// 补的是实测出来的两个真漏（本机 2026-09-20 查点时发现的）：
    ///  1) 13 个 bundle 里有 9 个声明了 `dsh.bundle.patch: ./cordis.patch.yml` ——
    ///     同一批条目 id 在**第三层**被反复写（皮肤互斥行、`- insert:` 登记行都在那儿），
    ///     主雷达原先只读 home/profile 两层 ⇒ 占用表残缺，"没冲突"是假的。
    ///  2) `dsh.client.inject: [...]` 是插件对**宿主模块**的静态注入声明。两个插件注入
    ///     同一个宿主模块 ⇒ 该模块被改写多次（§8 L4 的叠加式/覆盖式冲突，静态可判的那一半）。
    ///
    /// 服务名（provider/consumer）仍然扫不了：本机 15 个插件清单里**没有** cordis 声明字段，
    /// 服务是运行时在代码里注册的 ⇒ 只能靠日志里的 pending (waiting for service …) 与 WP3 对照。
    /// 这条如实留在未扫清单里，不含糊过去。
    /// </summary>
    public static class ConflictRadarHosts
    {
        public static int ManifestsScanned = 0;
        public static int BundlePatchesRead = 0;
        public static int InjectionsSeen = 0;

        public static void Reset()
        {
            ManifestsScanned = 0;
            BundlePatchesRead = 0;
            InjectionsSeen = 0;
        }

        // ---------------- 扫描 ----------------
        /// <summary>扫插件清单，补「客户端注入点」占用与第三层 bundle patch 的占用记录。</summary>
        public static void Scan(ConflictRadar.ScanResult r)
        {
            List<string> bases = new List<string>();
            string profileDir = Path.Combine(DshCore.DshHome, "profiles", "web");
            bases.Add(Path.Combine(profileDir, "node_modules"));
            bases.Add(Path.Combine(profileDir, "plugins"));
            bases.Add(Path.Combine(DshCore.DshHome, "profiles", "node_modules"));

            for (int b = 0; b < bases.Count; b++)
            {
                string baseDir = bases[b];
                if (!Directory.Exists(baseDir)) { r.FilesMissing.Add(baseDir + " (不存在)"); continue; }
                string[] entries;
                try { entries = Directory.GetDirectories(baseDir); } catch { continue; }
                for (int i = 0; i < entries.Length; i++)
                {
                    string leaf = Path.GetFileName(entries[i]);
                    if (leaf.StartsWith("_") || leaf.IndexOf(".bak", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (leaf.StartsWith("@"))
                    {
                        string[] subs;
                        try { subs = Directory.GetDirectories(entries[i]); } catch { subs = new string[0]; }
                        for (int s = 0; s < subs.Length; s++) FromManifest(subs[s], r);
                        continue;
                    }
                    FromManifest(entries[i], r);
                }
            }
        }

        static void FromManifest(string dir, ConflictRadar.ScanResult r)
        {
            string pkg = Path.Combine(dir, "package.json");
            if (!File.Exists(pkg)) return;
            string text;
            try { text = File.ReadAllText(pkg); } catch { return; }
            ManifestsScanned++;
            r.FilesScanned.Add(pkg);

            string name = JsonString(text, "name");
            if (name.Length == 0) name = Path.GetFileName(dir);
            string dsh = JsonObj(text, "dsh");
            if (dsh.Length == 0) return;

            // ① 宿主注入点
            string client = JsonObj(dsh, "client");
            if (client.Length > 0)
            {
                string[] inj = JsonArray(client, "inject");
                for (int i = 0; i < inj.Length; i++)
                {
                    if (inj[i].Length == 0) continue;
                    ConflictRadar.Occ o = new ConflictRadar.Occ();
                    o.Kind = "宿主注入声明";
                    o.Key = inj[i];
                    o.Writer = name;
                    // 证据可点开：给 package.json 的真实路径（合成标签 "manifest:<名>" 人拿不到位置）
                    o.File = pkg;
                    o.Line = 1;
                    // 目标模块在不在 node_modules 里看得见？看不见才是真问题（L3 那类）
                    o.Note = InjectTargetVisible(inj[i]) ? "目标可见" : "★目标找不到";
                    r.All.Add(o);
                    InjectionsSeen++;
                }
            }

            // ② 第三层：bundle 自带的 patch 覆盖清单
            string bundle = JsonObj(dsh, "bundle");
            string patchRel = bundle.Length > 0 ? JsonString(bundle, "patch") : "";
            if (patchRel.Length == 0) return;
            string patchPath = patchRel;
            try { if (!Path.IsPathRooted(patchPath)) patchPath = Path.Combine(dir, patchPath); } catch { return; }
            if (!File.Exists(patchPath))
            {
                ConflictRadar.Occ miss = new ConflictRadar.Occ();
                miss.Kind = "bundle patch 文件";
                miss.Key = name;
                miss.Writer = name;
                // 证据要能点开：原来写 "manifest:<包名>" 这种合成标签，人拿到以后找不到文件在哪。
                miss.File = pkg;
                miss.Line = 1;
                miss.Note = "声明了 patch 但文件不存在 ⇒ cannot resolve profile bundle";
                r.All.Add(miss);
                return;
            }
            int before = r.All.Count;
            r.FilesScanned.Add(patchPath);
            // tag 里放**绝对路径**（仍带 bundle: 前缀，别的判据按前缀认它）：
            // 「入口重复登记」这类红要给出可编辑的文件与行号，否则处置等于空话。
            ConflictRadar.ReadManifest(patchPath, "bundle:" + patchPath, r);
            // 这层里常没有 `# --- 写者 ---` 注释 ⇒ 归属会落到 unknown；用插件名兜底，
            // 否则「谁在第三层写了这个 id」这条证据就白拿了。
            for (int k = before; k < r.All.Count; k++)
            {
                if (r.All[k].Writer == null || r.All[k].Writer.Length == 0 || r.All[k].Writer == "unknown")
                    r.All[k].Writer = name;
            }
            // 这一层里的相对入口路径要按**插件包目录**解析
            for (int k = before; k < r.All.Count; k++) r.All[k].Base = dir;
            BundlePatchesRead++;
        }

        // ---------------- 判红补充 ----------------
        /// <summary>
        /// 同一条目的**启用取值在多层之间不一致** ⇒ 报红。
        /// 这条与写者数无关：写者是同一个人（皮肤管理器自己写 home+profile+bundle 三层）
        /// 也照样会打 —— 谁生效取决于叠加顺序，这正是「皮肤互斥」反复回来的形状。
        /// </summary>
        public static List<ConflictRadar.Conflict> Extras(List<ConflictRadar.Occ> all)
        {
            List<ConflictRadar.Conflict> outp = new List<ConflictRadar.Conflict>();
            Dictionary<string, List<ConflictRadar.Occ>> byKey = new Dictionary<string, List<ConflictRadar.Occ>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Kind != "启用开关") continue;
                string v = (all[i].Enabled ?? "").Trim().ToLowerInvariant();
                if (v != "true" && v != "false") continue;
                List<ConflictRadar.Occ> l;
                if (!byKey.TryGetValue(all[i].Key, out l)) { l = new List<ConflictRadar.Occ>(); byKey[all[i].Key] = l; }
                l.Add(all[i]);
            }
            List<string> keys = new List<string>(byKey.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            for (int k = 0; k < keys.Count; k++)
            {
                List<ConflictRadar.Occ> l = byKey[keys[k]];
                bool hasTrue = false, hasFalse = false;
                for (int i = 0; i < l.Count; i++)
                {
                    if (l[i].Enabled.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) hasTrue = true;
                    else hasFalse = true;
                }
                if (hasTrue && hasFalse)
                {
                    ConflictRadar.Conflict c = new ConflictRadar.Conflict();
                    c.Kind = "启用开关";
                    c.Key = keys[k];
                    c.Why = "同一条目的启用取值在多层之间不一致（true 与 false 同时存在）⇒ 谁生效取决于叠加顺序";
                    c.Occs = l;
                    outp.Add(c);
                }
            }
            // 宿主注入声明的目标找不到 ⇒ 真红（插件会 import 不到宿主模块，L3 形态）。
            // 注意：**不做「几个插件注入同一模块」的判红** —— inject 是「我要和它一起加载」的
            // 依赖声明，同一宿主模块被十几个插件注入是 cordis 的正常设计，判红就是满屏噪声。
            // ★ 2026-09-20 L1 实测后**改判**：这一类原来判硬红（"会 import 失败"），但真内核
            //   冷启动跑下来 tree_fail=0 / unresolved=0 / HTTP 正常 ⇒ 后果没有兑现，
            //   「会 import 失败」是我替 cordis 编的机制，不是量出来的。
            //   静态事实（目标包在两个 node_modules 根、嵌套 node_modules、内核安装树里都不存在）
            //   是真的，所以保留为提醒；但按任务书 §2.5「判不出来就标 unknown 并保守不动」⇒ 不判红。
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Kind != "宿主注入声明") continue;
                if ((all[i].Note ?? "").IndexOf("目标找不到", StringComparison.Ordinal) < 0) continue;
                ConflictRadar.Conflict c = new ConflictRadar.Conflict();
                c.Kind = "宿主注入声明"; c.Key = all[i].Key;
                bool pkgThere = ConflictRadar.PackageDirExists(all[i].Key);
                c.Soft = true;   // ★ 两种情形都只作提醒：见上面的改判理由
                c.Why = pkgThere
                    ? "宿主模块包在、子路径静态解析不到 ⇒ unknown，不判红"
                    : "目标宿主模块在本机任何一棵 node_modules（含嵌套）与内核安装树里都不存在（静态成立）；" +
                      "但 L1 真内核冷启动未见故障 ⇒ 后果 unknown，不判红（原来判硬红的理由已被实测推翻）";
                c.Occs.Add(all[i]);
                outp.Add(c);
            }

            // bundle 声明了 patch 但文件不存在 ⇒ 悬空（Kind 在扫描时就标好了）
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Kind != "bundle patch 文件") continue;
                ConflictRadar.Conflict c = new ConflictRadar.Conflict();
                c.Kind = all[i].Kind; c.Key = all[i].Key; c.Why = all[i].Note;
                c.Occs.Add(all[i]);
                outp.Add(c);
            }
            return outp;
        }

        /// <summary>给装载集闭包用：从一份 package.json 文本里取 `dsh.bundle.patch` 的相对路径。</summary>
        public static string BundlePatchOf(string pkgJsonText)
        {
            if (string.IsNullOrEmpty(pkgJsonText)) return "";
            string dsh = JsonObj(pkgJsonText, "dsh");
            if (dsh.Length == 0) return "";
            string bundle = JsonObj(dsh, "bundle");
            if (bundle.Length == 0) return "";
            return JsonString(bundle, "patch");
        }

        /// <summary>注入声明的宿主模块能否在 profile 的 node_modules（含 scoped）里找到。</summary>
        static bool InjectTargetVisible(string target)
        {
            if (string.IsNullOrEmpty(target)) return true;   // 空的当无声明，不报错
            string[] roots = new string[] {
                Path.Combine(DshCore.DshHome, "profiles", "web", "node_modules"),
                Path.Combine(DshCore.DshHome, "profiles", "node_modules") };
            for (int i = 0; i < roots.Length; i++)
            {
                try { if (Directory.Exists(Path.Combine(roots[i], target))) return true; } catch { }
            }
            return false;
        }

        // ---------------- 极简 JSON 取值（不引库；取不到就返回空，绝不猜） ----------------
        static string JsonObj(string text, string key)
        {
            if (text == null) return "";
            string pat = "\"" + key + "\"";
            int k = text.IndexOf(pat, StringComparison.Ordinal);
            if (k < 0) return "";
            int colon = text.IndexOf(':', k + pat.Length);
            if (colon < 0) return "";
            int i = colon + 1;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t' || text[i] == '\r' || text[i] == '\n')) i++;
            if (i >= text.Length || text[i] != '{') return "";
            int depth = 0; bool inStr = false;
            for (int j = i; j < text.Length; j++)
            {
                char c = text[j];
                if (c == '"' && (j == i || text[j - 1] != '\\')) inStr = !inStr;
                if (inStr) continue;
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return text.Substring(i, j - i + 1); }
            }
            return "";
        }
        static string JsonString(string text, string key)
        {
            if (text == null) return "";
            string pat = "\"" + key + "\"";
            int k = text.IndexOf(pat, StringComparison.Ordinal);
            if (k < 0) return "";
            int colon = text.IndexOf(':', k + pat.Length);
            if (colon < 0) return "";
            int q = text.IndexOf('"', colon + 1);
            if (q < 0) return "";
            int e = q + 1;
            while (e < text.Length && !(text[e] == '"' && text[e - 1] != '\\')) e++;
            if (e >= text.Length) return "";
            return text.Substring(q + 1, e - q - 1);
        }
        static string[] JsonArray(string text, string key)
        {
            if (text == null) return new string[0];
            string pat = "\"" + key + "\"";
            int k = text.IndexOf(pat, StringComparison.Ordinal);
            if (k < 0) return new string[0];
            int lb = text.IndexOf('[', k + pat.Length);
            if (lb < 0) return new string[0];
            int rb = text.IndexOf(']', lb);
            if (rb < 0) return new string[0];
            string inner = text.Substring(lb + 1, rb - lb - 1);
            List<string> outp = new List<string>();
            int i = 0;
            while (i < inner.Length)
            {
                int q = inner.IndexOf('"', i);
                if (q < 0) break;
                int e = q + 1;
                while (e < inner.Length && !(inner[e] == '"' && inner[e - 1] != '\\')) e++;
                if (e >= inner.Length) break;
                outp.Add(inner.Substring(q + 1, e - q - 1));
                i = e + 1;
            }
            return outp.ToArray();
        }

        // ---------------- 判据自检（阳性 + 阴性 + 覆盖面，且防"静默漏扫"） ----------------
        public static string SelfTest2()
        {
            int pass = 0, fail = 0;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("---- 补充扫描的判据自检（注入点 / 第三层 patch / 启用不一致） ----");

            // S7 ★2026-09-20 改判：这一类**必须只作提醒（soft）**，不许判硬红。
            //   原来这里写的是"目标找不到 ⇒ 必须报红"，理由是"插件会 import 失败"；
            //   L1 真内核冷启动实测把那个理由推翻了（tree_fail=0、unresolved=0、HTTP 正常），
            //   所以判据换成：既不能漏报（必须出现在表里、必须标 soft），也不能升成硬红。
            List<ConflictRadar.Occ> missInj = new List<ConflictRadar.Occ>();
            ConflictRadar.Occ m1 = Make("宿主注入声明", "@deepseek-ai/dsh-client-nope", "some-plugin");
            m1.Note = "★目标找不到";
            missInj.Add(m1);
            ConflictRadar.Conflict s7 = Find(Extras(missInj), "宿主注入声明", "dsh-client-nope");
            Check(sb, ref pass, ref fail, "S7 覆盖面：注入目标找不到 ⇒ 必须出现在表里且只作提醒（不升硬红）",
                s7 != null && s7.Soft);
            // S7n 阴性：同一宿主模块被两个插件注入是**正常设计** ⇒ 不该报红
            List<ConflictRadar.Occ> fan = new List<ConflictRadar.Occ>();
            ConflictRadar.Occ f1 = Make("宿主注入声明", "@deepseek-ai/dsh-client-connection", "alpha");
            f1.Note = "目标可见";
            ConflictRadar.Occ f2 = Make("宿主注入声明", "@deepseek-ai/dsh-client-connection", "beta");
            f2.Note = "目标可见";
            fan.Add(f1); fan.Add(f2);
            Check(sb, ref pass, ref fail, "S7n 阴性：两个插件注入同一宿主模块（正常设计）⇒ 不该报红",
                Extras(fan).Count == 0);
            // S7m 阴性：目标可见的注入声明，主雷达也不该按「多写者」判红
            Check(sb, ref pass, ref fail, "S7m 阴性：注入声明不进多写者判据 ⇒ 不该报红",
                ConflictRadar.Judge(fan).Count == 0);
            // S12 阳性对照的对照：bundle patch 里插件登记自己 ⇒ 不算竞争写者
            List<ConflictRadar.Occ> self = new List<ConflictRadar.Occ>();
            ConflictRadar.Occ s1 = Make("加载器条目", "ui-skin-x", "dsh-skin");
            s1.File = "home:cordis.patch.yml"; s1.Note = "override";
            ConflictRadar.Occ s2 = Make("加载器条目", "ui-skin-x", "dsh-skin");
            s2.File = "profile:cordis.patch.yml"; s2.Note = "override";
            ConflictRadar.Occ s3 = Make("加载器条目", "ui-skin-x", "@ext/dsh-client-ui-skin-x");
            s3.File = "bundle:@ext/dsh-client-ui-skin-x"; s3.Note = "insert";
            self.Add(s1); self.Add(s2); self.Add(s3);
            Check(sb, ref pass, ref fail,
                "S12 阴性：两层同一写者 + 第三层自身登记 ⇒ 不该判成抢占",
                ConflictRadar.Judge(self).Count == 0);
            // S12b 阴性：第三层证据换成 **Windows 绝对路径** 之后，自登记识别不许失效
            //   （包名里的 "/" 在路径里是 "\"，不归一就会把"插件登记自己"当成第二个写者）
            List<ConflictRadar.Occ> win = new List<ConflictRadar.Occ>();
            ConflictRadar.Occ w1 = Make("加载器条目", "ui-skin-y", "@ext/dsh-client-ui-skin-y");
            w1.File = "home:cordis.patch.yml"; w1.Note = "override";
            ConflictRadar.Occ w2 = Make("加载器条目", "ui-skin-y", "@ext/dsh-client-ui-skin-y");
            w2.File = "bundle:C:\\Users\\someone\\.dsh\\profiles\\node_modules\\@ext\\dsh-client-ui-skin-y\\cordis.patch.yml";
            w2.Note = "insert";
            win.Add(w1); win.Add(w2);
            Check(sb, ref pass, ref fail,
                "S12b 阴性：第三层证据是反斜杠绝对路径时，自登记仍不算第二写者 ⇒ 不该报红",
                ConflictRadar.Judge(win).Count == 0);

            // S9 阳性：同一条目在两层里 enabled 取值不一致（写者同名，故主雷达的"多写者"规则抓不到）
            List<ConflictRadar.Occ> sw = new List<ConflictRadar.Occ>();
            sw.Add(MakeSw("启用开关", "ui-x", "skin", "home:cordis.patch.yml", "false"));
            sw.Add(MakeSw("启用开关", "ui-x", "skin", "bundle:skin-pkg", "true"));
            Check(sb, ref pass, ref fail, "S9 阳性：同 id 两层启用值不一致 ⇒ 必须报红",
                HasRed(Extras(sw), "启用开关", "ui-x"));
            // S9n 阴性：两层取值一致 ⇒ 不该报红
            List<ConflictRadar.Occ> sw2 = new List<ConflictRadar.Occ>();
            sw2.Add(MakeSw("启用开关", "ui-y", "skin", "home:cordis.patch.yml", "true"));
            sw2.Add(MakeSw("启用开关", "ui-y", "skin", "bundle:skin-pkg", "true"));
            Check(sb, ref pass, ref fail, "S9n 阴性：两层启用值一致 ⇒ 不该报红",
                Extras(sw2).Count == 0);

            // S10 手搓 JSON 取值器自己要被验（不引库，所以它是最容易悄悄失效的一环）
            string mj = "{ \"name\": \"dsh-demo\", \"dsh\": { \"bundle\": { \"patch\": \"./cordis.patch.yml\" }," +
                        " \"client\": { \"platform\": \"web\", \"inject\": [\"@a/b\", \"@c/d\"] } } }";
            string dsh = JsonObj(mj, "dsh");
            string client = JsonObj(dsh, "client");
            string[] inj = JsonArray(client, "inject");
            string bundle = JsonObj(dsh, "bundle");
            string bp = JsonString(bundle, "patch");
            Check(sb, ref pass, ref fail, "S10 覆盖面：JSON 切片必须取到 2 个注入点与 patch 相对路径",
                inj.Length == 2 && inj[0] == "@a/b" && inj[1] == "@c/d" && bp == "./cordis.patch.yml");
            Check(sb, ref pass, ref fail, "S10n 阴性：字段不存在时必须返回空（不许瞎猜出东西）",
                JsonObj(mj, "nosuch").Length == 0 && JsonArray(mj, "nosuch").Length == 0 && JsonString(mj, "nosuch").Length == 0);

            // S11 实时防漏扫：在本机真实 profile 上必须至少扫到 1 个注入点与 1 个第三层 patch，
            //     否则就是扫描器静默失效（任务书 §9：覆盖面空值 = 判据根本没跑）。
            ConflictRadar.ScanResult live = new ConflictRadar.ScanResult();
            // 用**本次调用的局部增量**计数，不用静态全局：全局会被前面几条自检污染，
            // 那样 S11 声称的"扫到了多少"就不是它实际数到的东西了。
            int m0 = ManifestsScanned, i0 = InjectionsSeen, p0 = BundlePatchesRead;
            try { ConflictRadarHosts.Scan(live); } catch (Exception e) { live.NotScanned.Add("扫描抛异常: " + e.Message); }
            int mN = ManifestsScanned - m0, iN = InjectionsSeen - i0, pN = BundlePatchesRead - p0;
            int injOcc = 0;
            for (int i = 0; i < live.All.Count; i++) if (live.All[i].Kind == "宿主注入声明") injOcc++;
            Check(sb, ref pass, ref fail,
                "S11 防漏扫：真实 profile 必须扫到 >=1 注入声明 且 >=1 第三层 bundle patch（实测 清单=" +
                mN + " 注入=" + iN + " patch=" + pN + "）⇒ 0 就是扫描器坏了",
                mN >= 5 && injOcc >= 1 && pN >= 1);
            sb.AppendLine("HOSTS_COVERAGE manifests=" + mN.ToString(CultureInfo.InvariantCulture) +
                          " injections=" + iN.ToString(CultureInfo.InvariantCulture) +
                          " bundle_patches=" + pN.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("自检结论：补充扫描 通过 " + pass.ToString(CultureInfo.InvariantCulture) +
                          " 项 / 失败 " + fail.ToString(CultureInfo.InvariantCulture) + " 项");
            return sb.ToString();
        }

        static ConflictRadar.Occ Make(string kind, string key, string writer)
        {
            ConflictRadar.Occ o = new ConflictRadar.Occ();
            o.Kind = kind; o.Key = key; o.Writer = writer; o.File = "synthetic"; o.Line = 1;
            return o;
        }
        static ConflictRadar.Occ MakeSw(string kind, string key, string writer, string file, string enabled)
        {
            ConflictRadar.Occ o = Make(kind, key, writer);
            o.File = file; o.Enabled = enabled;
            return o;
        }
        static void Check(StringBuilder sb, ref int pass, ref int fail, string name, bool ok)
        {
            if (ok) { pass++; sb.AppendLine("[OK]   " + name); }
            else { fail++; sb.AppendLine("[FAIL] " + name); }
        }
        static bool HasRed(List<ConflictRadar.Conflict> cs, string kind, string keyPart)
        {
            for (int i = 0; i < cs.Count; i++)
            {
                if (!string.Equals(cs[i].Kind, kind, StringComparison.Ordinal)) continue;
                if ((cs[i].Key ?? "").IndexOf(keyPart, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
        /// <summary>按类别 + 键片段找回某一条判定本身（自检要用它区分「报了但只是提醒」和「压根没报」）。</summary>
        static ConflictRadar.Conflict Find(List<ConflictRadar.Conflict> cs, string kind, string keyPart)
        {
            for (int i = 0; i < cs.Count; i++)
            {
                if (!string.Equals(cs[i].Kind, kind, StringComparison.Ordinal)) continue;
                if ((cs[i].Key ?? "").IndexOf(keyPart, StringComparison.OrdinalIgnoreCase) >= 0) return cs[i];
            }
            return null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BigFatFishRescuer
{
    /// <summary>
    /// WP4（**只做预演，不改一个字节**）：把每条真红翻成"可回滚的处置方案"，
    /// 并按本工程既有纪律强制自报范围 —— 必须同时印出「会动的」与「不动的」两行，
    /// 缺一行就算判据不通过。
    ///
    /// 为什么不直接实现 --fix：任务书 §4 红线 1/3 —— 没快照退路不许写用户文件，
    /// 而"禁用/卸载插件"必须逐条由人点头。所以这里只交方案与等价命令，
    /// 真动手要等 WP4 的 SafeConfig.Snapshot 通道单独评审后再开。
    ///
    /// 所有文案都是**结构性**的（文件、行号、条目 id 来自扫描结果），
    /// 不写死任何插件名/目录/端口（任务书 §2.5）。
    /// </summary>
    public static class ConflictPlan
    {
        public static string Render(List<ConflictRadar.Conflict> conf, List<ConflictRadar.Occ> all)
        {
            StringBuilder sb = new StringBuilder();
            List<ConflictRadar.Conflict> hard = new List<ConflictRadar.Conflict>();
            for (int i = 0; i < conf.Count; i++) if (!conf[i].Soft) hard.Add(conf[i]);

            sb.AppendLine("== 大肥鱼救星 v" + DshCore.AppVersion + " · 冲突处置预演（--conflict-plan，**一个字节都不写**） ==");
            sb.AppendLine("DSH home：" + DshCore.DshHome);
            sb.AppendLine("真红 " + hard.Count.ToString(CultureInfo.InvariantCulture) +
                          " 条 ⇒ 每条给「改哪 / 等价命令 / 复验 / 回滚」；提醒类不在此列（判不出谁赢，需 WP3 对照）。");
            sb.AppendLine();
            if (hard.Count == 0)
            {
                sb.AppendLine("（没有可处置的真红。提醒类见 --conflict 输出，别把「没报红」读成「没冲突」。）");
                return sb.ToString();
            }

            for (int i = 0; i < hard.Count; i++)
            {
                ConflictRadar.Conflict c = hard[i];
                sb.AppendLine("[" + (i + 1).ToString(CultureInfo.InvariantCulture) + "] " + c.Kind + " | " + c.Key);
                sb.AppendLine("    现象：" + c.Why);
                for (int j = 0; j < c.Occs.Count; j++)
                {
                    ConflictRadar.Occ o = c.Occs[j];
                    sb.AppendLine("    证据：" + o.Writer + " @ " + o.File +
                                  (o.Line > 0 ? (":" + o.Line.ToString(CultureInfo.InvariantCulture)) : ""));
                }

                // 处置建议：按**类别**给通用动作，认不出类别就明说 unknown 并保守不动
                string act = "unknown";
                string eqv = "";
                string verify = "";
                string rollback = "";
                if (c.Kind == "宿主注入声明")
                {
                    act = "补上缺失的宿主模块包（或去掉对该模块的注入声明后重装声明方插件）";
                    eqv = "在 profile 目录执行：dsh plugin --profile web add <被声明的模块包名>（包名见上行 Key）";
                    verify = "复跑 --conflict：该注入声明的证据应从「目标找不到」变为「目标可见」，且 CONFLICT_COVERAGE conflicts= 计数下降";
                    rollback = "本动作只增装依赖；如需撤销：dsh plugin --profile web remove <该模块包名>";
                }
                else if (c.Kind == "补丁登记")
                {
                    act = "让登记与实文件对齐（补回补丁文件，或删掉 patchedDependencies 里那一行）";
                    eqv = "手工编辑 " + Path.Combine("profiles", "web", "pnpm-workspace.yaml") +
                          " 的 patchedDependencies 段，使每条都指向存在的 .patch 文件";
                    verify = "复跑 --conflict：该「补丁登记」红条消失；再跑一次 install 不应再报缺失";
                    rollback = "编辑前先复制一份 pnpm-workspace.yaml 备份（文件名带时间戳），失败就整文件还原";
                }
                else if (c.Kind == "入口路径")
                {
                    act = "确认该条目的入口是否随包搬走过；要么恢复文件，要么在 patch 里停用该条目";
                    eqv = "把上方证据指到的那条 id 块改成 disabled: true（写者与文件见证据行）";
                    verify = "复跑 --conflict：该入口不再报悬空；启动 DSH 后日志里不应再出现 cannot resolve";
                    rollback = "改前先备份该 cordis.patch.yml；恢复只需把备份覆盖回去";
                }
                else if (c.Kind == "入口重复登记")
                {
                    act = "两个写者抢同一个入口文件 ⇒ 只留一份登记，其余那块删掉或改成停用；" +
                          "注意被删那块属于某个插件的安装器，**重装它还会再抢一次** ⇒ 同一次要把" +
                          "「不要再登记」记进该插件的处置备忘（WP4 台账）";
                    eqv = "在证据行里**行号较大**的那块（后写的）把整条 `- id: …` 块删掉或补 `disabled: true`；" +
                          "文件与行号见上面证据行（本工具不替你写，写动作要显式接管）";
                    verify = "复跑 --conflict：该「入口重复登记」红条消失；再用 L1 沙箱跑 " +
                             "wp3_ab.ps1 -Exp dup 复验一次（实测形状：A+B 从 3.5 秒崩变绿才算治好）";
                    rollback = "改前先整文件备份该 cordis.patch.yml（文件名带时间戳），恢复=把备份覆盖回去；" +
                               "备份路径写进本次动作日志，别只记在脑子里";
                }
                else if (c.Kind == "加载器条目" || c.Kind == "启用开关" || c.Kind == "配置键")
                {
                    act = "同一共享物有多个写者 ⇒ 只保留一个生效者，其余登记为备用（谁都不该被静默删掉）";
                    eqv = "在**优先级最高的那层**（见证据行号）保留一份声明，其余层里删掉重复块";
                    verify = "复跑 --conflict：该共享物的写者数从多于 1 变成 1";
                    rollback = "删任何一层之前先整文件备份；两层都要备份，恢复按备份逐层放回";
                }
                else
                {
                    act = "unknown ⇒ 本工具判不出这一类的正确处置，**保守不动**，请人工看证据";
                    eqv = "（无）";
                    verify = "（无）";
                    rollback = "（无）";
                }
                sb.AppendLine("    处置：" + act);
                sb.AppendLine("    等价命令：" + eqv);
                sb.AppendLine("    复验：" + verify);
                sb.AppendLine("    回滚：" + rollback);

                // ★ 范围自报：两行都必须有，缺一行即判据不通过（工程既有纪律）
                List<string> movers = new List<string>();
                for (int j = 0; j < c.Occs.Count; j++)
                {
                    string f = c.Occs[j].File ?? "";
                    if (f.StartsWith("manifest:") || f == "本机 TCP 表" || f.Length == 0) continue;
                    if (!Contains(movers, f)) movers.Add(f);
                }
                sb.AppendLine("    会动的：" + (movers.Count > 0 ? string.Join("、", movers.ToArray())
                                                       : "（本条不指向任何可编辑文件 ⇒ 只能人工处置）"));
                sb.AppendLine("    不动的：其它插件目录、node_modules 内容、真实实例进程与端口、以及未在证据里署名的任何写者");
                // ★ 机器可读的逐项凭据（ASCII）：中文行在 GBK 控制台下会被解码成乱码，
                //   拿 `-match '会动的：'` 断言等于断言一个我看不见的东西（今晚 fixture 就是这么假红的）。
                sb.AppendLine("    PLAN_ITEM kind=" + ConflictRadar.KindCode(c.Kind) +
                              " act=" + (act == "unknown" ? 0 : 1) +
                              " eqv=" + (eqv.Length > 0 ? 1 : 0) +
                              " verify=" + (verify.Length > 0 ? 1 : 0) +
                              " rollback=" + (rollback.Length > 0 ? 1 : 0) +
                              " movers=" + movers.Count +
                              " stays=1");
                sb.AppendLine();
            }
            sb.AppendLine("PLAN_COVERAGE hard=" + hard.Count.ToString(CultureInfo.InvariantCulture) +
                          " rows_with_editable_target=" + CountWithTargets(hard).ToString(CultureInfo.InvariantCulture) +
                          " unknown_actions=" + CountUnknown(hard).ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("★ 本次没有写任何文件。真要动手，请逐条批准并等 WP4 的 SafeConfig.Snapshot 通道开通。");
            return sb.ToString();
        }

        static int CountWithTargets(List<ConflictRadar.Conflict> hard)
        {
            int n = 0;
            for (int i = 0; i < hard.Count; i++)
            {
                for (int j = 0; j < hard[i].Occs.Count; j++)
                {
                    string f = hard[i].Occs[j].File ?? "";
                    if (f.Length > 0 && !f.StartsWith("manifest:") && f != "本机 TCP 表") { n++; break; }
                }
            }
            return n;
        }
        static int CountUnknown(List<ConflictRadar.Conflict> hard)
        {
            int n = 0;
            for (int i = 0; i < hard.Count; i++) if (hard[i].Kind == "unknown") n++;
            return n;
        }
        static bool Contains(List<string> l, string s)
        {
            for (int i = 0; i < l.Count; i++) if (string.Equals(l[i], s, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>
    /// WP4 的第一条**写动作**：把冲突雷达扫出来的真红，翻成「禁用那条登记」。
    ///
    /// 纪律（每条都能追到一次真实翻车）：
    ///   ① **只对参与硬红的 id 动手**：id 若在硬红里一次都没被提到 ⇒ 拒绝执行、一个字节都不写。
    ///      不设这道门，它迟早变成一颗「万能禁用按钮」，被用来禁掉本来好好的插件。
    ///   ② **复用既有安全通道**：PluginDiag.SetEntryDisabled 内部已经是
    ///      快照 → 定点改（纯函数 SetDisabledState）→ YAML 校验 → 原子写 → 写后校验。
    ///      这里另写一套 YAML 改写才是风险（两套实现迟早不一致）。
    ///   ③ **范围自报**：动手前必须打印「会动的 / 不动的」两行（工程既有纪律）。
    ///   ④ **真复验**：禁用后**重扫雷达**，要求与这个 id 有关的硬红归零、且总硬红数不上升。
    ///      ★ 复验不过**不自动回滚**：文件可能完全合法、只是还没热生效（patchReload 未开或需重启），
    ///        把一份合法改动覆盖回去比留着它更危险 ⇒ 打印警告 + 快照路径 + 回滚办法，退出码 2。
    ///   ⑤ 匹配用**宽松**规则（id 作为独立词出现在 Key / Why / 证据里即算相关）：
    ///      宽松只决定「允许动手」，而「算不算成功」由 ④ 的真复验兜底 —— 前者宁可多，后者必须准。
    /// </summary>
    public static class ConflictFix
    {
        /// <summary>不带 id：只列「可点名禁用」的候选（硬红 + 每条里出现过的 id），一个字节都不写。</summary>
        public static string ListCandidates()
        {
            ConflictRadar.ScanResult sc = ConflictRadar.Scan();
            List<ConflictRadar.Conflict> hard = Hard(ConflictRadar.Judge(sc.All));
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== 大肥鱼救星 v" + DshCore.AppVersion + " · 可禁用候选（--conflict-disable，**本次不写任何文件**） ==");
            sb.AppendLine("DSH home：" + DshCore.DshHome);
            sb.AppendLine("硬红 " + Num(hard.Count) + " 条；**只有参与硬红的 id** 才会被 --conflict-disable 接受。");
            sb.AppendLine();
            if (hard.Count == 0)
            {
                sb.AppendLine("（没有硬红 ⇒ 没有候选。提醒/unknown 类**不在此列**：那类判不出谁赢，禁了可能把好的那一半也禁掉。）");
                sb.AppendLine("CONFLICT_DISABLE_LIST hard=0 with_ids=0");
                return sb.ToString();
            }
            int withIds = 0;
            for (int i = 0; i < hard.Count; i++)
            {
                ConflictRadar.Conflict c = hard[i];
                sb.AppendLine("[" + Num(i + 1) + "] " + c.Kind + " | " + c.Key);
                sb.AppendLine("    为什么红：" + c.Why);
                for (int j = 0; j < c.Occs.Count; j++)
                    sb.AppendLine("    证据：" + c.Occs[j].Writer + " @ " + c.Occs[j].File +
                                  (c.Occs[j].Line > 0 ? (":" + Num(c.Occs[j].Line)) : ""));
                List<string> ids = IdsIn(c);
                if (ids.Count > 0) withIds++;
                sb.AppendLine("    该红里出现过的 id：" +
                              (ids.Count > 0 ? string.Join("、", ids.ToArray())
                                             : "（这条里没出现 id ⇒ 不能用 --conflict-disable 处置）"));
                sb.AppendLine();
            }
            sb.AppendLine("CONFLICT_DISABLE_LIST hard=" + Num(hard.Count) + " with_ids=" + Num(withIds));
            sb.AppendLine("★ 本次没有写任何文件。要真动手：--conflict-disable <上面列出的 id>（先快照 → 只改那一处登记 → 重扫复验）");
            return sb.ToString();
        }

        /// <summary>带 id：扫 → 确认真红相关 → 快照并只改那一处 → 重扫复验。exitCode：0 成功 / 1 拒绝或未改动 / 2 复验未过。</summary>
        public static string Disable(string id, out int exitCode)
        {
            exitCode = 0;
            StringBuilder sb = new StringBuilder();
            if (id == null || id.Length == 0)
            {
                exitCode = 2;
                return "未指定条目 id。用法：大肥鱼救星.exe --conflict-disable <条目id>\r\n" +
                       "（**不带 id** 时只列候选，一个字节都不写）\r\n";
            }

            ConflictRadar.ScanResult sc = ConflictRadar.Scan();
            List<ConflictRadar.Conflict> hard = Hard(ConflictRadar.Judge(sc.All));
            int before = hard.Count;
            List<ConflictRadar.Conflict> related = Related(hard, id);

            sb.AppendLine("== 大肥鱼救星 v" + DshCore.AppVersion + " · 禁用冲突条目（--conflict-disable " + id + "） ==");
            sb.AppendLine("DSH home：" + DshCore.DshHome);
            sb.AppendLine("扫描：硬红 " + Num(before) + " 条；其中与「" + id + "」有关的 " + Num(related.Count) + " 条");
            sb.AppendLine();

            if (related.Count == 0)
            {
                exitCode = 1;
                sb.AppendLine("★ **拒绝执行：这个 id 没有参与任何硬红 ⇒ 一个字节都不会写。**");
                sb.AppendLine("  为什么要有这道门：本动作是「拆掉正在打架的那条登记」；不设门它就成了万能禁用按钮。");
                sb.AppendLine("  当前硬红（供你挑真正该禁的那个 id）：");
                int shown = 0;
                for (int i = 0; i < hard.Count && shown < 3; i++)
                {
                    List<string> ids = IdsIn(hard[i]);
                    sb.AppendLine("    · " + hard[i].Kind + " | " + hard[i].Key + " ⇒ id：" +
                                  (ids.Count > 0 ? string.Join("、", ids.ToArray()) : "（这条里没有 id）"));
                    shown++;
                }
                if (hard.Count == 0) sb.AppendLine("    （当前一条硬红都没有 ⇒ 没什么可禁用的。）");
                sb.AppendLine("  只读总览：--conflict ；候选清单：--conflict-disable（不带参数）");
                sb.AppendLine(Machine(id, 0, before, before, 0, 0));
                return sb.ToString();
            }

            // ★ 范围自报：两行都必须有（缺一行即判据不通过）
            sb.AppendLine("会动的：「" + id + "」这一条登记本身（只写 1 个文件：profile 层或 home 层的 cordis.patch.yml，实际路径见下面执行结果）");
            sb.AppendLine("不动的：其它条目的登记、其它插件目录、node_modules 内容、真实实例进程与端口、以及没在本次证据里署名的任何写者");
            // ★ 与 --conflict-plan 的 PLAN_ITEM 同款：给上面两行配一条**纯 ASCII 凭据**。
            //   原因（工程既有铁律）：中文行在 GBK 控制台下会被解成乱码，
            //   拿 `-match '会动的：'` 去断言等于断言一个我自己看不见的东西（曾经因此假红）。
            sb.AppendLine("CONFLICT_DISABLE_SCOPE movers=1 stays=1");
            sb.AppendLine();

            string res;
            try { res = PluginDiag.SetEntryDisabled(id, true); }
            catch (Exception e)
            {
                exitCode = 1;
                sb.AppendLine("★ 执行抛异常 ⇒ 按**未改动**处理：" + e.GetType().Name + "：" + e.Message);
                sb.AppendLine(Machine(id, 1, before, before, 0, 0));
                return sb.ToString();
            }
            sb.AppendLine("执行结果（含快照路径）：");
            sb.AppendLine(res);
            sb.AppendLine();

            bool changed = res != null && res.StartsWith("已禁用", StringComparison.Ordinal);
            if (!changed)
            {
                exitCode = 1;
                sb.AppendLine("★ 没有发生写动作（上面那句就是原因）⇒ 硬红数不变，退出码 1。");
                sb.AppendLine(Machine(id, 1, before, before, 0, 0));
                return sb.ToString();
            }

            // ★ 真复验：重扫同一把尺子
            ConflictRadar.ScanResult sc2 = ConflictRadar.Scan();
            List<ConflictRadar.Conflict> hard2 = Hard(ConflictRadar.Judge(sc2.All));
            int after = hard2.Count;
            List<ConflictRadar.Conflict> still = Related(hard2, id);
            bool verified = still.Count == 0 && after <= before;

            sb.AppendLine("复验（重扫雷达）：硬红 " + Num(before) + " → " + Num(after) +
                          "；与「" + id + "」有关的 " + Num(related.Count) + " → " + Num(still.Count));
            if (verified)
            {
                sb.AppendLine("✅ 复验通过：那条冲突已经不在雷达上了。");
                sb.AppendLine("   生效方式：patchReload 为 live 时**热生效**；不确定就点一次「🚀 启动并打开」或重启服务。");
            }
            else
            {
                exitCode = 2;
                sb.AppendLine("⚠️ **复验未通过：文件已按上面的结果改好了，但雷达上那条冲突还在。**");
                sb.AppendLine("   为什么**不自动回滚**：文件本身可能完全合法，只是还没热生效（patchReload 未开 / 需要重启服务）。");
                sb.AppendLine("   把一份合法改动覆盖回去，比留着它更危险 —— 所以这里只报告，把选择权留给人。");
                sb.AppendLine("   下一步建议：① 先重启一次 DSH 再复扫（--conflict）；② 仍红就按下面回滚。");
                sb.AppendLine("   回滚办法：用上面执行结果里的「快照」路径 —— 打开救星 →「♻ 恢复配置」选那一条；");
                sb.AppendLine("             或直接把那份快照覆盖回去（覆盖前先看一眼快照里的 cordis.patch.yml）。");

                // ★★ 复验尺子的**已知边界**：必须在这里说出来。
                //   否则这句"未通过"会被读成"我的改动没用"，而真相可能是"这把尺子量不出这种改动"。
                List<string> stuck = new List<string>();
                for (int i = 0; i < still.Count; i++) if (!Has(stuck, still[i].Kind)) stuck.Add(still[i].Kind);
                if (stuck.Count > 0) sb.AppendLine("   ★ 仍红的那几条属于：" + string.Join("、", stuck.ToArray()));
                if (Has(stuck, "入口重复登记"))
                {
                    sb.AppendLine("   ★★ 已知边界（**不是**本次改动的问题）：雷达的「入口重复登记」判据数的是**登记次数**，");
                    sb.AppendLine("      而项目早已确认 `disabled:` 与 `insert:` **谁赢未验证**（ConflictRadar 故意只降级成提醒、不判红）。");
                    sb.AppendLine("      ⇒ 补上 `disabled: true` 之后这条红**不会消失**；要它消失必须把那一份重复登记**删掉**");
                    sb.AppendLine("        （照 --conflict-plan 给的行号删那块，或用 --plan-fix 的 dedup 动作）。");
                }
            }
            sb.AppendLine(Machine(id, 1, before, after, 1, verified ? 1 : 0));
            return sb.ToString();
        }

        // ---------------- 内部小工具 ----------------

        static bool Has(List<string> l, string s)
        {
            for (int i = 0; i < l.Count; i++) if (string.Equals(l[i], s, StringComparison.Ordinal)) return true;
            return false;
        }

        static string Machine(string id, int accepted, int before, int after, int changed, int verified)
        {
            return "CONFLICT_DISABLE id=" + id + " accepted=" + Num(accepted) +
                   " hard_before=" + Num(before) + " hard_after=" + Num(after) +
                   " changed=" + Num(changed) + " verified=" + Num(verified);
        }

        static List<ConflictRadar.Conflict> Hard(List<ConflictRadar.Conflict> all)
        {
            List<ConflictRadar.Conflict> r = new List<ConflictRadar.Conflict>();
            for (int i = 0; i < all.Count; i++) if (!all[i].Soft) r.Add(all[i]);
            return r;
        }

        /// <summary>宽松匹配：id 作为独立词出现在 Key / Why / 任一证据里即算「参与这条红」。</summary>
        static List<ConflictRadar.Conflict> Related(List<ConflictRadar.Conflict> hard, string id)
        {
            List<ConflictRadar.Conflict> r = new List<ConflictRadar.Conflict>();
            for (int i = 0; i < hard.Count; i++)
            {
                ConflictRadar.Conflict c = hard[i];
                bool hit = Hits(c.Key, id) || Hits(c.Why, id);
                if (!hit)
                {
                    for (int j = 0; j < c.Occs.Count; j++)
                    {
                        if (Hits(c.Occs[j].Key, id) || Hits(c.Occs[j].Writer, id) || Hits(c.Occs[j].File, id))
                        { hit = true; break; }
                    }
                }
                if (hit) r.Add(c);
            }
            return r;
        }

        static bool Hits(string s, string id)
        {
            if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(id)) return false;
            int from = 0;
            while (from <= s.Length - id.Length)
            {
                int k = s.IndexOf(id, from, StringComparison.OrdinalIgnoreCase);
                if (k < 0) return false;
                bool lOk = (k == 0) || !IsWordChar(s[k - 1]);
                int end = k + id.Length;
                bool rOk = (end >= s.Length) || !IsWordChar(s[end]);
                if (lOk && rOk) return true;
                from = k + 1;
            }
            return false;
        }

        /// <summary>词字符：字母 / 数字 / - / _ 。★ 故意**不含点** —— 这样 "id.configKey" 能被 id 命中（禁掉该条目正是这一类红的一种处置）。</summary>
        static bool IsWordChar(char ch)
        {
            return char.IsLetterOrDigit(ch) || ch == '-' || ch == '_';
        }

        /// <summary>把这条红里出现过的 id 抠出来（**提示用，可能不全**；真正放行与否由 Related + 复验决定）。</summary>
        static List<string> IdsIn(ConflictRadar.Conflict c)
        {
            List<string> ids = new List<string>();
            string why = c.Why ?? "";
            try
            {
                System.Text.RegularExpressions.Match m =
                    System.Text.RegularExpressions.Regex.Match(why, "id 有 \\d+ 个：([^（]+)");
                if (m.Success)
                {
                    string[] parts = m.Groups[1].Value.Split(',');
                    for (int i = 0; i < parts.Length; i++)
                    {
                        string t = parts[i].Trim();
                        if (t.Length > 0) AddId(ids, t);
                    }
                }
            }
            catch { }
            AddIdIfLooksLikeId(ids, c.Key);
            for (int j = 0; j < c.Occs.Count; j++) AddIdIfLooksLikeId(ids, c.Occs[j].Key);
            return ids;
        }

        static void AddIdIfLooksLikeId(List<string> ids, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            if (s.IndexOf('\\') >= 0 || s.IndexOf('/') >= 0 || s.IndexOf(':') >= 0) return;   // 路径/端口之类不算 id
            if (s.Length > 80) return;
            AddId(ids, s);
        }

        static void AddId(List<string> ids, string id)
        {
            for (int i = 0; i < ids.Count; i++) if (string.Equals(ids[i], id, StringComparison.OrdinalIgnoreCase)) return;
            ids.Add(id);
        }

        static string Num(int n)
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }
    }
}

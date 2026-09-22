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
}

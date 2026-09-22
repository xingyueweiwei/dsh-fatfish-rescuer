# 大肥鱼救星 · BigFatFish Rescuer

> Windows 上给 **DeepSeek Harness（DSH）** 用的排障 / 救援小工具。
> 起不来、打不开、端口被占、插件打架、配置被写坏 —— 它**先把现场看清楚**，再给你**能回滚**的动作。

> ⚠️ **这是社区工具，与 DeepSeek 官方无关**，也没有任何官方背书。
> 仓库名里有 `dsh` 只是为了能被搜到；产品名是「大肥鱼救星」。

---

## 下载（不想自己编译的话）

👉 **[Releases 页](https://github.com/xingyueweiwei/dsh-fatfish-rescuer/releases/latest)** —— 里面有打包好的便携版 zip：
下载 → 解压 → 双击 `大肥鱼救星.exe`。包内附《先看这里》《如果被杀软误删》《异地实测清单》。

> 还没有 Release 的时候（或者你想要自己编译），按下面「30 秒上手」那节自己构建即可。

⚠️ **它是未签名的**，所以 Windows 可能弹 SmartScreen、杀软可能启发式报毒（这个工具会结束进程、改配置，天生像"可疑行为"）。要么自己编译、要么看包内《如果被杀软误删.md》。**代码全在这里，你可以自己核。**

---

## 为什么会有这个东西

它是一次次把自己从坑里捞出来之后长出来的：

- DSH 起不来时，**日志里躺着上一次的报错**，人（和我）会把旧崩溃当成本次原因 —— 于是它现在启动前先记下"本次起点"（日志字节偏移 + 时间戳分界），**只归因起点之后新增的内容**；
- 端口上有个"卡死的残留"，其实人家只是**忙**（一秒二没答上话）—— 于是它的探活改成多次重试 + 宽限，且**"忙"和"死"分开判**；
- 插件装完之后**端口还占着 / `node_modules` 半装** —— 于是它杀进程改成按 `ParentProcessId` 逐层收窄、只动**自己起的那个 PID 及其后代**，并强制输出「会动谁 / 不动谁」。

## 它会动什么、不会动什么

| | |
|---|---|
| ✅ 会做 | 读取 DSH 自己的日志 / 配置 / 进程表；起停**本机**的 DSH web（只认当前端口那个实例）；写配置前**先快照**、失败自动回滚；结束**它自己起的**进程及其后代 |
| ❌ 不做 | 不联网、不发送任何数据、无遥测；不写注册表 Run 键 / 启动项 / 计划任务；不解码载荷、不下载执行远程内容；不碰凭据、浏览器、文档；**不碰别的端口上的 DSH 实例** |
| 🛡 安全总闸 | `--readonly on` 之后**绝不结束 / 启动任何 dsh 进程**（界面也有「🛡 只诊断」） |

## 30 秒上手

```powershell
git clone https://github.com/xingyueweiwei/dsh-fatfish-rescuer.git
cd dsh-fatfish-rescuer
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1   # 只用 Windows 自带的 csc，零依赖
.\BigFatFishRescuer.exe            # 打开界面
.\BigFatFishRescuer.exe --selftest # 直接跑本机判据（无界面）
```

**构建要求**：Windows + .NET Framework 4.x（系统自带 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`）+ PowerShell 5.1+。
**不需要** Visual Studio、不需要 NuGet、不需要联网 —— `build.ps1` 会把版本信息 / 应用清单 / 图标一起编进 `src\version.res`。

> 注意：本工程**不是可复现构建**（PE 头带时间戳），**每次编译出来的 sha256 都会不同**。要核对是不是同一份，请比对同一次构建的产物。

## 能力一览（都是命令行，界面是它的壳）

**只读体检**（不改任何东西）

```
--selftest      本机判据全套（含正负样本对照）
--envcheck      运行环境体检        --configtest   配置体检（可自动回滚）
--pathaudit     中文路径 / 危险字符体检
--plugincheck   插件兼容性          --layercheck   分层冲突
--whitescreen   白屏体检            --patchlock    补丁锁体检
--selfheal-boundary  自愈边界       --classify     日志归因分类
--corpus-check  归因语料回归        --diagnose     深度诊断
--errtext       报错原文对照        --rollbackadvice 回滚建议
--headless      无界面只读体检，出一份可转发报告
--manualcheck   说明书页布局自检
```

**预演**（一个字节都不写，只告诉你"真做的话会动谁"）

```
--plan-kill        结束进程预演
--plan-fix all     修复动作预演（每条给：改哪 / 等价命令 / 复验判据 / 回滚方式）
--killtree-plan <PID>  按进程树收窄的预演
--conflict         插件冲突雷达（谁是共享物的写者）
--conflict-plan    冲突处置预演
--conflict-tiers   插件档位表（只列成员，不起进程）
--scout <包名>     新插件适配预演
```

**动手（带回滚）**

```
--fix-apply <动作id>  执行某条修复      --fixdangling  清理悬空引用
--dedup               去掉重复条目      --enableentry / --disableentry  启用/禁用某条目
--patchlock-fix       重打补丁          --reinstall    重装插件
--autorecover         自动恢复          --smart        一键智能启动（健康则只打开）
--start / --open      起服务 / 打开页面 --skin         换皮肤
```

## 它怎么证明自己

这一条是这个项目最在意的：

```powershell
.\BigFatFishRescuer.exe --selftest          # 本机判据（含正负样本）
.\BigFatFishRescuer.exe --conflict-selftest # 冲突判据（全在临时目录里跑）
.\tools\killtree_judge.ps1                  # 进程树判据：自造替身树，会动数必须 ≥2
```

- `--selftest` 与 `--conflict-selftest` **自带负样本**：会故意造出"应该报红"的输入，报了才算过；
- `tools\killtree_judge.ps1` 会**自己造一棵两层进程树**当替身，要求"会动数 ≥ 2"（只杀根 = 判红）、预演后替身必须**全部存活**、树以外的进程**不许被提到** —— 收尾只清自己造的替身；
- 判据数量会随版本增长，**别信 README 里的数字，跑一遍看你自己的输出**。

## 已知短板（别当成做完了）

- **插件冲突只能看清 + 预演，不能替你把冲突改掉**：真正的写动作（自动禁用一个冲突条目）**尚未实现**；
- 冲突雷达**只有命令行入口，界面上没有按钮**；
- 日志归因**认得准但认得少**：作者本机语料上精确率高、**召回率偏低**，很多故障仍需人来读；
- 一些边界没定论（例如同一 profile 里"哪个 patch 条目最终生效"在某些组合下静态判不出来，工具会**如实标 `unknown` 而不是猜**）；
- 本仓库**不含**内部回归套件（语料 / 故障注入 / 沙箱），那部分与本机环境强耦合。

## 被杀软误报怎么办

它天生容易被启发式盯上（会**结束进程、写配置文件**）。当前版本做了这些去形状：

- **没有持久化**（不写 Run 键 / 启动项 / 计划任务）；
- **base64 解码不再是主路径**（`launcher.ini` 的 `ArgumentsBase64` 只在显式开关下才解析）；
- **起 node 不经过 `cmd.exe`**，也不建管道（日志走两个文件句柄，两路异步排空）；
- **不拼 `taskkill` 命令行**，改按 PID + `ParentProcessId` 逐层收窄并逐个 `Process.Kill()`；
- 二进制里带**软件身份元数据**（VERSIONINFO + 应用清单 + 图标）。

仍然被拦的话：**自己编译**是最稳的（见上），或者把误报提交给杀软厂商。**本仓库不提供签名**，所以下载预编译 exe 时请自行判断。

## 许可

- **代码：MIT**（见 `LICENSE`）。
- **立绘 / 图标 / 表情包等美术素材：见 `NOTICE`**（不在 MIT 覆盖范围内，保留权利）。

## 致谢

- 皮肤的配色与版式研究参考了社区几套 DSH 皮肤**公开的配色 token 与版式思路**（只取色值与做法，不使用其素材文件）；
- 进程树 / 冲突判据的设计反复被真实事故教育过 —— 每一条判据背后都对应一次翻车。

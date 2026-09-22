# ============================================================
#  大肥鱼救星 · 构建脚本（2026-09-16 定稿，以后一律用它）
#
#  ⚠️ 为什么必须有这个脚本：
#    README 里那条 csc 命令**漏了 /resource:**，编出来的 exe 里没有立绘资源，
#    于是主页立绘是空的 —— MainForm.LoadMaidImage() 是从**嵌入资源**里取图的
#    （它遍历 GetManifestResourceNames()，找名字含 "assets" 且以 .png 结尾的，
#      并优先选 big-medicine）。
#    校验方法：原版 exe = 1,051,136 字节（带图）；漏掉 resource 的版本只有 ~259 KB。
#    259 KB + big-medicine.png(853 KB) ≈ 1.11 MB，与原版吻合 ⇒ 原版嵌的就是它。
#
#  用法：
#      pwsh -File build.ps1              # 构建到 BigFatFishRescuer.exe
#      pwsh -File build.ps1 -Deploy      # 构建并部署到桌面「大肥鱼救星.exe」
# ============================================================
param(
    [switch]$Deploy
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "找不到 csc.exe: $csc" }

$sources = @(
    'src\Program.cs',
    'src\DshCore.cs',
    'src\MainForm.cs',
    'src\SafeConfig.cs',
    'src\DshBoot.cs',
    'src\PatchGuard.cs',
    'src\PatchLock.cs',
    'src\PathAudit.cs',
    'src\Manual.cs',
    'src\SkinTheme.cs',
    'src\SkinFrame.cs',
    'src\SkinArt.cs',
    'src\PluginDiag.cs',
    'src\RepairPlan.cs',
    'src\PluginScout.cs',
    'src\ConflictRadar.cs',
    'src\ConflictRadarHosts.cs',
    'src\ConflictRadarStatic.cs',
    'src\ConflictPlan.cs',
    'src\ConflictTiers.cs'
)
foreach ($s in $sources) { if (-not (Test-Path $s)) { throw "缺源文件: $s" } }

# ============================================================
#  软件身份元数据（2026-09-20 加：为降低杀软机器学习误报）
#
#  背景：二进制里没有任何版本信息/清单 = 「没有身份」，
#        容易被 Defender 的 !ml 启发式判成 Trojan:Win32/Bearfoos.B!ml。
#
#  ① 版本资源 VERSIONINFO —— 本机**没有 Windows SDK，也没有 rc.exe**，
#     所以不能走 .rc 那条老路：改用自写工具 tools\MakeVersionRes.exe
#     按 Win32 资源文件格式**手写** src\version.res（空资源头 +
#     RT_ICON/RT_GROUP_ICON + RT_MANIFEST + RT_VERSION=16/VS_VERSION_INFO=1），
#     交给 csc /win32res:。
#  ② 应用清单 src\app.manifest —— 同样由 MakeVersionRes 编成
#     RT_MANIFEST(24)/name=1 写进 version.res
#     （asInvoker / PerMonitorV2 DPI / longPathAware / Win10-11 supportedOS）；
#     **不能**给 csc /win32manifest:，它与 /win32res: 互斥。
#  ③ 应用图标 assets\big-fat-fish.ico —— 同样编进 version.res
#     （csc 的 /win32icon: 与 /win32res: 也互斥）。
#
#  版本号唯一出口仍是 src\DshCore.cs 的 AppVersion：
#  MakeVersionRes 只从那里读，并把四段版本（如 5.0.1.0）同步进 app.manifest。
# ============================================================
$verTool = 'tools\MakeVersionRes.exe'
$verToolSrc = 'tools\MakeVersionRes.cs'
$appIcon = 'assets\big-fat-fish.ico'
foreach ($f in @($verToolSrc, 'src\app.manifest', $appIcon)) { if (-not (Test-Path $f)) { throw "缺身份元数据来源: $f" } }

Write-Host "编译版本资源工具 MakeVersionRes…"
& $csc /nologo /target:exe /codepage:65001 "/out:$verTool" $verToolSrc
if ($LASTEXITCODE -ne 0) { throw "MakeVersionRes 编译失败（exit $LASTEXITCODE）" }

# ★ 图标必须走 --icon= 编进同一个 .res：
#   本机 csc **不允许 /win32icon: 与 /win32res: 同时出现**
#   （error CS1565: 指定的选项冲突: Win32 资源文件与 Win32 图标），
#   而 rc.exe 的老做法本来就是"一个 .rc 同时放 ICON + VERSIONINFO"。
#   所以这里把 big-fat-fish.ico 一起编进 version.res，csc 那边只给 /win32res:。
Write-Host "生成版本资源（MakeVersionRes -> src\version.res，含图标）…"
& $verTool "--icon=$appIcon"
if ($LASTEXITCODE -ne 0) { throw "MakeVersionRes 运行失败（exit $LASTEXITCODE）" }
if (-not (Test-Path 'src\version.res')) { throw "版本资源没有生成: src\version.res" }

# 嵌入资源：名字里必须含 "assets" 才能被 LoadMaidImage 认出来
# ★ 2026-09-17：说明书结语那个表情包**故意不叫 assets.*** ——
#   否则它会被 LoadMaidImage 当成"立绘候选"参与挑选（那套逻辑是"含 assets 就算候选"）。
$resources = @(
    # ★ 2026-09-18：素材以"主人的图"为准 ——
    #   左：assets\big-medicine.png（大的药来了，主人原有立绘）
    #   右：assets\whale.png（主人挑的鲸鱼；白底由程序运行时抠掉）
    #   装饰（蝴蝶结/海浪/金线/边框）全部自绘 ⇒ 不再有任何第三方署名要求。
    '/resource:assets\big-medicine.png,assets.big-medicine.png',
    '/resource:assets\whale.png,skinart.whale.png',
    # 说明书结语的表情包（主人自己的图；名字故意不含 assets）
    '/resource:assets\goodnight.png,manual.goodnight.png'
)
$out = 'BigFatFishRescuer.exe'
if (Test-Path $out) { Remove-Item $out -Force }

$cscArgs = @(
    '/nologo', '/target:winexe', '/codepage:65001',
    "/out:$out",
    # ★ 软件身份元数据（2026-09-20）：版本信息 + 应用清单 + 图标，全部由
    #   src\version.res 提供（MakeVersionRes 生成）。
    #   ⚠ 本机 csc 的 /win32res: 与 **/win32icon: 和 /win32manifest: 都互斥**：
    #       error CS1565 指定的选项冲突: Win32 资源文件与 Win32 图标
    #       error CS1564 指定的选项冲突: Win32 资源文件与 Win32 清单
    #     所以图标与清单必须编进同一个 .res —— 这正是 rc.exe 的老做法
    #     （一个 .rc 里同时写 ICON / 24 "app.manifest" / VERSIONINFO，一次出 .res）。
    '/win32res:src\version.res',
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll'
) + $resources + $sources

Write-Host "编译中…（含 $(($resources).Count) 个嵌入资源）"
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败（exit $LASTEXITCODE）" }

$exe = Get-Item $out
Write-Host ("构建成功: " + $exe.Length + " 字节")

# 自检：立绘资源必须真的进去了。经验值：含图 ≈ 1.0~1.2 MB，漏掉 ≈ 0.25 MB
if ($exe.Length -lt 500KB) {
    Write-Warning "exe 只有 $([int]($exe.Length/1KB)) KB —— 很可能**没带上素材资源**（2026-09-18 起素材换成小体积 CC0 图，正常应 ≈800KB 以上）。请检查 /resource: 那几行。"
} else {
    Write-Host "  立绘资源体积看起来正常 ✔"
}

# 跑一次无界面自检（只读），确认没编坏
# ★ 2026-09-18 修：报告路径必须跟救星的 DshHome 走 —— 救星支持 BFF_DSH_HOME
#   覆盖 DshHome（给沙箱/CI 用）。原来这里硬编码 $env:USERPROFILE\.dsh，
#   于是设了 BFF_DSH_HOME 时报告写到别处、这里比对的是旧文件，
#   会打出"自检并未真正执行"的**假警告**（实测踩到）。
$dshHome = if ($env:BFF_DSH_HOME) { $env:BFF_DSH_HOME } else { Join-Path $env:USERPROFILE '.dsh' }
$rep = Join-Path $dshHome 'big-fat-fish-rescuer\envcheck-report.txt'
$repBefore = if (Test-Path $rep) { (Get-Item $rep).LastWriteTimeUtc } else { $null }
try {
    # ★ 2026-09-18 v5 修正：原来用 Start-Process，在本机**必抛异常**且被 catch 吞掉，
    #   于是"构建成功"其实是"自检一次都没跑"的假绿灯。
    #   根因：本机进程环境块里有仅大小写不同的重复变量 (HTTP_PROXY/http_proxy、
    #   HTTPS_PROXY/https_proxy)，而 .NET 的 ProcessStartInfo.EnvironmentVariables 是
    #   **大小写不敏感**字典 ⇒ ArgumentException "已添加项。字典中的关键字"。
    #   [System.Diagnostics.Process]::Start 不走那条路，实测正常。
    $exeFull = Join-Path $root $out
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exeFull
    $psi.Arguments = '--envcheck'
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = $root
    # ★★ 必须重定向而不是让它直接打到控制台：envcheck 的输出里含**一次性 token URL**
    #    （形如 http://127.0.0.1:3080/?token=...）。按本项目纪律 8「敏感内容零回显」，
    #    这种东西一旦进构建日志/对话历史就收不回来 ⇒ 这里只统计数字，绝不回显原文。
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $pr = [System.Diagnostics.Process]::Start($psi)
    $stdout = $pr.StandardOutput.ReadToEnd()
    [void]$pr.StandardError.ReadToEnd()
    $pr.WaitForExit()

    $okN = ([regex]::Matches($stdout, '(?m)^\[OK\]')).Count
    $failN = ([regex]::Matches($stdout, '(?m)^\[FAIL\]')).Count
    # 凭据类字段自检：命中就说明输出里有敏感串，而我们确实没把它打出来
    $secN = ([regex]::Matches($stdout, '(?i)(token|api[-_]?key|secret|password)\s*[=:]')).Count
    Write-Host ("  自检 --envcheck exit = " + $pr.ExitCode + "   [OK] " + $okN + " / [FAIL] " + $failN)
    if ($secN -gt 0) {
        Write-Host ("  ⚠ 输出含 " + $secN + " 处凭据类字段（一次性 token 等）⇒ **已按纪律 8 不回显**")
    }
    if ($failN -gt 0) { Write-Warning "envcheck 有 $failN 项失败，详情见 $rep （该文件含 token，别外发）" }

    # 别再只看退出码：报告文件没被重写 = 自检其实没执行（winexe 静默 no-op 的坑）
    $repAfter = if (Test-Path $rep) { (Get-Item $rep).LastWriteTimeUtc } else { $null }
    if ($repBefore -and $repAfter -and $repAfter -le $repBefore) {
        Write-Warning "自检退出码是 $code，但 envcheck-report.txt **没有被重写** ⇒ 自检并未真正执行。"
    } else {
        Write-Host "  自检报告已重写 ✔"
    }
} catch { Write-Warning "自检没跑成：$_" }

if ($Deploy) {
    $desk = Join-Path $env:USERPROFILE 'Desktop\大肥鱼救星.exe'
    Copy-Item (Join-Path $root $out) $desk -Force
    Write-Host ("已部署到桌面: " + $desk + "  (" + (Get-Item $desk).Length + " 字节)")
}

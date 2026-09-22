# killtree 判据（--killtree-plan 的行为验收）
#
# 为什么要有这个脚本：
#   2026-09-22 验收时发现真缺陷 —— KillTreeManaged 只杀根、子进程全漏
#   （QueryCmdLine 返回带 CRLF、QueryChildren 侧 Trim 过 ⇒ 身份守卫对所有子进程误命中）。
#   而当时**没有任何判据**能发现它：既有判据只看「输出里有没有会动/不动两行」。
#   所以这里补上硬判据：① 真造一棵两层树，会动数必须 ≥2；② 预演之后替身必须全部还活着；
#   ③ 每个子 PID 都必须在报告里出现；④ **清理后按命令行特征全机复查残留必须 0**；外加阴性对照。
#
# ★ 2026-09-22 第二次修（这个判据自己出过的两个洋相，别再犯）：
#   ① **别弹窗**：替身必须用 Start-Process -WindowStyle Hidden 建。
#      宿主（DSH）本身没有控制台 ⇒ 子控制台程序会**新建一个可见黑窗**；
#      第一版用 WMI Win32_Process.Create 默认创建，于是每跑一次就弹一个 cmd 加一个 ping（还会挂满时长）。
#      （若你在自己的终端里跑本脚本，子进程会继承你的控制台，本来就不会弹窗。）
#   ② **别漏子进程**：清理必须**先杀逐个发现的子进程、再杀根**，
#      并且收尾要按命令行特征**全机复查** —— 第一版只杀根 ⇒ cmd 被杀了、它的 ping 变成孤儿继续跑。
#      教训：修好「被验收对象漏杀子进程」的当天，我自己的判据犯了同一个错。
#
# 纪律（沿用本项目铁律）：
#   · 只动**自己造的**替身；不按进程名扫全机去杀；
#   · 计数一律写 @(...).Count（单个 PSCustomObject 没有合成 .Count，是假绿的根源）；
#   · 判据必须能被负对照证伪：拿修前的 exe 跑它，B1 必须报红。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File v5\killtree_judge.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File v5\killtree_judge.ps1 -Exe <exe 路径>

param(
    [string]$Exe = "",
    [int]$SettleSec = 4,
    [int]$ProbeSeconds = 20          # 替身存活时长（秒）——够跑完一次预演即可，短一点免得残留难清
)

$ErrorActionPreference = 'Continue'

if (-not $Exe -or $Exe -eq "") {
    $here = Split-Path -Parent $MyInvocation.MyCommand.Path
    $proj = Split-Path -Parent $here
    $Exe = Join-Path $proj 'BigFatFishRescuer.exe'
}

# 替身的命令行特征（用于收尾全机复查；必须与本脚本造替身时用的命令一致）
$probeRe = "ping\s+-n\s+$ProbeSeconds\s+127\.0\.0\.1"

Write-Host "== killtree 判据（--killtree-plan）=="
Write-Host ("exe: " + $Exe)
if (-not (Test-Path -LiteralPath $Exe)) {
    Write-Host ("FAIL 找不到 exe：" + $Exe)
    Write-Host "KILLTREE_JUDGE ok=0 fail=1 aborted=1"
    exit 1
}

$ok = 0
$fail = 0
$aborted = 0
$mine = New-Object System.Collections.ArrayList   # 我自己造的 PID（含后来发现的子进程），收尾逐个杀

function Judge([string]$id, [bool]$pass, [string]$detail) {
    if ($pass) { $script:ok++; $line = "[OK]   " + $id + "  " + $detail }
    else       { $script:fail++; $line = "[FAIL] " + $id + "  " + $detail }
    Write-Host $line
}

# 造两层树的根：cmd -> ping。**必须隐藏窗口**（见文件头说明 ①）
function New-Probe {
    $p = Start-Process -FilePath 'cmd' -ArgumentList @('/c', "ping -n $ProbeSeconds 127.0.0.1") `
         -PassThru -WindowStyle Hidden
    if ($p -and $p.Id -gt 0) { [void]$script:mine.Add([int]$p.Id); return [int]$p.Id }
    return 0
}

# 直接起 ping（用于「无关哨兵」与「幽灵 PID」两处，不套 cmd）
function New-PingProbe {
    $p = Start-Process -FilePath 'ping' -ArgumentList @('-n', "$ProbeSeconds", '127.0.0.1') `
         -PassThru -WindowStyle Hidden
    if ($p -and $p.Id -gt 0) { [void]$script:mine.Add([int]$p.Id); return [int]$p.Id }
    return 0
}

function Get-Kids([int]$parentPid) {
    $k = Get-CimInstance Win32_Process -Filter ("ParentProcessId=" + $parentPid) -ErrorAction SilentlyContinue
    return @($k | ForEach-Object { [int]$_.ProcessId })
}

function Alive([int]$p) { return ($null -ne (Get-Process -Id $p -ErrorAction SilentlyContinue)) }

# 全机按命令行特征复查替身残留（这是能**客观测**的那把尺子）
function Get-ProbeLeftovers {
    $all = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue
    return @($all | Where-Object { $_.CommandLine -and ($_.CommandLine -match $probeRe) })
}

try {
    # ---------- 0. 造替身：一棵两层树（cmd -> ping）+ 一个无关哨兵 ----------
    $root = New-Probe
    $sentinel = New-PingProbe
    if ($root -le 0 -or $sentinel -le 0) { throw ("替身进程没造出来（root=" + $root + " sentinel=" + $sentinel + "）") }

    $kids = @()
    for ($i = 0; $i -lt 12; $i++) {
        Start-Sleep -Milliseconds 600
        $kids = Get-Kids $root
        if (@($kids).Count -ge 1) { break }
    }
    foreach ($k in @($kids)) { [void]$mine.Add([int]$k) }     # ★ 子进程也要记进待清理名单
    Write-Host ("替身：root=" + $root + " 子=" + (@($kids) -join ',') + " 无关哨兵=" + $sentinel)
    Judge 'A1' (@($kids).Count -ge 1) ("替身树必须真的有两层（子进程数=" + @($kids).Count + "）")

    # ---------- 1. 跑预演 ----------
    $out = Join-Path $env:TEMP ("ktj_" + [guid]::NewGuid().ToString('N').Substring(0, 8) + ".txt")
    $err = $out + ".err.txt"
    $p = Start-Process -FilePath $Exe -ArgumentList @('--killtree-plan', "$root") -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput $out -RedirectStandardError $err
    $text = Get-Content $out -Raw -Encoding UTF8
    if ($null -eq $text) { $text = "" }

    Judge 'A2' ($p.ExitCode -eq 0) ("--killtree-plan 退出码必须 0（实测 " + $p.ExitCode + "）")

    $willKill = @($text -split "`r?`n" | Where-Object { $_ -match '预演会动：' })
    $willSkip = @($text -split "`r?`n" | Where-Object { $_ -match '不动：' })

    # ---------- 2. 硬判据：会动数必须 ≥2（根 + 至少一个子进程） ----------
    Judge 'B1' (@($willKill).Count -ge 2) ("会动条目数必须 ≥2（根+子进程）；实测 " + @($willKill).Count + " 条")

    # ---------- 3. 每个子 PID 都必须在报告里出现（不许静默漏掉） ----------
    $missing = @()
    foreach ($k in @($kids)) { if ($text -notmatch ("\b" + $k + "\b")) { $missing += $k } }
    Judge 'B2' (@($missing).Count -eq 0) ("每个子 PID 都要出现在报告里（会动或不动）；漏掉 " + @($missing).Count + " 个: " + (@($missing) -join ','))

    # ---------- 4. 阴性对照①：预演一个都不许杀 ----------
    $deadlist = @()
    foreach ($x in (@($root) + @($kids) + @($sentinel))) { if (-not (Alive $x)) { $deadlist += $x } }
    Judge 'C1' (@($deadlist).Count -eq 0) ("预演之后替身必须全部存活；实测死掉 " + @($deadlist).Count + " 个: " + (@($deadlist) -join ','))

    # ---------- 5. 阴性对照②：无关进程不许被提到 ----------
    Judge 'C2' ($text -notmatch ("\b" + $sentinel + "\b")) ("树以外的无关进程不许出现在报告里（哨兵 PID " + $sentinel + "）")

    # ---------- 6. 阴性对照③：不存在的 PID 不许报成「会动」 ----------
    $ghost = New-PingProbe
    Stop-Process -Id $ghost -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    $o2 = $out + ".2.txt"
    $null = Start-Process -FilePath $Exe -ArgumentList @('--killtree-plan', "$ghost") -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput $o2 -RedirectStandardError ($o2 + ".err")
    $t2 = Get-Content $o2 -Raw -Encoding UTF8
    if ($null -eq $t2) { $t2 = "" }
    $ghostKill = @($t2 -split "`r?`n" | Where-Object { $_ -match '预演会动：' })
    Judge 'C3' (@($ghostKill).Count -eq 0) ("已退出的 PID 不许报成会动（实测会动 " + @($ghostKill).Count + " 条）")

    # ---------- 7. 正向：报告必须自报范围 ----------
    Judge 'D1' ($text -match '范围：只动') "报告必须自报范围（会动/不动两类行）"

    Remove-Item $out, $err, $o2, ($o2 + ".err") -Force -ErrorAction SilentlyContinue
}
catch {
    Write-Host ("ABORT " + $_.Exception.Message)
    $aborted = 1
}
finally {
    # ★ 先杀子进程、再杀根（顺序反了就会留孤儿 —— 实测踩过）
    $killOrder = @($script:mine)
    for ($i = $killOrder.Count - 1; $i -ge 0; $i--) {
        Stop-Process -Id $killOrder[$i] -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 900

    $left = @(Get-ProbeLeftovers)
    if (@($left).Count -gt 0) {
        Write-Host ("[FAIL] Z1  收尾没清干净：按命令行特征全机复查仍有 " + @($left).Count + " 个替身残留 ⇒ " +
                    (@($left | ForEach-Object { $_.ProcessId }) -join ','))
        Write-Host "        （这正是「只杀根、漏子进程」的形态：cmd 杀了，它的 ping 变孤儿继续跑）"
        foreach ($x in $left) { Stop-Process -Id $x.ProcessId -Force -ErrorAction SilentlyContinue }
        $script:fail++
    } else {
        Write-Host "[OK]   Z1  收尾干净：按命令行特征全机复查，替身残留 0"
        $script:ok++
    }
}

Write-Host ""
Write-Host ("结论：通过 " + $ok + " 项 / 失败 " + $fail + " 项")
Write-Host ("KILLTREE_JUDGE ok=" + $ok + " fail=" + $fail + " aborted=" + $aborted)
if ($fail -gt 0) { exit 1 } else { exit 0 }

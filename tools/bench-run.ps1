# 百万级基准:复制最新构建(排除数据目录)→ 生成/校验 100 万库 + Evidence → 懒回填 → 逐命令计时
# 用法: powershell -ExecutionPolicy Bypass -File tools/bench-run.ps1
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent
$benchDir = 'E:\sip-bench'
$db = "$benchDir\readwithhotsoup\rss.db"

if (Test-Path $benchDir) { Remove-Item $benchDir -Recurse -Force }
New-Item -ItemType Directory -Path $benchDir | Out-Null

# 1) 复制构建产物(用 robocopy 排除 readwithhotsoup,避免开发数据混入/覆盖基准)
robocopy "$root\bin\Debug\net10.0" $benchDir /E /XD readwithhotsoup /NFL /NDL /NJH /NJS /NP | Out-Null

# 2) 建空库 + 生成 100 万篇 + Evidence 相关表
& "$benchDir\sip.exe" --help 2>&1 | Out-Null
node "$root\tools\bench-gen.js" $db

# 3) 首次 grep 触发懒回填(105s 量级)
$out = "$benchDir\bench-out.txt"
cmd /c "`"$benchDir\sip.exe`" --grep 量子计算 --limit 1 --ignoresafeannouncement > `"$out`" 2>&1"

# 4) 基准
function Bench([string]$label, [string]$cmdLine) {
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  cmd /c "`"$benchDir\sip.exe`" $cmdLine > `"$out`" 2>&1"
  $sw.Stop()
  $line = (Get-Content $out -Encoding UTF8 | Select-Object -First 1)
  "{0,-30} {1,8:N2}s   {2}" -f $label, $sw.Elapsed.TotalSeconds, $line
}
"===== RSS 阅读器基准(100 万篇)====="
Bench "grep 量子计算(4字,FTS)" "--grep 量子计算 --limit 5 --ignoresafeannouncement"
Bench "grep 分布式(3字,FTS)" "--grep 分布式 --limit 5 --ignoresafeannouncement"
Bench "grep RAG(3字,FTS)" "--grep RAG --limit 5 --ignoresafeannouncement"
Bench "grep 熊猫(2字,LIKE回退)" "--grep 熊猫 --limit 5 --ignoresafeannouncement"
Bench "-l 1 --limit 20" "-l 1 --limit 20 --ignoresafeannouncement"
Bench "-l 1 全量(1万篇)" "-l 1 --ignoresafeannouncement"
Bench "--today(48h窗口)" "--today --ignoresafeannouncement"
Bench "--dedup scan(48h)" "--dedup scan --ignoresafeannouncement"

""
"===== Evidence 基准(1万条)====="
Bench "ingest list --limit 20" "ingest list --limit 20 --ignoresafeannouncement"
Bench "ingest list 全量" "ingest list --ignoresafeannouncement"
Bench "ingest show 1" "ingest show 1 --ignoresafeannouncement"
Bench "ingest stats" "ingest stats --ignoresafeannouncement"
Bench "ingest tag list" "ingest tag list --ignoresafeannouncement"
Bench "ingest list --tag 标签1" "ingest list --tag 标签1 --ignoresafeannouncement"
Bench "ingest watch list" "ingest watch list --ignoresafeannouncement"
Bench "ingest group --limit 10" "ingest group --limit 10 --ignoresafeannouncement"

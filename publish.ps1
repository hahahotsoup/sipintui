# Build single-file executables for all platforms with unique names (version + arch).
# Usage: powershell -ExecutionPolicy Bypass -File publish.ps1
# Output: publish/<rid>/sip-v<version>-<rid>[.exe] + languages/ (runtime data auto-cleaned)
$ErrorActionPreference = 'Stop'

# 版本号自动读取自 sip.csproj(发布产物带版本,不硬编码)
[xml]$csproj = Get-Content (Join-Path $PSScriptRoot 'sip.csproj')
$version = $csproj.Project.PropertyGroup.Version
if (-not $version) { $version = '0.0.0' }
Write-Host "sip v$version"

$rids = @('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')

# 关键修复:项目目录名含撇号(hahahotsoup's),若直接把 PublishDir 设为项目内 publish/,
# 则 MSBuild 单文件发布的 transform 字符串 '$(PublishDir)%(RelativePath)' 中的撇号会
# 提前闭合单引号 → 被误解析成单个目标路径 → MSB3094(DestinationFiles=1 / SourceFiles=N)。
# 故先发布到无撇号的临时暂存目录($env:TEMP),再整目录拷回项目 publish/。
$stageRoot = Join-Path $env:TEMP 'sip-publish-stage'

foreach ($rid in $rids) {
    Write-Host "==> Publishing $rid ..."
    $stageDir = Join-Path $stageRoot $rid
    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
    dotnet publish -c Release -r $rid --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugSymbols=false -o $stageDir
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }

    # 产物唯一名: sip-v<版本>-<架构>[.exe](不再互相覆盖)
    $isWin = $rid -like 'win*'
    $src = Join-Path $stageDir ($(if ($isWin) { 'sip.exe' } else { 'sip' }))
    $name = "sip-v$version-$rid" + $(if ($isWin) { '.exe' } else { '' })
    Move-Item $src (Join-Path $stageDir $name) -Force

    $outDir = Join-Path $PSScriptRoot "publish/$rid"
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
    # best-effort 清理旧产物(保留 sip-web 等发布件):sip*/sip.exe/sip-v* + 运行时测试数据(readwithhotsoup*/) + 更新残留(_upd_*)
    # 某些沙箱环境的 safe-delete 会拦截显式删除,不应因此中断整个发布
    try {
        Get-ChildItem $outDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'sip*' -and $_.Name -notlike 'sip-web*' } | ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
        Get-ChildItem $outDir -Directory -Filter 'readwithhotsoup*' -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
        Get-ChildItem $outDir -File -Filter '_upd_*' -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
    } catch { Write-Host "  (cleanup skipped: $($_.Exception.Message))" }
    Copy-Item -Path "$stageDir\*" -Destination $outDir -Recurse -Force
    Write-Host "    -> publish/$rid/$name ($([math]::Round((Get-Item (Join-Path $outDir $name)).Length / 1MB, 1)) MB)"
}

Write-Host ''
Write-Host 'Done. Unique-named outputs:'
Get-ChildItem (Join-Path $PSScriptRoot 'publish') -Directory | ForEach-Object {
    $f = Get-ChildItem $_.FullName -File | Where-Object { $_.Name -like 'sip-v*' }
    if ($f) { Write-Host ("  {0,-12} {1}" -f $_.Name, $f.Name) }
}
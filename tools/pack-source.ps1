#Requires -Version 5.1
<#
============================================================================
SwitchCFWizard 源码打包 —— 一键把「可发布的源码树」打成一个 zip

  用法（任选）：
    双击仓库根目录的「打包源码.bat」                        ← 一键
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\pack-source.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\pack-source.ps1 -OpenOutputDir
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\pack-source.ps1 -List

  选项：
    -Out DIR        输出目录（默认 <项目>\publish\source）
    -Name BASE      压缩包主名（默认 SwitchCFWizard-<版本>-src；不带时间戳 = 反复打包覆盖同一个）
    -List           只列出「会打包哪些文件」，不生成 zip
    -OpenOutputDir  打完后打开输出目录

  打包内容 = 项目根下、除「构建产物 / 发布产物 / 运行痕迹 / 编辑器缓存」之外的全部文件。
  排除规则就是下面那三个 $Skip* 列表，没有别的暗规则；结束时会把**实际跳过了什么**打印出来，
  包内的 _SOURCE-INFO.txt 里也留一份。

  为什么要挑着打、而不是直接压整个目录：这个仓库根下同时躺着
  publish/（165 MB 发布产物）、dist/（163 MB 交付目录）、src/**/bin|obj（161 MB 构建中间件）、
  .pkgs/（NuGet 缓存）—— 全压进去会得到一个 300+ MB 的「源码包」，
  而真正的源码只有 80 来个文件、不到 2 MB。

  ⚠️ 本文件含中文，必须存成 **UTF-8 带 BOM**（Windows PowerShell 5.1 没有 BOM 时会按 GBK 解码，
  中文全变乱码，而且**由中文拼出来的路径会直接不存在**）。改完跑一次 scripts 里的规范化即可。
============================================================================
#>

[CmdletBinding()]
param(
    [string] $Out = '',
    [string] $Name = '',
    [switch] $List,
    [switch] $OpenOutputDir
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- 排除规则（唯一的一处）

# 目录名命中即整个跳过（任意层级、大小写不敏感）
$SkipDirNames = @(
    'bin', 'obj', '.pkgs', '__pycache__',             # 构建产物
    'dist', 'publish', 'out', 'download', 'logs',     # 发布 / 运行产物
    '.vs', '.idea', '.git', 'node_modules',           # 编辑器与依赖缓存
    'TestResults',
    '.workbuddy-ai'                                   # AI 工作数据（记忆、备份、截图）
)

# 文件名命中即跳过
$SkipFileNames = @('settings.json', 'Thumbs.db', '.DS_Store', 'desktop.ini')

# 扩展名命中即跳过（.log = 运行日志；.pdb/.cache = 编译中间件漏网的）
$SkipExtensions = @('.log', '.user', '.suo', '.pdb', '.cache', '.tmp')

# ---------------------------------------------------------------- 输出小工具

function Write-Head([string] $Text) { Write-Host ''; Write-Host $Text -ForegroundColor Cyan }
function Write-Ok([string] $Text)   { Write-Host "  [OK] $Text" -ForegroundColor Green }
function Write-Warn([string] $Text) { Write-Host "  [!]  $Text" -ForegroundColor Yellow }
function Write-Fail([string] $Text) { Write-Host "  [X]  $Text" -ForegroundColor Red }
function Write-Note([string] $Text) { Write-Host "       $Text" -ForegroundColor DarkGray }

function Format-Size([long] $Bytes) {
    if ($Bytes -ge 1MB) { return ('{0:N2} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N1} KB' -f ($Bytes / 1KB)) }
    return "$Bytes B"
}

# ---------------------------------------------------------------- 前置检查

function Get-ProjectRoot {
    if ([string]::IsNullOrEmpty($PSScriptRoot)) {
        throw '拿不到脚本所在目录（$PSScriptRoot 为空）—— 请用 -File 方式运行，别把内容粘进控制台。'
    }

    $root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path.TrimEnd('\')
    $marker = Join-Path $root 'src\SwitchCfwWizard\SwitchCfwWizard.csproj'

    if (-not (Test-Path -LiteralPath $marker)) {
        throw "找不到项目根：$root 下没有 src\SwitchCfwWizard\SwitchCfwWizard.csproj —— 本脚本必须放在 <项目>\tools\ 下。"
    }

    return $root
}

function Get-ProjectVersion([string] $Root) {
    $csproj = Join-Path $Root 'src\SwitchCfwWizard\SwitchCfwWizard.csproj'
    $text = Get-Content -LiteralPath $csproj -Raw -Encoding UTF8
    $m = [regex]::Match($text, '<Version>([^<]+)</Version>')

    if ($m.Success) { return $m.Groups[1].Value.Trim() }
    Write-Warn 'csproj 里没有 <Version>，版本号按 0.0.0 记'
    return '0.0.0'
}

# ---------------------------------------------------------------- 收集文件（自己的遍历，不用 Get-ChildItem -Recurse）

function Get-PackFiles([string] $Root, [string] $ExcludeFullPath) {
    $files = New-Object System.Collections.Generic.List[object]
    $skippedDirs = @{}
    $skippedFiles = New-Object System.Collections.Generic.List[string]
    $totalBytes = [long] 0

    $stack = New-Object System.Collections.Stack
    $stack.Push($Root)

    while ($stack.Count -gt 0) {
        $dir = $stack.Pop()

        foreach ($sub in [System.IO.Directory]::GetDirectories($dir)) {
            $dirName = [System.IO.Path]::GetFileName($sub)

            if ($SkipDirNames -contains $dirName) {
                if ($skippedDirs.ContainsKey($dirName)) { $skippedDirs[$dirName]++ }
                else { $skippedDirs[$dirName] = 1 }
                continue
            }

            $stack.Push($sub)
        }

        foreach ($full in [System.IO.Directory]::GetFiles($dir)) {
            $fileName = [System.IO.Path]::GetFileName($full)
            $ext = [System.IO.Path]::GetExtension($fileName).ToLowerInvariant()
            $rel = $full.Substring($Root.Length + 1)

            $skip = ($SkipFileNames -contains $fileName) -or
                    ($SkipExtensions -contains $ext) -or
                    ($full -eq $ExcludeFullPath)

            if ($skip) {
                $skippedFiles.Add($rel)
                continue
            }

            $size = (New-Object System.IO.FileInfo($full)).Length
            $totalBytes += $size
            $files.Add([pscustomobject]@{ Full = $full; Rel = $rel; Size = $size })
        }
    }

    $sorted = $files | Sort-Object -Property Rel

    return [pscustomobject]@{
        Files        = @($sorted)
        SkippedDirs  = $skippedDirs
        SkippedFiles = @($skippedFiles)
        TotalBytes   = $totalBytes
    }
}

# ---------------------------------------------------------------- 覆盖前先确认「文件真的没了」

# ⚠️ 这个环境在删除上叠加了一层安全删除：Remove-Item 可能抛错而文件其实已经不在了。
#    所以判定标准是「Test-Path 说它没了」，不是「命令退出码为 0」。删不掉要**大声报错**，
#    否则会得到一个「看着打完了、其实是上一轮的旧包」的假产物。
function Remove-ExistingFile([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }

    try {
        Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
    }
    catch {
        Write-Warn "常规删除失败（多半是安全删除层），改用 .NET API 重试：$(Split-Path -Leaf $Path)"
        try { [System.IO.File]::Delete($Path) } catch { }
    }

    if (Test-Path -LiteralPath $Path) {
        throw "无法覆盖已存在的文件（可能被压缩软件或杀毒软件占用）：`n  $Path"
    }

    Write-Note "已覆盖旧包：$(Split-Path -Leaf $Path)"
}

# ---------------------------------------------------------------- 主流程

$root = Get-ProjectRoot
$version = Get-ProjectVersion $root

if ([string]::IsNullOrWhiteSpace($Name))        { $Name = "SwitchCFWizard-$version-src" }
if ([string]::IsNullOrWhiteSpace($Out))         { $Out = Join-Path $root 'publish\source' }
elseif (-not [System.IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $root $Out }

$outDir = [System.IO.Path]::GetFullPath($Out)
$zipPath = Join-Path $outDir ($Name + '.zip')
$rootInZip = $Name

Write-Head "SwitchCFWizard 源码打包"
Write-Host "  项目根     $root"
Write-Host "  版本       $version（取自 csproj）"
Write-Host "  输出       $zipPath"

$pack = Get-PackFiles -Root $root -ExcludeFullPath $zipPath

if ($pack.Files.Count -eq 0) {
    throw '一个文件都没收集到 —— 项目根判断错了？'
}

# ---------------------------------------------------------------- -List：只列不打包

if ($List) {
    Write-Head "将要打包的文件（$($pack.Files.Count) 个，共 $(Format-Size $pack.TotalBytes)）"
    foreach ($f in $pack.Files) {
        Write-Host ('  {0,10}  {1}' -f $f.Size, $f.Rel)
    }
    Write-Head '跳过'
    foreach ($k in ($pack.SkippedDirs.Keys | Sort-Object)) {
        Write-Host "  目录 $k/ … $($pack.SkippedDirs[$k]) 处"
    }
    foreach ($f in $pack.SkippedFiles) { Write-Host "  文件 $f" }
    Write-Host ''
    Write-Ok '仅列出，未生成压缩包（去掉 -List 才会真的打包）'
    return
}

# ---------------------------------------------------------------- 组装清单文本

$buildTime = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')

$skippedDirLines = @()
foreach ($k in ($pack.SkippedDirs.Keys | Sort-Object)) {
    $skippedDirLines += ('  {0,-18} {1} 处' -f ($k + '/'), $pack.SkippedDirs[$k])
}
if ($skippedDirLines.Count -eq 0) { $skippedDirLines += '  （无）' }

$skippedFileLines = @()
if ($pack.SkippedFiles.Count -eq 0) {
    $skippedFileLines += '  （无）'
}
else {
    foreach ($f in ($pack.SkippedFiles | Select-Object -First 20)) { $skippedFileLines += "  $f" }
    if ($pack.SkippedFiles.Count -gt 20) {
        $skippedFileLines += "  …另有 $($pack.SkippedFiles.Count - 20) 个"
    }
}

# 顶层目录概览（让人一眼看出包里有什么）
$topSummary = @{}
foreach ($f in $pack.Files) {
    $top = $f.Rel.Split('\')[0]
    if ($f.Rel -notmatch '\\') { $top = '(根目录文件)' }
    if ($topSummary.ContainsKey($top)) { $topSummary[$top]++ } else { $topSummary[$top] = 1 }
}
$topLines = @()
foreach ($k in ($topSummary.Keys | Sort-Object)) {
    $topLines += ('  {0,-22} {1} 个文件' -f $k, $topSummary[$k])
}

$manifest = @()
$manifest += 'SwitchCFWizard 源码包'
$manifest += '=' * 46
$manifest += ''
$manifest += "  版本        $version（取自 src/SwitchCfwWizard/SwitchCfwWizard.csproj）"
$manifest += "  打包时间    $buildTime"
$manifest += '  打包脚本    tools/pack-source.ps1（仓库自带，可重复执行）'
$manifest += "  文件数      $($pack.Files.Count)"
$manifest += "  解压后体积  $(Format-Size $pack.TotalBytes)"
$manifest += "  压缩包      $Name.zip"
$manifest += ''
$manifest += '内容概览'
$manifest += '-' * 46
$manifest += $topLines
$manifest += ''
$manifest += '已排除（打包时跳过，不在本包内）'
$manifest += '-' * 46
$manifest += '  按目录名整块跳过：'
$manifest += $skippedDirLines
$manifest += '  按文件名 / 扩展名跳过：'
$manifest += $skippedFileLines
$manifest += ''
$manifest += '怎么用'
$manifest += '-' * 46
$manifest += '  1. 装 .NET 8 SDK（global.json 里锁了版本；仓库是 WPF 项目，Windows 上才编得动）'
$manifest += '  2. 构建：  bash tools/build.sh --help      # 四种发布形态，含 --verify 自检'
$manifest += '  3. 回归：  dotnet run -c Release --project tools/ConfigSmokeTest'
$manifest += '  4. 自检：  构建出来的 SwitchCfwWizard.exe --selftest'
$manifest += '  详细说明（构建、护栏、历史轮次、界面约定）见 README.md'
$manifest += ''

# ---------------------------------------------------------------- 打包

# ⚠️ 这两个类型在**两个不同的程序集**里：`ZipFile` / `ZipFileExtensions` 在
#    System.IO.Compression.FileSystem，而 `ZipArchiveMode` / `CompressionLevel` 在
#    System.IO.Compression。只加载前者的话，`[ZipArchiveMode]` 不会在这里报错，
#    而是在**用到它的那一行**才炸「找不到类型」。
# ⚠️⚠️ 千万别把这两句包进 try/catch 了事：第一版就是这么写的，于是这条错误被静静吞掉、
#    一路走到 `[System.IO.Compression.ZipFile]::Open(...)` 才失败 —— 典型的本项目最恨的
#    「静默失败」。（顺带说明：显式加载是必要的，别指望 PowerShell 自己按需解析。）
foreach ($assembly in @('System.IO.Compression', 'System.IO.Compression.FileSystem')) {
    Add-Type -AssemblyName $assembly -ErrorAction Stop
}

if (-not (Test-Path -LiteralPath $outDir)) {
    [void](New-Item -ItemType Directory -Path $outDir -Force)
}

Remove-ExistingFile $zipPath

Write-Head '打包中'

$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)

try {
    # 清单先写，保证它在包里的第一项
    $entry = $zip.CreateEntry("$rootInZip/_SOURCE-INFO.txt", [System.IO.Compression.CompressionLevel]::Optimal)
    $stream = $entry.Open()
    $writer = New-Object System.IO.StreamWriter($stream, (New-Object System.Text.UTF8Encoding($true)))
    try { $writer.Write(($manifest -join "`r`n")) } finally { $writer.Dispose() }

    foreach ($f in $pack.Files) {
        $entryName = "$rootInZip/" + ($f.Rel -replace '\\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $f.Full, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally {
    $zip.Dispose()
}

# ---------------------------------------------------------------- 复核（用**另一个**读取路径再打开一次）

$zipBytes = (New-Object System.IO.FileInfo($zipPath)).Length
$entries = @()

$read = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    foreach ($e in $read.Entries) { $entries += $e.FullName }
}
finally {
    $read.Dispose()
}

$problems = New-Object System.Collections.Generic.List[string]

if ($entries.Count -ne $pack.Files.Count + 1) {
    $problems.Add("包内条目数 $($entries.Count) 与预期 $($pack.Files.Count + 1)（含清单）不符")
}

foreach ($bad in @('/bin/', '/obj/', '/.pkgs/', '/publish/', '/dist/', '/.workbuddy-ai/', '/__pycache__/')) {
    $hit = @($entries | Where-Object { $_.Contains($bad) })
    if ($hit.Count -gt 0) { $problems.Add("包内不该出现 $bad（$($hit.Count) 项，例如 $($hit[0])）") }
}

foreach ($must in @(
        "$rootInZip/_SOURCE-INFO.txt",
        "$rootInZip/README.md",
        "$rootInZip/ico.ico",
        "$rootInZip/src/SwitchCfwWizard/SwitchCfwWizard.csproj",
        "$rootInZip/src/SwitchCfwWizard/MainWindow.xaml",
        "$rootInZip/src/SwitchCfwWizard/Localization/Strings.zh-Hans.json",
        "$rootInZip/tools/build.sh",
        "$rootInZip/tools/pack-source.ps1")) {
    if ($entries -notcontains $must) { $problems.Add("包内缺少必须有的文件：$must") }
}

Write-Head '结果'
Write-Host "  文件数     $($pack.Files.Count) 个（包内条目 $($entries.Count)，含 _SOURCE-INFO.txt）"
Write-Host "  解压后     $(Format-Size $pack.TotalBytes)"
Write-Host "  压缩包     $(Format-Size $zipBytes)"
Write-Host "  路径       $zipPath"

if ($pack.SkippedDirs.Count -gt 0) {
    $dirSummary = ($pack.SkippedDirs.Keys | Sort-Object | ForEach-Object { "$_/×$($pack.SkippedDirs[$_])" }) -join ' '
    Write-Note "跳过目录：$dirSummary"
}
Write-Note "跳过文件：$($pack.SkippedFiles.Count) 个（*.log / settings.json / 被占用等）"

if ($problems.Count -gt 0) {
    Write-Head '复核不通过'
    foreach ($p in $problems) { Write-Fail $p }
    throw "打包复核失败，共 $($problems.Count) 个问题（压缩包已生成但不可信，请检查上面的条目）"
}

Write-Ok '复核通过：条目数一致、无构建产物混入、关键文件齐全'

if ($OpenOutputDir) {
    Write-Host ''
    Write-Note '打开输出目录…'
    # ⚠️ 这一步**不许影响结论**：包已经打好、也复核过了。资源管理器没起来（或在无人值守的
    #    环境里根本没有桌面）只该是一句提醒，不能让它把整件事变成「FAILED」。
    try {
        Start-Process -FilePath 'explorer.exe' -ArgumentList $outDir -ErrorAction Stop
    }
    catch {
        Write-Warn "没能自动打开目录（不影响打包结果）：$($_.Exception.Message)"
    }
}

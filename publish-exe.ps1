<#
.SYNOPSIS
把当前目录下的 .cs（基于文件的应用）发布成 exe，并检查 exe 的完整性级别。

.DESCRIPTION
工作区目录带 Windows「低完整性」标签（Low Mandatory Level，(OI)(CI) 向下继承），
在里面 dotnet publish 出来的 exe 会继承该标签，进程按低完整性启动后就写不了普通文件
（例如授权时写 access-token.config.json 会报 Access to the path is denied）。

新文件的完整性级别 = min(发布进程的完整性级别, 输出目录的完整性级别)，
所以这个脚本做三件事：
  1. 默认把 exe 发布到 %USERPROFILE%\bin（工作区之外）；
  2. 发布后逐个用 icacls 检查 exe 的完整性级别；
  3. 级别不对时打印需要在管理员终端执行的 icacls 命令（脚本不自己提权），
     并区分「终端本身是低完整性」和「输出目录带低标签」两种情况。

.PARAMETER OutDir
发布输出目录，默认为 %USERPROFILE%\bin。

.PARAMETER SkipPublish
只检查已有 exe 的完整性级别，不重新发布。

.PARAMETER Name
只处理指定的脚本（不含 .cs），可以传多个，例如 -Name calibre-backup,pack-epub。

.PARAMETER NoCopyBack
不把 exe 复制回当前目录。默认是复制回来的（覆盖同名文件），方便在当前目录直接运行。

复制回来的完整性级别取决于目标文件原来存不存在：
  * 覆盖已存在的同名文件 —— CopyFile 是截断写入不是删除重建，原有安全描述符（含完整性
    标签）会保留，所以本来就是 Medium 的文件复制后仍然是 Medium；
  * 目标不存在（新建）—— 新文件继承当前目录的低完整性标签，变成 Low。
所以复制完会重新检查一遍，是 Low 的会打印需要在【管理员】终端执行的 icacls 命令。

.EXAMPLE
.\publish-exe.ps1

.EXAMPLE
.\publish-exe.ps1 -SkipPublish

.EXAMPLE
.\publish-exe.ps1 -Name calibre-backup -OutDir D:\tools

.EXAMPLE
.\publish-exe.ps1 -NoCopyBack

.NOTES
本文件必须保存成「UTF-8 with BOM」。Windows PowerShell 5.1 读取没有 BOM 的脚本时会按
ANSI(GBK) 解码，中文注释和字符串会被打乱，直接报语法错误（大量 "Unexpected token"）。
有些编辑器/工具会悄悄去掉 BOM，重新加回来：

    $p = '.\publish-exe.ps1'
    $t = [IO.File]::ReadAllText((Resolve-Path $p), [Text.Encoding]::UTF8)
    [IO.File]::WriteAllText((Resolve-Path $p), $t, (New-Object Text.UTF8Encoding($true)))

如果直接 `.\publish-exe.ps1` 报「未对文件进行数字签名 / cannot be loaded」，
说明本机对脚本的限制比执行策略更严（`Get-ExecutionPolicy` 显示 RemoteSigned 也会被拒，
`-ExecutionPolicy Unrestricted` 同样不行，只有 `Bypass` 能跑）。此时改用同目录的
`publish-exe.cmd`（内部固定带 `-ExecutionPolicy Bypass`），或者手动：

    powershell -NoProfile -ExecutionPolicy Bypass -File .\publish-exe.ps1
#>
[CmdletBinding()]
param(
	[string]$OutDir = (Join-Path $env:USERPROFILE 'bin'),
	[switch]$SkipPublish,
	[switch]$NoCopyBack,
	[string[]]$Name
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# 工具函数
# ---------------------------------------------------------------------------

# 读取指定文件的完整性级别；没有显式标签时按「中」处理。
function Get-IntegrityLevel {
	param([Parameter(Mandatory)][string]$Path)

	$text = (& icacls $Path 2>&1) -join "`n"
	if ($LASTEXITCODE -ne 0) {
		return 'Unknown'
	}
	if ($text -match 'Mandatory Label\\Low Mandatory Level') {
		return 'Low'
	}
	if ($text -match 'Mandatory Label\\High Mandatory Level') {
		return 'High'
	}
	return 'Medium'
}

# 读取当前终端的完整性级别（用 SID 判断，避免中文系统下匹配本地化文本）。
function Get-CurrentIntegrityLevel {
	$groups = & whoami /groups /fo csv 2>$null
	if ($groups) {
		foreach ($line in $groups) {
			if ($line -match 'S-1-16-4096') { return 'Low' }      # 低
			if ($line -match 'S-1-16-12288') { return 'High' }    # 高
			if ($line -match 'S-1-16-16384') { return 'System' }  # 系统
		}
	}
	return 'Medium'
}

# 判断路径是否在当前目录（工作区）下面。
function Test-InsideWorkspace {
	param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Root)

	$full = [System.IO.Path]::GetFullPath($Path)
	$rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + '\'
	return $full.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)
}

# ---------------------------------------------------------------------------
# 准备
# ---------------------------------------------------------------------------

$sourceDir = (Get-Location).Path
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
	Write-Host '找不到 dotnet 命令，请先安装 .NET SDK。' -ForegroundColor Red
	exit 1
}

$files = @(Get-ChildItem -Path $sourceDir -Filter '*.cs' -File | Sort-Object Name)
if ($Name) {
	# 用 .cmd / powershell -File 调用时，逗号分隔的列表会被当成一个字符串，这里拆开。
	$nameList = @($Name | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | ForEach-Object { $_.Trim() })
	$files = @($files | Where-Object { $nameList -contains $_.BaseName })
}
if ($files.Count -eq 0) {
	Write-Host "在 $sourceDir 下没有找到要处理的 .cs 文件。" -ForegroundColor Red
	exit 1
}

if (-not $SkipPublish -and -not (Test-Path -LiteralPath $OutDir)) {
	New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}

$outDirFull = [System.IO.Path]::GetFullPath($OutDir)
$selfLevel = Get-CurrentIntegrityLevel
Write-Host "脚本目录:     $sourceDir"
Write-Host "输出目录:     $outDirFull"
Write-Host "复制回:       $(if ($NoCopyBack) { '不复制（-NoCopyBack）' } else { $sourceDir })"
Write-Host "当前终端:     $selfLevel 完整性"
if ($selfLevel -eq 'Low') {
	Write-Host '警告: 当前终端本身就是「低完整性」，发布出来的 exe 一定是 Low，' -ForegroundColor Yellow
	Write-Host '      请在普通的 PowerShell 窗口（不是受限/沙箱环境）里重新运行本脚本。' -ForegroundColor Yellow
}
if (-not $SkipPublish -and (Test-InsideWorkspace -Path $outDirFull -Root $sourceDir)) {
	Write-Host '警告: 输出目录在工作区内（工作区带低完整性标签），exe 会继承 Low。' -ForegroundColor Yellow
	Write-Host '      建议改用默认输出目录（%USERPROFILE%\bin）。' -ForegroundColor Yellow
}
Write-Host ''

# ---------------------------------------------------------------------------
# 发布 + 检查
# ---------------------------------------------------------------------------

$results = foreach ($file in $files) {
	$exePath = Join-Path $outDirFull ($file.BaseName + '.exe')
	$publishError = $null

	if (-not $SkipPublish) {
		Write-Host ("发布 {0} …" -f $file.Name) -NoNewline
		$watch = [System.Diagnostics.Stopwatch]::StartNew()
		$output = & dotnet publish $file.FullName -o $outDirFull --nologo -v m 2>&1
		$watch.Stop()
		if ($LASTEXITCODE -eq 0) {
			Write-Host (" 完成（{0:0.0}s）" -f $watch.Elapsed.TotalSeconds) -ForegroundColor Green
		}
		else {
			Write-Host ' 失败' -ForegroundColor Red
			$publishError = ($output | Out-String).Trim()
			Write-Host $publishError -ForegroundColor DarkGray
		}
	}

	$exists = Test-Path -LiteralPath $exePath
	[pscustomobject]@{
		Script     = $file.BaseName
		Exe        = $exePath
		Exists     = $exists
		Level      = if ($exists) { Get-IntegrityLevel -Path $exePath } else { 'Missing' }
		PublishErr = $publishError
	}
}

# ---------------------------------------------------------------------------
# 复制回当前目录（默认；-NoCopyBack 关闭）
# ---------------------------------------------------------------------------

$report = $results
if (-not $NoCopyBack) {
	$report = foreach ($item in $results) {
		if (-not $item.Exists) {
			$item
			continue
		}
		$dest = Join-Path $sourceDir (Split-Path -Leaf $item.Exe)
		# 输出目录就是当前目录时不需要复制。
		if ([System.IO.Path]::GetFullPath($dest) -eq [System.IO.Path]::GetFullPath($item.Exe)) {
			$item
			continue
		}
		# 目标是否已存在决定复制后的完整性级别：覆盖保留原有标签，新建会继承当前目录的低标签。
		$existed = Test-Path -LiteralPath $dest
		$copyError = $null
		try {
			Copy-Item -LiteralPath $item.Exe -Destination $dest -Force -ErrorAction Stop
		}
		catch {
			$copyError = $_.Exception.Message
		}
		$exists = Test-Path -LiteralPath $dest
		[pscustomobject]@{
			Script     = $item.Script
			Exe        = $dest
			Existed    = $existed
			Exists     = $exists
			Level      = if ($exists) { Get-IntegrityLevel -Path $dest } else { 'Missing' }
			PublishErr = $item.PublishErr
			CopyErr    = $copyError
		}
	}
}

# ---------------------------------------------------------------------------
# 结果
# ---------------------------------------------------------------------------

Write-Host ''
$props = @(
	'Script',
	@{ Name = '完整性'; Expression = { $_.Level } },
	@{ Name = '状态'; Expression = {
		if ($_.CopyErr) { '复制失败' }
		elseif (-not $_.Exists) { '缺少 exe' }
		elseif ($_.Level -eq 'Low') { '需要修正' }
		elseif ($_.Level -eq 'Unknown') { '无法判断' }
		else { 'OK' }
	} },
	@{ Name = '大小(KB)'; Expression = {
		if ($_.Exists) { [math]::Round((Get-Item -LiteralPath $_.Exe).Length / 1KB) } else { 0 }
	} }
)
if (-not $NoCopyBack) {
	$props += @{ Name = '覆盖已有'; Expression = {
		if (-not $_.Exists) { '-' }
		elseif ($_.Existed) { '是' }
		else { '否（新建）' }
	} }
}
$props += 'Exe'
$report | Select-Object $props | Format-Table -AutoSize | Out-String | Write-Host

$low = @($report | Where-Object { $_.Exists -and $_.Level -eq 'Low' })
$missing = @($report | Where-Object { -not $_.Exists })
$failed = @($report | Where-Object { $_.PublishErr -or $_.CopyErr })

if ($low.Count -gt 0) {
	Write-Host '以下 exe 的完整性级别是「低」，运行时写不了书库和配置文件：' -ForegroundColor Yellow
	foreach ($item in $low) {
		Write-Host ('    {0}  ({1})' -f $item.Script, $item.Exe)
	}
	Write-Host ''

	if ($selfLevel -eq 'Low') {
		Write-Host '原因: 当前终端是低完整性（受限/沙箱环境），发布/复制出来的 exe 必然也是 Low。' -ForegroundColor Yellow
		Write-Host '      请在普通的 PowerShell 窗口里重新运行本脚本，通常就不需要下面这些命令。' -ForegroundColor Yellow
		Write-Host ''
	}
	elseif (-not $NoCopyBack) {
		$created = @($low | Where-Object { -not $_.Existed })
		if ($created.Count -gt 0) {
			Write-Host ('原因: 这些是【新建】的文件（当前目录里原本没有同名 exe），会继承工作区的低完整性标签：{0}' -f
				(($created | ForEach-Object { $_.Script }) -join '、')) -ForegroundColor Yellow
			Write-Host '      覆盖已存在的同名文件才会保留原有（Medium）标签。' -ForegroundColor Yellow
			Write-Host ''
		}
		$kept = @($low | Where-Object { $_.Existed })
		if ($kept.Count -gt 0) {
			Write-Host ('原因: 覆盖已有文件后仍是低完整性，说明原来的同名文件就不是 Medium：{0}' -f
				(($kept | ForEach-Object { $_.Script }) -join '、')) -ForegroundColor Yellow
			Write-Host ''
		}
	}
	elseif (-not $SkipPublish -and (Test-InsideWorkspace -Path $outDirFull -Root $sourceDir)) {
		Write-Host '原因: 输出目录在工作区内，带低完整性标签。建议用默认的 %USERPROFILE%\bin 重新发布。' -ForegroundColor Yellow
		Write-Host ''
	}

	Write-Host '手动修正（需要【管理员】PowerShell），逐条执行：' -ForegroundColor Yellow
	Write-Host ''
	foreach ($item in $low) {
		Write-Host ('    icacls "{0}" /setintegritylevel Medium' -f $item.Exe)
	}
	Write-Host ''
	Write-Host '执行完复查（应该看不到 Low Mandatory Level）：'
	Write-Host ('    icacls "{0}" | findstr /i Mandatory' -f $low[0].Exe)
	Write-Host ''
}

if ($failed.Count -gt 0) {
	foreach ($item in $failed) {
		if ($item.CopyErr) {
			Write-Host ('复制失败: {0} → {1}: {2}' -f $item.Script, $item.Exe, $item.CopyErr) -ForegroundColor Red
		}
	}
	$publishFailed = @($failed | Where-Object { $_.PublishErr })
	if ($publishFailed.Count -gt 0) {
		Write-Host ('发布失败: {0}' -f (($publishFailed | ForEach-Object { $_.Script }) -join '、')) -ForegroundColor Red
	}
}
if ($missing.Count -gt 0) {
	Write-Host ('找不到 exe: {0}（{1}）' -f (($missing | ForEach-Object { $_.Script }) -join '、'),
		$(if ($SkipPublish) { '去掉 -SkipPublish 重新发布' } else { '见上面的发布错误信息' })) -ForegroundColor Red
}

if ($low.Count -eq 0 -and $missing.Count -eq 0 -and $failed.Count -eq 0) {
	Write-Host ('全部完成：exe 的完整性级别检查通过{0}。' -f
		$(if ($NoCopyBack) { '' } else { '（已复制回当前目录）' })) -ForegroundColor Green
	exit 0
}

exit 1

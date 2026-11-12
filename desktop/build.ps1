# 编译 WPF 版控制台（只用 .NET Framework 自带程序集，不需要装 SDK）
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $dir '肥鱼控制台WPF.exe'

# WPF 程序集在 GAC 里，用完整路径引用
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF'
$refs = @(
  "$fw\PresentationFramework.dll",
  "$fw\PresentationCore.dll",
  "$fw\WindowsBase.dll",
  'System.Xaml.dll',
  'System.Windows.Forms.dll',
  'System.Drawing.dll',
  'System.Web.Extensions.dll',
  'System.Core.dll'
)
$refArgs = $refs | ForEach-Object { '/r:' + $_ }

# 图标：/win32icon 决定 exe 文件图标 + 任务栏图标；/resource 让托盘能按资源名取指定尺寸
# （以前两样都没传，所以 exe 是默认白纸图标、托盘退回 SystemIcons.Application）
$ico = Join-Path $dir 'fish_icon.ico'
$iconArgs = @()
if (Test-Path $ico) {
  $iconArgs += '/win32icon:' + $ico
  $iconArgs += '/resource:' + $ico + ',fish.ico'
} else {
  Write-Host '警告：找不到 fish_icon.ico，exe 和托盘都会用系统默认图标'
}

Write-Host '编译中…'
& $csc /nologo /target:winexe /platform:x64 /out:$out @refArgs @iconArgs (Join-Path $dir 'FishBotConsole.cs') 2>&1 |
  ForEach-Object { $_ } | Where-Object { $_ -match 'error|警告' }
if ($LASTEXITCODE -ne 0) { throw "编译失败 (exit $LASTEXITCODE)" }
Write-Host ("OK -> $out  " + [math]::Round((Get-Item $out).Length / 1KB, 1) + " KB")

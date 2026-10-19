# 编译电脑端控制台（不需要安装任何东西，用 Windows 自带 .NET Framework 的 csc）
# 用法：powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Definition
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "找不到 csc.exe（需要 .NET Framework 4.0+）" }

$out = Join-Path $here "肥鱼机器人开关.exe"
& $csc /nologo /target:winexe /platform:anycpu /optimize+ `
    /out:"$out" `
    /win32icon:"$here\fish_icon.ico" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll `
    /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
    "$here\FishBotConsole.cs"

if ($LASTEXITCODE -eq 0) {
    Write-Host "OK -> $out" -ForegroundColor Green
    Write-Host "运行前请设置环境变量 FATBOT_CONSOLE_TOKEN（后端 token）" -ForegroundColor Yellow
} else {
    throw "编译失败，退出码 $LASTEXITCODE"
}

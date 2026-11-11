<#
.SYNOPSIS
  fatbot 一键部署向导（在**你自己的 Windows 电脑**上跑）。

.DESCRIPTION
  填几个空 → 它把 deploy/ 里的一整套东西传到你的云服务器 → 在服务器上装好
  nonebot2 + 插件 + systemd 服务 → 打印控制台地址，并写好本机控制台的连接配置。

  不带参数直接双击运行 = 图形界面；带参数 = 命令行（CI/脚本用）。

.PARAMETER Server
  云服务器 IP 或域名。

.PARAMETER SshUser
  SSH 用户名（腾讯云/阿里云 Ubuntu 镜像一般是 ubuntu 或 root）。

.PARAMETER SshPassword
  SSH 密码。用密钥登录可以不填，改用 -KeyFile。

.PARAMETER Qq
  机器人自己的 QQ 号（要能被扫码登录的那个号）。

.PARAMETER Admin
  超管 QQ（默认 = 机器人 QQ）。

.PARAMETER AiBase / AiModel / AiKey
  文字模型渠道，可以都不填，之后在控制台「API 渠道」里配。

.PARAMETER Installed
  装完把控制台连接信息写到 console-config.json（默认就写）。

.EXAMPLE
  pwsh -File setup.ps1
  pwsh -File setup.ps1 -Server 1.2.3.4 -SshUser ubuntu -SshPassword xxx -Qq 123456789 -NoGui
#>
[CmdletBinding()]
param(
  # ⚠️ 别叫 $Host：PowerShell 里 $Host 是只读的自动变量，用它当参数名会直接报
  #    "Cannot overwrite variable Host because it is read-only or constant"
  [Alias('Host')]
  [string]$Server = "",
  [string]$SshUser = "ubuntu",
  [string]$SshPassword = "",
  [string]$KeyFile = "",
  [string]$Qq = "",
  [string]$Admin = "",
  [string]$Token = "",
  [string]$AiBase = "",
  [string]$AiModel = "",
  [string]$AiKey = "",
  [string]$Ws = "",
  [int]$Port = 8090,
  [int]$SshPort = 22,
  [string]$Name = "fishbot",
  [string]$Prefix = "",
  [switch]$NoSystemd,
  [switch]$WithNapcat,
  [switch]$NoGui,
  [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$script:LogBox = $null

# 服务器那边输出的是 UTF-8；Windows 默认按 GBK 解码就是一堆乱码（"锛?裝"那种）。
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
try { $OutputEncoding = [Text.Encoding]::UTF8 } catch { }

function Clean-Ansi([string]$text) {
  if (-not $text) { return '' }
  return ($text -replace "`e\[[0-9;]*m", '')
}

function Write-Log([string]$msg, [string]$color = 'Gray') {
  $line = (Get-Date -Format 'HH:mm:ss') + '  ' + $msg
  if ($script:LogBox) {
    $script:LogBox.SelectionColor = switch ($color) {
      'green' { [Drawing.Color]::FromArgb(120, 220, 140) }
      'red'   { [Drawing.Color]::FromArgb(240, 120, 120) }
      'yellow'{ [Drawing.Color]::FromArgb(240, 200, 120) }
      'cyan'  { [Drawing.Color]::FromArgb(120, 200, 240) }
      default { [Drawing.Color]::FromArgb(210, 214, 220) }
    }
    $script:LogBox.AppendText($line + "`r`n")
    $script:LogBox.SelectionStart = $script:LogBox.TextLength
    $script:LogBox.ScrollToCaret()
    [Windows.Forms.Application]::DoEvents()
  } else {
    Write-Host $line -ForegroundColor $color
  }
}

function Get-PlinkOrSsh {
  # 优先 plink（能直接用密码，Windows 自带 ssh 不吃密码），没 plink 就用 ssh（要密钥）
  $plink = Get-Command plink -ErrorAction SilentlyContinue
  if ($plink) { return @{ kind = 'plink'; path = $plink.Source } }
  foreach ($p in @("$env:TEMP\comfy_plink.exe", "C:\Program Files\PuTTY\plink.exe")) {
    if (Test-Path $p) { return @{ kind = 'plink'; path = $p } }
  }
  $ssh = Get-Command ssh -ErrorAction SilentlyContinue
  if ($ssh) { return @{ kind = 'ssh'; path = $ssh.Source } }
  throw "既没有 plink 也没有 ssh。装一个 PuTTY（含 plink）或开 Windows 的 OpenSSH 客户端。"
}

function New-SshRunner {
  param($HostName, $User, $Password, $Key, $PortNum, $Tool)
  $script:Ssh = @{ host = $HostName; user = $User; pass = $Password; key = $Key; port = $PortNum; tool = $Tool }
}

function Invoke-Remote {
  param([string]$Command, [string]$StdinFile = "")
  $s = $script:Ssh
  if ($s.tool.kind -eq 'plink') {
    $args = @('-ssh', '-batch', '-P', "$($s.port)", '-l', $s.user)
    if ($s.pass) { $args += @('-pw', $s.pass) }
    elseif ($s.key) { $args += @('-i', $s.key) }
    if ($s.hostkey) { $args += @('-hostkey', $s.hostkey) }
    $args += @($s.host, $Command)
    if ($StdinFile) {
      # PowerShell 没有 `< file` 这种输入重定向；`Get-Content -Raw | exe` 等价于把文件喂给 stdin
      $out = Get-Content -Raw -LiteralPath $StdinFile | & $s.tool.path @args 2>&1
    } else {
      $out = & $s.tool.path @args 2>&1
    }
  } else {
    $base = @('-p', "$($s.port)", '-o', 'StrictHostKeyChecking=no', '-o', 'UserKnownHostsFile=/dev/null')
    if ($s.key) { $base += @('-i', $s.key) }
    $args = $base + @("$($s.user)@$($s.host)", $Command)
    if ($StdinFile) { $out = Get-Content -Raw -LiteralPath $StdinFile | & $s.tool.path @args 2>&1 }
    else { $out = & $s.tool.path @args 2>&1 }
  }
  $script:LastExit = $LASTEXITCODE
  return ($out | Out-String)
}

function Register-HostKey {
  # 首次连接必须在 `-batch` 下拿到主机指纹，否则 plink 直接放弃：
  #   "Cannot confirm a host key in batch mode"
  # 做法：先不带 -hostkey 跑一次 `exit`（必然失败），从**报错里**把 SHA256 指纹抠出来，
  # 之后所有调用都带 `-hostkey <那个指纹>` —— 全程无交互，也不用改注册表。
  $s = $script:Ssh
  if (-not $s -or $s.tool.kind -ne 'plink' -or $s.hostkey) { return }
  try {
    $a = @('-ssh', '-batch', '-P', "$($s.port)", '-l', $s.user)
    if ($s.pass) { $a += @('-pw', $s.pass) }
    elseif ($s.key) { $a += @('-i', $s.key) }
    $a += @($s.host, 'exit')
    $probe = & $s.tool.path @a 2>&1 | Out-String
    if ($probe -match '(SHA256:[A-Za-z0-9+/=]{20,})') {
      $s.hostkey = $Matches[1]
      Write-Log "拿到主机指纹：$($s.hostkey)" gray
      return
    }
    if ($LASTEXITCODE -eq 0) {
      Write-Log "主机指纹已缓存，直接用" gray
      return
    }
    Write-Log "取指纹没成功：$($probe.Trim() -split "`n" | Select-Object -First 2)" yellow
  } catch {
    Write-Log "取主机指纹出错：$($_.Exception.Message)" yellow
  }
}

function Test-TunnelReady([string]$base) {
  try {
    $r = Invoke-WebRequest -Uri $base -TimeoutSec 8 -UseBasicParsing
    return $r.StatusCode -eq 200
  } catch { return $false }
}

# ---------------------------------------------------------------- 图形界面
function Show-Wizard {
  Add-Type -AssemblyName System.Windows.Forms, System.Drawing

  $form = New-Object Windows.Forms.Form
  $form.Text = 'fatbot 部署向导'
  $form.Size = New-Object Drawing.Size(760, 720)
  $form.StartPosition = 'CenterScreen'
  $form.BackColor = [Drawing.Color]::FromArgb(24, 26, 30)
  $form.ForeColor = [Drawing.Color]::FromArgb(230, 234, 240)
  $form.Font = New-Object Drawing.Font('Microsoft YaHei UI', 9.5)

  $y = 14
  function Add-Row([string]$label, [string]$value, [bool]$secret = $false, [string]$hint = '') {
    $lb = New-Object Windows.Forms.Label
    $lb.Text = $label; $lb.Location = New-Object Drawing.Point(18, ($y + 4)); $lb.Size = New-Object Drawing.Size(120, 22)
    $lb.ForeColor = [Drawing.Color]::FromArgb(170, 178, 190)
    $form.Controls.Add($lb)
    $tb = New-Object Windows.Forms.TextBox
    $tb.Text = $value; $tb.Location = New-Object Drawing.Point(140, $y); $tb.Size = New-Object Drawing.Size(320, 26)
    $tb.BackColor = [Drawing.Color]::FromArgb(38, 41, 46); $tb.ForeColor = $form.ForeColor; $tb.BorderStyle = 'FixedSingle'
    if ($secret) { $tb.UseSystemPasswordChar = $true }
    $form.Controls.Add($tb)
    if ($hint) {
      $hb = New-Object Windows.Forms.Label
      $hb.Text = $hint; $hb.Location = New-Object Drawing.Point(468, ($y + 4)); $hb.Size = New-Object Drawing.Size(260, 22)
      $hb.ForeColor = [Drawing.Color]::FromArgb(130, 138, 150)
      $form.Controls.Add($hb)
    }
    $script:y = $y + 34
    return $tb
  }

  $y = 14
  $tbHost = Add-Row '服务器 IP' '' $false '云服务器的公网 IP'
  $y += 34
  $tbUser = Add-Row 'SSH 用户名' 'ubuntu' $false '腾讯云/阿里云一般 ubuntu 或 root'
  $y += 34
  $tbPass = Add-Row 'SSH 密码' '' $true '用密钥登录就留空'
  $y += 34
  $tbKey = Add-Row 'SSH 私钥' '' $false '如 C:\Users\你\.ssh\id_rsa（可空）'
  $y += 34
  $tbQq = Add-Row '机器人 QQ 号' '' $false '要扫码登录的那个号'
  $y += 34
  $tbAdmin = Add-Row '超管 QQ' '' $false '留空 = 同机器人 QQ'
  $y += 34
  $tbToken = Add-Row '控制台口令' ((Get-Random -Count 16 -InputObject ([char[]]('abcdefghijkmnpqrstuvwxyz23456789' -split ''))) -join '') $false '记下来，打开控制台要用'
  $y += 34
  $tbAiBase = Add-Row 'AI 地址' '' $false '可空，如 https://api.deepseek.com'
  $y += 34
  $tbAiModel = Add-Row 'AI 模型' '' $false '可空，如 deepseek-chat'
  $y += 34
  $tbAiKey = Add-Row 'AI Key' '' $true '可空，之后控制台里也能配'
  $y += 34
  $cbNapcat = New-Object Windows.Forms.CheckBox
  $cbNapcat.Text = '顺便装 NapCat（Docker，协议端）'
  $cbNapcat.Location = New-Object Drawing.Point(140, $y); $cbNapcat.Size = New-Object Drawing.Size(400, 24)
  $cbNapcat.ForeColor = $form.ForeColor
  $form.Controls.Add($cbNapcat)
  $y += 34

  $btn = New-Object Windows.Forms.Button
  $btn.Text = '开始部署'
  $btn.Location = New-Object Drawing.Point(140, $y); $btn.Size = New-Object Drawing.Size(320, 40)
  $btn.BackColor = [Drawing.Color]::FromArgb(74, 132, 240); $btn.ForeColor = [Drawing.Color]::White
  $btn.FlatStyle = 'Flat'; $btn.Font = New-Object Drawing.Font('Microsoft YaHei UI', 11, [Drawing.FontStyle]::Bold)
  $form.Controls.Add($btn)
  $y += 52

  $log = New-Object Windows.Forms.RichTextBox
  $log.Location = New-Object Drawing.Point(18, $y); $log.Size = New-Object Drawing.Size(706, 240)
  $log.BackColor = [Drawing.Color]::FromArgb(16, 18, 21); $log.ForeColor = $form.ForeColor
  $log.Font = New-Object Drawing.Font('Consolas', 9); $log.ReadOnly = $true; $log.BorderStyle = 'None'
  $form.Controls.Add($log)
  $script:LogBox = $log

  $script:Result = $null
  $btn.Add_Click({
    if (-not $tbHost.Text.Trim()) { [Windows.Forms.MessageBox]::Show('先填服务器 IP'); return }
    if (-not $tbQq.Text.Trim())   { [Windows.Forms.MessageBox]::Show('先填机器人 QQ 号'); return }
    $btn.Enabled = $false
    try {
      $script:Result = Invoke-Deploy -HostName $tbHost.Text.Trim() -User $tbUser.Text.Trim() `
        -Password $tbPass.Text -Key $tbKey.Text.Trim() -Qq $tbQq.Text.Trim() `
        -Admin $tbAdmin.Text.Trim() -Token $tbToken.Text.Trim() `
        -AiBase $tbAiBase.Text.Trim() -AiModel $tbAiModel.Text.Trim() -AiKey $tbAiKey.Text `
        -WithNapcat $cbNapcat.Checked
      if ($script:Result.ok) {
        [Windows.Forms.MessageBox]::Show("装好了！`n`n控制台：`n$($script:Result.url)`n`n配置已写到 console-config.json", '成功')
      } else {
        [Windows.Forms.MessageBox]::Show("没成功：$($script:Result.error)", '失败')
      }
    } catch {
      Write-Log "出错了：$($_.Exception.Message)" red
      [Windows.Forms.MessageBox]::Show($_.Exception.Message, '失败')
    } finally { $btn.Enabled = $true }
  })

  [void]$form.ShowDialog()
}

# ---------------------------------------------------------------- 部署主体
function Invoke-Deploy {
  param($HostName, $User, $Password, $Key, $Qq, $Admin, $Token, $AiBase, $AiModel, $AiKey, $WithNapcat)

  $tool = Get-PlinkOrSsh
  New-SshRunner -HostName $HostName -User $User -Password $Password -Key $Key -PortNum $SshPort -Tool $tool
  Register-HostKey
  Write-Log "连接 ${User}@${HostName}:${SshPort}（$($tool.kind)）" cyan

  $who = Invoke-Remote 'id -un && uname -sr'
  if ($script:LastExit -ne 0) { throw "连不上服务器：$($who.Trim())" }
  Write-Log "服务器：$($who.Trim() -replace "`n", ' / ')" green

  # 1) 打包 deploy 目录（install.sh + app.py + static）
  # 注意：$MyInvocation 在**函数里**指向函数调用，拿不到脚本路径，得用 $PSScriptRoot
  $deployDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $PSCommandPath }
  $zip = Join-Path $env:TEMP ("fatbot-deploy-" + [guid]::NewGuid().ToString('N').Substring(0, 8) + ".zip")
  $stage = Join-Path $env:TEMP ("fatbot-stage-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
  New-Item -ItemType Directory -Force -Path $stage | Out-Null
# install.sh 必须是 LF：Windows 上编辑过会变 CRLF，bash 会报 `$'\r': command not found`
  $shRaw = [IO.File]::ReadAllText((Join-Path $deployDir 'install.sh'))
  $shRaw = $shRaw -replace "`r`n", "`n"          # CRLF->LF
  [IO.File]::WriteAllText((Join-Path $stage 'install.sh'), $shRaw, (New-Object Text.UTF8Encoding($false)))
  $appPy = Join-Path (Split-Path -Parent $deployDir) 'server\app.py'
  if (Test-Path $appPy) {
    Copy-Item $appPy $stage -Force
    $static = Join-Path (Split-Path -Parent $deployDir) 'server\static'
    if (Test-Path $static) { Copy-Item $static $stage -Recurse -Force }
  }
  Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
  Write-Log ("打包完成：" + [math]::Round((Get-Item $zip).Length / 1KB, 1) + " KB") gray

  # 2) 上传（base64 走 stdin，避开 scp 依赖）
  # 上传：**不靠 stdin 管道**（PowerShell 把大字符串喂给原生 stdin 会打断长行，
  # ssh 那边就报 `base64: invalid input`）。改成把 base64 切成小段、每段一次 `echo -n >> file`。
  $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($zip))
  $chunk = 3000
  $null = Invoke-Remote 'rm -f /tmp/fatbot.b64'
  $okUp = $true
  for ($i = 0; $i -lt $b64.Length; $i += $chunk) {
    $part = $b64.Substring($i, [Math]::Min($chunk, $b64.Length - $i))
    $r = Invoke-Remote "printf '%s' '$part' >> /tmp/fatbot.b64 && echo CHUNK_OK"
    if ($r -notmatch 'CHUNK_OK') { $okUp = $false; Write-Log "第 $($i / $chunk + 1) 段上传失败：$($r.Trim())" red; break }
  }
  if (-not $okUp) { throw "上传失败（分块写入没成功）" }
  $up = Invoke-Remote 'base64 -d /tmp/fatbot.b64 > /tmp/fatbot.zip && rm -f /tmp/fatbot.b64 && ls -l /tmp/fatbot.zip && echo UPLOAD_OK'
  if ($up -notmatch 'UPLOAD_OK') { throw "上传失败：$up" }
  Write-Log "已上传到服务器 /tmp/fatbot.zip（$([math]::Round($b64.Length/1KB,1)) KB base64）" green

  $unz = Invoke-Remote 'rm -rf /tmp/fatbot && mkdir -p /tmp/fatbot && cd /tmp/fatbot && python3 -m zipfile -e /tmp/fatbot.zip . && ls'
  Write-Log "解包：$($unz.Trim() -replace "`n", ' ')" gray

  # 3) 组装参数
  $args = @('--qq', $Qq, '--port', "$Port", '--name', $Name)
  if ($Prefix) { $args += @('--prefix', $Prefix) }
  if ($NoSystemd) { $args += '--no-systemd' }
  if ($Admin)   { $args += @('--admin', $Admin) }
  if ($Token)   { $args += @('--token', $Token) }
  if ($AiBase)  { $args += @('--ai-base', $AiBase) }
  if ($AiModel) { $args += @('--ai-model', $AiModel) }
  if ($AiKey)   { $args += @('--ai-key', $AiKey) }
  if ($Ws)      { $args += @('--ws', $Ws) }
  if ($WithNapcat) { $args += '--with-napcat' }
  $quoted = ($args | ForEach-Object { "'" + ($_ -replace "'", "'\''") + "'" }) -join ' '

  $canSudo = (Invoke-Remote 'sudo -n true && echo SUDO_OK') -match 'SUDO_OK'
  $runner = if ($canSudo) { 'sudo -n bash' } else { 'bash' }
  if (-not $canSudo) { Write-Log "没有免密 sudo，直接跑（装不了 systemd 时会提示）" yellow }
  $cmd = "cd /tmp/fatbot && $runner install.sh $quoted"
  if ($DryRun) { $cmd = $cmd + ' --dry-run' }
  Write-Log "在服务器上安装（第一次会慢，pip 装 9 个插件）…" cyan
  $out = Invoke-Remote $cmd
  $out = Clean-Ansi $out
  foreach ($ln in ($out -split "`n")) {
    if ($ln.Trim()) {
      $color = if ($ln -match '✓|FATBOT_INSTALL_OK') { 'green' } elseif ($ln -match '✗|失败|Error|Traceback') { 'red' } elseif ($ln -match '!') { 'yellow' } else { 'gray' }
      Write-Log $ln.TrimEnd() $color
    }
  }

  # 用 ASCII 标记判断（中文在 GBK 控制台下会变乱码，不能拿它当依据）
  $url = ''
  $m = [regex]::Match($out, 'FATBOT_WEB=(\S+)')
  if ($m.Success) {
    $url = $m.Groups[1].Value.Trim()
  } else {
    $m2 = [regex]::Match($out, 'http://[0-9a-zA-Z\.\-]+:\d+/\?token=[A-Za-z0-9]+')
    if ($m2.Success) { $url = $m2.Value }
  }
  $okInstall = ($out -match 'FATBOT_INSTALL_OK') -or $DryRun

  Remove-Item $zip -Force -ErrorAction SilentlyContinue
  Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

  if ($okInstall -and $url -and -not $DryRun) {
    $cfgPath = Join-Path (Split-Path -Parent $deployDir) 'console-config.json'
    $cfg = @{
      base = $url.Split('/?')[0]
      token = if ($Token) { $Token } else { '' }
      host = $HostName
      ssh_user = $User
      bot = 'fatbot'
      installed_at = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
    } | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText($cfgPath, $cfg, (New-Object Text.UTF8Encoding($false)))
    Write-Log "控制台配置已写：$cfgPath" green
    Write-Log "地址：$url" green
    return @{ ok = $true; url = $url; error = '' }
  }
  return @{ ok = $false; url = $url; error = '安装输出里没看到「装完了」，看上面的日志' }
}

# ---------------------------------------------------------------- 入口
if ($NoGui -or $Server) {
  if (-not $Server) { throw "命令行模式要给 -Server <服务器IP>" }
  if (-not $Qq) { throw "命令行模式要给 -Qq" }
  if (-not $Token) { $Token = (Get-Random -Count 16 -InputObject ([char[]]('abcdefghijkmnpqrstuvwxyz23456789' -split ''))) -join '' }
  $r = Invoke-Deploy -HostName $Server -User $SshUser -Password $SshPassword -Key $KeyFile `
    -Qq $Qq -Admin $Admin -Token $Token -AiBase $AiBase -AiModel $AiModel -AiKey $AiKey -WithNapcat:$WithNapcat.IsPresent
  if ($r.ok) { Write-Log "OK  $($r.url)" green; exit 0 }
  Write-Log "FAILED  $($r.error)" red
  exit 1
}

Show-Wizard


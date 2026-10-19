# 编译手机端控制台 APK（Windows + JDK 17/21 + Android SDK build-tools 35）
# 用法：pwsh -File build-apk.ps1
#
# 需要：
#   - Android SDK：platforms;android-34、build-tools;35.0.0
#     路径用环境变量 ANDROID_SDK 指定，不设则用 F:\android-sdk
#   - JDK：环境变量 JAVA_HOME，不设则用 C:\Program Files\Java\jdk-21
#
# 产物：本目录下 app-release.apk

$ErrorActionPreference = 'Stop'

$root = if ($env:ANDROID_SDK) { $env:ANDROID_SDK } else { 'F:\android-sdk' }
$bt = Join-Path $root 'build-tools\35.0.0'
$platform = Join-Path $root 'platforms\android-34\android.jar'
if (-not (Test-Path $platform)) {
    throw "找不到 $platform —— 先装 Android SDK 的 platforms;android-34 与 build-tools;35.0.0，或用 ANDROID_SDK 指定路径"
}
$javaHome = if ($env:JAVA_HOME) { $env:JAVA_HOME } else { 'C:\Program Files\Java\jdk-21' }
$javac = Join-Path $javaHome 'bin\javac.exe'
$jar = Join-Path $javaHome 'bin\jar.exe'
$keytool = Join-Path $javaHome 'bin\keytool.exe'
if (-not (Test-Path $javac)) { throw "找不到 javac：$javac（设 JAVA_HOME 指向 JDK）" }

# aapt2 不认中文/空格路径 → 先拷到纯 ASCII 的临时目录再编
$srcDir = $PSScriptRoot
$proj = Join-Path $env:TEMP 'fatbot-apkbuild'
$ks = Join-Path $env:TEMP 'fatbot-demo.keystore'   # 演示证书放在外面，重复编译不会重建

Remove-Item $proj -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path "$proj\src", "$proj\res", "$proj\build\gen", "$proj\build\classes" | Out-Null
Copy-Item (Join-Path $srcDir 'AndroidManifest.xml') "$proj\" -Force
Copy-Item (Join-Path $srcDir 'src\*') "$proj\src" -Recurse -Force
Copy-Item (Join-Path $srcDir 'res\*') "$proj\res" -Recurse -Force
Remove-Item "$proj\build\res.zip", "$proj\build\base.apk", "$proj\build\classes.jar" -Force -ErrorAction SilentlyContinue
Remove-Item "$proj\build\classes\*", "$proj\build\gen\*" -Recurse -Force -ErrorAction SilentlyContinue

Write-Host '1/6 aapt2...'
& "$bt\aapt2.exe" compile --dir "$proj\res" -o "$proj\build\res.zip"
& "$bt\aapt2.exe" link -o "$proj\build\base.apk" -I $platform --manifest "$proj\AndroidManifest.xml" `
    -R "$proj\build\res.zip" --java "$proj\build\gen" --min-sdk-version 24 --target-sdk-version 34 --auto-add-overlay

Write-Host '2/6 javac...'
$files = @(Get-ChildItem "$proj\src" -Recurse -Filter '*.java' | ForEach-Object { $_.FullName }) +
         @(Get-ChildItem "$proj\build\gen" -Recurse -Filter '*.java' | ForEach-Object { $_.FullName })
& $javac -nowarn -source 8 -target 8 -bootclasspath $platform -classpath $platform -d "$proj\build\classes" @files

Write-Host '3/6 dex...'
Push-Location "$proj\build\classes"
& $jar cf "$proj\build\classes.jar" .
Pop-Location
& "$bt\d8.bat" --min-api 24 --lib $platform --output "$proj\build" "$proj\build\classes.jar"

Write-Host '4/6 package...'
Copy-Item "$proj\build\base.apk" "$proj\build\unsigned.apk" -Force
Push-Location "$proj\build"
& $jar uf "$proj\build\unsigned.apk" classes.dex
Pop-Location
& "$bt\zipalign.exe" -f -p 4 "$proj\build\unsigned.apk" "$proj\build\aligned.apk"

Write-Host '5/6 sign...'
# 演示用自签名证书（密码 changeit）；正式发布请换成你自己的 keystore
if (-not (Test-Path $ks)) {
    & $keytool -genkeypair -v -keystore $ks -alias fatbot -keyalg RSA -keysize 2048 `
        -validity 10000 -storepass changeit -keypass changeit `
        -dname "CN=Fatbot Console, OU=Console, O=Fatbot, L=CN, ST=CN, C=CN" | Out-Null
}
& "$bt\apksigner.bat" sign --ks $ks --ks-key-alias fatbot --ks-pass pass:changeit --key-pass pass:changeit `
    --out "$proj\build\app-release.apk" "$proj\build\aligned.apk"

Write-Host '6/6 verify + copy...'
& "$bt\apksigner.bat" verify "$proj\build\app-release.apk"
$dest = Join-Path $srcDir 'app-release.apk'
Copy-Item "$proj\build\app-release.apk" $dest -Force
Write-Host ("OK -> {0} ({1} KB)" -f $dest, [math]::Round((Get-Item $dest).Length / 1KB, 1))

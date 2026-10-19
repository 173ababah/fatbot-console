# Build FishBot console APK (Windows, JDK21, Android SDK build-tools 35)
# Run: pwsh -File build-apk.ps1
# SDK:  F:\android-sdk  (platforms;android-34, build-tools;35.0.0)

$ErrorActionPreference = 'Stop'
$root = 'F:\android-sdk'
$proj = 'F:\apkbuild'
$bt = "$root\build-tools\35.0.0"
$platform = "$root\platforms\android-34\android.jar"
$env:JAVA_HOME = 'C:\Program Files\Java\jdk-21'
$javac = "$env:JAVA_HOME\bin\javac.exe"
$jar = "$env:JAVA_HOME\bin\jar.exe"
$keytool = "$env:JAVA_HOME\bin\keytool.exe"
$ks = "$proj\feiyu.keystore"

# sources (folder name contains CJK, so use $PSScriptRoot and copy to ASCII path)
$srcDir = Join-Path $PSScriptRoot 'apk'
New-Item -ItemType Directory -Force -Path "$proj\build\gen", "$proj\build\classes" | Out-Null
Copy-Item (Join-Path $srcDir 'AndroidManifest.xml') "$proj\" -Force
Copy-Item (Join-Path $srcDir 'src\*') "$proj\src" -Recurse -Force
Copy-Item (Join-Path $srcDir 'res\*') "$proj\res" -Recurse -Force
Remove-Item "$proj\build\res.zip", "$proj\build\base.apk", "$proj\build\classes.jar" -Force -ErrorAction SilentlyContinue
Remove-Item "$proj\build\classes\*" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$proj\build\gen\*" -Recurse -Force -ErrorAction SilentlyContinue

Write-Host '1/6 aapt2...'
& "$bt\aapt2.exe" compile --dir "$proj\res" -o "$proj\build\res.zip"
& "$bt\aapt2.exe" link -o "$proj\build\base.apk" -I $platform --manifest "$proj\AndroidManifest.xml" `
    -R "$proj\build\res.zip" --java "$proj\build\gen" --min-sdk-version 24 --target-sdk-version 34 --auto-add-overlay

Write-Host '2/6 javac...'
$files = @(Get-ChildItem "$proj\src" -Recurse -Filter '*.java' | ForEach-Object { $_.FullName }) + (Get-ChildItem "$proj\build\gen" -Recurse -Filter '*.java' | ForEach-Object { $_.FullName })
& $javac -nowarn -source 8 -target 8 -bootclasspath $platform -classpath $platform -d "$proj\build\classes" @files 2>$null

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
if (-not (Test-Path $ks)) {
    & $keytool -genkeypair -v -keystore $ks -alias feiyu -keyalg RSA -keysize 2048 `
        -validity 10000 -storepass changeit -keypass changeit `
        -dname "CN=FeiYu Console, OU=FishBot, O=Feiyu, L=CN, ST=CN, C=CN" | Out-Null
}
& "$bt\apksigner.bat" sign --ks $ks --ks-key-alias feiyu --ks-pass pass:changeit --key-pass pass:changeit `
    --out "$proj\build\fish-console.apk" "$proj\build\aligned.apk"

Write-Host '6/6 verify + copy...'
& "$bt\apksigner.bat" verify "$proj\build\fish-console.apk"
$dest = Join-Path $PSScriptRoot '肥鱼控制台.apk'
Copy-Item "$proj\build\fish-console.apk" $dest -Force
$bak = 'F:\肥鱼机器人\06-备份\fish-console.apk'
Copy-Item "$proj\build\fish-console.apk" $bak -Force
$kb = [math]::Round((Get-Item $dest).Length / 1KB, 1)
Write-Host ("OK -> {0} ({1} KB)" -f $dest, $kb)

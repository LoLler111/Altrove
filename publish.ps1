# Builds a release of both apps, with the version in version.txt:
#   artifacts\publish\Altrove\          Windows app and altrove-cli (self-contained), with the virtual display driver
#   artifacts\releases\                 what goes on the GitHub release:
#       Altrove-win-Setup.exe           installer (updates itself from the GitHub releases)
#       Altrove-win-Portable.zip        same app to unpack anywhere, updates itself too
#       Altrove-<version>-full.nupkg, releases.win.json, ...   read by the Windows updater
#       Altrove-android.apk             Android receiver, signed with the release key
# -DownloadPrevious fetches the latest release first so the Windows update also gets a small delta package.
param([switch]$DownloadPrevious)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = (Get-Content (Join-Path $root 'version.txt') -Raw).Trim()
$out = Join-Path $root 'artifacts\publish\Altrove'
$releases = Join-Path $root 'artifacts\releases'
$repoUrl = 'https://github.com/LoLler111/Altrove'
$dotnet = "$env:ProgramFiles\dotnet\dotnet.exe"
$onPath = Get-Command dotnet -ErrorAction SilentlyContinue
if ($onPath) { $dotnet = $onPath.Source }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
foreach ($project in 'src\Altrove.App', 'tools\Altrove.Cli') {
    & $dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained -o $out -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
}

if (Test-Path $releases) { Remove-Item $releases -Recurse -Force }
New-Item -ItemType Directory $releases | Out-Null

# Android receiver: needs the Android SDK and a JDK (Android Studio's, unless JAVA_HOME is set).
$studioJdk = "$env:ProgramFiles\Android\Android Studio\jbr"
if (-not $env:JAVA_HOME -and (Test-Path $studioJdk)) { $env:JAVA_HOME = $studioJdk }
if ($env:JAVA_HOME) {
    $signing = Join-Path $env:USERPROFILE '.altrove\signing.properties'
    if (-not $env:ALTROVE_KEYSTORE -and -not (Test-Path $signing)) {
        Write-Warning "Chiave di rilascio Android non trovata ($signing): l'APK viene firmato con la chiave di debug e non potrà aggiornare quello pubblicato."
    }
    & (Join-Path $root 'android\gradlew.bat') -p (Join-Path $root 'android') assembleRelease --console=plain -q
    if ($LASTEXITCODE -ne 0) { throw 'Android build failed.' }
    Copy-Item (Join-Path $root 'android\app\build\outputs\apk\release\app-release.apk') (Join-Path $releases 'Altrove-android.apk') -Force
} else {
    Write-Warning 'JDK non trovato: APK non compilato.'
}

# Windows installer and update packages (Velopack: dotnet tool install -g vpk).
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) { throw 'vpk non trovato: dotnet tool install -g vpk --version 1.2.161' }
if ($DownloadPrevious) {
    $token = if ($env:GITHUB_TOKEN) { @('--token', $env:GITHUB_TOKEN) } else { @() }
    vpk download github --repoUrl $repoUrl --outputDir $releases @token
    if ($LASTEXITCODE -ne 0) { Write-Warning 'Nessuna release precedente scaricata: niente pacchetto delta.' }
}
vpk pack --packId Altrove --packVersion $version --packDir $out --mainExe Altrove.exe --runtime win-x64 `
    --packTitle Altrove --packAuthors LoLler111 --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed.' }
Write-Host "Release $version`: $releases"

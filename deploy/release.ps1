<#
.SYNOPSIS
  Builds a TMSpline release (the Windows app and the Openplanet plugin) and, with -Publish, puts it on the website.

.DESCRIPTION
  - The app: src/TmSpline.Desktop for win-x64 with the .NET runtime included (nothing to install), the editor bundle
    and the preview core rebuilt as needed, without debug symbols; zipped with LICENSE and a short readme as
    artifacts/release/TMSpline-<version>-win-x64.zip.
  - The plugin: tools/OpenplanetPlugin as artifacts/release/TMSpline.op (a zip with info.toml at its root).
  - latest.json: the version, both file names and the notes; the website links it and the app checks it for updates.
  - -Publish copies the three files into /opt/tmspline/releases on the Mac (latest.json last, so the website never
    names a file that is not there yet). Files of older releases stay there until removed by hand.

  NadeoImporter and everything of the game stay out: the app downloads the importer from Nadeo on first use.

.EXAMPLE
  .\deploy\release.ps1 -Version 0.1.0 -Notes "First release."
  .\deploy\release.ps1 -Version 0.1.0 -Notes "First release." -Publish
#>
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$Notes = "",
    [switch]$Publish,
    [string]$Mac = "finn@192.168.178.28"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$out = Join-Path $root "artifacts\release"
$app = Join-Path $out "app"
$zipName = "TMSpline-$Version-win-x64.zip"
$zip = Join-Path $out $zipName
$plugin = Join-Path $out "TMSpline.op"
$manifest = Join-Path $out "latest.json"
New-Item -ItemType Directory -Force $out | Out-Null

# ---- the app ----------------------------------------------------------------------------------------------------------
Write-Host "Publishing TMSpline $Version for win-x64..." -ForegroundColor Cyan
if (Test-Path $app) { Remove-Item -Recurse -Force $app }
dotnet publish (Join-Path $root "src\TmSpline.Desktop") -c Release -r win-x64 --self-contained -p:Version=$Version -o $app -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
if (-not (Test-Path (Join-Path $app "wwwroot\index.html"))) { throw "The editor bundle is missing from the app (wwwroot)." }

# Not needed by users: debug symbols, the crash dump tool, the other projects' own launchers, the LZO static library.
Get-ChildItem $app -Recurse -Include *.pdb, createdump.exe, Microsoft.DiaSymReader.Native.*.dll, *.a | Remove-Item -Force
Remove-Item (Join-Path $app "TmSpline.Server.exe"), (Join-Path $app "VanillaLib.exe") -ErrorAction SilentlyContinue

Copy-Item (Join-Path $root "LICENSE") (Join-Path $app "LICENSE.txt")
@"
TMSpline $Version
=================

Start TMSpline.exe. Your files are kept in %LOCALAPPDATA%\TMSpline (tracks, the block library, settings).

- Exporting needs Nadeo's NadeoImporter: the export dialog downloads it from Nadeo the first time.
- Live update and the block library need the TMSpline Openplanet plugin (Openplanet in developer signature mode).
- Sign in (top right) to keep your tracks in the cloud and share them.

TMSpline is free software under the GNU General Public License v3 (LICENSE.txt).
Source code: https://github.com/pixartist/tmspline
TMSpline is not affiliated with Nadeo or Ubisoft. Trackmania is a trademark of Ubisoft Entertainment.
"@ | Set-Content -Encoding utf8 (Join-Path $app "README.txt")

# tar (bsdtar, part of Windows) writes zip paths with "/"; Windows PowerShell's Compress-Archive writes "\", which
# other unzip tools take for part of the file name.
if (Test-Path $zip) { Remove-Item -Force $zip }
tar -a -c -f $zip -C $app *
if ($LASTEXITCODE -ne 0) { throw "zipping the app failed" }
Write-Host ("App: {0} ({1:0.0} MB)" -f $zipName, ((Get-Item $zip).Length / 1MB))

# ---- the plugin -------------------------------------------------------------------------------------------------------
$pluginDir = Join-Path $root "tools\OpenplanetPlugin"
if (Test-Path $plugin) { Remove-Item -Force $plugin }
$pluginZip = Join-Path $out "TMSpline-plugin.zip"
if (Test-Path $pluginZip) { Remove-Item -Force $pluginZip }
# Only the plugin's own files: anything else lying in the folder (e.g. a zip made by hand) stays out.
$pluginFiles = @(Get-ChildItem $pluginDir -File | Where-Object { $_.Name -eq "info.toml" -or $_.Extension -eq ".as" } | ForEach-Object Name)
if (-not ($pluginFiles -contains "info.toml")) { throw "tools/OpenplanetPlugin has no info.toml" }
tar -a -c -f $pluginZip -C $pluginDir @pluginFiles
if ($LASTEXITCODE -ne 0) { throw "zipping the plugin failed" }
Move-Item $pluginZip $plugin
Write-Host ("Plugin: TMSpline.op ({0:0} KB)" -f ((Get-Item $plugin).Length / 1KB))

# ---- manifest ---------------------------------------------------------------------------------------------------------
[ordered]@{
    version   = $Version
    app       = $zipName
    plugin    = "TMSpline.op"
    notes     = $Notes
    published = (Get-Date).ToUniversalTime().ToString("o")
} | ConvertTo-Json | Set-Content -Encoding utf8 $manifest
Write-Host "Built in $out" -ForegroundColor Green

if (-not $Publish) { return }

# ---- publish ----------------------------------------------------------------------------------------------------------
Write-Host "Copying the release to the Mac..." -ForegroundColor Cyan
scp $zip $plugin "${Mac}:/opt/tmspline/releases/"
if ($LASTEXITCODE -ne 0) { throw "copying the release failed" }
scp $manifest "${Mac}:/opt/tmspline/releases/latest.json"
if ($LASTEXITCODE -ne 0) { throw "copying latest.json failed" }
Write-Host "Published TMSpline ${Version}: https://tmspline.ddns.pixartist.de" -ForegroundColor Green

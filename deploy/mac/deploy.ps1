<#
.SYNOPSIS
  Publishes TMSpline's hosted service for the home Mac and copies it there.

.DESCRIPTION
  - Publishes src/TmSpline.Cloud for macOS arm64 with the .NET runtime included (artifacts/cloud-publish/app).
  - -Setup: copies the app, the LaunchDaemon and setup.sh to ~/tmspline-deploy on the Mac; then run there once
    `sudo sh ~/tmspline-deploy/setup.sh` (creates the service account, folders and the LaunchDaemon).
  - Otherwise (an update): syncs the app into /opt/tmspline/app, signs it and restarts the service (the sudo rule
    from setup.sh allows exactly that restart without a password). Data and releases are left alone.

.EXAMPLE
  .\deploy\mac\deploy.ps1 -Setup
  .\deploy\mac\deploy.ps1
#>
param(
    [switch]$Setup,
    [string]$Mac = "finn@192.168.178.28"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$out = Join-Path $root "artifacts\cloud-publish"
$app = Join-Path $out "app"

Write-Host "Publishing TmSpline.Cloud for osx-arm64..." -ForegroundColor Cyan
if (Test-Path $app) { Remove-Item -Recurse -Force $app }
dotnet publish (Join-Path $root "src\TmSpline.Cloud") -c Release -r osx-arm64 --self-contained -o $app -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Remove-Item (Join-Path $app "appsettings.Development.json") -ErrorAction SilentlyContinue

# One archive keeps the copy quick and the file modes right (tar from Windows has no exec bits; the scripts set them).
$tar = Join-Path $out "tmspline-app.tar.gz"
tar -czf $tar -C $out app
if ($LASTEXITCODE -ne 0) { throw "tar failed" }

if ($Setup) {
    ssh $Mac "rm -rf ~/tmspline-deploy && mkdir -p ~/tmspline-deploy"
    scp $tar (Join-Path $PSScriptRoot "setup.sh") (Join-Path $PSScriptRoot "de.pixartist.tmspline.plist") "${Mac}:tmspline-deploy/"
    ssh $Mac "cd ~/tmspline-deploy && tar -xzf tmspline-app.tar.gz && rm tmspline-app.tar.gz && chmod +x app/TmSpline.Cloud"
    if ($LASTEXITCODE -ne 0) { throw "copying to the Mac failed" }
    Write-Host "Copied to ${Mac}:~/tmspline-deploy. Now run on the Mac:  sudo sh ~/tmspline-deploy/setup.sh" -ForegroundColor Green
    return
}

scp $tar "${Mac}:/tmp/tmspline-app.tar.gz"
ssh $Mac @"
set -e
rm -rf /tmp/tmspline-update && mkdir /tmp/tmspline-update
tar -xzf /tmp/tmspline-app.tar.gz -C /tmp/tmspline-update
rsync -a --delete /tmp/tmspline-update/app/ /opt/tmspline/app/
chmod +x /opt/tmspline/app/TmSpline.Cloud
codesign --force -s - /opt/tmspline/app/TmSpline.Cloud
rm -rf /tmp/tmspline-update /tmp/tmspline-app.tar.gz
sudo -n /bin/launchctl kickstart -k system/de.pixartist.tmspline
sleep 3
curl -fsS -o /dev/null http://127.0.0.1:5090/site.css && echo 'TMSpline restarted and answering.'
"@
if ($LASTEXITCODE -ne 0) { throw "updating the Mac failed" }

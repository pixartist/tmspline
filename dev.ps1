<#
.SYNOPSIS
  Runs the local dev stack: the export server (http://localhost:5080) and the Vite editor (http://localhost:5173).

.DESCRIPTION
  - Installs the web dependencies if node_modules is missing.
  - Rebuilds the .NET WebAssembly preview core when TmSpline.Core or TmSpline.Wasm changed since the last build
    (or always with -Wasm, never with -NoWasm).
  - Starts the export server in the background (skipped if something already listens on :5080), its output goes to
    artifacts/dev-server.log, and runs Vite in the foreground. Ctrl+C stops both.

.EXAMPLE
  .\dev.ps1            # usual start
  .\dev.ps1 -Wasm      # force a preview core rebuild
  .\dev.ps1 -NoServer  # editor only
#>
param(
    [switch]$Wasm,
    [switch]$NoWasm,
    [switch]$NoServer
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$web = Join-Path $root "web"
$serverPort = 5080
$log = Join-Path $root "artifacts\dev-server.log"

function Test-Port([int]$port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try { $client.Connect("127.0.0.1", $port); return $true } catch { return $false } finally { $client.Dispose() }
}

function Get-NewestWrite([string[]]$paths, [string]$filter) {
    $files = $paths | Where-Object { Test-Path $_ } | ForEach-Object {
        Get-ChildItem $_ -Recurse -File -Filter $filter | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
    }
    if (-not $files) { return [datetime]::MinValue }
    return ($files | Measure-Object -Property LastWriteTime -Maximum).Maximum
}

# ---- web dependencies ----------------------------------------------------------------------------
if (-not (Test-Path (Join-Path $web "node_modules"))) {
    Write-Host "Installing web dependencies..." -ForegroundColor Cyan
    Push-Location $web
    try { npm install; if ($LASTEXITCODE -ne 0) { throw "npm install failed" } } finally { Pop-Location }
}

# ---- preview core (WASM) -------------------------------------------------------------------------
$framework = Join-Path $web "public\_framework"
$coreWasm = Join-Path $framework "TmSpline.Core.wasm"
$sources = @((Join-Path $root "src\TmSpline.Core"), (Join-Path $root "src\TmSpline.Wasm"))
$stale = -not (Test-Path $coreWasm)
if (-not $stale) {
    $built = (Get-Item $coreWasm).LastWriteTime
    $stale = (Get-NewestWrite $sources "*.cs") -gt $built -or (Get-NewestWrite $sources "*.csproj") -gt $built
}
if ($Wasm -or ($stale -and -not $NoWasm)) {
    Write-Host "Building the preview core (TmSpline.Wasm)..." -ForegroundColor Cyan
    Push-Location $web
    try { npm run wasm; if ($LASTEXITCODE -ne 0) { throw "npm run wasm failed" } } finally { Pop-Location }
} elseif ($stale) {
    Write-Host "Preview core is older than the C# sources (-NoWasm given, not rebuilding)." -ForegroundColor Yellow
}

# ---- export server -------------------------------------------------------------------------------
$server = $null
if ($NoServer) {
    Write-Host "Not starting the export server (-NoServer)." -ForegroundColor Yellow
} elseif (Test-Port $serverPort) {
    Write-Host "Something already listens on :$serverPort, using it as the export server." -ForegroundColor Yellow
} else {
    New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
    Write-Host "Starting the export server on http://localhost:$serverPort (log: $log)..." -ForegroundColor Cyan
    $project = Join-Path $root "src\TmSpline.Server\TmSpline.Server.csproj"
    $server = Start-Process dotnet -ArgumentList "run", "--project", "`"$project`"", "--launch-profile", "http" `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err" -NoNewWindow -PassThru
    # Wait until it answers, so the first export from the editor does not fail.
    $deadline = (Get-Date).AddSeconds(90)
    while (-not (Test-Port $serverPort)) {
        if ($server.HasExited) { throw "The export server exited (code $($server.ExitCode)), see $log and $log.err" }
        if ((Get-Date) -gt $deadline) { throw "The export server did not start within 90 s, see $log" }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "Export server is up." -ForegroundColor Green
}

# ---- editor --------------------------------------------------------------------------------------
try {
    Write-Host "Starting the editor on http://localhost:5173 (Ctrl+C stops everything)..." -ForegroundColor Cyan
    Push-Location $web
    npx vite --port 5173 --strictPort
} finally {
    Pop-Location
    if ($server -and -not $server.HasExited) {
        Write-Host "Stopping the export server..." -ForegroundColor Cyan
        # dotnet run starts the server as a child process; stop the whole tree.
        & taskkill /PID $server.Id /T /F | Out-Null
    }
}

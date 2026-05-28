$root = $PSScriptRoot
$apiDir = Join-Path $root 'backend\src\QuantFlowBots.Api'
$workerDir = Join-Path $root 'backend\src\QuantFlowBots.Worker'
$frontendDir = Join-Path $root 'frontend'
$composeFile = Join-Path $root 'docker-compose.yml'
$envFile = Join-Path $root '.env'

# Kill any previously-spawned API/Worker so we never end up with duplicate instances fighting
# over port 5087 (the #1 cause of "login broken" — a zombie API holding the port while the new
# one fails to bind). Matches our processes specifically so unrelated dotnet apps are untouched.
function Stop-QfbProcesses {
    $killed = 0

    # 1) The apphost executables — most reliable signal. `dotnet run` / `dotnet watch run`
    #    launch the built app as QuantFlowBots.Api.exe / QuantFlowBots.Worker.exe (NOT dotnet.exe),
    #    so matching by process name catches the real server no matter how it was started.
    foreach ($name in @('QuantFlowBots.Api', 'QuantFlowBots.Worker')) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue; $killed++
        }
    }

    # 2) Whatever still owns the API port (zombie / non-standard launch).
    try {
        $owners = Get-NetTCPConnection -LocalPort 5087 -State Listen -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty OwningProcess -Unique
        foreach ($ownerPid in $owners) {
            Stop-Process -Id $ownerPid -Force -ErrorAction SilentlyContinue; $killed++
        }
    } catch {}

    # 3) The `dotnet run` / `dotnet watch run` parent shells for this repo. The apphost dies in
    #    step 1, but the parent can respawn it — kill parents whose command line is QFB-flavoured.
    try {
        $procs = Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -and ($_.CommandLine -like '*quant-flow-bots*' -or $_.CommandLine -like '*watch run*' -or $_.CommandLine -like '*run --no-build*') }
        foreach ($p in $procs) {
            Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue; $killed++
        }
    } catch {}

    if ($killed -gt 0) {
        Write-Host "Cleaned up $killed stale QFB process(es)." -ForegroundColor DarkYellow
        Start-Sleep -Seconds 2   # let the OS release the socket before we rebind
    }
}

Write-Host 'Stopping any previous QFB API/Worker instances...' -ForegroundColor Yellow
Stop-QfbProcesses

# Load .env into the current shell so docker-compose substitutes ${POSTGRES_PASSWORD} etc.
if (Test-Path $envFile) {
    Get-Content $envFile | ForEach-Object {
        if ($_ -match '^\s*#') { return }
        if ($_ -match '^\s*([^=\s]+)\s*=\s*(.*)\s*$') {
            $name = $Matches[1]
            $value = $Matches[2].Trim('"').Trim("'")
            Set-Item -Path "Env:$name" -Value $value
        }
    }
} else {
    Write-Host '.env not found. Copy .env.example to .env and set POSTGRES_PASSWORD before running.' -ForegroundColor Red
    exit 1
}

function Test-DockerRunning {
    try {
        docker info --format '{{.ServerVersion}}' 2>$null | Out-Null
        return $LASTEXITCODE -eq 0
    } catch { return $false }
}

function Start-DockerDesktop {
    $candidates = @(
        "$env:ProgramFiles\Docker\Docker\Docker Desktop.exe",
        "$env:LOCALAPPDATA\Docker\Docker Desktop.exe"
    )
    $exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $exe) {
        Write-Host 'Docker Desktop executable not found. Please start Docker Desktop manually, then re-run this script.' -ForegroundColor Red
        exit 1
    }
    Write-Host "Starting Docker Desktop: $exe" -ForegroundColor Yellow
    Start-Process -FilePath $exe | Out-Null
}

Write-Host 'Checking Docker daemon...' -ForegroundColor Yellow
if (-not (Test-DockerRunning)) {
    Start-DockerDesktop
    Write-Host 'Waiting for Docker daemon to be ready (up to 120s)...' -ForegroundColor Yellow
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        if (Test-DockerRunning) { break }
    }
    if (-not (Test-DockerRunning)) {
        Write-Host 'Docker daemon did not start within 120s. Please start Docker Desktop manually.' -ForegroundColor Red
        exit 1
    }
}
Write-Host 'Docker is ready.' -ForegroundColor Green

Write-Host 'Ensuring docker stack is up (timescaledb, redis, pgadmin)...' -ForegroundColor Yellow
docker compose -f $composeFile up -d
if ($LASTEXITCODE -ne 0) {
    Write-Host 'docker compose up failed. Aborting.' -ForegroundColor Red
    exit 1
}

Write-Host 'Waiting for qfb-timescaledb to be healthy (up to 90s)...' -ForegroundColor Yellow
$deadline = (Get-Date).AddSeconds(90)
$healthy = $false
while ((Get-Date) -lt $deadline) {
    $status = (docker inspect -f '{{.State.Health.Status}}' qfb-timescaledb 2>$null)
    if ($status -eq 'healthy') { $healthy = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $healthy) {
    Write-Host 'Postgres did not become healthy in time. Check `docker logs qfb-timescaledb`.' -ForegroundColor Red
    exit 1
}
Write-Host 'Postgres is healthy.' -ForegroundColor Green

# Force Development env so appsettings.Development.json (which carries the dev DB password)
# is loaded by ASP.NET — `dotnet watch run` spawned via Start-Process does NOT inherit
# launchSettings.json automatically.
$envSetup = "`$env:ASPNETCORE_ENVIRONMENT='Development'; `$env:DOTNET_ENVIRONMENT='Development';"
Start-Process powershell -ArgumentList '-NoExit', '-Command', "$envSetup `$env:QFB_PROCESS='api'; cd '$apiDir'; Write-Host '=== Quant Flow Bots API (http://localhost:5087)  [watch mode] ===' -ForegroundColor Green; dotnet watch run --urls http://localhost:5087"
Start-Process powershell -ArgumentList '-NoExit', '-Command', "$envSetup `$env:QFB_PROCESS='worker'; cd '$workerDir'; Write-Host '=== Quant Flow Bots Worker  [watch mode] ===' -ForegroundColor Magenta; dotnet watch run"
Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$frontendDir'; Write-Host '=== Quant Flow Bots Frontend (http://localhost:3000, or next free port) ===' -ForegroundColor Cyan; npm run dev -- --port 3000"

Write-Host ''
Write-Host 'Started Quant Flow Bots dev stack:' -ForegroundColor Green
Write-Host "  Postgres : localhost:5433  (db=$env:POSTGRES_DB user=$env:POSTGRES_USER pass=<from .env>)"
Write-Host '  Redis    : localhost:6379'
Write-Host '  pgAdmin  : http://localhost:5050  (admin@qfb.local / admin)'
Write-Host '  Backend  : http://localhost:5087   (swagger: /swagger)'
Write-Host '  Worker   : background, logs in its own window'
Write-Host '  Frontend : http://localhost:3000  (or the next free port printed by Vite)'

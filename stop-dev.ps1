# Stops the Quant Flow Bots dev stack started by start-dev.ps1.
# Kills the API + Worker (dotnet) and, optionally, the Vite frontend (node) — but leaves the
# Docker containers (Postgres/Redis) running so data and warm caches survive. Pass -All to also
# stop the frontend, -Docker to also bring the containers down.
param(
    [switch]$All,
    [switch]$Docker
)

$root = $PSScriptRoot
$killed = 0

Write-Host 'Stopping QFB API + Worker...' -ForegroundColor Yellow

# 1) The apphost executables — the actual running servers (named, not dotnet.exe).
foreach ($name in @('QuantFlowBots.Api', 'QuantFlowBots.Worker')) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue; $killed++
    }
}

# 2) API port owner (zombie / non-standard launch).
try {
    $owners = Get-NetTCPConnection -LocalPort 5087 -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique
    foreach ($ownerPid in $owners) { Stop-Process -Id $ownerPid -Force -ErrorAction SilentlyContinue; $killed++ }
} catch {}

# 3) `dotnet run` / `dotnet watch run` parent shells for this repo.
try {
    $procs = Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and ($_.CommandLine -like '*quant-flow-bots*' -or $_.CommandLine -like '*watch run*' -or $_.CommandLine -like '*run --no-build*') }
    foreach ($p in $procs) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue; $killed++ }
} catch {}

Write-Host "Stopped $killed dotnet process(es)." -ForegroundColor Green

if ($All) {
    Write-Host 'Stopping Vite frontend (node on :3000)...' -ForegroundColor Yellow
    try {
        $feOwners = Get-NetTCPConnection -LocalPort 3000 -State Listen -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty OwningProcess -Unique
        foreach ($ownerPid in $feOwners) { Stop-Process -Id $ownerPid -Force -ErrorAction SilentlyContinue }
        Write-Host 'Frontend stopped.' -ForegroundColor Green
    } catch {}
}

if ($Docker) {
    Write-Host 'Bringing docker stack down...' -ForegroundColor Yellow
    docker compose -f (Join-Path $root 'docker-compose.yml') down
} else {
    Write-Host 'Docker containers left running (use -Docker to stop them).' -ForegroundColor DarkGray
}

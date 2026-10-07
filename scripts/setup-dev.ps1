#Requires -Version 7.0
# One-time local setup for Windows (PowerShell 7+). Same steps as setup-dev.sh.
# Run from the repo root:   pwsh -ExecutionPolicy Bypass -File scripts/setup-dev.ps1
# Safe to re-run: never overwrites a value already in .env.
# -EnvOnly: only create/fill .env (no Docker, restore or npm). Used by CI to test this script on Windows.
param([switch]$EnvOnly)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$tools = if ($EnvOnly) { @() } else { @('docker', 'dotnet', 'node', 'npm') }
foreach ($t in $tools) {
  if (-not (Get-Command $t -ErrorAction SilentlyContinue)) { throw "Missing $t — see docs/GETTING_STARTED.md" }
}
if (-not (Test-Path .env)) { Copy-Item .env.example .env; Write-Host 'Created .env from .env.example' }

function Get-Val([string]$k) {
  $line = Get-Content .env | Where-Object { $_ -match "^$k=" } | Select-Object -First 1
  if ($null -eq $line) { return '' }
  return $line.Substring($k.Length + 1)
}
function Set-IfBlank([string]$k, [string]$v) {
  if ([string]::IsNullOrEmpty((Get-Val $k))) {
    $lines = @(Get-Content .env)
    $found = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
      if ($lines[$i] -match "^$k=") { $lines[$i] = "$k=$v"; $found = $true; break }
    }
    if (-not $found) { $lines += "$k=$v" }
    Set-Content -Path .env -Value $lines -Encoding utf8NoBOM
    Write-Host "  set $k"
  }
}
function New-Secret([int]$bytes) {
  $b = [byte[]]::new($bytes)
  [Security.Cryptography.RandomNumberGenerator]::Fill($b)
  return , $b   # the comma stops PowerShell unrolling the array into object[]
}
function Invoke-Checked([scriptblock]$cmd, [string]$what) {
  & $cmd
  if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

Set-IfBlank POSTGRES_PASSWORD ([Convert]::ToHexString((New-Secret 16)).ToLower())
$u = Get-Val POSTGRES_USER; $p = Get-Val POSTGRES_PASSWORD; $d = Get-Val POSTGRES_DB; $port = Get-Val POSTGRES_PORT
Set-IfBlank DATABASE_URL "Host=localhost;Port=$port;Database=$d;Username=$u;Password=$p"
Set-IfBlank RESORT_TEST_DB "Host=localhost;Port=$port;Database=resort_test;Username=$u;Password=$p"
Set-IfBlank Jwt__SigningKey ([Convert]::ToBase64String((New-Secret 48)))
Set-IfBlank Encryption__Key ([Convert]::ToBase64String((New-Secret 32)))
if ([string]::IsNullOrEmpty((Get-Val Bootstrap__OwnerPassword))) {
  $pw = 'Dev-' + [Convert]::ToHexString((New-Secret 6)).ToLower()
  Set-IfBlank Bootstrap__OwnerPassword $pw
  Write-Host "  Local owner sign-in: $(Get-Val Bootstrap__OwnerEmail) / $pw   (also in .env)"
}

if ($EnvOnly) { Write-Host '.env ready (-EnvOnly: skipped Docker, restore and npm).'; exit 0 }

Write-Host 'Starting PostgreSQL…'
Invoke-Checked { docker compose --env-file .env -f infra/docker/docker-compose.yml up -d --wait db } 'Starting PostgreSQL (is Docker Desktop running?)'
Write-Host 'Restoring .NET packages…'
Invoke-Checked { dotnet restore ResortOS.sln } 'dotnet restore'
Write-Host 'Installing frontend packages…'
Push-Location frontend
try { Invoke-Checked { npm ci } 'npm ci' } finally { Pop-Location }

Write-Host @"

Done. Next:
  API:      cd backend/Resort.Api; dotnet watch run     -> http://localhost:5180/swagger
  Web app:  cd frontend; npm start                      -> http://localhost:4300
  Tests:    pwsh -File scripts/test.ps1
"@

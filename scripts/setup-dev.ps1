# One-time local setup for Windows (PowerShell 7+). Same steps as setup-dev.sh.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

foreach ($t in 'docker','dotnet','node','npm') {
  if (-not (Get-Command $t -ErrorAction SilentlyContinue)) { throw "Missing $t — see docs/SETUP.md" }
}
if (-not (Test-Path .env)) { Copy-Item .env.example .env; Write-Host 'Created .env from .env.example' }

function Get-Val($k) { (Get-Content .env | Where-Object { $_ -match "^$k=" } | Select-Object -First 1) -replace "^$k=", '' }
function Set-IfBlank($k, $v) {
  if ([string]::IsNullOrEmpty((Get-Val $k))) {
    $lines = Get-Content .env
    if ($lines -match "^$k=") { $lines = $lines -replace "^$k=.*", "$k=$v" } else { $lines += "$k=$v" }
    Set-Content .env $lines; Write-Host "  set $k"
  }
}
function New-Secret([int]$bytes) { $b = New-Object byte[] $bytes; [Security.Cryptography.RandomNumberGenerator]::Fill($b); $b }

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

docker compose --env-file .env -f infra/docker/docker-compose.yml up -d --wait db
dotnet restore ResortOS.sln
Push-Location frontend; npm ci; Pop-Location
Write-Host "`nDone. API: cd backend/Resort.Api; dotnet run    Web: cd frontend; npm start    Tests: scripts/test.ps1"

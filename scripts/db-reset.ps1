#Requires -Version 7.0
# Drops and recreates the LOCAL development database (Docker only). The API re-applies migrations and the
# bootstrap owner on its next start.   pwsh -ExecutionPolicy Bypass -File scripts/db-reset.ps1
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
function Get-Val([string]$k) {
  $line = Get-Content .env | Where-Object { $_ -match "^$k=" } | Select-Object -First 1
  if ($null -eq $line) { return '' } ; return $line.Substring($k.Length + 1)
}
$db = Get-Val POSTGRES_DB; $u = Get-Val POSTGRES_USER
$ok = Read-Host "Drop and recreate local database '$db'? [y/N]"
if ($ok -ne 'y') { exit 0 }
docker compose --env-file .env -f infra/docker/docker-compose.yml exec -T db `
  psql -U $u -d postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS `"$db`" WITH (FORCE)" -c "CREATE DATABASE `"$db`""
if ($LASTEXITCODE -ne 0) { throw 'Reset failed (is the database container running?)' }
Write-Host 'Reset. Start the API to migrate.'

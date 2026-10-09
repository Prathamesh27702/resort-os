#Requires -Version 7.0
# Runs every automated test: backend (unit + integration on real PostgreSQL) and frontend unit tests.
#   pwsh -ExecutionPolicy Bypass -File scripts/test.ps1
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
if (-not $env:RESORT_TEST_DB -and (Test-Path .env)) {
  $line = Get-Content .env | Where-Object { $_ -match '^RESORT_TEST_DB=' } | Select-Object -First 1
  if ($line) { $env:RESORT_TEST_DB = $line.Substring('RESORT_TEST_DB='.Length) }
}
if (-not $env:RESORT_TEST_DB) { throw 'Set RESORT_TEST_DB (run scripts/setup-dev.ps1)' }

# Release build: a separate bin\Release copy, so this works while `dotnet watch run` (Debug) has the API running.
dotnet test ResortOS.sln -c Release --logger "console;verbosity=normal"
if ($LASTEXITCODE -ne 0) { throw "Backend tests failed" }
Push-Location frontend
try {
  npm run test:ci
  if ($LASTEXITCODE -ne 0) { throw "Frontend tests failed" }
} finally { Pop-Location }
Write-Host "`nAll tests passed."

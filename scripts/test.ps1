$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
if (-not $env:RESORT_TEST_DB -and (Test-Path .env)) {
  $env:RESORT_TEST_DB = ((Get-Content .env | Where-Object { $_ -match '^RESORT_TEST_DB=' }) -replace '^RESORT_TEST_DB=', '')
}
if (-not $env:RESORT_TEST_DB) { throw 'Set RESORT_TEST_DB (run scripts/setup-dev.ps1)' }
dotnet test ResortOS.sln --logger "console;verbosity=normal"
Push-Location frontend; npm run test:ci; Pop-Location

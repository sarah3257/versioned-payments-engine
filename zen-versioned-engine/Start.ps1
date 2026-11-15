param([int]$Port = 5188)
$ErrorActionPreference = 'Stop'
$taskTools = Join-Path (Split-Path $PSScriptRoot -Parent) '.tools'
$taskDotnet = Join-Path $taskTools 'dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $taskTools 'cli-home'
$env:NUGET_PACKAGES = Join-Path $taskTools 'nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Write-Host "POC: http://127.0.0.1:$Port"
& $taskDotnet run --project (Join-Path $PSScriptRoot 'src/Rules.Api/Rules.Api.csproj') --urls "http://127.0.0.1:$Port"
if ($LASTEXITCODE -ne 0) { throw "Application stopped with exit code $LASTEXITCODE" }

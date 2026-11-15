$ErrorActionPreference = 'Stop'
$taskTools = Join-Path (Split-Path $PSScriptRoot -Parent) '.tools'
$taskDotnet = Join-Path $taskTools 'dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $taskTools 'cli-home'
$env:NUGET_PACKAGES = Join-Path $taskTools 'nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $taskDotnet test (Join-Path $PSScriptRoot 'RulesEngine.slnx') --logger 'trx;LogFileName=verification.trx' --results-directory (Join-Path $PSScriptRoot 'test-results')
if ($LASTEXITCODE -ne 0) { throw "Tests failed: $LASTEXITCODE" }

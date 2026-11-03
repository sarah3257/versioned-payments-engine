$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$localSdk = Join-Path (Split-Path $projectRoot) '.tools/dotnet/dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $projectRoot
try { & $dotnetCommand run --project 'src/Payments.Api/Payments.Api.csproj' --no-launch-profile }
finally { Pop-Location }

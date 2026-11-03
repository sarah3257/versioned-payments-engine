$ErrorActionPreference = 'Stop'
$localSdk = Join-Path (Split-Path $PSScriptRoot) '.tools/dotnet/dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
Push-Location $PSScriptRoot
try { & $dotnetCommand test 'tests/Payments.Tests/Payments.Tests.csproj' --logger 'trx;LogFileName=engine-tests.trx' --results-directory 'test-results'; if ($LASTEXITCODE) { throw 'Tests failed' } }
finally { Pop-Location }

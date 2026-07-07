# Runs Ostraplan's xUnit test suite and prints a pass / skip / fail summary.
#
# Most tests are game-free and run on any machine. Tests that genuinely need a local
# Ostranauts install (the Law parity corpus, real prices, sprite rendering) report as
# SKIPPED — not passed — when the game is absent, so a green run is always honest.
# See docs/TESTING.md.
#
#   .\test.ps1                        # run everything (Debug)
#   .\test.ps1 -Filter Rooms          # only tests whose full name contains "Rooms"
#   .\test.ps1 -Configuration Release
param(
    [string]$Filter,
    [string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'tests\Ostraplan.Tests\Ostraplan.Tests.csproj'

$dotnetArgs = @('test', $proj, '-c', $Configuration, '--nologo')
if ($Filter) { $dotnetArgs += @('--filter', "FullyQualifiedName~$Filter") }

& dotnet @dotnetArgs
$code = $LASTEXITCODE
if ($code -ne 0) { Write-Host "`nTests FAILED (exit $code)." -ForegroundColor Red }
else { Write-Host "`nTests passed." -ForegroundColor Green }
exit $code

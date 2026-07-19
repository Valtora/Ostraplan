# Builds the end-user release artifacts with Velopack: an installer
# (Ostraplan-win-Setup.exe), a portable zip (Ostraplan-win-Portable.zip), the
# update package (*-full.nupkg) and the update manifest (releases.win.json).
# Output: publish\releases\. Upload the whole folder with `vpk upload github`
# (see docs\usage.md / README).
#
# Close the running app first: it locks its own exe and the publish will fail.
# -NoLaunch is accepted for old scripts/agent runs (this script never launches
# the app itself, so it's a no-op — the artifacts are installers, not the app).
param([switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot
$rawDir = Join-Path $root 'publish\raw'          # plain self-contained publish (what Velopack packs)
$relDir = Join-Path $root 'publish\releases'     # the release artifacts
$csproj = Join-Path $root 'src\Ostraplan.App\Ostraplan.App.csproj'
$icon   = Join-Path $root 'src\Ostraplan.App\app.ico'

# vpk (the Velopack CLI) is a global dotnet tool. Install it once with:
#   dotnet tool install -g vpk
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "vpk (the Velopack CLI) is not installed. Run:  dotnet tool install -g vpk"
}

# 1) Plain self-contained publish to a directory. NOT single-file: Velopack does
#    its own bundling, and a normal layout keeps the WPF native DLLs
#    (PresentationNative, wpfgfx, D3DCompiler) beside the exe so they load
#    without the single-file self-extract dance the old build needed.
if (Test-Path $rawDir) { Remove-Item $rawDir -Recurse -Force }
dotnet publish $csproj -c Release -r win-x64 `
    --self-contained true `
    -p:DebugType=None `
    -o $rawDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$rawExe = Join-Path $rawDir 'Ostraplan.exe'
if (-not (Test-Path $rawExe)) { throw "Publish did not produce $rawExe." }

# 2) Smoke-test the PUBLISHED exe (not bin\Release): --smoke shows and closes a
#    native-backed WPF window, then exits. This catches a native-load or theming
#    regression here instead of on a user's machine.
$smoke = Start-Process -FilePath $rawExe -ArgumentList '--smoke' -Wait -PassThru -NoNewWindow
if ($smoke.ExitCode -ne 0) { throw "Published exe failed its WPF smoke test (exit $($smoke.ExitCode)). Do NOT ship this build." }

# 3) Version from the built exe (the single source of truth: csproj <Version>).
$ver = ((Get-Item $rawExe).VersionInfo.ProductVersion -split '\+')[0]
if (-not $ver) { throw "Could not read a version from $rawExe." }

# 4) Pack with Velopack. Produces installer + portable + update package + manifest
#    in $relDir. --channel defaults to 'win', so the manifest is releases.win.json
#    (exactly what the in-app UpdateManager reads on Windows).
if (Test-Path $relDir) { Remove-Item $relDir -Recurse -Force }
vpk pack `
    --packId Ostraplan `
    --packVersion $ver `
    --packDir $rawDir `
    --mainExe Ostraplan.exe `
    --packTitle Ostraplan `
    --packAuthors Valtora `
    --icon $icon `
    --outputDir $relDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed with exit code $LASTEXITCODE." }

"`nRelease artifacts (v$ver)  ->  $relDir" | Write-Output
Get-ChildItem $relDir | Sort-Object Name | ForEach-Object {
    "  {0,-36} {1,8:N1} KB" -f $_.Name, ($_.Length / 1KB)
}
"`nWPF smoke passed. Publish with:" | Write-Output
"  vpk upload github --outputDir publish\releases --repoUrl https://github.com/Valtora/Ostraplan --publish --releaseName v$ver --tag v$ver --token (gh auth token)" | Write-Output
exit 0

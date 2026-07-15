# Builds the runnable release: ONE self-contained Ostraplan.exe (no .NET install
# needed on the machine). Output: publish\Ostraplan-vX.Y.Z.exe — just double-click it.
# Run this after code changes to refresh the exe.
# CLOSE the running Ostraplan first — a running app locks its own exe (checked below).
# -NoLaunch skips the launch at the end (for scripted/agent runs that shouldn't leave a window behind).
param([switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'publish'

# Refuse up front while a previously built exe is still running: it holds its own file open, so the versioned
# rename below dies on a bare "Access to the path ... is denied" that never says why. The usual culprit is this
# script itself — its last line launches what it built, so it's typically the previous run's window. Prompt
# rather than kill: that window may hold an unsaved design. Checked before the build, so it fails in a second
# rather than after a minute of publishing.
$held = @(Get-Process -Name 'Ostraplan*' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path -like (Join-Path $out '*') })
if ($held) {
    $list = ($held | ForEach-Object { '    {0}  (PID {1})' -f $_.Path, $_.Id }) -join "`n"
    throw "Close the running Ostraplan before publishing - it locks its own exe:`n$list`n`nThis script launches the exe it builds, so this is usually the previous run's window. Re-run with -NoLaunch to stop it happening again."
}

# IncludeNativeLibrariesForSelfExtract is REQUIRED for WPF single-file: without it
# the app dies at the first window with a DllNotFoundException (PresentationNative /
# wpfgfx / D3DCompiler can't load from the in-memory bundle). This flag extracts the
# native libs to a temp folder on first run so LoadLibrary finds them.
dotnet publish (Join-Path $PSScriptRoot 'src\Ostraplan.App\Ostraplan.App.csproj') -c Release -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$exe = Get-Item (Join-Path $out 'Ostraplan.exe')

# Smoke-test the PUBLISHED single-file exe (not just bin\Release): a real launch of
# WPF's native visual stack (--smoke shows and closes an offscreen window, then exits).
# This is what catches single-file-only native-load failures a bin\Release run cannot.
& $exe.FullName --smoke | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Published exe failed its WPF smoke test (exit $LASTEXITCODE) - single-file WPF is broken, do NOT ship this build." }

# Name the validated artifact with its version (Ostraplan-vX.Y.Z.exe), replacing
# any previously-built versioned exe so publish\ holds just the current release.
$ver = ($exe.VersionInfo.ProductVersion -split '\+')[0]
Get-ChildItem $out -Filter 'Ostraplan-v*.exe' -ErrorAction SilentlyContinue | Remove-Item -Force
$named = Join-Path $out "Ostraplan-v$ver.exe"
Move-Item $exe.FullName $named -Force
$exe = Get-Item $named

"`n{0}`n  v{1}   {2:N1} MB   single-file, self-contained win-x64   (WPF smoke passed)" -f `
    $exe.FullName, $exe.VersionInfo.ProductVersion, ($exe.Length / 1MB)

# Launch the freshly-built exe so the release can be eyeballed immediately.
# Start-Process is non-blocking, so the GUI opens and this script returns. Note this is what leaves the exe
# locked for the NEXT run — the preflight check at the top turns that into a clear message; -NoLaunch avoids it.
if (-not $NoLaunch) { Start-Process -FilePath $exe.FullName }
exit 0

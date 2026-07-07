# Builds the runnable release: ONE self-contained Ostraplan.exe (no .NET install
# needed on the machine). Output: publish\Ostraplan.exe — just double-click it.
# Run this after code changes to refresh the exe.
# CLOSE the running Ostraplan first — a running app locks its own exe.
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'publish'

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
# Start-Process is non-blocking, so the GUI opens and this script returns.
Start-Process -FilePath $exe.FullName
exit 0

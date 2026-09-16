# Tests, publishes the self-contained app into dist\app and compiles the installer.
param([switch]$SkipTests, [switch]$SkipInstaller)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not (Test-Path "$root\third_party\engine\sing-box.exe") -or -not (Test-Path "$root\third_party\engine\wintun.dll")) {
    & "$PSScriptRoot\fetch-deps.ps1"
}

if (-not $SkipTests) {
    dotnet test "$root\tests\MoonlightTunneling.Core.Tests" -c Release
    if ($LASTEXITCODE) { throw "tests failed" }
}

$out = "$root\dist\app"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish "$root\src\MoonlightTunneling.App\MoonlightTunneling.App.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out
# No EnableCompressionInSingleFile: it made the installer bigger (LZMA can't
# squeeze pre-compressed data) and decompresses the whole app on every start.
if ($LASTEXITCODE) { throw "publish failed" }
foreach ($f in "MoonlightTunneling.exe", "engine\sing-box.exe", "engine\wintun.dll") {
    if (-not (Test-Path "$out\$f")) { throw "missing $f in publish output" }
}

if (-not $SkipInstaller) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found" }
    & $iscc /Q "$root\installer\MoonlightTunneling.iss"
    if ($LASTEXITCODE) { throw "installer build failed" }
    Get-ChildItem "$root\installer\output\*.exe" | ForEach-Object { "{0}  {1:N1} MB" -f $_.FullName, ($_.Length / 1MB) }
}

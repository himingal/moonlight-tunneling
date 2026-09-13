# Tests, publishes the self-contained app into dist\app and compiles the installer.
param([switch]$SkipTests, [switch]$SkipInstaller)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not (Test-Path "$root\third_party\engine\sing-box.exe") -or -not (Test-Path "$root\third_party\engine\wintun.dll")) {
    & "$PSScriptRoot\fetch-deps.ps1"
}

if (-not $SkipTests) {
    dotnet test "$root\tests\MingalTunnel.Core.Tests" -c Release
    if ($LASTEXITCODE) { throw "tests failed" }
}

$out = "$root\dist\app"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish "$root\src\MingalTunnel.App\MingalTunnel.App.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out
if ($LASTEXITCODE) { throw "publish failed" }
foreach ($f in "MingalTunnel.exe", "engine\sing-box.exe", "engine\wintun.dll") {
    if (-not (Test-Path "$out\$f")) { throw "missing $f in publish output" }
}

if (-not $SkipInstaller) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found" }
    & $iscc /Q "$root\installer\MingalTunnel.iss"
    if ($LASTEXITCODE) { throw "installer build failed" }
    Get-ChildItem "$root\installer\output\*.exe" | ForEach-Object { "{0}  {1:N1} MB" -f $_.FullName, ($_.Length / 1MB) }
}

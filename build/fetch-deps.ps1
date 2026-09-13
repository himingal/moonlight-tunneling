# Downloads the pinned third-party binaries into third_party\engine and
# verifies them against the hashes below. Nothing is downloaded at runtime:
# the installer ships exactly these files.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$engine = Join-Path $root "third_party\engine"
New-Item -ItemType Directory -Force $engine | Out-Null

$deps = @(
    @{ Name = "sing-box"; Url = "https://github.com/SagerNet/sing-box/releases/download/v1.14.0/sing-box-1.14.0-windows-amd64.zip"
       Sha256 = "3ffb56267da14e287be48bd10cf7e6505260125bad940b75101fbb4d5d58e5d6"; Pick = "sing-box.exe"; Match = "sing-box\.exe$" },
    @{ Name = "wintun"; Url = "https://www.wintun.net/builds/wintun-0.14.1.zip"
       Sha256 = "07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51"; Pick = "wintun.dll"; Match = "\\amd64\\wintun\.dll$" }
)

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("mt-deps-" + [guid]::NewGuid())
New-Item -ItemType Directory $tmp | Out-Null
try {
    foreach ($d in $deps) {
        $zip = Join-Path $tmp "$($d.Name).zip"
        Write-Host "Downloading $($d.Name)..."
        Invoke-WebRequest -Uri $d.Url -OutFile $zip -UseBasicParsing
        $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($d.Sha256 -notlike "*_SHA" -and $hash -ne $d.Sha256) {
            throw "$($d.Name): SHA256 mismatch (got $hash, expected $($d.Sha256))"
        }
        Write-Host "  sha256 $hash"
        $out = Join-Path $tmp $d.Name
        Expand-Archive $zip -DestinationPath $out -Force
        $file = Get-ChildItem $out -Recurse -File | Where-Object { $_.FullName -match $d.Match } | Select-Object -First 1
        if (-not $file) { throw "$($d.Name): $($d.Pick) not found in archive" }
        Copy-Item $file.FullName (Join-Path $engine $d.Pick) -Force
    }
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
& (Join-Path $engine "sing-box.exe") version | Select-Object -First 1

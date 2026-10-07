param(
    [string]$Version = "v0.1.0",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$releaseRoot = Join-Path $repoRoot (Join-Path "releases" $Version)

& (Join-Path $repoRoot "build.ps1") -Configuration $Configuration -OutputRoot (Join-Path $repoRoot "artifacts")
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

Remove-Item -Recurse -Force $releaseRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
Copy-Item (Join-Path $repoRoot "README.md") $releaseRoot
Copy-Item (Join-Path $repoRoot "README.en.md") $releaseRoot
Copy-Item (Join-Path $repoRoot "RESEARCH.md") $releaseRoot

$runtimes = @(
    @{ Rid = "win-x64"; Name = "docker-transfer.exe" },
    @{ Rid = "win-arm64"; Name = "docker-transfer.exe" },
    @{ Rid = "linux-x64"; Name = "docker-transfer" },
    @{ Rid = "linux-arm64"; Name = "docker-transfer" },
    @{ Rid = "osx-x64"; Name = "docker-transfer" },
    @{ Rid = "osx-arm64"; Name = "docker-transfer" }
)

$packageRows = [System.Collections.Generic.List[string]]::new()
foreach ($runtime in $runtimes) {
    $source = Join-Path $repoRoot (Join-Path "artifacts" $runtime.Rid)
    $packageDir = Join-Path $releaseRoot ("docker-transfer-" + $Version + "-" + $runtime.Rid)
    New-Item -ItemType Directory -Force -Path $packageDir | Out-Null
    Copy-Item (Join-Path $source $runtime.Name) $packageDir
    Copy-Item (Join-Path $source "SHA256SUMS") $packageDir
    Copy-Item (Join-Path $repoRoot "README.md") $packageDir
    Copy-Item (Join-Path $repoRoot "README.en.md") $packageDir

    $zip = Join-Path $releaseRoot ("docker-transfer-" + $Version + "-" + $runtime.Rid + ".zip")
    Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $zip -CompressionLevel Optimal
    $zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $packageRows.Add("$zipHash  $(Split-Path $zip -Leaf)")
}

$packageRows | Set-Content -Encoding ascii (Join-Path $releaseRoot "RELEASE-SHA256SUMS")
@"
$Version Docker Transfer release

Interactive cross-platform Docker discovery and backup CLI.

Platforms:
- Windows x64 / arm64
- Linux x64 / arm64
- macOS Intel / Apple Silicon

Each platform ZIP includes the executable, SHA256SUMS, and Chinese/English README files.
"@ | Set-Content -Encoding utf8 (Join-Path $releaseRoot "RELEASE-NOTES.md")

Write-Output "Release packages created under $releaseRoot"

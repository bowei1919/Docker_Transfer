param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = "artifacts"
)

$ErrorActionPreference = "Stop"
$targets = @(
    @{ Rid = "win-x64"; Name = "docker-transfer.exe" },
    @{ Rid = "win-arm64"; Name = "docker-transfer.exe" },
    @{ Rid = "linux-x64"; Name = "docker-transfer" },
    @{ Rid = "linux-arm64"; Name = "docker-transfer" },
    @{ Rid = "osx-x64"; Name = "docker-transfer" },
    @{ Rid = "osx-arm64"; Name = "docker-transfer" }
)

Remove-Item -Recurse -Force $OutputRoot -ErrorAction SilentlyContinue
foreach ($target in $targets) {
    $destination = Join-Path $OutputRoot $target.Rid
    dotnet publish .\DockerTransfer.csproj -c $Configuration -r $target.Rid --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o $destination
    $binary = Join-Path $destination $target.Name
    if (-not (Test-Path $binary)) { throw "Expected publish output was not produced: $binary" }
    $hash = (Get-FileHash $binary -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($target.Name)" | Set-Content -Encoding ascii (Join-Path $destination "SHA256SUMS")
}
Write-Output "Built $($targets.Count) self-contained artifacts under $OutputRoot"

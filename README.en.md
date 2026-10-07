# Docker Transfer

Docker Transfer is a cross-platform Docker migration tool. Run it without
arguments to open the interactive Chinese wizard, or use the command-line mode
for automation.

中文文档：[README.md](README.md)

## Current capabilities

- Discovers running and stopped containers automatically.
- Discovers images, tags, digests, named volumes, anonymous volumes, and bind mounts.
- Detects Docker Compose projects and standalone `docker run` containers.
- Captures ports, environment variables, networks, restart policies, health checks,
  and container configuration.
- Tolerates missing images: keeps the container configuration, records a warning,
  and continues scanning the host.
- Provides an interactive Chinese menu.
- Creates a backup ZIP containing image exports, volume archives, bind-mount
  archives, a manifest, and SHA-256 checksums.
- Ships as self-contained single-file binaries for Windows, Linux, and macOS on
  amd64 and arm64.

The current backup phase does not stop containers automatically. Database
consistent logical dumps, upload/download, and destination restore orchestration
are still being implemented.

## Interactive mode

Run the binary without arguments:

```powershell
docker-transfer.exe
```

The menu provides:

1. Scan the current Docker host
2. Create a backup package
3. Show command help
0. Exit

## Command-line mode

Write a discovery manifest:

```powershell
docker-transfer discover --output docker-manifest.json
```

Print JSON to stdout:

```powershell
docker-transfer discover --stdout
```

Create a backup package:

```powershell
docker-transfer backup --output docker-backup.zip
```

Set the Docker endpoint with `DOCKER_HOST` or `--endpoint`. Windows probes the
Docker Desktop named pipes by default; Linux and macOS use
`/var/run/docker.sock` by default.

## Release packages

Run `.\release.ps1 -Version v0.1.0` to create six platform ZIP packages. Each
package contains the target binary, both README files, and `SHA256SUMS`. The ZIP
files can then be uploaded as GitHub Release assets. Verify the checksum before
running a binary.

## Build from source

The .NET 10 SDK is required. Run:

```powershell
.\build.ps1
```

The output is written to `artifacts/<runtime>/` for:

- `win-x64`, `win-arm64`
- `linux-x64`, `linux-arm64`
- `osx-x64`, `osx-arm64`

## Reused open-source projects

See [RESEARCH.md](RESEARCH.md) for the project audit and reuse boundaries. The
implementation references the designs of `ctool`, `BackupDock`,
`docker-autocompose`, and `Repliqate` instead of reimplementing their existing
capabilities from scratch.

## License

The final open-source license for this project has not yet been selected and
must be decided before a public release.

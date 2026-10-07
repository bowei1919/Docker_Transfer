# Docker Transfer

This repository contains a cross-platform .NET CLI for Docker migration. The
formal implementation is the single-file `docker-transfer` executable, while
the original Python prototype remains useful as a small reference test.

The discovery command inventories running and stopped containers, image
IDs/tags/digests, named and anonymous volumes, bind mounts, networks, and Docker
Compose metadata through the Docker Engine API.

## Discover the current Docker host

```powershell
dotnet run -- discover --output .\docker-manifest.json
```

The command does not stop or modify containers. Review the generated manifest
before a later backup or restore operation. On Windows it probes Docker Desktop
named pipes; on Linux and macOS it uses `DOCKER_HOST` or `/var/run/docker.sock`.

If a stopped container still exists but its image was deleted from the host,
the scan continues. The manifest records the image ID with `missing: true` and a
warning; that image is skipped during export and must be restored separately
from a registry or another backup.

Create a downloadable backup package from the discovered resources:

```powershell
dotnet run -- backup --output .\docker-backup.zip
```

The first backup implementation exports image tar files and archives named
volumes and existing directory bind mounts through a temporary Alpine helper
container. It writes the manifest and SHA-256 for every payload into the ZIP.
Containers remain running during this phase; database-consistent stop/final-sync
and logical database dumps are separate migration steps still being added.

## Build for all desktop/server platforms

The build script produces self-contained single-file binaries for Windows,
Linux, and macOS on amd64 and arm64:

```powershell
.\build.ps1
```

Artifacts are written below `artifacts/<runtime>/` with a SHA-256 file. The
binary does not require Python or the .NET runtime on the destination machine.

The next implementation stages will add image export, volume/bind-mount backup,
database-aware logical dumps, archive checksums, transfer, and restore.

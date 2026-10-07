# Docker migration wheel audit

The following repositories were cloned through the configured HTTP proxy on
2026-10-07 and reviewed locally:

- `third_party/ctool` at `2486044d83610389247f7337945b560bc84b764c` — README says MIT. It discovers Compose files and named volumes and has working volume archive/restore flows, including Compose volume-name mapping. It does not scan arbitrary containers or archive images. No LICENSE file is present in the checkout, so it should be wrapped or vendored only after confirming upstream licensing.
- `third_party/BackupDock` at `ea44454a38bae8d2926b6610e748d05c7217b489` — Apache-2.0. It uses the Docker Python SDK to discover all containers, Compose metadata, bind mounts, volumes, environment files, and dependency ordering. It deliberately excludes image archival and standalone-container recreation, and describes itself as early alpha. Its test suite cannot import on Windows because it references the POSIX-only `signal.SIGHUP`; the relevant code targets Linux.
- `third_party/docker-autocompose` at `f9f210a274d8c990d0975dfe64a464ca3493a164` — GPLv2. It uses the Docker Python SDK and converts one or more current containers into Compose YAML, including mounts, environment, ports, networks, labels, entrypoint, and command. It is the reusable adapter for standalone `docker run` containers.
- `third_party/repliqate` at `2b12b6c07df23e831833cb2a98b6bf4913dabf14` — MIT. It uses Docker.DotNet to enumerate all containers at startup, inspect their mounts and volumes, and listen for container create/destroy events. Its backup selection is label-driven and it is not a migration archive tool.

## Current implementation

`docker_transfer.py` is the project discovery layer. It is read-only and uses
the Docker CLI so it works with Docker Desktop's Windows named pipe as well as
Linux socket setups. It records running and stopped containers, image IDs/tags/
digests, named and anonymous volumes, bind mounts, networks, restart policies,
health checks, environment, ports, and Compose labels. The JSON manifest is the
contract for later image export, volume backup, database dumps, transfer, and
restore steps.

The Docker daemon was not running during this audit, so live discovery was not
claimed. Unit tests use mocked inspect data and pass locally.

## Reuse decision

The implementation will call or wrap the existing projects at their natural
boundaries instead of copying their full backup engines:

1. Use the local discovery manifest as the single inventory source.
2. Use `docker-autocompose` for standalone container configuration generation.
3. Reuse `ctool`'s volume archive/mapping approach for Compose volumes after its
   license metadata is confirmed.
4. Borrow BackupDock's stop-order, restart-state, and safety validation ideas;
   do not depend on its POSIX-only process layer for the Windows controller.
5. Add image export, bind-mount archiving, database logical dumps, checksums,
   upload/download, and restore orchestration around the manifest.

## Cross-platform implementation status

The formal CLI is now a dependency-free .NET 10 console application. It uses
the Docker Engine HTTP API over `/var/run/docker.sock` or `DOCKER_HOST` on
Linux/macOS and Docker Desktop named pipes on Windows. `build.ps1` publishes
self-contained single-file binaries for `win-x64`, `win-arm64`, `linux-x64`,
`linux-arm64`, `osx-x64`, and `osx-arm64`; each artifact has a `SHA256SUMS`
file. The current `backup` command packages image exports and volume/bind-mount
archives into a ZIP without stopping containers.

The discovery API tolerates resources disappearing during a scan. In
particular, a retained container whose image was deleted is represented as a
missing image warning instead of aborting the entire inventory; backup skips
that image and preserves the warning in the manifest.

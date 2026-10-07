#!/usr/bin/env python3
"""Discover Docker resources and write a migration manifest.

The discovery phase is intentionally read-only. Backup and restore commands can
consume the generated manifest after it has been reviewed by an operator.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


class DockerError(RuntimeError):
    pass


def docker_json(*args: str) -> Any:
    command = ["docker", *args]
    try:
        completed = subprocess.run(
            command,
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
    except OSError as exc:
        raise DockerError(f"Unable to execute Docker CLI: {exc}") from exc
    if completed.returncode:
        detail = (completed.stderr or completed.stdout).strip()
        raise DockerError(f"{' '.join(command)} failed: {detail}")
    try:
        return json.loads(completed.stdout or "null")
    except json.JSONDecodeError as exc:
        raise DockerError(f"Docker returned invalid JSON for {' '.join(command)}") from exc


def docker_lines(*args: str) -> list[str]:
    command = ["docker", *args]
    try:
        completed = subprocess.run(
            command,
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
    except OSError as exc:
        raise DockerError(f"Unable to execute Docker CLI: {exc}") from exc
    if completed.returncode:
        detail = (completed.stderr or completed.stdout).strip()
        raise DockerError(f"{' '.join(command)} failed: {detail}")
    return [line.strip() for line in completed.stdout.splitlines() if line.strip()]


def labels_with_prefix(labels: dict[str, Any], prefix: str) -> dict[str, str]:
    return {
        key: str(value)
        for key, value in labels.items()
        if key.startswith(prefix)
    }


def classify_mount(mount: dict[str, Any]) -> str:
    mount_type = str(mount.get("Type", "unknown")).lower()
    if mount_type == "volume":
        name = mount.get("Name")
        return "anonymous_volume" if not name else "named_volume"
    if mount_type == "bind":
        return "bind_mount"
    return mount_type


def discover_container(container_id: str) -> dict[str, Any]:
    inspected = docker_json("container", "inspect", container_id)[0]
    config = inspected.get("Config") or {}
    host_config = inspected.get("HostConfig") or {}
    labels = config.get("Labels") or {}
    mounts = inspected.get("Mounts") or []
    image_ref = inspected.get("Config", {}).get("Image")
    image_id = inspected.get("Image")

    compose_keys = {
        "project": "com.docker.compose.project",
        "service": "com.docker.compose.service",
        "working_dir": "com.docker.compose.project.working_dir",
        "config_files": "com.docker.compose.project.config_files",
        "version": "com.docker.compose.version",
    }
    compose = {
        name: labels[key]
        for name, key in compose_keys.items()
        if labels.get(key)
    }

    return {
        "id": inspected.get("Id", container_id),
        "short_id": inspected.get("Id", container_id)[:12],
        "name": str(inspected.get("Name", "")).lstrip("/"),
        "state": inspected.get("State") or {},
        "image": {
            "reference": image_ref,
            "id": image_id,
        },
        "config": {
            "hostname": config.get("Hostname"),
            "user": config.get("User"),
            "working_dir": config.get("WorkingDir"),
            "entrypoint": config.get("Entrypoint"),
            "command": config.get("Cmd"),
            "environment": config.get("Env") or [],
            "labels": labels,
            "healthcheck": config.get("Healthcheck"),
        },
        "host_config": {
            "restart_policy": host_config.get("RestartPolicy") or {},
            "network_mode": host_config.get("NetworkMode"),
            "port_bindings": host_config.get("PortBindings") or {},
            "privileged": bool(host_config.get("Privileged")),
            "devices": host_config.get("Devices") or [],
            "cap_add": host_config.get("CapAdd") or [],
            "cap_drop": host_config.get("CapDrop") or [],
        },
        "mounts": [
            {
                "kind": classify_mount(mount),
                "type": mount.get("Type"),
                "name": mount.get("Name"),
                "source": mount.get("Source"),
                "destination": mount.get("Destination"),
                "mode": mount.get("Mode"),
                "rw": mount.get("RW"),
                "driver": (mount.get("Driver") or "local"),
                "volume_options": mount.get("VolumeOptions") or {},
            }
            for mount in mounts
        ],
        "networks": inspected.get("NetworkSettings", {}).get("Networks") or {},
        "compose": compose,
        "compose_labels": labels_with_prefix(labels, "com.docker.compose."),
    }


def discover_images(image_ids: set[str]) -> list[dict[str, Any]]:
    images: list[dict[str, Any]] = []
    for image_id in sorted(image_ids):
        inspected = docker_json("image", "inspect", image_id)[0]
        images.append(
            {
                "id": inspected.get("Id"),
                "created": inspected.get("Created"),
                "repo_tags": inspected.get("RepoTags") or [],
                "repo_digests": inspected.get("RepoDigests") or [],
                "architecture": inspected.get("Architecture"),
                "os": inspected.get("Os"),
                "size": inspected.get("Size"),
            }
        )
    return images


def discover_volumes(volume_names: set[str]) -> list[dict[str, Any]]:
    volumes: list[dict[str, Any]] = []
    for volume_name in sorted(volume_names):
        inspected = docker_json("volume", "inspect", volume_name)[0]
        volumes.append(
            {
                "name": inspected.get("Name", volume_name),
                "driver": inspected.get("Driver"),
                "mountpoint": inspected.get("Mountpoint"),
                "scope": inspected.get("Scope"),
                "labels": inspected.get("Labels") or {},
                "options": inspected.get("Options") or {},
            }
        )
    return volumes


def discover() -> dict[str, Any]:
    container_ids = docker_lines("ps", "-aq")
    containers = [discover_container(container_id) for container_id in container_ids]
    image_ids = {
        str(container["image"]["id"])
        for container in containers
        if container["image"].get("id")
    }
    volume_names = {
        str(mount["name"])
        for container in containers
        for mount in container["mounts"]
        if mount["kind"] in {"named_volume", "anonymous_volume"} and mount.get("name")
    }
    network_ids = docker_lines("network", "ls", "-q")
    network_details = [docker_json("network", "inspect", network_id)[0] for network_id in network_ids]

    compose_projects: dict[str, dict[str, Any]] = {}
    for container in containers:
        project = container["compose"].get("project")
        if not project:
            continue
        item = compose_projects.setdefault(
            project,
            {
                "name": project,
                "working_dir": container["compose"].get("working_dir"),
                "config_files": container["compose"].get("config_files"),
                "services": [],
            },
        )
        service = container["compose"].get("service")
        if service and service not in item["services"]:
            item["services"].append(service)

    return {
        "schema_version": 1,
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "docker": {
            "server_version": docker_json("version").get("Server", {}).get("Version"),
            "server_api_version": docker_json("version").get("Server", {}).get("ApiVersion"),
        },
        "containers": containers,
        "images": discover_images(image_ids),
        "volumes": discover_volumes(volume_names),
        "networks": [
            {
                "id": network.get("Id"),
                "name": network.get("Name"),
                "driver": network.get("Driver"),
                "scope": network.get("Scope"),
                "internal": network.get("Internal"),
                "labels": network.get("Labels") or {},
            }
            for network in network_details
        ],
        "compose_projects": list(compose_projects.values()),
    }


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Discover Docker resources for migration")
    parser.add_argument("--output", "-o", type=Path, default=Path("docker-manifest.json"))
    parser.add_argument("--stdout", action="store_true", help="Print JSON instead of writing a file")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv or sys.argv[1:])
    try:
        manifest = discover()
    except DockerError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2
    serialized = json.dumps(manifest, ensure_ascii=False, indent=2) + "\n"
    if args.stdout:
        print(serialized, end="")
    else:
        args.output.write_text(serialized, encoding="utf-8")
        print(
            f"discovered {len(manifest['containers'])} containers, "
            f"{len(manifest['images'])} images, {len(manifest['volumes'])} volumes, "
            f"{len(manifest['compose_projects'])} compose projects -> {args.output}"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

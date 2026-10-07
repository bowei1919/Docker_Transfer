import json
import unittest
from unittest.mock import patch

import docker_transfer


class DiscoveryTests(unittest.TestCase):
    @patch("docker_transfer.subprocess.run")
    def test_docker_lines_parses_plain_cli_output(self, run):
        run.return_value = type(
            "Completed", (), {"returncode": 0, "stdout": "one\n\ntwo\n", "stderr": ""}
        )()
        self.assertEqual(docker_transfer.docker_lines("ps", "-aq"), ["one", "two"])

    def test_classify_mount(self):
        self.assertEqual(docker_transfer.classify_mount({"Type": "bind"}), "bind_mount")
        self.assertEqual(
            docker_transfer.classify_mount({"Type": "volume", "Name": "app_data"}),
            "named_volume",
        )
        self.assertEqual(
            docker_transfer.classify_mount({"Type": "volume"}),
            "anonymous_volume",
        )

    @patch("docker_transfer.docker_json")
    def test_discover_container_keeps_compose_and_mount_metadata(self, docker_json):
        docker_json.return_value = [
            {
                "Id": "abcdef1234567890",
                "Name": "/web",
                "Image": "sha256:image-id",
                "Config": {
                    "Image": "nginx:latest",
                    "Env": ["PORT=80"],
                    "Labels": {
                        "com.docker.compose.project": "demo",
                        "com.docker.compose.service": "web",
                    },
                },
                "HostConfig": {"RestartPolicy": {"Name": "unless-stopped"}},
                "State": {"Status": "running"},
                "Mounts": [
                    {
                        "Type": "volume",
                        "Name": "demo_data",
                        "Source": "/var/lib/docker/volumes/demo_data/_data",
                        "Destination": "/data",
                        "RW": True,
                    },
                    {"Type": "bind", "Source": "C:/config", "Destination": "/etc/app"},
                ],
                "NetworkSettings": {"Networks": {"demo_default": {"Aliases": ["web"]}}},
            }
        ]
        result = docker_transfer.discover_container("abcdef123456")
        self.assertEqual(result["compose"]["project"], "demo")
        self.assertEqual(result["mounts"][0]["kind"], "named_volume")
        self.assertEqual(result["mounts"][1]["kind"], "bind_mount")
        self.assertEqual(result["host_config"]["restart_policy"]["Name"], "unless-stopped")

    @patch("docker_transfer.docker_lines")
    @patch("docker_transfer.docker_json")
    def test_discover_builds_inventory_and_compose_groups(self, docker_json, docker_lines):
        docker_lines.side_effect = lambda *args: {
            ("ps", "-aq"): ["container-1"],
            ("network", "ls", "-q"): ["network-1"],
        }[args]

        container = {
            "Id": "container-123456",
            "Name": "/db",
            "Image": "sha256:image-1",
            "Config": {
                "Image": "postgres:16",
                "Labels": {
                    "com.docker.compose.project": "demo",
                    "com.docker.compose.service": "db",
                },
            },
            "HostConfig": {},
            "State": {"Status": "running"},
            "Mounts": [{"Type": "volume", "Name": "demo_db", "Destination": "/var/lib/postgresql/data"}],
            "NetworkSettings": {"Networks": {}},
        }

        def json_side_effect(*args):
            if args == ("container", "inspect", "container-1"):
                return [container]
            if args == ("image", "inspect", "sha256:image-1"):
                return [{"Id": "sha256:image-1", "RepoTags": ["postgres:16"], "RepoDigests": []}]
            if args == ("volume", "inspect", "demo_db"):
                return [{"Name": "demo_db", "Driver": "local", "Mountpoint": "/var/lib/docker/volumes/demo_db/_data"}]
            if args == ("network", "inspect", "network-1"):
                return [{"Id": "network-1", "Name": "bridge", "Driver": "bridge", "Scope": "local"}]
            if args == ("version",):
                return {"Server": {"Version": "27.0", "ApiVersion": "1.46"}}
            raise AssertionError(args)

        docker_json.side_effect = json_side_effect
        result = docker_transfer.discover()
        self.assertEqual(len(result["containers"]), 1)
        self.assertEqual(result["containers"][0]["name"], "db")
        self.assertEqual(result["compose_projects"][0]["name"], "demo")
        self.assertEqual(result["images"][0]["repo_tags"], ["postgres:16"])
        self.assertEqual(result["volumes"][0]["name"], "demo_db")


if __name__ == "__main__":
    unittest.main()

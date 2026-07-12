#!/usr/bin/env python3
"""Publish each release target twice and fail if any output bytes differ."""

from __future__ import annotations

import hashlib
import subprocess
import sys
import tempfile
from pathlib import Path


TARGETS = (
    ("src/ClothingRepacker.Cli/ClothingRepacker.Cli.csproj", "LinuxX64"),
    ("src/ClothingRepacker.Cli/ClothingRepacker.Cli.csproj", "WinX64"),
    ("src/ClothingRepacker.Gui/ClothingRepacker.Gui.csproj", "LinuxX64"),
    ("src/ClothingRepacker.Gui/ClothingRepacker.Gui.csproj", "WinX64"),
)


def digest_tree(root: Path) -> dict[str, str]:
    files: dict[str, str] = {}
    for path in sorted(path for path in root.rglob("*") if path.is_file()):
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        files[str(path.relative_to(root))] = digest
    return files


def main() -> int:
    repo_root = Path(__file__).resolve().parent.parent
    with tempfile.TemporaryDirectory(prefix="red40-reproducible-") as temporary_directory:
        root = Path(temporary_directory)
        for index, (project_name, profile) in enumerate(TARGETS):
            project = repo_root / project_name
            outputs = (root / str(index) / "first", root / str(index) / "second")
            for output in outputs:
                command = [
                    "dotnet",
                    "publish",
                    str(project),
                    f"-p:PublishProfile={profile}",
                    "-p:Configuration=Release",
                    "-p:SelfContained=true",
                    "-p:Deterministic=true",
                    "-p:DeterministicSourcePaths=true",
                    "-p:ContinuousIntegrationBuild=true",
                    f"-p:PathMap={repo_root}=/_/red40_clothing_packer",
                    f"-p:PublishDir={output}/",
                    "--no-restore",
                ]
                try:
                    subprocess.run(command, cwd=repo_root, check=True)
                except subprocess.CalledProcessError as error:
                    print(
                        f"Publish failed for {project_name} ({profile}), exit {error.returncode}",
                        file=sys.stderr,
                    )
                    return error.returncode or 1

            first, second = map(digest_tree, outputs)
            if first != second:
                print(f"Non-reproducible publish: {project_name} ({profile})", file=sys.stderr)
                for path in sorted(set(first) | set(second)):
                    if first.get(path) != second.get(path):
                        print(f"  differs: {path}", file=sys.stderr)
                return 1
            print(f"Reproducible: {project_name} ({profile})")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())

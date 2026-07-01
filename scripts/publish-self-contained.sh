#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cli_project="$repo_root/src/ClothingRepacker.Cli/ClothingRepacker.Cli.csproj"
gui_project="$repo_root/src/ClothingRepacker.Gui/ClothingRepacker.Gui.csproj"
publish_root="$repo_root/artifacts/publish"

rm -rf \
  "$publish_root/cli/linux-x64" \
  "$publish_root/cli/win-x64" \
  "$publish_root/gui/linux-x64" \
  "$publish_root/gui/win-x64"

dotnet publish "$cli_project" -p:PublishProfile=LinuxX64
dotnet publish "$cli_project" -p:PublishProfile=WinX64
dotnet publish "$gui_project" -p:PublishProfile=LinuxX64
dotnet publish "$gui_project" -p:PublishProfile=WinX64

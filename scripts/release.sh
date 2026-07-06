#!/usr/bin/env bash

set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <version> [--prerelease]"
  echo "Example: $0 1.2.3"
  echo "Example: $0 1.2.3 --prerelease"
  exit 1
fi

version="$1"
tag="v$version"
prerelease_flag=""

if [[ "${2:-}" == "--prerelease" ]]; then
  prerelease_flag="--prerelease"
fi

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[A-Za-z0-9.-]+)?$ ]]; then
  echo "Invalid version: $version"
  echo "Use a SemVer-like version such as 1.2.3 or 1.2.3-beta.1"
  exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet is not installed or not on PATH"
  exit 1
fi

if ! command -v gh >/dev/null 2>&1; then
  echo "GitHub CLI is not installed or not on PATH"
  exit 1
fi

if ! gh auth status >/dev/null 2>&1; then
  echo "GitHub CLI is not authenticated"
  echo "Run: gh auth login"
  exit 1
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

cli_project="$repo_root/src/ClothingRepacker.Cli/ClothingRepacker.Cli.csproj"
gui_project="$repo_root/src/ClothingRepacker.Gui/ClothingRepacker.Gui.csproj"

publish_root="$repo_root/artifacts/publish"
release_root="$repo_root/artifacts/release"

echo "Preparing release $tag"

rm -rf \
  "$publish_root/cli/linux-x64" \
  "$publish_root/cli/win-x64" \
  "$publish_root/gui/linux-x64" \
  "$publish_root/gui/win-x64" \
  "$release_root"

mkdir -p "$release_root"

echo "Restoring projects..."
dotnet restore "$repo_root"

echo "Publishing CLI Linux x64..."
dotnet publish "$cli_project" \
  -p:PublishProfile=LinuxX64 \
  -p:Version="$version" \
  -p:AssemblyVersion="$version" \
  -p:FileVersion="$version"

echo "Publishing CLI Windows x64..."
dotnet publish "$cli_project" \
  -p:PublishProfile=WinX64 \
  -p:Version="$version" \
  -p:AssemblyVersion="$version" \
  -p:FileVersion="$version"

echo "Publishing GUI Linux x64..."
dotnet publish "$gui_project" \
  -p:PublishProfile=LinuxX64 \
  -p:Version="$version" \
  -p:AssemblyVersion="$version" \
  -p:FileVersion="$version"

echo "Publishing GUI Windows x64..."
dotnet publish "$gui_project" \
  -p:PublishProfile=WinX64 \
  -p:Version="$version" \
  -p:AssemblyVersion="$version" \
  -p:FileVersion="$version"

echo "Collecting release assets..."

cp "$publish_root/cli/linux-x64/ClothingRepacker.Cli" \
  "$release_root/ClothingRepacker-CLI-linux-x64-v$version"

cp "$publish_root/cli/win-x64/ClothingRepacker.Cli.exe" \
  "$release_root/ClothingRepacker-CLI-windows-x64-v$version.exe"

cp "$publish_root/gui/linux-x64/ClothingRepacker.Gui" \
  "$release_root/ClothingRepacker-GUI-linux-x64-v$version"

cp "$publish_root/gui/win-x64/ClothingRepacker.Gui.exe" \
  "$release_root/ClothingRepacker-GUI-windows-x64-v$version.exe"

chmod +x \
  "$release_root/ClothingRepacker-CLI-linux-x64-v$version" \
  "$release_root/ClothingRepacker-GUI-linux-x64-v$version"

release_notes="$release_root/release-notes.md"

cat > "$release_notes" <<EOF
## ClothingRepacker $version

### Downloads

| Download | Platform | App |
|---|---|---|
| CLI - Linux x64 | Linux x64 | CLI |
| CLI - Windows x64 | Windows x64 | CLI |
| GUI - Linux x64 | Linux x64 | GUI |
| GUI - Windows x64 | Windows x64 | GUI |
EOF

echo "Creating GitHub release $tag..."

gh release create "$tag" \
  "$release_root/ClothingRepacker-CLI-linux-x64-v$version#CLI - Linux x64" \
  "$release_root/ClothingRepacker-CLI-windows-x64-v$version.exe#CLI - Windows x64" \
  "$release_root/ClothingRepacker-GUI-linux-x64-v$version#GUI - Linux x64" \
  "$release_root/ClothingRepacker-GUI-windows-x64-v$version.exe#GUI - Windows x64" \
  --title "ClothingRepacker $version" \
  --notes-file "$release_notes" \
  $prerelease_flag

echo "Release created: $tag"

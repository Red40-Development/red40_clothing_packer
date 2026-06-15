#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/src/ClothingRepacker.Cli/ClothingRepacker.Cli.csproj"

dotnet publish "$project" -p:PublishProfile=LinuxX64
dotnet publish "$project" -p:PublishProfile=WinX64

#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "Usage: $0 <source_path> <new_root_path>"
  exit 1
fi

SOURCE_PATH="$1"
DEST_ROOT="$2"
PLACEHOLDER_TEXT="empty test file"

if [ ! -d "$SOURCE_PATH" ]; then
  echo "Error: source path is not a directory: $SOURCE_PATH"
  exit 1
fi

SOURCE_PATH="$(realpath "$SOURCE_PATH")"
DEST_ROOT="$(realpath -m "$DEST_ROOT")"

mkdir -p "$DEST_ROOT"

copy_tree_optimized() {
  local src="$1"
  local dst="$2"

  mkdir -p "$dst"

  find -L "$src" -mindepth 1 \
    \( -type d -name '.*' -prune \) -o \
    -type d -print0 -o \
    -type f -print0 |
  while IFS= read -r -d '' path; do
    local rel="${path#$src/}"
    local out="$dst/$rel"

    if [ -d "$path" ]; then
      mkdir -p "$out"
      continue
    fi

    mkdir -p "$(dirname "$out")"

    case "$path" in
      *.meta|*.ymt|*.xml|*.lua)
        cp --dereference -- "$path" "$out"
        ;;
      *)
        printf '%s\n' "$PLACEHOLDER_TEXT" > "$out"
        ;;
    esac
  done
}

copy_tree_optimized "$SOURCE_PATH" "$DEST_ROOT"

echo "Done."
echo "Copied from: $SOURCE_PATH"
echo "Created at:  $DEST_ROOT"
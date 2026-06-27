# Red40 Clothing Repacker

`Red40 Clothing Repacker` is a .NET CLI for analyzing GTA V/FiveM clothing resources, merging ped variation collections into a generated resource, and optionally applying those changes back to your working resource set with a reversible backup manifest.

It supports:

- Scanning resource folders for `.ymt` and `.ymt.xml` clothing data
- Exporting binary `.ymt` files to XML for inspection
- Generating a merge plan as JSON
- Building a new merged resource without touching the originals
- Applying the plan to your resources with backup metadata
- Restoring everything from the backup manifest

# [**Red40 Development**](https://red40.dev/scripts)
Like this tool and want to support further development? Checkout our store [Red40 Development](https://red40.dev/scripts)

## Download and Install

### Option 1: Download built binaries

Download the [release version](https://github.com/Red40-Development/red40_clothing_packer) approriate for your architecture (Windows/Linux x86-64 builds)

Copy the executable to your clothing folder

### Option 2: Clone and run from source

Prerequisites:

- Git
- .NET 10 SDK

Clone the repository with the CodeWalker submodule:

```bash
git clone --recurse-submodules https://github.com/Red40-Development/red40_clothing_packer.git
cd red40_clothing_packer
```

Run the CLI directly:

```bash
dotnet run --project src/ClothingRepacker.Cli -- --help
```
### Command Summary

If you are running from source, use `dotnet run --project src/ClothingRepacker.Cli -- ...` instead.

```bash
ClothingRepacker.Cli analyze --resources <path> --target-resource <name> --out <plan.json>
  [--max-drawables-per-component <128>] [--max-drawables-per-prop <255>]

ClothingRepacker.Cli build --plan <plan.json> --out <folder>
  [--include-ymt-xml <true|false>] [--include-debug-client <true|false>]
ClothingRepacker.Cli apply --plan <plan.json> --backup-root <folder>
ClothingRepacker.Cli restore --backup-manifest <backup-manifest.json>
ClothingRepacker.Cli validate --plan <plan.json>
ClothingRepacker.Cli validate --resources <path>
ClothingRepacker.Cli export-xml --folder <path> [--overwrite]
```

### How to use

Assume your clothing resources live in the same directory as the executable.

Open a terminal (Powershell/Terminal) and navigate to your folder with all your clothing assets such as `[clothing]`

Export `.ymt` files to XML:

```bash
ClothingRepacker.Cli export-xml --folder .
```

Create a merge plan:

```bash
ClothingRepacker.Cli analyze \
  --resources . \
  --target-resource zz_merged_clothing_meta \
  --out plan.json
```

Validate the generated plan:

```bash
ClothingRepacker.Cli validate --plan plan.json
```

Build the merged resource into a separate output folder (disable the ymt-xml or debug commands as appropriate):

```bash
ClothingRepacker.Cli build \
  --plan plan.json \
  --out . \
  --include-ymt-xml true \
  --include-debug-client true
```

This writes a generated resource like:

- `./zz_merged_clothing_meta/fxmanifest.lua`
- `./zz_merged_clothing_meta/stream/*.ymt`
- `./zz_merged_clothing_meta/stream/*.ymt.xml`
- `./zz_merged_clothing_meta/data/*.meta`
- `./zz_merged_clothing_meta/client/validate_collections.lua`

The two optional `build` toggles both default to `true`:

- `--include-ymt-xml false` skips writing the preview `stream/*.ymt.xml` files
- `--include-debug-client false` skips generating `client/validate_collections.lua` and removes its `client_script` line from `fxmanifest.lua`

Creature metadata is preserved and remapped when it has a matching source `ShopPedApparel` `creatureMetaData` reference. Creature metadata without a matching shop metadata reference is treated as broken, warned about during analyze, skipped during build, and only moved into the backup during apply.

Apply the plan to your actual resource set:

```bash
ClothingRepacker.Cli apply \
  --plan plan.json \
  --backup-root ./backups
```

`apply` does three important things:

- Renames stream files according to the plan
- Copies original source `.ymt` files into a timestamped backup folder, then removes them from the source resources
- Creates the generated merged resource as a sibling folder next to your resources root

## How to Undo What the Tool Did

Every `apply` run writes a timestamped backup folder under your chosen `--backup-root`, including a `backup-manifest.json` file.

Example:

```text
./backups/2026-06-16T103000Z/backup-manifest.json
```

To undo an `apply`, run:

```bash
ClothingRepacker.Cli restore \
  --backup-manifest ./backups/2026-06-16T103000Z/backup-manifest.json
```

`restore` will:

- Delete the generated merged resource created by `apply`
- Put backed-up source `.ymt` files back in their original locations
- Move renamed stream files back to their original names

NOTE:

- Keep the entire timestamped backup directory until you have verified the restore worked
- Prefer running `build` first so you can inspect output before modifying your real resources with `apply`

## Credits
- Red40 Development (c) 2026
- [dexyfex/CodeWalker](https://github.com/dexyfex/CodeWalker)

This project uses code and file-format handling from CodeWalker. Credit and thanks to the CodeWalker project for making GTA V/FiveM resource inspection and serialization work possible.

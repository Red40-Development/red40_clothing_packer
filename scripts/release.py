#!/usr/bin/env -S uv run --script
# /// script
# dependencies = [
#   "httpx>=0.27,<1",
# ]
# ///

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path
from urllib.parse import quote

import httpx


API_ROOT = "https://www.virustotal.com/api/v3"
POLL_ATTEMPTS = 60
POLL_DELAY_SECONDS = 10
VERSION_PATTERN = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?$")
CONVENTIONAL_COMMIT_PATTERN = re.compile(
    r"^(?P<type>[A-Za-z]+)(?:\((?P<scope>[^)]+)\))?(?P<breaking>!)?:\s+(?P<subject>.+)$"
)
PR_SUFFIX_PATTERN = re.compile(r"\s*\(#(?P<number>\d+)\)$")

CHANGELOG_TYPES = (
    (("feat", "feature"), "New Features", ":sparkles:"),
    (("fix", "bugfix"), "Bug Fixes", ":bug:"),
    (("perf",), "Performance Improvements", ":zap:"),
    (("refactor",), "Refactors", ":recycle:"),
    (("test", "tests"), "Tests", ":white_check_mark:"),
    (("build", "ci"), "Build System", ":construction_worker:"),
    (("doc", "docs"), "Documentation Changes", ":memo:"),
    (("style",), "Code Style Changes", ":art:"),
    (("chore",), "Chores", ":wrench:"),
    (("other",), "Other Changes", ":flying_saucer:"),
)
EXCLUDED_CHANGELOG_TYPES = {"build", "docs", "other", "style"}


class ReleaseError(RuntimeError):
    """Raised when a release cannot be prepared or published."""


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("version", help="Release version, for example 1.2.3")
    parser.add_argument("--prerelease", action="store_true", help="Mark the release as prerelease")
    return parser.parse_args()


def run(command: list[str], *, cwd: Path | None = None) -> None:
    subprocess.run(command, cwd=cwd, check=True)


def run_json(command: list[str]) -> object:
    result = subprocess.run(command, check=True, capture_output=True, text=True)
    try:
        payload = json.loads(result.stdout)
    except json.JSONDecodeError as error:
        raise ReleaseError(f"Command returned invalid JSON: {' '.join(command)}") from error
    return payload


def require_command(command: str) -> None:
    if shutil.which(command) is None:
        raise ReleaseError(f"{command} is not installed or not on PATH")


def latest_tag() -> str:
    result = subprocess.run(
        ["git", "tag", "--sort=-creatordate"],
        check=True,
        capture_output=True,
        text=True,
    )
    tags = [tag.strip() for tag in result.stdout.splitlines() if tag.strip()]
    if not tags:
        raise ReleaseError("At least one existing Git tag is required to generate release notes")
    return tags[0]


def previous_tag(tag: str) -> str:
    result = subprocess.run(
        ["git", "tag", "--sort=-creatordate", "--merged", tag],
        check=True,
        capture_output=True,
        text=True,
    )
    tags = [candidate.strip() for candidate in result.stdout.splitlines() if candidate.strip()]
    try:
        tags.remove(tag)
    except ValueError as error:
        raise ReleaseError(f"Could not find release tag locally: {tag}") from error
    if not tags:
        raise ReleaseError(f"At least one previous tag is required before {tag}")
    return tags[0]


def compare_commits(repo: str, base_tag: str, head_ref: str = "HEAD") -> list[dict]:
    comparison = run_json(
        [
            "gh",
            "api",
            "--paginate",
            "--slurp",
            f"repos/{repo}/compare/{quote(base_tag, safe='')}...{quote(head_ref, safe='')}",
        ]
    )
    # gh --paginate --slurp returns one comparison object per page.
    pages = comparison
    if isinstance(comparison, dict):
        pages = comparison.get("data", comparison)
    if not isinstance(pages, list):
        pages = [pages]
    commits: list[dict] = []
    for page in pages:
        if isinstance(page, dict) and isinstance(page.get("commits"), list):
            commits.extend(commit for commit in page["commits"] if isinstance(commit, dict))
    return commits


def changelog_subject(subject: str, commit: dict, repo: str) -> str:
    pr_match = PR_SUFFIX_PATTERN.search(subject)
    author = commit.get("author") or {}
    login = author.get("login") if isinstance(author, dict) else None
    if pr_match:
        suffix = f" *(PR #{pr_match['number']}"
        if login:
            suffix += f" by @{login}"
        return f"{subject[:pr_match.start()]}{suffix})*"
    return f"{subject} @{login}" if login else subject


def generate_release_notes(
    repo: str,
    version: str,
    *,
    target_ref: str = "HEAD",
) -> str:
    """Generate the release-note portion produced by changelog-action.

    The action requires the GitHub Actions runtime, so this uses the same GitHub
    compare API through gh while remaining runnable from a local checkout.
    """
    base_tag = previous_tag(target_ref) if target_ref != "HEAD" else latest_tag()
    commits = compare_commits(repo, base_tag, target_ref)
    parsed: list[dict] = []
    breaking: list[tuple[dict, str]] = []

    for commit in commits:
        message = str((commit.get("commit") or {}).get("message") or "")
        subject = message.splitlines()[0].strip()
        match = CONVENTIONAL_COMMIT_PATTERN.match(subject)
        if not match:
            continue
        item = {
            "type": match.group("type").lower(),
            "scope": match.group("scope"),
            "subject": match.group("subject"),
            "sha": str(commit.get("sha") or ""),
            "author": commit.get("author"),
        }
        parsed.append(item)
        body = "\n".join(message.splitlines()[1:])
        if match.group("breaking") or re.search(r"^BREAKING CHANGE:\s*", body, re.MULTILINE):
            note = re.search(r"^BREAKING CHANGE:\s*(.+(?:\n(?!\w[^:]*:).*)*)", body, re.MULTILINE)
            breaking.append((item, note.group(1).strip() if note else "Breaking change"))

    sections: list[str] = []
    if breaking:
        lines = ["### :boom: BREAKING CHANGES"]
        for commit, note in breaking:
            lines.append(
                f"- due to `{commit['sha'][:7]}` - "
                f"{changelog_subject(commit['subject'], commit, repo)}:\n\n  {note}"
            )
        sections.append("\n".join(lines))

    for types, header, icon in CHANGELOG_TYPES:
        if set(types) & EXCLUDED_CHANGELOG_TYPES:
            continue
        matching = [commit for commit in parsed if commit["type"] in types]
        if not matching:
            continue
        lines = [f"### {icon} {header}"]
        for commit in matching:
            scope = f"**{commit['scope']}**: " if commit["scope"] else ""
            lines.append(
                f"- `{commit['sha'][:7]}` - {scope}"
                f"{changelog_subject(commit['subject'], commit, repo)}"
            )
        sections.append("\n".join(lines))

    if sections:
        print(f"Generating changelog for {version} from {base_tag} to {target_ref}")
    else:
        print(f"No included conventional commits found since {base_tag} for {target_ref}")
    return "\n\n".join(sections)


def request_json(client: httpx.Client, method: str, url: str, **kwargs) -> dict:
    try:
        response = client.request(method, url, **kwargs)
        response.raise_for_status()
        return response.json()
    except (httpx.HTTPError, ValueError) as error:
        detail = ""
        if isinstance(error, httpx.HTTPStatusError):
            detail = f": {error.response.text[:500]}"
        raise ReleaseError(f"VirusTotal request failed{detail}") from error


def upload_to_virustotal(file: Path, client: httpx.Client) -> str:
    if not file.is_file():
        raise ReleaseError(f"Release asset does not exist: {file}")

    upload_url = request_json(client, "GET", f"{API_ROOT}/files/upload_url")["data"]

    with file.open("rb") as handle:
        response = request_json(
            client,
            "POST",
            upload_url,
            files={"file": (file.name, handle)},
        )

    try:
        return response["data"]["id"]
    except (KeyError, TypeError) as error:
        raise ReleaseError(f"VirusTotal returned an invalid upload response for {file}") from error


def existing_virustotal_stats_for_hash(file_hash: str, client: httpx.Client) -> dict | None:
    """Return cached VirusTotal results, or None when the hash is unknown."""
    try:
        response = client.get(f"{API_ROOT}/files/{file_hash}")
        response.raise_for_status()
        payload = response.json()
    except httpx.HTTPStatusError as error:
        if error.response.status_code == 404:
            return None
        detail = error.response.text[:500]
        raise ReleaseError(f"VirusTotal hash lookup failed: {detail}") from error
    except (httpx.HTTPError, ValueError) as error:
        raise ReleaseError("VirusTotal hash lookup failed") from error

    try:
        attributes = payload["data"]["attributes"]
        stats = attributes["last_analysis_stats"]
    except (KeyError, TypeError) as error:
        raise ReleaseError(f"VirusTotal returned no scan statistics for {file_hash}") from error
    if not isinstance(stats, dict):
        raise ReleaseError(f"VirusTotal returned invalid scan statistics for {file_hash}")
    stats["_bkav_likely_false_positive"] = any(
        str(engine).casefold() == "bkav"
        and isinstance(result, dict)
        and result.get("category") in {"malicious", "suspicious"}
        for engine, result in (attributes.get("last_analysis_results") or {}).items()
    )
    return stats


def existing_virustotal_stats(file: Path, client: httpx.Client) -> dict | None:
    return existing_virustotal_stats_for_hash(file_sha256(file), client)


def wait_for_virustotal(analysis_id: str, file: Path, client: httpx.Client) -> dict:
    for attempt in range(POLL_ATTEMPTS):
        response = request_json(client, "GET", f"{API_ROOT}/analyses/{analysis_id}")
        try:
            attributes = response["data"]["attributes"]
            status = attributes["status"]
        except (KeyError, TypeError) as error:
            raise ReleaseError(f"VirusTotal returned an invalid analysis response for {file}") from error

        if status == "completed":
            try:
                stats = attributes["stats"]
            except (KeyError, TypeError) as error:
                raise ReleaseError(f"VirusTotal returned no scan statistics for {file}") from error
            # The analysis response has aggregate stats, while the file response
            # contains the per-engine result needed to identify Bkav.
            file_stats = existing_virustotal_stats(file, client)
            return file_stats or stats
        if status == "failed":
            raise ReleaseError(f"VirusTotal analysis failed for {file}")

        if attempt < POLL_ATTEMPTS - 1:
            time.sleep(POLL_DELAY_SECONDS)

    raise ReleaseError(f"Timed out waiting for VirusTotal analysis: {file}")


def file_sha256(file: Path | str) -> str:
    digest = hashlib.sha256()
    if isinstance(file, Path):
        with file.open("rb") as handle:
            for chunk in iter(lambda: handle.read(1024 * 1024), b""):
                digest.update(chunk)
    else:
        digest.update(file.encode("utf-8"))
    return digest.hexdigest()


def virus_total_row(file: Path | str, stats: dict, file_hash: str | None = None) -> str:
    file_name = file.name if isinstance(file, Path) else file
    link = f"https://www.virustotal.com/gui/file/{file_hash or file_sha256(file)}/detection"
    malicious = f"{stats.get('malicious', 0)} malicious"
    if stats.get("_bkav_likely_false_positive"):
        malicious += " (Bkav likely false positive)"
    return (
        f"| [{file_name}]({link}) | {malicious}, "
        f"{stats.get('suspicious', 0)} suspicious | {stats.get('harmless', 0)} harmless, "
        f"{stats.get('undetected', 0)} undetected |"
    )


def scan_assets(files: list[Path]) -> str:
    api_key = os.environ.get("VIRUSTOTAL_API_KEY")
    if not api_key:
        raise ReleaseError("VIRUSTOTAL_API_KEY is required to scan release assets")

    rows: list[str] = []
    with httpx.Client(
        headers={"x-apikey": api_key},
        timeout=120,
        follow_redirects=True,
    ) as client:
        for file in files:
            stats = existing_virustotal_stats(file, client)
            if stats is None:
                print(f"Scanning {file.name}...", file=sys.stderr)
                analysis_id = upload_to_virustotal(file, client)
                stats = wait_for_virustotal(analysis_id, file, client)
            else:
                print(f"Using existing VirusTotal scan for {file.name}...", file=sys.stderr)
            rows.append(virus_total_row(file, stats))

    return "\n".join(
        [
            "### VirusTotal scan",
            "",
            "Release assets were checked against VirusTotal. Results may continue to update as engines finish scanning.",
            "",
            "| Asset | Detections | Other results |",
            "|---|---|---|",
            *rows,
            "",
        ]
    )


def publish_project(project: Path, profile: str, version: str, repo_root: Path) -> None:
    run(
        [
            "dotnet",
            "publish",
            str(project),
            f"-p:PublishProfile={profile}",
            f"-p:Version={version}",
            f"-p:AssemblyVersion={version}",
            f"-p:FileVersion={version}",
            "-p:Deterministic=true",
            "-p:DeterministicSourcePaths=true",
            "-p:ContinuousIntegrationBuild=true",
            f"-p:PathMap={repo_root}=/_/red40_clothing_packer",
        ]
    )


def main() -> int:
    args = parse_args()
    version = args.version
    if not VERSION_PATTERN.fullmatch(version):
        print(f"Invalid version: {version}", file=sys.stderr)
        print("Use a SemVer-like version such as 1.2.3 or 1.2.3-beta.1", file=sys.stderr)
        return 1

    try:
        require_command("dotnet")
        require_command("gh")
        require_command("uv")
        result = subprocess.run(["gh", "auth", "status"], capture_output=True, text=True)
        if result.returncode != 0:
            print("GitHub CLI is not authenticated", file=sys.stderr)
            print("Run: gh auth login", file=sys.stderr)
            return 1

        repo_root = Path(__file__).resolve().parent.parent
        repo_result = subprocess.run(
            ["gh", "repo", "view", "--json", "nameWithOwner"],
            capture_output=True,
            text=True,
            check=True,
        )
        repo = json.loads(repo_result.stdout)["nameWithOwner"]
        cli_project = repo_root / "src/ClothingRepacker.Cli/ClothingRepacker.Cli.csproj"
        gui_project = repo_root / "src/ClothingRepacker.Gui/ClothingRepacker.Gui.csproj"
        publish_root = repo_root / "artifacts/publish"
        release_root = repo_root / "artifacts/release"
        tag = f"v{version}"

        print(f"Preparing release {tag}")
        for path in (
            publish_root / "cli/linux-x64",
            publish_root / "cli/win-x64",
            publish_root / "gui/linux-x64",
            publish_root / "gui/win-x64",
            release_root,
        ):
            shutil.rmtree(path, ignore_errors=True)
        release_root.mkdir(parents=True, exist_ok=True)

        print("Restoring projects...")
        run(["dotnet", "restore", str(repo_root)])

        for project, profile, label in (
            (cli_project, "LinuxX64", "CLI Linux x64"),
            (cli_project, "WinX64", "CLI Windows x64"),
            (gui_project, "LinuxX64", "GUI Linux x64"),
            (gui_project, "WinX64", "GUI Windows x64"),
        ):
            print(f"Publishing {label}...")
            publish_project(project, profile, version, repo_root)

        assets = [
            release_root / f"ClothingRepacker-CLI-linux-x64-v{version}",
            release_root / f"ClothingRepacker-CLI-windows-x64-v{version}.exe",
            release_root / f"ClothingRepacker-GUI-linux-x64-v{version}",
            release_root / f"ClothingRepacker-GUI-windows-x64-v{version}.exe",
        ]
        sources = [
            publish_root / "cli/linux-x64/ClothingRepacker.Cli",
            publish_root / "cli/win-x64/ClothingRepacker.Cli.exe",
            publish_root / "gui/linux-x64/ClothingRepacker.Gui",
            publish_root / "gui/win-x64/ClothingRepacker.Gui.exe",
        ]

        print("Collecting release assets...")
        for source, asset in zip(sources, assets):
            shutil.copy2(source, asset)
        for asset in (assets[0], assets[2]):
            asset.chmod(asset.stat().st_mode | 0o111)

        print("Uploading release assets to VirusTotal...")
        virus_total_notes = scan_assets(assets)
        download_rows = []
        for asset, label in zip(
            assets,
            ("CLI - Linux x64", "CLI - Windows x64", "GUI - Linux x64", "GUI - Windows x64"),
        ):
            asset_url = (
                f"https://github.com/{repo}/releases/download/{quote(tag)}/{quote(asset.name)}"
            )
            download_rows.append(f"| [{label}]({asset_url}) | {label.split(' - ')[1]} | {label.split(' - ')[0]} |")
        generated_changes = generate_release_notes(repo, version)
        release_notes = "\n".join(
            [
                f"## ClothingRepacker {version}",
                "",
                "### Downloads",
                "",
                "| Download | Platform | App |",
                "|---|---|---|",
                *download_rows,
                "",
                "### Changes",
                "",
                generated_changes,
                "" if generated_changes else "No user-facing changes.",
                "",
                virus_total_notes,
            ]
        )
        notes_path = release_root / "release-notes.md"
        notes_path.write_text(release_notes, encoding="utf-8")

        print(f"Creating GitHub release {tag}...")
        command = [
            "gh",
            "release",
            "create",
            tag,
            *[f"{asset}#{label}" for asset, label in zip(
                assets,
                ("CLI - Linux x64", "CLI - Windows x64", "GUI - Linux x64", "GUI - Windows x64"),
            )],
            "--title",
            f"ClothingRepacker {version}",
            "--notes-file",
            str(notes_path),
        ]
        if args.prerelease:
            command.append("--prerelease")
        run(command)
        print(f"Release created: {tag}")
        return 0
    except (OSError, ReleaseError, subprocess.CalledProcessError) as error:
        print(f"Release failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

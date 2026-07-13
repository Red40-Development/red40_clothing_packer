#!/usr/bin/env python3
"""Validate the contributor-editable localization catalogs."""

import json
import re
import sys
from pathlib import Path

PLACEHOLDER = re.compile(r"\{([A-Za-z0-9_.-]+)\}")
KEY = re.compile(r"^[A-Za-z][A-Za-z0-9_.-]*$")
LOCALE = re.compile(r"^[a-z]{2,3}(?:-[A-Z][a-z]{3})?(?:-[A-Z]{2}|-[0-9]{3})?$")


def load(path: Path) -> dict:
    def pairs(items: list[tuple[str, object]]) -> dict:
        result: dict[str, object] = {}
        for key, value in items:
            if key in result:
                raise ValueError(f"{path}: duplicate JSON key {key!r}")
            result[key] = value
        return result

    with path.open(encoding="utf-8") as stream:
        catalog = json.load(stream, object_pairs_hook=pairs)
    if not isinstance(catalog, dict):
        raise ValueError(f"{path}: root must be an object")
    locale = catalog.get("locale")
    display_name = catalog.get("displayName")
    translations = catalog.get("translations")
    if not isinstance(locale, str) or not LOCALE.fullmatch(locale):
        raise ValueError(f"{path}: locale must be a BCP-47-like tag")
    if not isinstance(display_name, str) or not display_name.strip():
        raise ValueError(f"{path}: displayName must be non-empty")
    if not isinstance(translations, dict) or not translations:
        raise ValueError(f"{path}: translations must be a non-empty object")
    invalid_keys = sorted(key for key in translations if not isinstance(key, str) or not KEY.fullmatch(key))
    if invalid_keys:
        raise ValueError(f"{path}: invalid translation key(s): {', '.join(map(repr, invalid_keys))}")
    if any(not isinstance(value, str) for value in translations.values()):
        raise ValueError(f"{path}: translation values must be strings")
    return catalog


def placeholders(value: str) -> set[str]:
    return set(PLACEHOLDER.findall(value))


def main() -> int:
    root = Path(__file__).resolve().parents[1]
    directory = root / "src" / "ClothingRepacker.Core" / "Localization" / "Locales"
    paths = sorted(directory.glob("*.json"))
    if not paths:
        print(f"No localization catalogs found in {directory}", file=sys.stderr)
        return 1

    catalogs = {}
    try:
        for path in paths:
            catalog = load(path)
            locale = catalog["locale"].lower()
            if locale in catalogs:
                raise ValueError(f"duplicate locale {locale!r}: {path}")
            catalogs[locale] = catalog
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(error, file=sys.stderr)
        return 1

    if "en" not in catalogs:
        print("English catalog en.json is required", file=sys.stderr)
        return 1

    english = catalogs["en"]["translations"]
    failed = False
    for locale, catalog in catalogs.items():
        translations = catalog["translations"]
        missing = sorted(set(english) - set(translations))
        if missing:
            print(f"{locale}: missing keys: {', '.join(missing)}", file=sys.stderr)
            failed = True
        for key in sorted(set(english) & set(translations)):
            expected = placeholders(english[key])
            actual = placeholders(translations[key])
            if expected != actual:
                print(f"{locale}:{key}: placeholders {sorted(actual)} != English {sorted(expected)}", file=sys.stderr)
                failed = True

    if failed:
        return 1
    print(f"Validated {len(catalogs)} localization catalog(s), {len(english)} key(s).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

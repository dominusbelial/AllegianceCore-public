#!/usr/bin/env python3
"""P811 — GLOSSARY.md link-checker.

Extracts every repository-relative path mentioned in GLOSSARY.md (backtick
spans and table cells) and fails if any of them does not exist in the repo, so
the agent-maintained index can never drift from the actual tree.

Wired into CI (see .github/workflows/ci.yml) and runnable directly:

    python3 tools/check-glossary-links.py

Exit code 1 lists every missing path. Globs ('*'), placeholders ('<' or '>'),
inline-code that is not a path (contains spaces), native-source citations and
external URLs are skipped. Directory references are valid if the directory
exists; a trailing slash is stripped before the existence check.
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GLOSSARY = os.path.join(ROOT, "GLOSSARY.md")

# Tokens that are clearly not repo paths.
SKIP_PREFIX = ("http://", "https://", "~/", "$", "`")
# A candidate is a checkable repo path only if it starts with a known repo root
# (a full repo-relative path). Abridged shorthand (e.g. `Rendering/foo.cs`),
# bare filenames, globs, placeholders and external citations are skipped.
ROOT_DIRS = ("src/", "tests/", "docs/", "tools/", "scripts/", "docker/",
             ".github/", "data/", "packaging/", "assets/")


def candidates(lines):
    """Yield backtick spans and table-cell paths from the markdown lines."""
    for line in lines:
        # Backtick spans like `src/Allegiance.Core/Sim/Ship.cs`.
        for tok in re.findall(r"`([^`]+)`", line):
            yield tok.strip()
        # Table cells are delimited by pipes; grab bare cell contents that
        # look like a full repo path.
        for cell in line.split("|"):
            cell = cell.strip()
            if cell.startswith(ROOT_DIRS):
                yield cell


def is_path_token(tok):
    if not tok:
        return False
    if any(tok.startswith(p) for p in SKIP_PREFIX):
        return False
    if any(ch in tok for ch in "*<>()[]{}"):
        return False
    if " " in tok:
        return False
    # Only full repo-root-relative paths are asserted.
    return tok.startswith(ROOT_DIRS)


def main():
    with open(GLOSSARY, encoding="utf-8") as fh:
        lines = fh.readlines()

    missing = []
    seen = set()
    for tok in candidates(lines):
        if not is_path_token(tok):
            continue
        norm = tok.rstrip("/")
        if norm in seen:
            continue
        seen.add(norm)
        if not os.path.exists(os.path.join(ROOT, norm)):
            missing.append(tok)

    if missing:
        print(f"GLOSSARY.md references {len(missing)} missing path(s):")
        for m in sorted(set(missing)):
            print("  MISSING:", m)
        print("\nFix the paths (or the link-check excludes placeholders/globs).")
        return 1

    print(f"GLOSSARY.md link check OK ({len(seen)} distinct paths checked).")
    return 0


if __name__ == "__main__":
    sys.exit(main())

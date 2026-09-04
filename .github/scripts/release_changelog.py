#!/usr/bin/env python3
"""Prepare the CHANGELOG.md section for the release being cut.

Two modes:
  - If CHANGELOG.md already has a "## [VERSION]" section, its body is used as
    the release notes and the file is left untouched.
  - Otherwise the "## [Unreleased]" section is promoted in place:
      * "## [Unreleased]" becomes "## [VERSION] - YYYY-MM-DD", with the Firely
        Server compatibility for this build recorded underneath it
      * a fresh, empty "## [Unreleased]" section is opened above it
      * the link-reference block at the bottom (if present) gains a line for
        the new version and its "Unreleased" line is re-pointed at the new tag

Also writes release-notes-changelog.md containing just the body of the
section, for use in the GitHub release notes.

Environment:
  VERSION         release version being cut, e.g. "2.1.0"           (required)
  FIRELY_VERSION  Firely Server version this build targets           (required)
  VONK_VERSION    Vonk SDK version from the csproj                   (required)
  REPO            "owner/name", defaults to the openFHIR repo
  CHANGELOG       path to the changelog, defaults to CHANGELOG.md
  NOTES           notes output path, defaults to release-notes-changelog.md
"""

import datetime
import os
import re
import sys

VERSION = os.environ["VERSION"]
FIRELY_VERSION = os.environ["FIRELY_VERSION"]
VONK_VERSION = os.environ["VONK_VERSION"]
REPO = os.environ.get("REPO", "openFHIR/openfhir-firely-plugin")
PATH = os.environ.get("CHANGELOG", "CHANGELOG.md")
NOTES_PATH = os.environ.get("NOTES", "release-notes-changelog.md")

text = open(PATH, encoding="utf-8").read()


def section_span(start):
    """Body of a section starting at `start` (end of its heading), plus the
    index where the section ends: the next "## " heading, the link-reference
    block, or end of file — whichever comes first."""
    next_heading = re.compile(r"^## ", re.MULTILINE).search(text, start)
    link_block = re.compile(r"^\[Unreleased\]:", re.MULTILINE).search(text, start)
    ends = [m.start() for m in (next_heading, link_block) if m]
    end = min(ends) if ends else len(text)
    return text[start:end].strip("\n"), end


existing = re.search(rf"^## \[{re.escape(VERSION)}\].*$", text, re.MULTILINE)
if existing:
    body, _ = section_span(existing.end())
    if not body.strip():
        sys.exit(f"CHANGELOG.md's existing '## [{VERSION}]' section is empty")
    with open(NOTES_PATH, "w", encoding="utf-8") as fh:
        fh.write(body + "\n")
    print(f"Using the existing CHANGELOG section for {VERSION}; CHANGELOG.md unchanged")
    sys.exit(0)

heading_re = re.compile(r"^## \[Unreleased\].*$", re.MULTILINE)
match = heading_re.search(text)
if not match:
    sys.exit("CHANGELOG.md has no '## [Unreleased]' heading to promote")

body, body_end = section_span(match.end())
if not body.strip():
    sys.exit(
        "The CHANGELOG.md 'Unreleased' section is empty — "
        "add entries describing this release before tagging it."
    )

# The most recently released version, read before any rewriting, so the new
# version's compare link points at the right predecessor.
prev_match = re.search(r"^## \[(?!Unreleased\])([^\]]+)\]", text, re.MULTILINE)
prev_version = prev_match.group(1) if prev_match else None

date = datetime.date.today().isoformat()
version_block = (
    f"## [{VERSION}] - {date}\n"
    f"\n"
    f"- **Firely Server:** {FIRELY_VERSION} or later (Vonk SDK `{VONK_VERSION}`)\n"
    f"\n"
    f"{body}\n"
    f"\n"
)

text = text[:match.start()] + "## [Unreleased]\n\n" + version_block + text[body_end:].lstrip("\n")

# Refresh the link-reference block at the bottom (if the changelog has one) so
# both the new version and "Unreleased" point at the right compare ranges.
if "[Unreleased]:" in text:
    text = re.sub(
        r"^\[Unreleased\]: .*$",
        f"[Unreleased]: https://github.com/{REPO}/compare/{VERSION}...HEAD",
        text,
        count=1,
        flags=re.MULTILINE,
    )
    if f"[{VERSION}]:" not in text:
        if prev_version:
            new_link = f"[{VERSION}]: https://github.com/{REPO}/compare/{prev_version}...{VERSION}"
        else:
            new_link = f"[{VERSION}]: https://github.com/{REPO}/releases/tag/{VERSION}"
        text = re.sub(
            r"^(\[Unreleased\]: .*)$",
            lambda m: m.group(1) + "\n" + new_link,
            text,
            count=1,
            flags=re.MULTILINE,
        )

with open(PATH, "w", encoding="utf-8") as fh:
    fh.write(text)

with open(NOTES_PATH, "w", encoding="utf-8") as fh:
    fh.write(body + "\n")

print(f"Promoted 'Unreleased' to {VERSION} (Firely Server {FIRELY_VERSION})")

# -----------------------------------------------------------------------------
# PyMCU CLI Driver -- stale-artifact detection
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
#
# The driver runs whatever its resolution order finds, and used to say nothing when that
# artifact did not match the sources it was about to compile. Five incidents in one night came
# out of that, to two people who both knew the trap existed, and three of them wore the face of
# a real bug: a days-old bundled stdlib compiling an old HAL while the repository sat at HEAD,
# a three-day-old backend disagreeing with a green suite, and a leftover compiler binary failing
# a brand-new fixture with a plausible diagnostic.
#
# Two checks live here, because the incidents came in two shapes.
#
#   * A stdlib copy SHADOWING a newer one. `pymcu` is a namespace package and
#     get_stdlib_path takes the first __path__ entry that has chips/ in it. A physical copy in
#     site-packages sorts ahead of the editable .pth pointing at a checkout, so the checkout is
#     never read. Note that `pymcu.__file__` is None either way, which is why the usual check
#     does not tell them apart -- comparing the __path__ ENTRIES does.
#
#   * A compiler binary OLDER than the sources it is compiling. That is the leftover-build
#     case, and the one where a source tree moved (a branch switch, a reset) while the binary
#     stayed where it was.
#
# Both are warnings and never errors: a released install legitimately runs a compiler older
# than any file it can see, and a user who installed last month's release and wrote a program
# today is doing nothing wrong. The mtime check is therefore gated on the artifacts being part
# of a CHECKOUT, which is the only situation where the two are expected to move together.

from __future__ import annotations

import os
import subprocess
from datetime import datetime
from pathlib import Path
from typing import Iterable, Optional

# Sources whose age is worth comparing against a compiler binary. Anything the compiler READS.
_SOURCE_SUFFIXES = (".py",)

# A scan that walks an unexpectedly large tree should not delay a build. The stdlib is ~190
# files and takes about 5 ms; this only exists so a pathological include path cannot hang.
_MAX_FILES_SCANNED = 20_000


def _fmt(ts: float) -> str:
    return datetime.fromtimestamp(ts).strftime("%Y-%m-%d %H:%M")


def newest_source(roots: Iterable[Path]) -> Optional[tuple[Path, float]]:
    """The most recently modified source file under any of `roots`, or None."""
    best: Optional[tuple[Path, float]] = None
    seen = 0

    for root in roots:
        if root is None:
            continue
        root = Path(root)
        try:
            if root.is_file():
                candidates: Iterable[Path] = [root]
            elif root.is_dir():
                candidates = root.rglob("*")
            else:
                continue

            for f in candidates:
                seen += 1
                if seen > _MAX_FILES_SCANNED:
                    return best
                if f.suffix not in _SOURCE_SUFFIXES:
                    continue
                try:
                    ts = f.stat().st_mtime
                except OSError:
                    continue
                if best is None or ts > best[1]:
                    best = (f, ts)
        except (OSError, ValueError):
            continue

    return best


def in_git_worktree(path: Path) -> bool:
    """True when `path` lives inside a git working tree.

    This is the gate that keeps a released install quiet. A wheel's compiler and stdlib sit in
    site-packages, which is not a checkout, so neither can be expected to track a source tree
    and the comparison below has nothing to say about them.
    """
    try:
        start = path if path.is_dir() else path.parent
        if not str(start):
            return False
        return subprocess.run(
            ["git", "-C", str(start), "rev-parse", "--is-inside-work-tree"],
            capture_output=True, text=True, timeout=5,
        ).stdout.strip() == "true"
    except (OSError, subprocess.SubprocessError):
        return False


def shadowed_stdlib_copies(chosen: str) -> list[tuple[Path, float]]:
    """Other `pymcu` copies with a chips/ directory that the chosen one is hiding.

    Returns only copies NEWER than the chosen one, because an older shadowed copy is the
    ordinary case (a leftover nobody reads) while a newer one means the build is compiling a
    library that is not the one being edited.
    """
    try:
        import pymcu  # noqa: PLC0415
    except Exception:
        return []

    try:
        chosen_path = Path(chosen).resolve() if chosen else None
    except (OSError, ValueError):
        # A path the filesystem will not even look at cannot be shadowing anything, and this
        # check exists to report a problem, never to become one.
        return []
    chosen_ts = None
    if chosen_path is not None:
        newest = newest_source([chosen_path])
        chosen_ts = newest[1] if newest else None
    if chosen_ts is None:
        return []

    out: list[tuple[Path, float]] = []
    for entry in getattr(pymcu, "__path__", []):
        try:
            p = Path(entry).resolve()
            if chosen_path is not None and p == chosen_path:
                continue
            if not (p / "chips").is_dir():
                continue
        except (OSError, ValueError):
            continue
        newest = newest_source([p])
        if newest and newest[1] > chosen_ts:
            out.append((p, newest[1]))

    return out


def resolution_report(compiler: Path, stdlib: str, candidates: Iterable[str]) -> list[str]:
    """Lines describing WHICH artifacts were resolved, for PYMCU_VERBOSE.

    In every one of the five incidents the first question asked was "which binary is this
    actually running", and answering it took a `find` each time.
    """
    lines = [f"resolved compiler: {compiler}"]
    try:
        lines.append(f"  built {_fmt(Path(compiler).stat().st_mtime)}")
    except OSError:
        lines.append("  (not on disk)")

    lines.append("  tried, in order:")
    lines.extend(f"    {c}" for c in candidates)
    lines.append(f"resolved stdlib: {stdlib or '(none)'}")

    try:
        import pymcu  # noqa: PLC0415
        for entry in getattr(pymcu, "__path__", []):
            mark = "*" if stdlib and Path(entry).resolve() == Path(stdlib).resolve() else " "
            has = "chips/" if (Path(entry) / "chips").is_dir() else "no chips/"
            lines.append(f"  {mark} {entry}  ({has})")
    except Exception:
        pass

    return lines


def warnings_for(compiler: Path, stdlib: str, source_roots: Iterable[Path] = ()) -> list[str]:
    """Every stale-artifact warning that applies to this build. Empty is the normal answer.

    `source_roots` is accepted and deliberately NOT used for the age comparison. The pair that
    has to move together is the compiler binary and the STDLIB: both are PyMCU artifacts that a
    checkout updates at once, and a mismatch between them is what every incident was. A user's
    own program being newer than the compiler is the ordinary case -- it is true of every build
    anyone has ever run -- and warning about it would put a line on every build in the AVR
    suite, which is the fastest way to teach people to stop reading warnings.
    """
    out: list[str] = []

    for shadowed, ts in shadowed_stdlib_copies(stdlib):
        out.append(
            f"the stdlib being compiled is {stdlib}, but a NEWER copy exists at {shadowed} "
            f"(modified {_fmt(ts)}). `pymcu` is a namespace package and the first entry with "
            f"a chips/ directory wins, so the newer one is never read. Note that "
            f"`pymcu.__file__` is None for both, so that check does not tell them apart."
        )

    if not stdlib:
        return out
    roots = [Path(stdlib)]

    # Only a checkout is expected to keep its binary and its stdlib in step. In a wheel the two
    # ship together and are the same age, so this would be silent there anyway; the gate is what
    # makes that a guarantee rather than a coincidence.
    if not (in_git_worktree(Path(compiler)) or in_git_worktree(Path(stdlib))):
        return out

    try:
        binary_ts = Path(compiler).stat().st_mtime
    except OSError:
        return out

    newest = newest_source(roots)
    if newest and newest[1] > binary_ts:
        out.append(
            f"pymcuc was built {_fmt(binary_ts)} and the newest stdlib file it is compiling "
            f"is "
            f"{_fmt(newest[1])} ({newest[0]}). It may be compiling a different version of the "
            f"stdlib than the one on disk.\n         resolved: {compiler}"
        )

    return out

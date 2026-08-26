# tests/driver/test_staleness.py
#
# The driver used to run whatever its resolution order found and say nothing when that artifact
# did not match the sources. Five incidents in one night came out of that, three of them wearing
# the face of a real bug. What is pinned here is that each shape is now named, and -- just as
# important -- that a released install stays quiet, because a warning nobody can act on is the
# fastest way to make people stop reading warnings.

import os
import subprocess
import time
from pathlib import Path

import pytest

from src.driver.core.staleness import (
    in_git_worktree,
    newest_source,
    resolution_report,
    shadowed_stdlib_copies,
    warnings_for,
)


def write(path: Path, when: float | None = None) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("x = 1\n")
    if when is not None:
        os.utime(path, (when, when))
    return path


def git_init(path: Path) -> Path:
    path.mkdir(parents=True, exist_ok=True)
    subprocess.run(["git", "init", "-q"], cwd=path, check=True, capture_output=True)
    return path


def stdlib_at(root: Path, when: float) -> Path:
    write(root / "chips" / "__init__.py", when)
    write(root / "types.py", when)
    return root


@pytest.fixture(autouse=True)
def isolated_namespace_path(monkeypatch):
    """Pin `pymcu.__path__` so the machine's real installs cannot answer for a test.

    Without this, a test that builds a stdlib in tmp_path is still compared against every
    pymcu copy on the developer's machine, and the shadow check reports one of those. The
    tests that are ABOUT shadowing set the path themselves.
    """
    import pymcu
    monkeypatch.setattr(pymcu, "__path__", [], raising=False)


NOW = time.time()
OLD = NOW - 86_400
OLDER = NOW - 172_800


# ── newest_source ────────────────────────────────────────────────────────────

def test_newest_source_finds_the_most_recent_file(tmp_path):
    write(tmp_path / "a.py", OLD)
    newest = write(tmp_path / "sub" / "b.py", NOW)

    found = newest_source([tmp_path])

    assert found is not None and found[0] == newest


def test_newest_source_ignores_files_the_compiler_does_not_read(tmp_path):
    write(tmp_path / "a.py", OLD)
    binary = tmp_path / "firmware.hex"
    binary.write_text("stale artifacts are not sources")
    os.utime(binary, (NOW, NOW))

    found = newest_source([tmp_path])

    assert found is not None and found[0].suffix == ".py"


def test_newest_source_of_nothing_is_none(tmp_path):
    assert newest_source([tmp_path / "does-not-exist"]) is None


# ── the gate that keeps a released install quiet ─────────────────────────────

def test_a_binary_older_than_its_stdlib_outside_a_checkout_says_nothing(tmp_path):
    # A user who installed last month's release and wrote a program today is doing nothing
    # wrong, and this is the case that made the check a warning rather than an error.
    compiler = write(tmp_path / "bin" / "pymcuc", OLD)
    lib = stdlib_at(tmp_path / "site-packages" / "pymcu", NOW)

    assert warnings_for(compiler, str(lib), []) == []


def test_a_users_own_program_being_newer_than_the_compiler_says_nothing(tmp_path):
    # True of every build anyone has ever run. Warning about it would put a line on each of
    # the ~2200 builds in the AVR suite, which teaches people to stop reading warnings.
    repo = git_init(tmp_path / "repo")
    compiler = write(repo / "build" / "bin" / "pymcuc", NOW)
    lib = stdlib_at(repo / "lib" / "src" / "pymcu", OLD)
    project = tmp_path / "someone-elses-project"
    write(project / "main.py", NOW + 60)

    assert warnings_for(compiler, str(lib), [project]) == []


def test_a_binary_older_than_its_stdlib_inside_a_checkout_warns(tmp_path):
    repo = git_init(tmp_path / "repo")
    compiler = write(repo / "build" / "bin" / "pymcuc", OLD)
    lib = stdlib_at(repo / "lib" / "src" / "pymcu", NOW)

    msgs = warnings_for(compiler, str(lib), [])

    assert len(msgs) == 1
    assert "pymcuc was built" in msgs[0]


def test_the_warning_names_both_timestamps_and_both_paths(tmp_path):
    # Every one of the five incidents opened with "which artifact is this actually running",
    # and answering it took a `find` each time.
    repo = git_init(tmp_path / "repo")
    compiler = write(repo / "build" / "bin" / "pymcuc", OLD)
    lib = stdlib_at(repo / "lib" / "src" / "pymcu", NOW)
    newest = lib / "types.py"

    msg = warnings_for(compiler, str(lib), [])[0]

    assert str(newest) in msg
    assert str(compiler) in msg
    assert msg.count("2026") >= 2 or msg.count("-") >= 4, "both dates have to be in there"


def test_a_binary_newer_than_its_stdlib_says_nothing(tmp_path):
    repo = git_init(tmp_path / "repo")
    compiler = write(repo / "build" / "bin" / "pymcuc", NOW)
    lib = stdlib_at(repo / "lib" / "src" / "pymcu", OLD)

    assert warnings_for(compiler, str(lib), []) == []


def test_in_git_worktree_is_false_outside_one(tmp_path):
    assert in_git_worktree(tmp_path) is False


# ── the shadowed stdlib, which is incidents 1 and 4 ──────────────────────────

def test_a_newer_shadowed_copy_is_reported(tmp_path, monkeypatch):
    import pymcu

    chosen = stdlib_at(tmp_path / "site-packages" / "pymcu", OLDER)
    checkout = stdlib_at(tmp_path / "repo" / "lib" / "src" / "pymcu", NOW)
    monkeypatch.setattr(pymcu, "__path__", [str(chosen), str(checkout)], raising=False)

    found = shadowed_stdlib_copies(str(chosen))

    assert [p for p, _ in found] == [checkout.resolve()]


def test_an_older_shadowed_copy_is_not_worth_saying(tmp_path, monkeypatch):
    # A leftover nobody reads is the ordinary case; only a NEWER copy means the build is
    # compiling a library that is not the one being edited.
    import pymcu

    chosen = stdlib_at(tmp_path / "a" / "pymcu", NOW)
    leftover = stdlib_at(tmp_path / "b" / "pymcu", OLDER)
    monkeypatch.setattr(pymcu, "__path__", [str(chosen), str(leftover)], raising=False)

    assert shadowed_stdlib_copies(str(chosen)) == []


def test_a_path_entry_without_chips_is_not_a_stdlib_copy(tmp_path, monkeypatch):
    import pymcu

    chosen = stdlib_at(tmp_path / "a" / "pymcu", OLDER)
    plugin = tmp_path / "backend" / "pymcu"
    write(plugin / "backend" / "avr" / "__init__.py", NOW)
    monkeypatch.setattr(pymcu, "__path__", [str(chosen), str(plugin)], raising=False)

    assert shadowed_stdlib_copies(str(chosen)) == []


def test_the_shadow_warning_says_why_the_usual_check_misses_it(tmp_path, monkeypatch):
    # pymcu.__file__ is None for BOTH a namespace package and an editable install, which is
    # exactly why incident 4 was invisible to the check everyone already knew about.
    import pymcu

    chosen = stdlib_at(tmp_path / "site-packages" / "pymcu", OLDER)
    checkout = stdlib_at(tmp_path / "repo" / "pymcu", NOW)
    monkeypatch.setattr(pymcu, "__path__", [str(chosen), str(checkout)], raising=False)

    msg = warnings_for(Path("/nonexistent/pymcuc"), str(chosen), [])[0]

    assert str(checkout) in msg and str(chosen) in msg
    assert "__file__" in msg


def test_a_single_stdlib_copy_is_silent(tmp_path, monkeypatch):
    import pymcu

    chosen = stdlib_at(tmp_path / "only" / "pymcu", NOW)
    monkeypatch.setattr(pymcu, "__path__", [str(chosen)], raising=False)

    assert warnings_for(Path("/nonexistent/pymcuc"), str(chosen), []) == []


# ── the resolution report ────────────────────────────────────────────────────

def test_the_report_names_the_resolved_binary_and_what_was_tried(tmp_path):
    compiler = write(tmp_path / "bin" / "pymcuc", NOW)

    lines = "\n".join(resolution_report(compiler, "", ["/first/pymcuc", str(compiler)]))

    assert str(compiler) in lines
    assert "/first/pymcuc" in lines
    assert "tried, in order" in lines


def test_the_report_survives_a_binary_that_is_not_there(tmp_path):
    lines = "\n".join(resolution_report(Path("/nonexistent/pymcuc"), "", []))

    assert "not on disk" in lines


# ── nothing here may break a build ───────────────────────────────────────────

@pytest.mark.parametrize("bad", ["", "/dev/null/nope", "\0"])
def test_a_nonsense_stdlib_path_does_not_raise(bad):
    warnings_for(Path("/nonexistent/pymcuc"), bad, [])

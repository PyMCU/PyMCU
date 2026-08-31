"""`pymcu --version` says WHICH installation it is describing.

PyMCU#248. The numbers in that table change with the working directory:

    cd ~/Repos/PyMCU  &&  pymcu --version   ->  pymcu-compiler 0.1.0a3
    cd /tmp           &&  pymcu --version   ->  pymcu-compiler 0.1.0a9

The cause is not the lookup. `_ensure_venv()` re-executes the CLI with a project's `.venv`
interpreter when the working directory has one, on purpose, so a project that pins its own
PyMCU gets the one it pinned. `importlib.metadata` then reports that installation, correctly.

What was missing is that the table named no environment, so the same command told two stories
and neither said which. It was silent in the flattering direction: from inside a checkout, which
is where anyone investigating a version question is standing, it reported the project's older
set as though it were the machine's.

EVERY TEST HERE CHANGES DIRECTORY. That is not incidental: a test that ran from one directory
would have passed for the whole life of the bug, because from any single directory the answer is
self-consistent. The defect only exists between two runs.
"""

import os
import sys
from pathlib import Path

import pytest

import src.driver.commands.version as version_cmd

# Imported inside each test that needs it, not at module level. A module-level import of a
# symbol the fix introduces turns a reverted fix into a COLLECTION error, and a collection
# error takes the whole file down with it -- including the one test here that does not need
# the symbol and can therefore fail on an assertion about behaviour rather than on a missing
# name. Keeping that test runnable is the difference between proving the behaviour is new and
# proving the symbol is.


def test_a_directory_with_no_venv_is_reported_as_the_global_install(tmp_path, monkeypatch):
    from src.driver.commands.version import _resolved_environment

    monkeypatch.chdir(tmp_path)

    path, how = _resolved_environment()

    assert path == str(Path(sys.prefix).resolve())
    assert how == "global install"


def test_a_directory_whose_venv_is_the_running_one_is_reported_as_the_projects(monkeypatch):
    from src.driver.commands.version import _resolved_environment

    """The re-exec case, reconstructed: cwd is the parent of the venv we are running from.

    That is exactly the state `_ensure_venv()` leaves the process in after switching, so this
    is the shape the user sees when the table reports a project's pinned versions.
    """
    prefix = Path(sys.prefix).resolve()
    monkeypatch.chdir(prefix.parent)

    # Only meaningful when the running interpreter really is a `.venv` directory; a pipx or
    # system interpreter is not named `.venv` and cannot produce this case.
    if prefix.name != ".venv":
        pytest.skip(f"the running interpreter is not a project .venv ({prefix})")

    path, how = _resolved_environment()

    assert path == str(prefix)
    assert "project" in how


def test_the_answer_changes_with_the_directory_and_says_so(tmp_path, monkeypatch):
    from src.driver.commands.version import _resolved_environment

    """The two runs side by side, which is the only place the defect was visible.

    Asserting that BOTH answers are labelled, not that they differ: on a machine where the
    interpreter is not a project venv they legitimately agree, and a test that demanded a
    difference would fail there for the wrong reason.
    """
    monkeypatch.chdir(tmp_path)
    away = _resolved_environment()

    monkeypatch.chdir(Path(sys.prefix).resolve().parent)
    near = _resolved_environment()

    for path, how in (away, near):
        assert path, "the environment must be named, not left blank"
        assert how in ("global install",
                       "this project's .venv, switched into automatically")


def test_a_deleted_working_directory_does_not_crash_the_version_command(tmp_path, monkeypatch):
    from src.driver.commands.version import _resolved_environment

    """`--version` is what someone runs when things are already broken, so it must not add to it.

    Path.cwd() raises if the directory has been removed underneath the process, which is
    reachable in a shell whose directory was deleted.
    """
    doomed = tmp_path / "gone"
    doomed.mkdir()
    monkeypatch.chdir(doomed)
    try:
        doomed.rmdir()
    except OSError:
        pytest.skip("this platform will not remove the working directory")

    path, how = _resolved_environment()

    assert path == str(Path(sys.prefix).resolve())
    assert how == "global install"


def test_the_printed_table_names_the_environment_from_two_directories(tmp_path, monkeypatch, capsys):
    """The user-visible surface, asserted on the OUTPUT rather than on a helper.

    The tests above import a function that did not exist before the fix, so under the old code
    they fail at collection -- which proves the symbol is new, not that the behaviour is. This
    one runs the real command and reads what it printed, so it fails on the old code the way a
    user met the bug: a table with no statement of which installation it describes.
    """
    seen = []
    for where in (tmp_path, Path(sys.prefix).resolve().parent):
        monkeypatch.chdir(where)
        capsys.readouterr()
        version_cmd.version()
        out = capsys.readouterr().out
        assert "Environment:" in out, (
            f"the table printed from {where} does not say which installation it describes"
        )
        seen.append(out)

    # And the two runs must each name a real path, not the literal word.
    for out in seen:
        after = out.split("Environment:", 1)[1].strip()
        assert after.startswith("/"), f"expected a path after 'Environment:', got {after[:40]!r}"

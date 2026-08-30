"""A parse error in an imported module names that module, its line and its column.

The twin of `test_diagnostic_names_the_callee`, for the path that does not go through
`UserError`. That one covers a diagnostic the IR generator raises; this one covers the ones the
front end raises, which never set `CompilerError.File` at all.

The null was read as two different things by two readers. `CompilerPhaseBase` takes it as "the
entry file", and `DependencyGraphBuilder`'s `catch (CompilerError e) when (e.File == null)`
takes it as "this error has no location of its own" and relocates it onto the import statement.
That catch was written for ImportError, which really has no location; a SyntaxError has a line
and a column and lost both to it. Measured before the fix:

    helper.py:6      b: uint8 = a +)          <- what the message is about

    main.py:2:1: error: SyntaxError: Expected expression
    2 | from helper import f

No file name, no real line, no caret, and three lines of a file the message is not about.

WHAT DISCRIMINATES: every test in this file. Against the unfixed compiler each reports
main.py:2, the import statement.

WHAT IS INVARIANT, and here on purpose: `test_a_module_that_cannot_be_resolved_still_points_at
_the_import`. An ImportError genuinely has no location of its own, and the relocation onto the
import line is right for it. A fix that names the file for every error coming out of the loader
would take that with it, and the reader would be sent to a file that does not exist.
"""

import os
import re
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

LOCATION = re.compile(r"([A-Za-z0-9_./-]+\.py):(\d+):(\d+):")

MAIN = (
    "from pymcu.types import uint8\n"      # 1
    "from helper import f\n"               # 2
    "\n\n"                                 # 3-4
    "def main():\n"                        # 5
    "    x: uint8 = f(1)\n"                # 6
    "    while True:\n"                    # 7
    "        pass\n"                       # 8
)


def build(tmp_path: Path, files: dict, py_parser: bool = False):
    for name, text in files.items():
        (tmp_path / name).write_text(text)
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", "/dev/null",
         "--target", "atmega328p", "--freq", "16000000",
         "-I", str(tmp_path), "-I", str(STDLIB),
         "--emit-ir", str(tmp_path / "firmware.mir")],
        capture_output=True, text=True,
        env={**os.environ, **({"PYMCU_PY_PARSER": "1"} if py_parser else {})},
    )
    return proc.stdout + proc.stderr


def location(out: str):
    m = LOCATION.search(out)
    assert m, f"no located diagnostic in:\n{out}"
    return Path(m.group(1)).name, int(m.group(2)), int(m.group(3))


# --- what discriminates -------------------------------------------------------

# A stray `)` on line 6. helper.py and main.py both have a line 6, which is what made the old
# report read as correct rather than as obviously misdirected.
SYNTAX_HELPER = (
    "from pymcu.types import uint8, inline\n"   # 1
    "\n\n"                                      # 2-3
    "@inline\n"                                 # 4
    "def f(a: uint8) -> uint8:\n"               # 5
    "    b: uint8 = a +)\n"                     # 6
    "    return b\n"                            # 7
)

INDENT_HELPER = (
    "from pymcu.types import uint8, inline\n"   # 1
    "\n\n"                                      # 2-3
    "@inline\n"                                 # 4
    "def f(a: uint8) -> uint8:\n"               # 5
    "        b: uint8 = a\n"                    # 6  over-indented
    "    return a\n"                            # 7
)


@pytest.mark.parametrize("helper", [SYNTAX_HELPER, INDENT_HELPER],
                         ids=["syntax", "indentation"])
def test_a_parse_error_in_an_imported_module_names_that_module(tmp_path, helper):
    out = build(tmp_path, {"helper.py": helper, "main.py": MAIN})
    name, line, _ = location(out)
    assert name == "helper.py", \
        f"the text that cannot be parsed is in helper.py, and the report names {name}"
    text = (tmp_path / name).read_text().splitlines()
    assert line <= len(text), f"{name} has {len(text)} lines; the diagnostic claims line {line}"


def test_the_caret_lands_in_the_module_and_not_on_the_import(tmp_path):
    """The line alone is not enough: main.py:2 and helper.py:2 both exist.

    What separates a fixed report from the old one is that the column is a measurement in the
    named file. The old one printed column 1, which is a placeholder, on the import statement.
    """
    out = build(tmp_path, {"helper.py": SYNTAX_HELPER, "main.py": MAIN})
    name, line, col = location(out)
    text = (tmp_path / name).read_text().splitlines()[line - 1]
    assert col > 1, "column 1 is the placeholder the unlocated path prints"
    assert col <= len(text) + 1, f"caret at column {col} of a {len(text)}-character line"


def test_the_python_front_end_reports_it_the_same_way(tmp_path):
    """A location is produced by the front end, so the two parsers are two chances to lose it."""
    out = build(tmp_path, {"helper.py": SYNTAX_HELPER, "main.py": MAIN}, py_parser=True)
    name, _, _ = location(out)
    assert name == "helper.py", f"the Python front end names {name}"


# --- invariant: an error with no location of its own still points at the import ---

def test_a_module_that_cannot_be_resolved_still_points_at_the_import(tmp_path):
    """An ImportError has no line of its own, and the import statement is the right place.

    This is what the relocation in DependencyGraphBuilder exists for, and a fix that names a
    file for everything coming out of the loader would break it: there is no file to name.
    """
    out = build(tmp_path, {"main.py": "from nowhere import thing\n\n\ndef main():\n    pass\n"})
    name, line, _ = location(out)
    assert name == "main.py", f"the import to fix is in main.py, got {name}"
    assert line == 1, f"the import is on line 1, got {line}"

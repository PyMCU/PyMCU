"""A diagnostic raised inside an @inline defined in the ENTRY file names that body's line.

The imported-module half of this was #164 and has worked since. The entry file's half did not,
because the two are decided by the same test and the entry file failed it:

    string? calleeSourcePath = ... && !string.IsNullOrEmpty(calleePath) ? calleePath : null;

`RecordSourcePaths` runs for the entry file too, and records its functions with an EMPTY path,
so the lookup succeeded and the emptiness test then threw the answer away. Callee-line tracking
stayed off, and every UNLOCATED diagnostic raised inside such an expansion fell back to
`currentStmtLine`, which during an expansion is the CALL.

    class Box:
        def __init__(self, n: uint8) -> None:
            self.buf: uint8[n]      <- line 7, what the message is about
    b = Box(s)                      <- line 13, what was reported

A class body is where it shows up first because a class body is lowered when the class is
constructed, but it is not about classes: the same shape in a plain @inline function reports
the call too, as long as the diagnostic is unlocated. Being unlocated is the discriminator.
Issue #233.

WHAT DISCRIMINATES: `test_an_array_size_in_a_class_body_names_the_declaration`. Against the
unfixed compiler it reports the construction.

WHAT IS INVARIANT, and here on purpose:
  - the imported-module spelling, which already worked and must not move
  - the caret's ABSENCE. The column is a separate defect, blocked by the string wall in #177:
    'n' comes from splitting the annotation's text and `AnnAssign.Annotation` is a string, so
    there is no node to take a column from. A fix that produced a caret here would have
    invented one, so the test pins column 1 rather than tolerating any column.
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

BOX = (
    "class Box:\n"                                  # +1
    "    def __init__(self, n: uint8) -> None:\n"   # +2
    "        self.buf: uint8[n]\n"                  # +3  <- the declaration
    "        self.n: uint8 = n\n"                   # +4
)

ENTRY = (
    "from pymcu.types import uint8\n"                        # 1
    "from pymcu.chips.atmega328p import GPIOR0\n"            # 2
    "\n\n"                                                   # 3-4
    + BOX +                                                  # 5-8, declaration on 7
    "\n\n"
    "def main() -> None:\n"                                  # 11
    "    s: uint8 = GPIOR0.value\n"                          # 12
    "    b = Box(s)\n"                                       # 13  <- the construction
    "    while True:\n"
    "        pass\n"
)

MODULE_BOX = "from pymcu.types import uint8\n\n\n" + BOX      # declaration on 6
MODULE_MAIN = (
    "from pymcu.types import uint8\n"
    "from pymcu.chips.atmega328p import GPIOR0\n"
    "from box import Box\n"
    "\n\n"
    "def main() -> None:\n"
    "    s: uint8 = GPIOR0.value\n"
    "    b = Box(s)\n"
    "    while True:\n"
    "        pass\n"
)


def build(tmp_path: Path, files: dict, py_parser: bool = False):
    for name, text in files.items():
        (tmp_path / name).write_text(text)
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "--emit-ir", str(tmp_path / "f.mir"),
         "--target", "atmega328p", "--freq", "16000000",
         "-I", str(tmp_path), "-I", str(STDLIB)],
        capture_output=True, text=True,
        env={**os.environ, **({"PYMCU_PY_PARSER": "1"} if py_parser else {})},
    )
    return proc.stdout + proc.stderr


def location(out: str):
    m = LOCATION.search(out)
    assert m, f"no located diagnostic in:\n{out}"
    return Path(m.group(1)).name, int(m.group(2)), int(m.group(3))


# --- what discriminates -------------------------------------------------------

@pytest.mark.parametrize("py_parser", [False, True], ids=["csharp", "python"])
def test_an_array_size_in_a_class_body_names_the_declaration(tmp_path, py_parser):
    out = build(tmp_path, {"main.py": ENTRY}, py_parser)
    name, line, _ = location(out)
    assert name == "main.py"
    text = (tmp_path / name).read_text().splitlines()
    assert "self.buf: uint8[n]" in text[line - 1], \
        f"main.py:{line} is {text[line - 1]!r}; the construction is not what the message is about"


# --- invariants ---------------------------------------------------------------

@pytest.mark.parametrize("py_parser", [False, True], ids=["csharp", "python"])
def test_the_imported_spelling_still_names_the_declaration(tmp_path, py_parser):
    """#164's half, which already worked. The fix must not move it."""
    out = build(tmp_path, {"box.py": MODULE_BOX, "main.py": MODULE_MAIN}, py_parser)
    name, line, _ = location(out)
    assert name == "box.py"
    text = (tmp_path / name).read_text().splitlines()
    assert "self.buf: uint8[n]" in text[line - 1]


def test_no_caret_is_invented_for_the_annotation(tmp_path):
    """The column is a separate defect and stays open.

    `'n'` is recovered by splitting the annotation's text, and `AnnAssign.Annotation` is a
    string, so nothing here knows where in the line that `n` is. Column 1 is the value that
    means "not measured", and the renderer draws no caret for it. A fix that reported any other
    column would be pointing at a character chosen because it was available.
    """
    _, _, col = location(build(tmp_path, {"main.py": ENTRY}))
    assert col == 1, f"column {col} is a claim about a character nothing measured"

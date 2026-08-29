"""PyMCU#178: an error inside an imported module must name that module's file.

A diagnostic raised while lowering an imported module carried the module's LINE number and
the ENTRY file's PATH, because CompilerError.File was set by four frontend checks and by none
of the IR generator's error sites. The reader was sent to a line of main.py that has nothing
to do with the message, and when the entry file also had an injected preamble the driver then
subtracted that preamble's offset from a line number belonging to a different file.

These run the real compiler, because the bug is in what the compiler prints, and the driver's
remap is the second half of the same story.
"""

import os
import subprocess
import sys
import textwrap
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler not built at build/bin/pymcuc"
)


def _compile(tmp_path: Path, main_src: str, module_src: str) -> str:
    (tmp_path / "drivers").mkdir(exist_ok=True)
    (tmp_path / "drivers" / "led.py").write_text(textwrap.dedent(module_src).lstrip())
    (tmp_path / "main.py").write_text(textwrap.dedent(main_src).lstrip())
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "--target", "atmega328p",
         "-o", os.devnull, "-I", str(tmp_path), "-I", str(STDLIB)],
        capture_output=True, text=True,
    )
    return proc.stderr


MODULE_WITH_AN_UNDEFINED_NAME = """
    from pymcu.types import uint8

    def helper(v: uint8) -> uint8:
        y: uint8 = v
        z: uint8 = y + not_a_real_name
        return z
"""

MAIN_CALLING_IT = """
    from pymcu.types import uint8
    from drivers.led import helper

    def main() -> None:
        a: uint8 = 3
        b: uint8 = helper(a)
        while True:
            pass
"""


def test_the_diagnostic_names_the_module_not_the_entry_file(tmp_path):
    err = _compile(tmp_path, MAIN_CALLING_IT, MODULE_WITH_AN_UNDEFINED_NAME)

    assert "not_a_real_name" in err
    header = next(l for l in err.splitlines() if "error:" in l)
    assert "drivers/led.py" in header, header
    assert "main.py:" not in header, header


def test_it_names_the_modules_own_line(tmp_path):
    # `z: uint8 = y + not_a_real_name` is line 5 of drivers/led.py.
    err = _compile(tmp_path, MAIN_CALLING_IT, MODULE_WITH_AN_UNDEFINED_NAME)

    header = next(l for l in err.splitlines() if "error:" in l)
    assert ":5:" in header, header


def test_the_snippet_shows_the_modules_source(tmp_path):
    # The snippet used to be rendered against the entry file at the module's line number,
    # which either showed an unrelated line of main.py or, when that line was blank, nothing.
    err = _compile(tmp_path, MAIN_CALLING_IT, MODULE_WITH_AN_UNDEFINED_NAME)

    assert "z: uint8 = y + not_a_real_name" in err, err
    assert "^" in err, err


def test_an_error_in_the_entry_file_still_names_the_entry_file(tmp_path):
    err = _compile(
        tmp_path,
        """
        from pymcu.types import uint8

        def main() -> None:
            c: uint8 = also_not_real
            while True:
                pass
        """,
        MODULE_WITH_AN_UNDEFINED_NAME.replace("y + not_a_real_name", "y"),
    )

    header = next(l for l in err.splitlines() if "error:" in l)
    assert "main.py" in header, header
    assert "drivers/led.py" not in header, header

# --- the file and the COLUMN must come from the same place (PyMCU#177 tail) ------------
#
# UserError(msg, node) takes the LINE and COLUMN from the node and the FILE from the module
# being lowered. While everything is one file those agree. A node that arrives from a
# different module than the one being lowered makes them disagree, and the result is worse
# than no caret: an arrow at a real column of the WRONG file, which no single-file test sees.
#
# These pin that the sites in Statements.cs and ControlFlow.cs do not have that shape. Each
# main.py deliberately has a LONG line where the module has a short one, so a file mixup puts
# the caret in the middle of the padding instead of on the token, visibly rather than subtly.

CALLER_WITH_A_LONG_LINE = """
    from pymcu.types import uint8
    from drivers.led import {name}

    def main() -> None:
        zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz: uint8 = 1
        b: uint8 = {call}
"""


@pytest.mark.parametrize("module_src,name,call,want_line,want_col", [
    # a string returned from a uint8 function: blames stmt.Value, a stamped StringLiteral
    ("""
        from pymcu.types import uint8

        def returns_str() -> uint8:
            x: uint8 = 1
            return "nope"
    """, "returns_str", "returns_str()", 5, 12),
])
def test_the_caret_and_the_file_agree_across_modules(
        tmp_path, module_src, name, call, want_line, want_col):
    err = _compile(
        tmp_path,
        CALLER_WITH_A_LONG_LINE.format(name=name, call=call),
        module_src,
    )

    header = next(l for l in err.splitlines() if "error:" in l)
    assert "drivers/led.py" in header, header
    assert f":{want_line}:{want_col}:" in header, header
def test_a_raise_inside_an_inlined_callee_reports_the_call_site(tmp_path):
    """The one site in ControlFlow.cs where the node is available and deliberately not passed.

    An @inline callee that raises unconditionally is refused while it is being expanded. The
    node in hand is the callee's `raise`; the useful location is the CALLER's call site, which
    is the line the reader can actually change. Passing the node would report the callee's line
    against whichever module is being lowered, which is the two halves of a location coming
    from different places.

    If this ever starts reporting drivers/led.py, someone passed the node.
    """
    err = _compile(
        tmp_path,
        """
        from pymcu.types import uint8
        from drivers.led import probe

        def main() -> None:
            qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq: uint8 = 1
            v: uint8 = probe(3)
        """,
        """
        from pymcu.types import uint8, inline

        @inline
        def probe(pin: uint8) -> uint8:
            raise ValueError("unsupported pin")
        """,
    )

    header = next(l for l in err.splitlines() if "error:" in l)
    assert "main.py:6:" in header, header
    assert "drivers/led.py" not in header, header
    assert "^" not in err, "no caret: the location is the call site, not a measured column"

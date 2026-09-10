"""A read of an undefined attribute is judged against ITS OWN class, not the whole program.

PyMCU#276. `d.mode` compiled or was refused depending on whether ANY class ANYWHERE in the
same firmware happened to declare a field called `mode`. When it compiled it lowered to an
unwritten `<base>_mode` slot: nothing writes it, the allocator hands out a register with no
writer, and the read is indeterminate. No diagnostic, no dispatch, a value on the wire that
was never computed.

EVERY COLLISION TEST HERE NEEDS TWO CLASSES IN ONE PROGRAM, AND THAT IS THE WHOLE POINT.
The class under test is byte-identical in the passing and failing versions; the only
difference is a second, unrelated class elsewhere in the file. Cutting the program down to a
minimal reproduction REMOVES the collision and the bug vanishes, which is exactly why it
survived: shrinking the program is the first thing anyone investigating it does.

The read path consulted `assignedMemberNames`, the program-wide union of every member name
assigned anywhere. That union was a deliberate choice, not an oversight -- see the comment on
it in State.cs, which says it avoids "per-class layout completeness, which is unreliable".
The fix keeps the assignment-based collection and adds the missing key. It deliberately does
NOT consult classFieldLayout: that map omits array fields (PyMCU#281) and never learns fields
assigned inside a `match`, so gating a READ on it would refuse valid code -- a worse failure
than the bug being fixed. `test_an_array_field_is_still_readable` is the guard on that.
"""

import os
import re
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"
TRANSLATOR = REPO / "src" / "compiler" / "Frontend" / "PyParser" / "pymcu_translate.py"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler not built at build/bin/pymcuc"
)

BOTH_FRONT_ENDS = pytest.mark.parametrize("py_parser", [False, True], ids=["cs", "bridge"])

HEADER = "from pymcu.hal.uart import UART\nfrom pymcu.types import uint8\n"

# An unrelated class that declares `nope`. Present in the collision cases, absent otherwise;
# nothing else differs between the two.
OTHER = """
class Other:
    def __init__(self):
        self.nope: uint8 = 9
"""


def _compile(tmp_path: Path, source: str, py_parser: bool):
    """(ok, stderr). Never asserts on its own, so a test can demand either outcome."""
    src = tmp_path / "main.py"
    src.write_text(HEADER + source)
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
        env["PYMCU_PY_PARSER_SCRIPT"] = str(TRANSLATOR)
    else:
        env.pop("PYMCU_PY_PARSER", None)
    proc = subprocess.run(
        [str(PYMCUC), str(src), "--target", "atmega328p",
         "--emit-ir", os.devnull, "-o", os.devnull,
         "-I", str(STDLIB), "-I", str(tmp_path)],
        capture_output=True, text=True, env=env,
    )
    return proc.returncode == 0, proc.stderr


@BOTH_FRONT_ENDS
def test_a_colliding_name_elsewhere_does_not_make_the_read_legal(tmp_path, py_parser):
    """THE BUG. This is the case that builds on the unfixed compiler.

    `C` here is character-for-character the same as in the test below. The only difference in
    the whole program is `Other`, which `C` never mentions and never touches.
    """
    ok, err = _compile(tmp_path, OTHER + """
class C:
    def __init__(self):
        self.real: uint8 = 1

def main():
    c = C()
    UART(9600).write(c.nope)
""", py_parser)

    assert not ok, (
        "a read of an attribute C never assigns compiled, because an unrelated class "
        "declared that name. That lowers to an unwritten slot and reads indeterminate."
    )
    # The diagnostic must name the RECEIVER'S class, or it cannot tell the reader which of the
    # two classes in the program is the one missing the field.
    assert "'C' has no attribute 'nope'" in err, err
    # And it must list what C does have, which is the data needed to spot a typo.
    assert "Assigned members: real" in err, err


@BOTH_FRONT_ENDS
def test_the_same_read_is_refused_with_no_collision_present(tmp_path, py_parser):
    """The control: identical `C`, no `Other`. Refused before the fix and after it.

    Without this, the test above would pass on a compiler that simply refuses every attribute
    read, and the pair is what shows the verdict no longer depends on the rest of the program.
    """
    ok, err = _compile(tmp_path, """
class C:
    def __init__(self):
        self.real: uint8 = 1

def main():
    c = C()
    UART(9600).write(c.nope)
""", py_parser)

    assert not ok
    assert "'C' has no attribute 'nope'" in err, err


@BOTH_FRONT_ENDS
def test_a_real_field_still_reads_with_a_collision_present(tmp_path, py_parser):
    """The fix must not refuse valid code, which is the failure mode worse than the bug."""
    ok, err = _compile(tmp_path, OTHER + """
class C:
    def __init__(self):
        self.real: uint8 = 1

def main():
    c = C()
    UART(9600).write(c.real)
""", py_parser)

    assert ok, err


@BOTH_FRONT_ENDS
def test_self_dot_attribute_inside_a_method_is_scoped_too(tmp_path, py_parser):
    """`self.nope` collapsed under a collision exactly as `c.nope` did.

    A separate position, not a restatement: the receiver is `self` rather than a local, so it
    resolves through a different binding and could have been missed by a fix that only handled
    a named instance.
    """
    ok, err = _compile(tmp_path, OTHER + """
class C:
    def __init__(self):
        self.real: uint8 = 1

    def get(self) -> uint8:
        return self.nope

def main():
    UART(9600).write(C().get())
""", py_parser)

    assert not ok, "self.nope compiled because an unrelated class declared `nope`"
    assert "'C' has no attribute 'nope'" in err, err


@BOTH_FRONT_ENDS
def test_an_array_field_is_still_readable(tmp_path, py_parser):
    """The guard on the fix's one real hazard.

    classFieldLayout is the obvious source for "does this class have this field" and it is the
    wrong one: it omits array fields entirely (PyMCU#281 -- a class with `self.buf: uint8[4]`
    reports `Declared fields: n, m`). A fix that consulted it would refuse `self.buf` on the
    HAL and on most drivers. This test fails loudly if anyone ever swaps the assignment-based
    lookup for a layout-based one.
    """
    ok, err = _compile(tmp_path, """
class C:
    def __init__(self):
        self.n: uint8 = 0
        self.buf: uint8[4] = [1, 2, 3, 4]

    def get(self) -> uint8:
        return self.buf[2]

def main():
    UART(9600).write(C().get())
""", py_parser)

    assert ok, err


@BOTH_FRONT_ENDS
def test_a_field_the_base_class_assigns_is_readable_from_the_subclass(tmp_path, py_parser):
    """Inheritance: the per-class set must be consulted up the chain, not just on the receiver.

    Getting this wrong refuses correct code rather than accepting wrong code, so it is the
    more dangerous half of the change.
    """
    ok, err = _compile(tmp_path, """
class Base:
    def __init__(self):
        self.n: uint8 = 7

class Foo(Base):
    def __init__(self):
        super().__init__()
        self.m: uint8 = 1

    def get(self) -> uint8:
        return self.n

def main():
    UART(9600).write(Foo().get())
""", py_parser)

    assert ok, err


@BOTH_FRONT_ENDS
def test_the_write_path_diagnostic_is_unchanged(tmp_path, py_parser):
    """The two paths must not diverge in the other direction.

    The write path already scoped to the receiver's class and already said so well; this fix
    touches only the read. If a later change unifies the two messages, this test should be
    updated deliberately rather than silently.
    """
    ok, err = _compile(tmp_path, """
class C:
    def __init__(self):
        self.real: uint8 = 1

    def bump(self):
        self.typoed = 5

def main():
    c = C()
    c.bump()
    UART(9600).write(c.real)
""", py_parser)

    assert not ok
    assert "'C' has no field 'typoed'" in err, err
    assert "Declared fields: real" in err, err

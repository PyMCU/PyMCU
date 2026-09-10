"""An undefined base class was accepted in silence, and every later message blamed something else.

`class Foo(Basse)` -- a one-character typo -- compiled. The class then behaved as though it had
no base at all, and the damage only surfaced when something inherited was used, as a three-step
sequence in which no message ever contained `Basse`:

    1  class Foo(Basse): ...                accepted, nothing said
    2  Foo()                                "class 'Foo' cannot be constructed: it has no
                                             __init__ method ... add `def __init__(self): ...`"
    3  follow that advice exactly           "'f' is an integer: 'greet()' is not available"

Step 2 is FALSE of the program: `Foo` does inherit an `__init__` from its base, and
`test_a_correctly_spelled_base_supplies_its_init` pins that. Following its advice adds a
constructor that shadows the inherited one, so a reader who obeys the diagnostic ends up further
from the fix than they started.

THE TEST THAT MATTERS IS THE SILENT ONE. Declaring the typo and never using anything inherited
builds clean on HEAD, on both front ends. The noisy steps were always visible; the accepted
program was not, which is why this is filed as silent acceptance rather than a bad message.

The check is deferred until every module has been scanned, so a base declared after its subclass
or in another module is still legal; `test_a_base_defined_after_its_subclass_still_builds` is
what stops the fix from being a new false refusal.
"""

import os
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

FRONTENDS = [pytest.param(False, id="csharp"), pytest.param(True, id="python")]

BASE = (
    "class Base:\n"
    "    def __init__(self) -> None:\n"
    "        self.v: uint8 = 1\n"
    "    def greet(self) -> uint8:\n"
    "        return 42\n\n\n"
)


def compile_(tmp_path: Path, source: str, py_parser: bool):
    (tmp_path / "main.py").write_text(source)
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", "/dev/null",
         "--target", "atmega328p", "--freq", "16000000",
         "-I", str(tmp_path), "-I", str(STDLIB), "--emit-ir", "/dev/null"],
        capture_output=True, text=True, env=env,
    )
    return "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_an_undefined_base_is_refused_even_when_nothing_inherited_is_used(tmp_path, py_parser):
    """The one that fails on HEAD. Nothing here touches the base, so nothing forced the issue."""
    ok, out = compile_(
        tmp_path,
        BASE + "class Foo(Basse):\n"
               "    def hello(self) -> uint8:\n"
               "        return 2\n\n\n"
               "def main() -> None:\n"
               "    pass\n",
        py_parser,
    )
    assert not ok, out
    assert "'Basse'" in out, out
    assert "base class" in out, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_the_refusal_names_the_base_and_offers_the_spelling(tmp_path, py_parser):
    """Naming the base is the whole fix: no message in the old sequence contained it."""
    ok, out = compile_(
        tmp_path,
        BASE + "class Foo(Basse):\n    pass\n\n\n"
               "def main() -> None:\n"
               "    f: Foo = Foo()\n"
               "    x: uint8 = f.greet()\n",
        py_parser,
    )
    assert not ok, out
    assert "class 'Foo' has a base class 'Basse' that is not defined" in out, out
    assert "did you mean 'Base'?" in out, out
    # The two messages the reader used to get instead, neither of which named the base.
    assert "cannot be constructed" not in out, out
    assert "is an integer" not in out, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_a_correctly_spelled_base_supplies_its_init(tmp_path, py_parser):
    """Why step 2 of the old sequence was false, pinned so the claim is not just narrative.

    `Foo` declares no `__init__` and constructs anyway, because it inherits one. The old advice
    to "add `def __init__(self): ...`" would have shadowed this.
    """
    ok, out = compile_(
        tmp_path,
        BASE + "class Foo(Base):\n    pass\n\n\n"
               "def main() -> None:\n"
               "    f: Foo = Foo()\n"
               "    x: uint8 = f.greet()\n",
        py_parser,
    )
    assert ok, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_a_base_defined_after_its_subclass_still_builds(tmp_path, py_parser):
    """The control against the cheap version of this fix.

    Checking the base where it is parsed would refuse this, and it is legal. The check is
    deferred until every module has been scanned for exactly this reason.
    """
    ok, out = compile_(
        tmp_path,
        "class Foo(Base):\n"
        "    def hello(self) -> uint8:\n"
        "        return 2\n\n\n"
        + BASE +
        "def main() -> None:\n"
        "    pass\n",
        py_parser,
    )
    assert ok, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_a_class_with_no_base_is_unaffected(tmp_path, py_parser):
    ok, out = compile_(
        tmp_path,
        "class Solo:\n"
        "    def __init__(self) -> None:\n"
        "        self.v: uint8 = 1\n\n\n"
        "def main() -> None:\n"
        "    s: Solo = Solo()\n",
        py_parser,
    )
    assert ok, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_the_no_init_message_still_fires_for_its_own_cause(tmp_path, py_parser):
    """Steps 2 and 3 are unreachable for THIS cause, not removed.

    A class that genuinely has no `__init__` and no base must still be told so. Without this,
    a fix that silenced the message entirely would look identical to one that made it
    unreachable only where it was wrong.
    """
    ok, out = compile_(
        tmp_path,
        "class C:\n"
        "    def hello(self) -> uint8:\n"
        "        return 1\n\n\n"
        "def main() -> None:\n"
        "    c: C = C()\n",
        py_parser,
    )
    assert not ok, out
    assert "has no __init__ method" in out, out

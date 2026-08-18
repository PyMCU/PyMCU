"""Every function that installs an ISR must be @inline.

Without the decorator the function is compiled standalone, its `name` argument
is no longer a compile-time constant, and `compile_isr()` cannot resolve the
handler. The failure surfaces far from the missing decorator, and on the two
chips where it happened it made every program that touches a pin fail to build.

The census is the test: a new chip HAL that forgets the decorator fails here
instead of six cells later.
"""

import ast
from pathlib import Path

import pytest

STDLIB = Path(__file__).resolve().parents[2] / "lib" / "src" / "pymcu"


def installs_an_isr(node):
    return any(isinstance(n, ast.Call) and isinstance(n.func, ast.Name)
               and n.func.id == "compile_isr"
               for n in ast.walk(node))


def decorated_inline(node):
    return any(isinstance(d, ast.Name) and d.id == "inline" for d in node.decorator_list)


def census():
    """Every function in the stdlib that calls compile_isr, with its file."""
    out = []
    for path in sorted(STDLIB.rglob("*.py")):
        tree = ast.parse(path.read_text())
        for node in ast.walk(tree):
            if isinstance(node, ast.FunctionDef) and installs_an_isr(node):
                out.append((path.relative_to(STDLIB).as_posix(), node.name,
                            node.lineno, decorated_inline(node)))
    return out


CENSUS = census()


@pytest.mark.parametrize("path,name,line,inlined",
                         CENSUS, ids=[f"{p}:{n}" for p, n, _, _ in CENSUS])
def test_every_isr_installer_is_inline(path, name, line, inlined):
    assert inlined, f"{path}:{line} {name}() calls compile_isr without @inline"


def test_the_census_is_not_empty():
    """A census that silently matches nothing would pass for ever."""
    assert len(CENSUS) >= 20, f"only {len(CENSUS)} installers found; the scan is broken"


def test_the_census_reaches_past_avr():
    """A scan limited to hal/avr would leave the PIC18 timer unguarded."""
    families = {p.split("/")[1] for p, _, _, _ in CENSUS if p.startswith("hal/")}
    assert {"avr", "pic18"} <= families, f"the census only covers {families}"


def test_every_file_that_mentions_compile_isr_is_accounted_for():
    """Integrity check on the instrument: a call outside a function would be missed."""
    mentions = {p.relative_to(STDLIB).as_posix() for p in STDLIB.rglob("*.py")
                if "compile_isr(" in p.read_text()}
    covered = {p for p, _, _, _ in CENSUS}
    unexplained = mentions - covered - {"types.py"}
    assert not unexplained, \
        f"these files call compile_isr somewhere the census does not look: {unexplained}"

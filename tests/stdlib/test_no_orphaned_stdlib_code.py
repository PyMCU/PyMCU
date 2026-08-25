"""No copy of the stdlib may sit where nothing can reach it.

`hal/_servo/` was a second copy of the servo HAL that nothing imported. It shipped
in the wheel, an import could still reach it, and it carried the `degrees * 11`
angle map that the live copy had been fixed away from after a logic analyser
measured it wrong on a real Uno. A copy no test covers drifts, and this one had.

`drivers/_ds18b20/avr.py` was the same fault inside one file: two complete
implementations of the 1-Wire driver concatenated, each defining `_ow_reset`,
`_ow_read` and `_avr_read`. Only the first was ever compiled, so the second half
of the file could say anything at all.

Both are structural, so both are caught by looking at the tree rather than at
generated code:

  - every package under the stdlib must be imported from somewhere in it
  - no file may define the same top-level name twice
"""

import ast
from pathlib import Path

import pytest

STDLIB = Path(__file__).resolve().parents[2] / "lib" / "src" / "pymcu"

# Packages reached by name rather than through an import statement:
# `pymcu.chips.<target>` and `pymcu.boards.<board>` are selected by the build, and
# `pymcu.math` is what the module loader resolves a user program's `import math`
# to, and where the driver finds the float and AVR math assembly it injects.
ENTRY_POINTS = {"chips", "boards", "math"}


def sources():
    return [p for p in sorted(STDLIB.rglob("*.py")) if "__pycache__" not in p.parts]


def imported_names():
    """Every dotted name any stdlib file imports, plus each of its prefixes."""
    names = set()
    for path in sources():
        for node in ast.walk(ast.parse(path.read_text())):
            if isinstance(node, ast.ImportFrom) and node.module:
                names.add(node.module)
                names.update(f"{node.module}.{a.name}" for a in node.names)
            elif isinstance(node, ast.Import):
                names.update(a.name for a in node.names)
    return {".".join(n.split(".")[:i])
            for n in names for i in range(1, len(n.split(".")) + 1)}


PACKAGES = [p.parent.relative_to(STDLIB).as_posix()
            for p in sorted(STDLIB.rglob("__init__.py"))
            if "__pycache__" not in p.parts and p.parent != STDLIB]


@pytest.mark.parametrize("package", PACKAGES)
def test_every_package_is_reachable(package):
    if package.split("/")[0] in ENTRY_POINTS:
        pytest.skip("selected by the build, not by an import")
    dotted = "pymcu." + package.replace("/", ".")
    assert dotted in imported_names(), \
        (f"nothing in the stdlib imports {dotted}; it ships in the wheel, an import "
         f"can still reach it, and no test covers what it says")


def test_the_package_scan_is_not_empty():
    """A scan that silently matched nothing would pass for ever."""
    assert len(PACKAGES) >= 20, f"only {len(PACKAGES)} packages found; the scan is broken"


@pytest.mark.parametrize("path", [p.relative_to(STDLIB).as_posix() for p in sources()],
                         ids=lambda p: p)
def test_no_file_defines_the_same_name_twice(path):
    """Only the first definition is compiled, so a second one is unreachable code."""
    tree = ast.parse((STDLIB / path).read_text())
    seen, dups = set(), []
    for node in tree.body:      # top level only: if/else branches are alternatives
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
            if node.name in seen:
                dups.append(f"{node.name} (line {node.lineno})")
            seen.add(node.name)
    assert not dups, f"{path} redefines {', '.join(dups)}; only the first is compiled"

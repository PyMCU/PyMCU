"""A module we ship can be imported, and `typing` is the way that stopped being true.

`lib/src/pymcu/pio.py` imported `typing`, which this compiler cannot resolve, so `pymcu.pio`
could not be imported in ANY spelling. The failure was an ImportError raised inside our own
stdlib rather than in the reader's program, and it made the whole PIO declaration module
unreachable (#199).

`pymcu.types` imports `typing` too and is fine, which is what hid this: it is in
`BuiltinModuleNames.All`, so the dependency graph never loads it from disk and its import is
never followed. Any OTHER stdlib module doing the same is fatal, and nothing said so.

The second test is the guard rather than the fix. Adding a `typing` import to a stdlib module
is an easy and natural edit -- it is what every type checker asks for -- and the module it
lands in stops being importable with no test failing anywhere near the change.
"""

import re
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"
BUILTIN_LIST = REPO / "src" / "compiler" / "Common" / "BuiltinModuleNames.cs"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

SPELLINGS = [
    "from pymcu.pio import PINS",
    "import pymcu.pio",
    "from pymcu.pio import pull, push",
    "from pymcu.pio import PINS, PIN, GPIO, NULL, ISR, OSR",
]


@pytest.mark.parametrize("line", SPELLINGS, ids=lambda s: s[:34])
def test_every_spelling_of_the_pio_import_builds(tmp_path, line):
    """The discriminator. All four answered `Module not found: typing`, pointing at
    lib/src/pymcu/pio.py:9 -- a file the reader did not write."""
    (tmp_path / "main.py").write_text(line + "\n\n\ndef main():\n    pass\n")
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", "/dev/null",
         "--target", "rp2350", "--freq", "125000000",
         "-I", str(tmp_path), "-I", str(STDLIB), "--emit-ir", "/dev/null"],
        capture_output=True, text=True,
    )
    out = proc.stdout + proc.stderr
    assert "[BUILD_OK]" in proc.stdout, out


def builtin_modules():
    """The module names the dependency graph never loads from disk."""
    text = BUILTIN_LIST.read_text()
    names = re.search(r"HashSet<string>\s+All\s*=\s*\[([^\]]*)\]", text)
    assert names, f"could not find the builtin module list in {BUILTIN_LIST}"
    return {n.strip().strip(chr(34)) for n in names.group(1).split(",") if n.strip()}


def test_no_stdlib_module_imports_typing_unless_it_is_never_loaded():
    """The guard, and the reason it is worth a test rather than a comment.

    A `typing` import is invisible until someone imports the module it sits in, and it is the
    edit a type checker asks for. `pymcu.types` gets away with it only because it is in
    BuiltinModuleNames.All and its source is never read; the exemption is checked against that
    list rather than hard-coded, so moving a module out of the list fails here instead of
    failing whoever imports it next.
    """
    builtin = builtin_modules()
    offenders = []
    for path in sorted((STDLIB / "pymcu").rglob("*.py")):
        module = ".".join(path.relative_to(STDLIB).with_suffix("").parts)
        if module.endswith(".__init__"):
            module = module[: -len(".__init__")]
        source = path.read_text()
        imports_typing = any(l.startswith(("from typing ", "import typing"))
                             for l in source.splitlines())
        if imports_typing and module not in builtin:
            offenders.append(f"{path.relative_to(REPO)} ({module})")

    assert not offenders, (
        "these stdlib modules import `typing` and ARE loaded from disk, so importing any of "
        "them fails with `Module not found: typing`: " + ", ".join(offenders))

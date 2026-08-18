"""The AVR memory geometry, checked end to end: avr-gcc -> chip file -> backend table.

Three places record where each chip's SRAM lives, and a wrong value in any of
them puts variables somewhere the chip does not have memory. They have already
disagreed twice, in opposite directions:

  - the PIC16F84A had the wrong number in the chip file and the backend faithfully
    emitted it, which only a check against the vendor catches;
  - the ATmega2560 had the right number in the chip file and the backend ignored
    it, using 0x100 where the SRAM starts at 0x200 -- every static landed in
    extended I/O, and a check against the vendor would have passed on all eight
    affected parts.

So neither comparison substitutes for the other, and this file does both links of
the chain that belong to the stdlib: vendor against chip file, and chip file
against the table the backend actually reads.
"""

import json
import re
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
CHIPS = REPO / "lib" / "src" / "pymcu" / "chips"
BACKEND_BINARY = (REPO.parent / "pymcu-avr" / "src" / "python" / "pymcu"
                  / "backend" / "avr" / "pymcuc-avr")

_avr_gcc = sorted(Path.home().glob(
    ".cache/uv/archive-v0/*/pymcu_avr_toolchain/bin/avr-gcc"))
AVR_GCC = _avr_gcc[-1] if _avr_gcc else None


def chip_constants(path: Path):
    """Read the constants from source; an import can serve a stale .pyc."""
    text = path.read_text()
    out = {}
    for name in ("RAM_START", "RAM_SIZE"):
        m = re.search(rf"^{name}\s*=\s*(0x[0-9A-Fa-f]+|\d+)", text, re.M)
        if m:
            out[name] = int(m.group(1), 0)
    arch = re.search(r'arch\s*=\s*"(\w+)"', text)
    out["arch"] = arch.group(1) if arch else None
    return out


AVR_CHIPS = sorted(f.stem for f in CHIPS.glob("*.py")
                   if not f.stem.startswith("_")
                   and chip_constants(f).get("arch") == "avr")


ARITHMETIC = re.compile(r"^[0-9a-fA-Fx+\-*/()\s]+$")


def vendor(chip: str):
    """RAMSTART, RAMEND and whether the core has the long jump and call.

    The values are taken after preprocessing rather than from the -dM text: on
    several parts RAMEND is published as `(RAMSTART + RAMSIZE - 1)`, so reading
    the macro body gives an expression and not a number. Letting the vendor's
    own preprocessor expand it keeps the vendor as the authority.
    """
    probe = ("#include <avr/io.h>\n"
             "__pymcu RAMSTART | RAMEND | 1\n")
    proc = subprocess.run(
        [str(AVR_GCC), f"-mmcu={chip}", "-E", "-P", "-x", "c", "-"],
        input=probe, capture_output=True, text=True)
    if proc.returncode != 0:
        return None
    line = next((l for l in proc.stdout.splitlines() if l.startswith("__pymcu")), None)
    if line is None:
        return None
    parts = [p.strip() for p in line[len("__pymcu"):].split("|")]

    def number(text):
        if not ARITHMETIC.match(text):
            return None
        return eval(text, {"__builtins__": {}}, {})

    macros = subprocess.run(
        [str(AVR_GCC), f"-mmcu={chip}", "-dM", "-E", "-x", "c", "-"],
        input="#include <avr/io.h>\n", capture_output=True, text=True).stdout
    return {"RAMSTART": number(parts[0]), "RAMEND": number(parts[1]),
            "HAS_JMP_CALL": "__AVR_HAVE_JMP_CALL__" in macros}


def backend_table():
    """Ask the backend what it knows, instead of reading what its source looks like.

    The previous version pattern-matched the C# table. That made a source file
    somebody else owns into this test's interface: two routine reformats made the
    pattern match nothing, and every backend check would have skipped with the
    false reason that the checkout was missing. The subcommand reads the catalogue
    through the same accessor the code generator calls, so what is checked here is
    what the compiler uses, not what its text appears to say.
    """
    if not BACKEND_BINARY.exists():
        return None
    proc = subprocess.run([str(BACKEND_BINARY), "devices"],
                          capture_output=True, text=True)
    if proc.returncode != 0:
        raise AssertionError(
            f"{BACKEND_BINARY} devices exited {proc.returncode}: {proc.stderr[:200]}")
    try:
        rows = json.loads(proc.stdout)
    except json.JSONDecodeError as exc:
        raise AssertionError(
            f"{BACKEND_BINARY} devices did not return JSON: {exc}") from exc
    return {r["Chip"]: (r["RamStart"], r["RamSize"], r["HasJmpCall"], r["RamEnd"])
            for r in rows}


BACKEND = backend_table()

needs_gcc = pytest.mark.skipif(AVR_GCC is None, reason="avr toolchain not installed")
needs_backend = pytest.mark.skipif(BACKEND is None, reason="AVR backend binary not present")


@needs_backend
def test_the_backend_answered_with_a_catalogue():
    """A binary that is there but says nothing is a broken instrument, never a skip.

    Only an absent binary is a legitimate skip. Exit codes and unreadable output
    already raise while the table is being built; an empty catalogue would pass
    every other check in this file by having nothing to disagree with.
    """
    assert BACKEND, (f"{BACKEND_BINARY} devices returned an empty catalogue; "
                     "these checks are not running")


@needs_gcc
@pytest.mark.parametrize("chip", AVR_CHIPS)
def test_ram_start_matches_the_vendor(chip):
    ours = chip_constants(CHIPS / f"{chip}.py")
    theirs = vendor(chip)
    if theirs is None or theirs["RAMSTART"] is None:
        pytest.skip(f"avr-gcc does not know {chip}")
    assert ours["RAM_START"] == theirs["RAMSTART"], \
        f"{chip}: RAM_START 0x{ours['RAM_START']:04X} vs avr-gcc 0x{theirs['RAMSTART']:04X}"


@needs_gcc
@pytest.mark.parametrize("chip", AVR_CHIPS)
def test_ram_size_matches_the_vendor(chip):
    """RAM_SIZE is checked through RAMEND, which is what the vendor publishes."""
    ours = chip_constants(CHIPS / f"{chip}.py")
    theirs = vendor(chip)
    if theirs is None or theirs["RAMEND"] is None:
        pytest.skip(f"avr-gcc does not know {chip}")
    end = ours["RAM_START"] + ours["RAM_SIZE"] - 1
    assert end == theirs["RAMEND"], \
        f"{chip}: our RAM ends at 0x{end:04X}, avr-gcc says 0x{theirs['RAMEND']:04X}"


@needs_backend
@pytest.mark.parametrize("chip", AVR_CHIPS)
def test_the_backend_table_agrees_with_the_chip_file(chip):
    """The ATmega2560 case: a correct chip file the backend was not reading."""
    if chip not in BACKEND:
        pytest.skip(f"the backend table has no entry for {chip}")
    start, size, _, _ = BACKEND[chip]
    ours = chip_constants(CHIPS / f"{chip}.py")
    assert (start, size) == (ours["RAM_START"], ours["RAM_SIZE"]), \
        (f"{chip}: backend has 0x{start:04X}/{size}, "
         f"chip file has 0x{ours['RAM_START']:04X}/{ours['RAM_SIZE']}")


@needs_gcc
@needs_backend
@pytest.mark.parametrize("chip", AVR_CHIPS)
def test_the_backend_knows_whether_the_core_has_jmp_and_call(chip):
    """The flag that decides whether JMP and CALL may be emitted at all.

    Guessing it from the chip name is right for the ATtinys by luck and wrong
    for the small ATmegas; the vendor publishes it as a macro.
    """
    if chip not in BACKEND:
        pytest.skip(f"the backend table has no entry for {chip}")
    theirs = vendor(chip)
    if theirs is None:
        pytest.skip(f"avr-gcc does not know {chip}")
    assert BACKEND[chip][2] == theirs["HAS_JMP_CALL"], \
        f"{chip}: backend says HasJmpCall={BACKEND[chip][2]}, avr-gcc says {theirs['HAS_JMP_CALL']}"


@needs_backend
@pytest.mark.parametrize("chip", AVR_CHIPS)
def test_the_backend_computes_its_own_ram_end(chip):
    """RamEnd is published, not transcribed; if it stops agreeing there is a third source."""
    if chip not in BACKEND:
        pytest.skip(f"the backend catalogue has no entry for {chip}")
    start, size, _, end = BACKEND[chip]
    assert end == start + size - 1, \
        f"{chip}: backend publishes RamEnd={end}, its own start and size give {start + size - 1}"


def test_there_are_avr_chips_to_check():
    assert len(AVR_CHIPS) >= 15, f"only {len(AVR_CHIPS)} AVR chip files found"


@needs_backend
def test_every_avr_chip_file_appears_in_the_backend_table():
    """A chip the backend has never heard of gets whatever default it falls back to."""
    missing = [c for c in AVR_CHIPS if c not in BACKEND]
    assert not missing, f"the backend catalogue has no entry for: {missing}"


def stub_binary(tmp_path, script):
    fake = tmp_path / "pymcuc-avr"
    fake.write_text("#!/bin/sh\n" + script)
    fake.chmod(0o755)
    return fake


def test_an_absent_binary_is_the_only_legitimate_skip(tmp_path, monkeypatch):
    monkeypatch.setattr(snap_module(), "BACKEND_BINARY", tmp_path / "nope")
    assert backend_table() is None


@pytest.mark.parametrize("script,why", [
    ("exit 3", "exited non-zero"),
    ("echo 'not json'", "returned something that is not JSON"),
])
def test_a_binary_that_answers_badly_is_a_hard_failure(tmp_path, monkeypatch, script, why):
    """A broken instrument is never a skip: it would take the checks out of service."""
    monkeypatch.setattr(snap_module(), "BACKEND_BINARY", stub_binary(tmp_path, script))
    with pytest.raises(AssertionError):
        backend_table()


def test_an_empty_catalogue_is_caught_by_its_own_check(tmp_path, monkeypatch):
    """Zero rows parses fine and would silently agree with everything."""
    monkeypatch.setattr(snap_module(), "BACKEND_BINARY", stub_binary(tmp_path, "echo '[]'"))
    assert backend_table() == {}


def snap_module():
    import sys
    return sys.modules[__name__]

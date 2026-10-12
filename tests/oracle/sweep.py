"""Runs the pymcu-avr language oracle corpus (tests/oracle/probes/*.py, read from a pymcu-avr
checkout) against the CH32V003 this repo builds for, using CH32VSharp as the reference emulator
the way pymcu-avr uses avr8sharp. MEASUREMENT ONLY: it does not modify the shared corpus.

Three adaptations are made, all mechanical and applied uniformly to every probe:

1. The CPython reference run's chip shim is set to the real target chip/arch instead of the
   hardcoded atmega328p/avr pymcu-avr's own test_oracle.py uses -- needed so a probe that
   branches on `__CHIP__.name`/`.arch` (e.g. 175_chip_conditional.py) is compared fairly.
2. 155/440 probes seed a value from `pymcu.chips.atmega328p.GPIOR0`, an AVR-only spare
   register (`ptr(0x3E)`, boots to 0, read-only in this whole corpus -- ORACLE_SEED is never
   set to anything but 0 anywhere in pymcu-avr). Compiling as-is for rp2040/rp2350 fails
   (`pymcu.chips.atmega328p` does not exist off-AVR). The import line is swapped for an
   equivalent inline register at a scratch RAM address near the top of the target chip's
   SRAM (same ptr[uint8] shape the real chip register tables use) -- a real, zero-initialized,
   read/write byte, not a folded constant. This ONLY changes the firmware build's source, not
   the CPython reference (whose _Ptr shim is address-content-agnostic).
3. A handful of probes (196, 277; 278 shares the line but never runs it -- it is refused for
   an unrelated reason before the pointer is read) declare a `BASE` constant that assumes the
   ATmega328P's low SRAM map (0x0400, or 0x04 used as a page number via `BASE * 256` ==
   0x0400) and build a `ptr()` from it. 0x0400 is inside RP2040/RP2350's BOOTROM, not SRAM --
   on real AVR hardware this is free RAM, so the probe's own logic is sound; only the literal
   address is AVR-specific. BASE's declaration line is swapped for one that resolves to a
   scratch address near the top of the target chip's SRAM (the same region GPIOR0's
   replacement lives next to, offset so the two never overlap), widened to `uint32` --
   `uint8`/`uint16` cannot HOLD a 32-bit SRAM address, which is exactly why this could not be
   a same-width literal swap like GPIOR0's.

4. One probe (461) imports the WHOLE chip module (`import pymcu.chips.atmega328p as chip`)
   rather than a single register, to exercise `getattr(module, name, default)` -- PyMCU's one
   compile-time form of getattr, which only folds when the first argument is a real module
   import. The import is swapped for the equivalent import of the TARGET chip's own module
   (`import pymcu.chips.ch32v003 as chip`, a module that genuinely exists for this target), same
   alias. run_cpython_for_target shims that module into sys.modules too (a GPIOR0 attribute
   that reads 0, the same shape install_cpython_shims already gives atmega328p's), so the
   CPython side resolves the SAME name through the SAME kind of object.
5. One probe (583) imports TWO registers on one line (`from pymcu.chips.atmega328p import
   GPIOR0, GPIOR1`) -- the single-name substring swap in adaptation 2 only replaced the
   "GPIOR0" part, leaving ", GPIOR1" dangling as a syntax error that masked the probe's own
   refusal (a `divmod` quotient that can exceed int32) behind an unrelated one. Generalized to
   any comma-separated list of register names on that import line, each becoming its own
   scratch byte (GPIOR1 one byte above GPIOR0's, so the two never alias).

Everything else about a probe -- its logic, its expected divergences, its `# tracked:` bugs --
is measured as found. A probe whose address literal isn't covered by one of the above (there
are none left in the corpus as of this writing, but a future one would hit this) is still
compiled and run unmodified; whatever happens (compile-fail, hard fault, wrong answer) is
itself the data point.

Usage: .venv/bin/python tests/oracle/sweep.py --chip ch32v003 --out /path/out.jsonl [--limit N] [--only NAME]
"""
from __future__ import annotations

import argparse
import contextlib
import importlib.util
import io
import json
import os
import re
import subprocess
import sys
import tempfile
import time
import types
from pathlib import Path

AVR_ORACLE_ROOT = Path.home() / "Repos" / "pymcu-avr-ch32ref"
PROBES_DIR = AVR_ORACLE_ROOT / "tests" / "oracle" / "probes"
TEST_ORACLE_FILE = AVR_ORACLE_ROOT / "tests" / "oracle" / "test_oracle.py"

REPO_ROOT = Path(__file__).resolve().parents[2]  # PyMCU-ch32 worktree
RUNNER_DLL = REPO_ROOT / "build" / "oracle-runner" / "PyMCU.OracleRunner.CH32.dll"

# Matches both the single-name form ("import GPIOR0") and 583's two-name one
# ("import GPIOR0, GPIOR1"), or any other comma-separated list of register names on the
# same import line -- group(1) is the raw name list, split and stripped below.
GPIOR_IMPORT_RE = re.compile(
    r"from pymcu\.chips\.atmega328p import ([A-Za-z0-9_, ]+)")

# A probe that imports the WHOLE chip module instead of one register (461), to call
# getattr(chip, name, default) -- PyMCU's one compile-time getattr form, which only folds
# for a real module import, not a class. group(1) is the import alias.
CHIP_MODULE_IMPORT_RE = re.compile(
    r"import pymcu\.chips\.atmega328p as (\w+)")

# CH32V003_SCRATCH: the 2 KiB SRAM has no spare corner. The stack grows down from 0x20000800
# and .data/.bss grow up from 0x20000000, so a fixed absolute SRAM byte is eventually owned by
# one of them (measured: 0x200007F0 is main's own frame). The stand-in lives in the TIM2 window
# (0x40000000, 1 KiB) instead: CH32VSharp keeps whatever is written to a peripheral nothing
# models, zero at first, which is exactly the AVR GPIOR0 contract (a spare, zero-initialized,
# read/write byte outside the compiler's storage).
#
# Scratch byte near the top of each chip's SRAM, standing in for the AVR's GPIOR0 spare
# register. Same ptr[uint8] shape as every real per-chip register table in lib/src/pymcu/chips.
# A probe naming more than one register (583's GPIOR0, GPIOR1) gets one scratch byte per
# name, immediately above this one -- SCRATCH_ADDR + 1, + 2, ... -- so they never alias.
SCRATCH_ADDR = {
    "ch32v003": 0x400000F0,  # see CH32V003_SCRATCH below
}

# 196's `BASE: const[uint16] = 0x0400` (free SRAM on the ATmega328P, used as a direct
# 16-bit pointer base) and 277/278's `BASE: const[uint8] = 0x04` (the same address, written
# as a page number: `BASE * 256` == 0x0400) both assume AVR's low SRAM map. On RP2040/RP2350
# 0x0400 is inside BOOTROM. These sit further below SCRATCH_ADDR's byte -- enough room for
# 196's `ptr(BASE + 256)` and 277's two-register block (`BASE*256`, `BASE*256 + 1`) -- so
# neither collides with GPIOR0's replacement or each other. Widened to uint32: the whole
# reason this needs code, not a same-width literal swap, is that uint8/uint16 cannot hold a
# 32-bit SRAM address, the way GPIOR0's ptr[uint8] holds AVR's 16-bit one.
BASE196_ADDR = {
    "ch32v003": 0x40000100,
}
# Stored pre-divided by 256: the probes' own source multiplies BASE back by 256
# (`BASE * 256`), so BASE itself must already be address // 256.
BASE277_PAGE = {
    "ch32v003": 0x40000300 // 256,
}

TARGET_CHIP_INFO = {
    "ch32v003": ("ch32v003", "riscv"),
}

# A `# expect: refuse <text>` header is worded for AVR's toolchain (binutils' as/ld), which
# this sweep's own refusal may say correctly but differently: `@extern` on a symbol nothing
# defines refuses the SAME way on both -- a link-time undefined reference -- but AVR's ld
# phrases it "undefined reference to X" where LLVM's opt/llc/ld.lld (081 hits this from opt,
# verifying the IR before llc or ld.lld ever run) say "use of undefined value". One linker's
# wording is not a bug in the other; only the text this corpus asks for is AVR's. Probe 081
# is the one case of this in the corpus today.
LINKER_DIAGNOSTIC_EQUIVALENTS = {
    "undefined reference": "use of undefined value",
}


def _diagnostic_matches(expected: str, log: str) -> bool:
    # The driver's own CLI pretty-prints diagnostics wrapped to a column width that
    # depends on the terminal/pipe it thinks it is writing to, which can -- and for 081,
    # does -- insert a real newline INSIDE the diagnostic text itself (observed: "use of
    # undefined" / "value '@...'" split across two lines once the temp project directory
    # name was long enough to shift the wrap point). Collapsing all whitespace runs to a
    # single space before comparing makes the match robust to wherever that wrap landed.
    normalized_log = " ".join(log.split())
    if " ".join(expected.split()) in normalized_log:
        return True
    equivalent = LINKER_DIAGNOSTIC_EQUIVALENTS.get(expected)
    return equivalent is not None and " ".join(equivalent.split()) in normalized_log


def _load_avr_oracle_module():
    spec = importlib.util.spec_from_file_location("avr_test_oracle", TEST_ORACLE_FILE)
    module = importlib.util.module_from_spec(spec)
    sys.modules["avr_test_oracle"] = module
    sys.path.insert(0, str(TEST_ORACLE_FILE.parent))
    try:
        spec.loader.exec_module(module)
    finally:
        sys.path.pop(0)
    return module


avr_oracle = _load_avr_oracle_module()


def probe_files() -> list[Path]:
    return sorted(PROBES_DIR.glob("*.py"))


def adapt_source(src: str, chip: str) -> str:
    # One line in, one line out for every substitution below: several "refuse" probes
    # carry a `# expect: refuse :LINE:COL: ...` header anchored to the ORIGINAL file's
    # line numbers, so a replacement that changes the line count would shift every
    # diagnostic below it and turn a real match into a false "wrong diagnostic".
    def gpior_sub(m: re.Match) -> str:
        names = [n.strip() for n in m.group(1).split(",") if n.strip()]
        decls = "; ".join(
            f"{name}: ptr[uint8] = ptr({hex(SCRATCH_ADDR[chip] + i)})"
            for i, name in enumerate(names)
        )
        return f"from pymcu.types import ptr, uint8; {decls}"

    src = GPIOR_IMPORT_RE.sub(gpior_sub, src)

    # 461: `import pymcu.chips.atmega328p as X` -> the equivalent import of the real
    # target chip module, same alias. run_cpython_for_target shims that module in too.
    src = CHIP_MODULE_IMPORT_RE.sub(rf"import pymcu.chips.{chip} as \1", src)

    # 196: BASE is a direct 16-bit pointer base (`ptr(BASE + off)`), so the replacement
    # just needs a wider type and a real address -- the arithmetic around it is untouched.
    # Neither probe imports uint32 (they only need the AVR-sized uint8/uint16 originally),
    # so the import rides the same line, same "GPIOR0 import" trick as above.
    base196 = "BASE: const[uint16] = 0x0400"
    if base196 in src:
        src = src.replace(
            base196,
            f"from pymcu.types import uint32; "
            f"BASE: const[uint32] = {hex(BASE196_ADDR[chip])}",
        )

    # 277/278: BASE is a page number the probe's own body multiplies by 256
    # (`ptr(BASE * 256)`), so the replacement stores the target address PRE-DIVIDED by
    # 256 rather than the address itself -- the body's `* 256` does the rest.
    base277 = "BASE: const[uint8] = 0x04"
    if base277 in src:
        src = src.replace(
            base277,
            f"from pymcu.types import uint32; "
            f"BASE: const[uint32] = {hex(BASE277_PAGE[chip])}",
        )

    return src


def run_cpython_for_target(src: str, probe_name: str, chip_name: str, arch: str) -> str:
    """Same as avr_oracle.run_cpython, but the chip shim reports the real target chip
    instead of the hardcoded atmega328p/avr -- see module docstring, adaptation 1."""
    buf = io.StringIO()
    previous = avr_oracle.install_cpython_shims()
    chip_mod = sys.modules["pymcu.chips.atmega328p"]
    chip_mod.__CHIP__.name = chip_name
    chip_mod.__CHIP__.arch = arch
    sys.modules["pymcu.chips"].__CHIP__ = chip_mod.__CHIP__

    # Adaptation 4 (461): a fake pymcu.chips.<target> module, GPIOR0 shimmed the same shape
    # as atmega328p's own (install_cpython_shims above), so `import pymcu.chips.rp2040 as
    # chip; getattr(chip, "GPIOR0", None)` resolves to the SAME kind of object the firmware
    # build's adapted source reads through its scratch ptr.
    target_chip_mod = types.ModuleType(f"pymcu.chips.{chip_name}")
    target_chip_mod.GPIOR0 = types.SimpleNamespace(value=chip_mod.GPIOR0.value)
    sys.modules[f"pymcu.chips.{chip_name}"] = target_chip_mod

    globals_ = {"__name__": "__main__", "__file__": str(PROBES_DIR / f"{probe_name}.py")}
    try:
        with contextlib.redirect_stdout(buf):
            try:
                exec(compile(src, probe_name, "exec"), globals_)
            except SystemExit:
                pass
            except BaseException as exc:  # noqa: BLE001 - mirrors avr_oracle.run_cpython
                msg = str(exc)
                print(f"E:{type(exc).__name__}: {msg}" if msg else f"E:{type(exc).__name__}")
    finally:
        avr_oracle.restore_modules(previous)
    return buf.getvalue()


def compile_probe(tmp_dir: Path, name: str, src: str, chip: str, pymcu: Path) -> tuple[int, str | None, str]:
    project = tmp_dir / name
    src_dir = project / "src"
    src_dir.mkdir(parents=True)
    (src_dir / "main.py").write_text(src)
    (project / "pyproject.toml").write_text(
        "[project]\n"
        'name = "oracle-probe"\n'
        'version = "0.1.0"\n'
        'requires-python = ">=3.11"\n\n'
        "[tool.pymcu]\n"
        f'target = "{chip}"\n'
        'sources = "src"\n'
        'entry = "main.py"\n'
    )
    result = subprocess.run(
        [str(pymcu), "build"], cwd=project, capture_output=True, text=True, timeout=90,
        env={**os.environ, "PATH": "/opt/homebrew/bin:" + os.environ["PATH"]},
    )
    bin_path = project / "dist" / "firmware.bin"
    return result.returncode, (str(bin_path) if bin_path.is_file() else None), result.stdout + result.stderr


def run_emulator(chip: str, firmware_bin: str, max_ms: float = 4000) -> str:
    result = subprocess.run(
        ["dotnet", str(RUNNER_DLL), chip, firmware_bin, str(max_ms)],
        capture_output=True, text=True, timeout=90,
    )
    return result.stdout.replace("\r\n", "\n")


def evaluate(probe: Path, chip: str, tmp_dir: Path, pymcu: Path) -> dict:
    src = probe.read_text()
    expectation = avr_oracle.parse_expectation(src)
    name = probe.stem
    record = {"probe": probe.name, "expect": expectation.kind, "chip": chip}
    if expectation.tracked:
        record["tracked"] = expectation.tracked

    if expectation.frontend is not None and expectation.frontend != "default":
        record["outcome"] = "no_medido"
        record["reason"] = f"restricted to the {expectation.frontend} front end; this sweep runs the default front end only"
        return record

    chip_name, arch = TARGET_CHIP_INFO[chip]
    adapted = adapt_source(src, chip)
    returncode, bin_path, log = compile_probe(tmp_dir, name, adapted, chip, pymcu)

    if expectation.kind == "refuse":
        if returncode != 0:
            if expectation.diagnostic and not _diagnostic_matches(expectation.diagnostic, log):
                record["outcome"] = "refused_wrong_diagnostic"
                record["detail"] = log.strip().splitlines()[-1][:200] if log.strip() else ""
            else:
                record["outcome"] = "refused"
            return record
        record["outcome"] = "unexpected_compile_success"
        return record

    if returncode != 0 or bin_path is None:
        interesting = [
            line for line in log.splitlines()
            if any(k in line.lower() for k in ("error", "exception", "unsupported", "refus"))
        ]
        detail = (interesting or log.splitlines() or [""])[-1][:200]
        record["outcome"] = "compile_fail"
        record["detail"] = detail
        return record

    # `# expect: compile` asks only that the build succeeds (the pymcu-avr suite checks the same
    # and never runs the image): the program prints nothing to compare.
    if expectation.kind == "compile":
        record["outcome"] = "compiled"
        return record

    try:
        actual = run_emulator(chip, bin_path)
    except subprocess.TimeoutExpired:
        record["outcome"] = "runner_timeout"
        return record

    expected = run_cpython_for_target(src, name, chip_name, arch)
    if expectation.kind == "divergence":
        expected = avr_oracle.apply_divergence(expectation.divergence_doc, expected)

    if expected == actual:
        record["outcome"] = "match"
    elif "END\n" not in actual and actual.strip() == "":
        record["outcome"] = "crash_or_timeout"
    else:
        record["outcome"] = "mismatch"
        record["detail"] = avr_oracle.first_difference(expected, actual)
        record["expected_tail"] = expected[-200:]
        record["actual_tail"] = actual[-200:]
    return record


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--chip", required=True, choices=["ch32v003"])
    ap.add_argument("--out", required=True)
    ap.add_argument("--limit", type=int, default=None)
    ap.add_argument("--only", default=None, help="substring filter on probe filename")
    ap.add_argument("--pymcu", default="/private/tmp/claude-501/ch32/.venv/bin/pymcu")
    args = ap.parse_args()

    pymcu = Path(args.pymcu)
    assert pymcu.is_file(), f"pymcu binary not found: {pymcu}"
    assert RUNNER_DLL.is_file(), f"runner not built: {RUNNER_DLL}"

    probes = probe_files()
    if args.only:
        probes = [p for p in probes if args.only in p.name]
    if args.limit:
        probes = probes[: args.limit]

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    t0 = time.time()
    with out_path.open("w") as fh:
        for i, probe in enumerate(probes, 1):
            with tempfile.TemporaryDirectory() as tmp:
                try:
                    record = evaluate(probe, args.chip, Path(tmp), pymcu)
                except Exception as exc:  # noqa: BLE001 - keep sweeping past a bad probe
                    record = {"probe": probe.name, "chip": args.chip, "outcome": "sweep_error",
                              "detail": repr(exc)}
            fh.write(json.dumps(record) + "\n")
            fh.flush()
            if i % 25 == 0 or i == len(probes):
                elapsed = time.time() - t0
                print(f"[{args.chip}] {i}/{len(probes)} ({elapsed:.0f}s)", file=sys.stderr)


if __name__ == "__main__":
    main()

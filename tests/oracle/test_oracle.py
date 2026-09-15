from __future__ import annotations

import contextlib
import io
import os
import subprocess
import sys
import types
from dataclasses import dataclass
from pathlib import Path

import pytest


ROOT = Path(__file__).resolve().parents[2]
PROBES = Path(__file__).resolve().parent / "probes"
# The compiler of record is this checkout's own venv: the driver is installed editable there
# and resolves pymcuc through src/driver/pymcuc, so a rebuilt build/bin is what gets measured.
# PYMCU_BIN overrides it (a worktree with its own build points here at its own venv). A user
# project's venv is never a test dependency: its wheels are whatever was built for that
# project on that day, and a probe measured through it reports that wheel, not this tree.
DEFAULT_PYMCU_BIN = ROOT / ".venv" / "bin" / "pymcu"
AVR8SHARP_SITE = next(
    iter(sorted((ROOT / ".venv" / "lib").glob("python3.*/site-packages"))),
    ROOT / ".venv" / "lib" / "python3.14" / "site-packages",
)
END = "END\n"


@dataclass(frozen=True)
class Expectation:
    kind: str
    diagnostic: str | None
    doc: str
    divergence_doc: str | None = None
    tracked: str | None = None
    frontend: str | None = None


@dataclass(frozen=True)
class CompileResult:
    returncode: int
    hex_text: str | None
    log: str


@dataclass(frozen=True)
class OracleOutcome:
    probe: str
    feature: str
    expectation: str
    outcome: str
    first_difference: str


def _probe_files() -> list[Path]:
    return sorted(PROBES.glob("*.py"))


def parse_expectation(src: str) -> Expectation:
    expect = None
    doc = None
    tracked = None
    frontend = None
    for line in src.splitlines()[:8]:
        if line.startswith("# expect: "):
            expect = line.removeprefix("# expect: ").strip()
        if line.startswith("# doc: "):
            doc = line.removeprefix("# doc: ").strip()
        if line.startswith("# tracked: "):
            tracked = line.removeprefix("# tracked: ").strip()
        if line.startswith("# frontend: "):
            frontend = line.removeprefix("# frontend: ").strip()
    if expect is None or doc is None:
        raise AssertionError("probe is missing # expect or # doc header")
    if frontend not in (None, "default", "py-parser"):
        raise AssertionError(f"unknown oracle frontend restriction: {frontend}")
    if expect == "match":
        return Expectation("match", None, doc, tracked=tracked, frontend=frontend)
    if expect.startswith("refuse "):
        return Expectation(
            "refuse", expect.removeprefix("refuse ").strip(), doc,
            tracked=tracked, frontend=frontend,
        )
    if expect.startswith("divergence "):
        return Expectation(
            "divergence",
            None,
            doc,
            divergence_doc=expect.removeprefix("divergence ").strip(),
            tracked=tracked,
            frontend=frontend,
        )
    raise AssertionError(f"unknown oracle expectation: {expect}")


# Known, documented CPython/emulator divergences: each maps the doc citation named in a
# probe's `# expect: divergence <citation>` header to the transform that turns CPython's
# raw output into the form the docs say the emulator prints instead. A probe whose citation
# is not one of these keys is a broken header, not a silent pass.
DIVERGENCE_TRANSFORMS: dict[str, "callable[[str], str]"] = {
    # `bool` is an alias of uint8 that folds True/False to 1/0 (type-system.md:20); any/all,
    # `in`/`not in`, `is`/`is not`, and dict/set membership all return a bool.
    "docs/language/type-system.md:20": lambda text: "\n".join(
        "1" if line == "True" else "0" if line == "False" else line
        for line in text.split("\n")
    ),
    # A triple-quoted string's leading newline, right after the opening quote, is stripped
    # (roadmap.md:64).
    "docs/language/roadmap.md:64": lambda text: text[1:] if text.startswith("\n") else text,
    # A field read before any write reachable from it executes: the interpreters resolve
    # attribute existence dynamically, per instance, by execution order and raise
    # AttributeError; PyMCU lays the field out statically and cannot see that the write comes
    # later in THIS run, so it reads the zero-initialized default instead (limitations.md:372).
    "docs/language/limitations.md:372": lambda text: text.replace("AttributeError", "0", 1),
    # `int` is 16-bit and a `uint8`-annotated parameter is a fixed 8-bit storage width, not
    # CPython's arbitrary-precision int (type-system.md:242): a value that does not fit wraps
    # silently at the width instead of being carried in full.
    "docs/language/type-system.md:242": lambda text: "\n".join(
        str(int(line) & 0xFF) if line.lstrip("-").isdigit() else line
        for line in text.split("\n")
    ),
}


def apply_divergence(citation: str, expected: str) -> str:
    transform = DIVERGENCE_TRANSFORMS.get(citation)
    if transform is None:
        raise AssertionError(f"unregistered oracle divergence citation: {citation}")
    return transform(expected)


def pymcu_bin() -> Path:
    path = Path(os.environ.get("PYMCU_BIN", DEFAULT_PYMCU_BIN))
    if not path.is_file():
        pytest.skip(f"PYMCU_BIN is unavailable: {path}")
    return path


def import_avr8sharp():
    if str(AVR8SHARP_SITE) not in sys.path:
        sys.path.insert(0, str(AVR8SHARP_SITE))
    return pytest.importorskip("avr8sharp", reason="avr8sharp is unavailable")


class _Type:
    def __init__(self, name: str, bits: int | None = None, signed: bool = False):
        self.name = name
        self.bits = bits
        self.signed = signed

    def __call__(self, value=0):
        value = int(value)
        if self.bits is None:
            return value
        mask = (1 << self.bits) - 1
        value &= mask
        if self.signed and value >= (1 << (self.bits - 1)):
            value -= 1 << self.bits
        return value

    def __getitem__(self, _item):
        return self

    def __repr__(self):
        return self.name


class _Const:
    def __getitem__(self, item):
        return item


class FixedDict(dict):
    def __init__(self, capacity: int):
        super().__init__()
        self.capacity = capacity

    def __setitem__(self, key, value):
        if key not in self and len(self) >= self.capacity:
            raise ValueError("fixed dict is full")
        super().__setitem__(key, value)


def _bitcast(target, value):
    import struct

    name = getattr(target, "name", str(target))
    if name == "uint32" and isinstance(value, float):
        return struct.unpack("<I", struct.pack("<f", value))[0]
    if name == "float":
        return struct.unpack("<f", struct.pack("<I", int(value) & 0xFFFFFFFF))[0]
    return value


def install_cpython_shims() -> dict[str, types.ModuleType | None]:
    previous = {name: sys.modules.get(name) for name in list(sys.modules)}

    pymcu = types.ModuleType("pymcu")
    types_mod = types.ModuleType("pymcu.types")
    exceptions_mod = types.ModuleType("pymcu.exceptions")
    ffi_mod = types.ModuleType("pymcu.ffi")
    collections_mod = types.ModuleType("pymcu.collections")
    chips_pkg = types.ModuleType("pymcu.chips")
    chip_mod = types.ModuleType("pymcu.chips.atmega328p")
    hal_pkg = types.ModuleType("pymcu.hal")
    console_mod = types.ModuleType("pymcu.hal.console")

    def identity(fn=None, *_args, **_kwargs):
        if fn is None:
            return lambda real: real
        return fn

    def extern(_symbol):
        return identity

    for name, bits, signed in [
        ("uint8", 8, False),
        ("int8", 8, True),
        ("uint16", 16, False),
        ("int16", 16, True),
        ("uint32", 32, False),
        ("int32", 32, True),
    ]:
        setattr(types_mod, name, _Type(name, bits, signed))
    types_mod.const = _Const()
    types_mod.ptr = _Type("ptr")
    types_mod.bitcast = _bitcast
    types_mod.Callable = _Type("Callable")
    types_mod.WriteableBuffer = bytearray
    types_mod.ReadableBuffer = bytearray
    types_mod.inline = identity

    exceptions_mod.CompileError = type("CompileError", (Exception,), {})
    ffi_mod.extern = extern
    collections_mod.FixedDict = FixedDict

    chip_mod.GPIOR0 = types.SimpleNamespace(
        value=int(os.environ.get("ORACLE_SEED", "0"))
    )
    chip_mod.__CHIP__ = types.SimpleNamespace(name="atmega328p", arch="avr")
    console_mod.print = print

    pymcu.inline = identity
    pymcu.interrupt = identity
    pymcu.naked = identity
    pymcu.asm = lambda *_args, **_kwargs: None
    pymcu.delay_ms = lambda *_args, **_kwargs: None
    pymcu.delay_us = lambda *_args, **_kwargs: None
    pymcu.__FREQ__ = 16000000
    pymcu.__CHIP__ = chip_mod.__CHIP__
    # Real firmware imports `__CHIP__` off the chips package itself
    # (`from pymcu.chips import __CHIP__`, as lib/src/pymcu/time.py and asyncio.py do),
    # not only off the per-chip submodule or the top-level package.
    chips_pkg.__CHIP__ = chip_mod.__CHIP__
    chips_pkg.__FREQ__ = pymcu.__FREQ__

    sys.modules.update(
        {
            "pymcu": pymcu,
            "pymcu.types": types_mod,
            "pymcu.exceptions": exceptions_mod,
            "pymcu.ffi": ffi_mod,
            "pymcu.collections": collections_mod,
            "pymcu.chips": chips_pkg,
            "pymcu.chips.atmega328p": chip_mod,
            "pymcu.hal": hal_pkg,
            "pymcu.hal.console": console_mod,
        }
    )
    return previous


def restore_modules(previous: dict[str, types.ModuleType | None]) -> None:
    for name in list(sys.modules):
        if name.startswith("pymcu"):
            sys.modules.pop(name, None)
    for name, module in previous.items():
        if module is not None:
            sys.modules[name] = module


def run_cpython(src: str, probe_name: str) -> str:
    buf = io.StringIO()
    previous = install_cpython_shims()
    globals_ = {
        "__name__": "__main__",
        "__file__": str(PROBES / f"{probe_name}.py"),
    }
    try:
        with contextlib.redirect_stdout(buf):
            try:
                exec(compile(src, probe_name, "exec"), globals_)
            except SystemExit:
                pass
            except BaseException as exc:
                print(f"E:{type(exc).__name__}")
    finally:
        restore_modules(previous)
    return buf.getvalue()


def compile_probe(tmp_path: Path, name: str, src: str, pymcu: Path) -> CompileResult:
    project = tmp_path / name
    src_dir = project / "src"
    src_dir.mkdir(parents=True)
    (src_dir / "main.py").write_text(src)
    (project / "pyproject.toml").write_text(
        '[project]\n'
        'name = "oracle-probe"\n'
        'version = "0.1.0"\n'
        'requires-python = ">=3.11"\n\n'
        '[tool.pymcu]\n'
        'target = "atmega328p"\n'
        'frequency = 16000000\n'
        'sources = "src"\n'
        'entry = "main.py"\n'
    )
    env = dict(os.environ)
    env["COLUMNS"] = "400"
    result = subprocess.run(
        [str(pymcu), "build"],
        cwd=project,
        capture_output=True,
        text=True,
        env=env,
        timeout=90,
    )
    hex_path = project / "dist" / "firmware.hex"
    hex_text = hex_path.read_text() if hex_path.is_file() else None
    return CompileResult(result.returncode, hex_text, result.stdout + result.stderr)


def run_emulator(hex_text: str, avr8sharp, max_ms: float = 4000) -> str:
    sim = avr8sharp.ArduinoUno()
    sim.with_hex(hex_text)
    try:
        sim.run_until_serial(sim.serial, END, max_ms=max_ms)
    except Exception:
        if not sim.serial.text:
            raise
    return sim.serial.text.replace("\r\n", "\n")


def first_difference(expected: str, actual: str) -> str:
    expected_lines = expected.splitlines()
    actual_lines = actual.splitlines()
    for index in range(max(len(expected_lines), len(actual_lines))):
        left = expected_lines[index] if index < len(expected_lines) else "<missing>"
        right = actual_lines[index] if index < len(actual_lines) else "<missing>"
        if left != right:
            return f"line {index + 1}: CPython={left!r}, emulator={right!r}"
    return ""


def evaluate_probe(probe: Path, tmp_path: Path, pymcu: Path, avr8sharp) -> OracleOutcome:
    src = probe.read_text()
    expectation = parse_expectation(src)
    compile_result = compile_probe(tmp_path, probe.stem, src, pymcu)
    feature = probe.stem.removeprefix("p").replace("_", " ")
    if expectation.kind == "match":
        expect_label = "match"
    elif expectation.kind == "divergence":
        expect_label = f"divergence {expectation.divergence_doc}"
    else:
        expect_label = f"refuse {expectation.diagnostic}"

    if expectation.kind == "refuse":
        if compile_result.returncode != 0:
            if expectation.diagnostic and expectation.diagnostic not in compile_result.log:
                return OracleOutcome(
                    probe.name,
                    feature,
                    expect_label,
                    "refused with different diagnostic",
                    compile_result.log.strip().splitlines()[-1][:180],
                )
            return OracleOutcome(probe.name, feature, expect_label, "refused", "")
        return OracleOutcome(
            probe.name,
            feature,
            expect_label,
            "compiled",
            "expected compiler refusal but build succeeded",
        )

    if compile_result.returncode != 0 or compile_result.hex_text is None:
        interesting = [
            line
            for line in compile_result.log.splitlines()
            if "error" in line.lower()
            or "exception" in line.lower()
            or "unsupported" in line.lower()
            or "refus" in line.lower()
        ]
        detail = (interesting or compile_result.log.splitlines() or [""])[-1][:180]
        return OracleOutcome(
            probe.name,
            feature,
            expect_label,
            "compile-fail",
            detail,
        )

    expected = run_cpython(src, probe.stem)
    if expectation.kind == "divergence":
        expected = apply_divergence(expectation.divergence_doc, expected)
    actual = run_emulator(compile_result.hex_text, avr8sharp)
    if expected == actual:
        return OracleOutcome(probe.name, feature, expect_label, "match", "")
    return OracleOutcome(
        probe.name,
        feature,
        expect_label,
        "mismatch",
        first_difference(expected, actual),
    )


@pytest.mark.parametrize("probe", _probe_files(), ids=lambda path: path.stem)
def test_probe_matches_cpython_or_refuses_as_documented(
    probe: Path, tmp_path: Path, request: pytest.FixtureRequest
):
    pymcu = pymcu_bin()
    avr8sharp = import_avr8sharp()
    expectation = parse_expectation(probe.read_text())
    if expectation.frontend is not None:
        # The two front ends are supposed to accept the same language subset, but a probe
        # marked `# frontend: default` / `# frontend: py-parser` documents a real, filed gap
        # where they do not (e.g. a construct one front end refuses and the other accepts,
        # possibly with different runtime behaviour). Such a probe cannot carry a single
        # `# expect:` that is honest under both engines, so it runs -- and is asserted --
        # only under the front end it names; the other run skips it rather than treating an
        # inherently one-sided assertion as a false pass or a false failure.
        running_py_parser = bool(os.environ.get("PYMCU_PY_PARSER"))
        wants_py_parser = expectation.frontend == "py-parser"
        if running_py_parser != wants_py_parser:
            pytest.skip(
                f"probe is restricted to the {expectation.frontend} front end "
                "(the two front ends disagree here; see the probe's # doc: issue)"
            )
    if expectation.tracked:
        # A `# tracked: #N` probe is a known compiler bug: xfail(strict) so the suite stays
        # green while it is open, and an XPASS -- the bug got fixed and nobody untracked the
        # probe -- turns back into a hard failure instead of a silent pass.
        request.node.add_marker(
            pytest.mark.xfail(
                reason=f"tracked at {expectation.tracked}; untrack this probe once it is fixed",
                strict=True,
            )
        )
    outcome = evaluate_probe(probe, tmp_path, pymcu, avr8sharp)
    assert outcome.outcome in {"match", "refused"}, (
        f"{outcome.probe}: {outcome.outcome}\n{outcome.first_difference}"
    )

"""Freeze what every chip compiles to, so a refactor can prove it changed nothing.

Compiles a fixed corpus against every chip in lib/src/pymcu/chips and records,
per (program, chip): whether it built, the hash of the IR, the hash of the
generated assembly and the ROM figure the driver reports. `capture` writes the
snapshot; `check` re-runs the corpus and diffs against it.

A program that does NOT build is a recorded outcome like any other: a refactor
that makes one start or stop compiling has changed behaviour and must say so.
"""

import argparse
import hashlib
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
CHIPS_DIR = REPO / "lib" / "src" / "pymcu" / "chips"
PYMCU = REPO / ".venv" / "bin" / "pymcu"

# Written as source literals: the portable Pin() takes a string on some
# architectures and an integer on others, which the harness must not paper over.
PIN_CANDIDATES = {
    "avr": ['"PB5"', '"PB0"', '"PB7"', '"PC7"'],
    "pic12": ['"GP0"', '"GP1"'],
    "pic14": ['"RB3"', '"RB0"', '"RD2"'],
    "pic14e": ['"RD2"', '"RB0"'],
    "pic18": ['"RD2"', '"RB0"'],
    "arm": ["25", "0", '"GP25"'],
    "riscv": ['"PD4"', '"PC0"', '"PA1"'],
}

FREQ = {"pic12": 4_000_000, "pic14": 4_000_000, "pic14e": 8_000_000,
        "pic18": 16_000_000, "avr": 16_000_000, "riscv": 48_000_000}

PROGRAMS = {
    "blink": """from pymcu.hal.gpio import Pin
from pymcu.time import delay_ms


def main():
    led = Pin({pin}, Pin.OUT)
    while True:
        led.high()
        delay_ms(100)
        led.low()
        delay_ms(100)
""",
    "uart": """from pymcu.types import uint8
from pymcu.hal.uart import UART


def main():
    u = UART(9600)
    n: uint8 = 0
    while True:
        u.write(0x41 + n)
        n = n + 1
""",
    "adc": """from pymcu.types import uint16
from pymcu.hal.adc import AnalogPin
from pymcu.hal.uart import UART


def main():
    a = AnalogPin({adc})
    u = UART(9600)
    while True:
        v: uint16 = a.read()
        u.write(v & 0xFF)
""",
    "asyncio": """import asyncio
from pymcu.hal.gpio import Pin


async def blink():
    led = Pin({pin}, Pin.OUT)
    while True:
        led.high()
        await asyncio.sleep_ms(50)
        led.low()
        await asyncio.sleep_ms(50)


def main():
    asyncio.run(blink())
""",
    "float": """from pymcu.types import uint8
from pymcu.hal.uart import UART


def main():
    u = UART(9600)
    a: float = 1.5
    b: float = 0.25
    while True:
        c: float = a * b + a / b
        u.write(uint8(c))
""",
}

ADC_CANDIDATES = {"avr": ['"PC0"', '"PB2"'], "pic14": ['"RA0"'], "pic14e": ['"RA0"'],
                  "pic18": ['"RA0"'], "arm": ["26", '"GP26"'], "riscv": ['"PA1"'],
                  "pic12": ['"GP0"']}


def provenance():
    """What produced this snapshot: the toolchain, and whether anyone was mid-edit.

    A gate that compares ROM without pinning the compiler measures whoever else
    happened to be building at the time; this campaign lost two verdicts that way
    before the rule became mechanical.
    """
    def sha(path):
        p = Path(path)
        return hashlib.sha256(p.read_bytes()).hexdigest()[:16] if p.exists() else None

    def git(*args):
        r = subprocess.run(["git", *args], cwd=REPO, capture_output=True, text=True)
        return r.stdout.strip()

    dirty = [l.split(maxsplit=1)[-1] for l in git("status", "--short", "src/compiler").splitlines() if l.strip()]
    return {
        "head": git("rev-parse", "--short", "HEAD"),
        "compiler_tree_dirty": dirty,
        "pymcuc": sha(REPO / "build" / "bin" / "pymcuc"),
        "pymcuc-avr": sha(REPO / "build" / "bin" / "pymcuc-avr"),
        "pymcuc-riscv": sha(REPO / "build" / "bin" / "pymcuc-riscv"),
        "pymcuc-rp2040": sha(REPO / "build" / "bin" / "pymcuc-rp2040"),
        "pymcuc-pic": sha(REPO.parent / "pymcu-pic" / "build" / "bin" / "pymcuc-pic"),
    }


def chips():
    out = {}
    for f in sorted(CHIPS_DIR.glob("*.py")):
        if f.stem.startswith("_"):
            continue
        m = re.search(r'device_info\((.*?)\)', f.read_text(), re.S)
        if not m:
            continue
        arch = re.search(r'arch\s*=\s*"(\w+)"', m.group(1))
        out[f.stem] = arch.group(1) if arch else "?"
    return out


def build(work: Path, chip: str, arch: str, source: str):
    (work / "src").mkdir(parents=True, exist_ok=True)
    (work / "src" / "main.py").write_text(source)
    (work / "pyproject.toml").write_text(
        f'[project]\nname="snap"\nversion="0.1.0"\n\n[tool.pymcu]\n'
        f'target="{chip}"\nfrequency={FREQ.get(arch, 16_000_000)}\n'
        f'sources="src"\nentry="main.py"\n')
    for stale in (work / "dist",):
        if stale.exists():
            subprocess.run(["rm", "-rf", str(stale)], check=False)
    proc = subprocess.run([str(PYMCU), "build"], cwd=work,
                          capture_output=True, text=True)
    rom = re.search(r"Flash:\s*(\d+)", proc.stdout)
    if rom is None:
        blob = " ".join((proc.stdout + proc.stderr).split())
        # Absolute paths carry the temp directory, which is new on every run and
        # would make every no-build cell diff against itself for ever.
        blob = re.sub(r"(/[\w.\-]+)+/", "", blob)
        reason = (re.search(r"(?:error|Codegen failed|CompileError):\s*(.{0,90})", blob)
                  or re.search(r"Error:\s*(.{0,90})", blob))
        return {"status": "no-build",
                "reason": (reason.group(1).strip() if reason else "unknown")[:90]}
    entry = {"status": "ok", "rom": int(rom.group(1))}
    for name, key in (("firmware.mir", "mir"), ("debug/firmware.asm", "asm")):
        f = work / "dist" / name
        if f.exists():
            entry[key] = hashlib.sha256(f.read_bytes()).hexdigest()[:16]
    return entry


def first_that_builds(work, chip, arch, template, candidates, key):
    for value in candidates:
        result = build(work, chip, arch, template.format(**{key: value}))
        if result["status"] == "ok":
            result[key] = value
            return result
    return {"status": "no-build", "reason": f"no {key} candidate compiled"}


def run_corpus():
    snapshot = {}
    for chip, arch in chips().items():
        with tempfile.TemporaryDirectory() as tmp:
            work = Path(tmp)
            for name, template in PROGRAMS.items():
                if "{pin}" in template:
                    r = first_that_builds(work, chip, arch, template,
                                          PIN_CANDIDATES.get(arch, ["PB5"]), "pin")
                elif "{adc}" in template:
                    r = first_that_builds(work, chip, arch, template,
                                          ADC_CANDIDATES.get(arch, ["PC0"]), "adc")
                else:
                    r = build(work, chip, arch, template)
                snapshot[f"{name}|{chip}"] = r
                print(f"  {name:8s} {chip:14s} {r['status']:9s} "
                      f"{r.get('rom', ''):>6} {r.get('asm', '')}", flush=True)
    return snapshot


# Why a cell proves nothing, keyed by what the driver said. A cell that cannot
# build is not a gap in the gate -- it is a gap in the product, and the two are
# worth telling apart when reading a diff.
NO_BUILD_KINDS = [
    ("no pin candidate", "sin-facade", "el facade portable no cubre esta arquitectura o el chip"),
    ("no adc candidate", "sin-periferico", "el chip no tiene ese periferico, o el facade no lo expone"),
    ("not supported on this architecture", "sin-facade", "el facade rechaza la arquitectura"),
    ("needs a timebase", "sin-timebase", "sin base de tiempos: async y millis no pueden existir"),
    ("static data needs", "sin-RAM", "el programa no cabe en la RAM del chip"),
    ("illegal opcode", "backend-roto", "el ensamblador rechaza lo que emite el backend"),
    ("InternalCompilerError", "backend-roto", "error interno del compilador"),
    ("undefined function", "hal-incompleto", "el HAL de ese chip no define la funcion"),
    ("only supports 1, 2, 4 bytes", "backend-roto", "limitacion del backend"),
]


def annotate(path):
    import collections
    stored = json.loads(path.read_text())
    cells = stored.get("cells", stored)
    kinds = collections.Counter()
    for key, cell in cells.items():
        if cell.get("status") == "ok":
            continue
        reason = cell.get("reason", "")
        kind, why = "sin-clasificar", "motivo no reconocido por el arnes"
        for needle, k, w in NO_BUILD_KINDS:
            if needle in reason:
                kind, why = k, w
                break
        cell["kind"] = kind
        cell["proves"] = "nada: " + why
        kinds[kind] += 1
    out = {"provenance": stored.get("provenance"), "cells": cells} if "cells" in stored else cells
    path.write_text(json.dumps(out, indent=1, sort_keys=True) + "\n")
    total = len(cells)
    ok = sum(1 for c in cells.values() if c.get("status") == "ok")
    print(f"  {ok}/{total} celdas prueban algo; {total - ok} no prueban nada:")
    for k, n in kinds.most_common():
        print(f"    {n:3d}  {k}")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("action", choices=["capture", "check", "annotate"])
    ap.add_argument("--file", default=str(REPO / "tests" / "tools" / "rom_snapshot.json"))
    args = ap.parse_args()

    if args.action == "annotate":
        return annotate(Path(args.file))

    prov = provenance()
    if prov["compiler_tree_dirty"]:
        print("AVISO: el arbol del compilador NO esta limpio -- "
              f"{', '.join(prov['compiler_tree_dirty'])}")
        print("       lo que midas incluye trabajo sin commitear de otra persona.\n")

    current = run_corpus()
    path = Path(args.file)

    if args.action == "capture":
        path.write_text(json.dumps(
            {"provenance": prov, "cells": current}, indent=1, sort_keys=True) + "\n")
        ok = sum(1 for v in current.values() if v["status"] == "ok")
        print(f"\ncapturado: {len(current)} celdas, {ok} compilan -> {path}")
        print(f"  toolchain: pymcuc {prov['pymcuc']} @ {prov['head']}"
              f"{' (ARBOL SUCIO)' if prov['compiler_tree_dirty'] else ''}")
        return 0

    stored = json.loads(path.read_text())
    before = stored.get("cells", stored)

    # What counts as a measurement. `reason`, `kind` and `proves` are prose about
    # a failure, not the failure itself: rewording an error message must not read
    # as a regression, while a cell flipping between ok and no-build must.
    COMMENTARY = ("reason", "kind", "proves")

    def measured(cell):
        return {k: v for k, v in cell.items() if k not in COMMENTARY} if cell else cell
    was = stored.get("provenance")
    if was:
        drifted = [k for k in was if was[k] != prov.get(k)]
        if drifted:
            print("PROCEDENCIA DISTINTA de la de la captura: " + ", ".join(drifted))
            for k in drifted:
                print(f"    {k}: {was[k]} -> {prov.get(k)}")
            print("    un diff de celdas aqui puede no ser tuyo.\n")
    diffs = []
    for key in sorted(set(before) | set(current)):
        a, b = measured(before.get(key)), measured(current.get(key))
        if a == b:
            continue
        if a and b and a.get("status") == b.get("status") == "ok":
            delta = b["rom"] - a["rom"]
            diffs.append((key, f"asm {a.get('asm')} -> {b.get('asm')}, ROM {delta:+d}", delta))
        else:
            diffs.append((key, f"{a} -> {b}", None))
    if not diffs:
        print(f"\nsin cambios: {len(current)} celdas identicas")
        return 0
    print(f"\n{len(diffs)} celdas cambiaron:")
    worse = 0
    for key, text, delta in diffs:
        flag = ""
        if delta is not None and delta > 0:
            flag = "  <-- ROM SUBE"
            worse += 1
        elif delta is None:
            flag = "  <-- CAMBIA SI COMPILA"
            worse += 1
        print(f"  {key:24s} {text}{flag}")
    return 1 if worse else 0


if __name__ == "__main__":
    sys.exit(main())

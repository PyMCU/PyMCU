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
        reason = re.search(r"(?:error|Codegen failed|CompileError):\s*(.{0,90})",
                           proc.stdout + proc.stderr)
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


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("action", choices=["capture", "check"])
    ap.add_argument("--file", default=str(REPO / "tests" / "tools" / "rom_snapshot.json"))
    args = ap.parse_args()

    current = run_corpus()
    path = Path(args.file)

    if args.action == "capture":
        path.write_text(json.dumps(current, indent=1, sort_keys=True) + "\n")
        ok = sum(1 for v in current.values() if v["status"] == "ok")
        print(f"\ncapturado: {len(current)} celdas, {ok} compilan -> {path}")
        return 0

    before = json.loads(path.read_text())
    diffs = []
    for key in sorted(set(before) | set(current)):
        a, b = before.get(key), current.get(key)
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

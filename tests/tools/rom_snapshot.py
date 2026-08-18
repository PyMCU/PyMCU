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
    "call": """from pymcu.types import uint8, uint16
from pymcu.hal.gpio import Pin


def mix(a: uint16, b: uint16) -> uint16:
    acc: uint16 = a
    i: uint8 = 0
    while i < 5:
        acc = (acc << 1) + b
        i = i + 1
    return acc


def main():
    led = Pin({pin}, Pin.OUT)
    n: uint16 = 1
    while True:
        n = mix(n, 137)
        if mix(n, 7) > n:
            led.high()
        else:
            led.low()
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


LABEL = re.compile(r"\bL_(\d+)\b")


def canonical_labels(asm):
    """Renumber L_<n> in order of first appearance.

    The counter that mints these labels advances with every branch the frontend
    visits, so folding one dead branch renumbers the whole file without touching
    a single instruction. Renumbering in order of appearance keeps a real
    reordering visible -- it changes the sequence -- while a uniform shift, which
    is what label noise looks like, disappears.
    """
    order = {}
    for m in LABEL.finditer(asm):
        order.setdefault(m.group(1), str(len(order)))
    return LABEL.sub(lambda m: "L_" + order[m.group(1)], asm)


STAMP = re.compile(rb"[0-9]+\.[0-9]+\.[0-9]+[-A-Za-z0-9.]*\+([0-9a-f]{40})")

# The chip each backend is asked about, to make the driver name its own binary
# instead of the harness guessing a path. Guessing is how this gate spent its
# first week recording a PIC binary that no build ever ran.
PROBE_CHIPS = {"pic": "pic18f45k50", "avr": "atmega328p",
               "rp2040": "rp2040", "riscv": "ch32v003"}

# Which backend compiles which architecture, so a diff can say whose it is.
ARCH_BACKEND = {"pic12": "pymcuc-pic", "pic14": "pymcuc-pic", "pic14e": "pymcuc-pic",
                "pic18": "pymcuc-pic", "avr": "pymcuc-avr", "arm": "pymcuc-rp2040",
                "riscv": "pymcuc-riscv"}

PROBE = """
import json, sys
sys.path.insert(0, sys.argv[1])
from driver.backends import get_backend_for_chip
out = {}
for name, chip in json.loads(sys.argv[2]).items():
    plugin = get_backend_for_chip(chip)
    binary = plugin.get_backend_binary() if plugin else None
    out[name] = str(binary) if binary else None
print(json.dumps(out))
"""


def git(repo, *args):
    r = subprocess.run(["git", *args], cwd=repo, capture_output=True, text=True)
    return r.stdout.strip()


# The source tree each binary is built from. Anything outside it -- a test, a
# stdlib file, another backend -- cannot change that binary, and counting it as
# "dirty" makes the gate cry wolf at its own edits.
SOURCE_SCOPE = {"pymcuc": "src/compiler",
                "pymcuc-riscv": "extensions/pymcu-backend-riscv"}


def binary_provenance(name, path):
    """What a compiler binary is, and what it was built from.

    The binary stamps its own commit into __cstring at link time, which beats
    both mtime and sha: two builds of identical source differ in 210 bytes
    (LC_UUID, MVID and the ad-hoc code signature), so a changed sha does not
    mean changed behaviour -- but a changed stamp does.
    """
    path = Path(path) if path else None
    if not path or not path.exists():
        return {"binary": str(path), "sha": None, "stamp": None}
    blob = path.read_bytes()
    found = STAMP.search(blob)
    stamp = found.group(1).decode()[:8] if found else None
    repo = next((p for p in path.parents if (p / ".git").exists()), None)
    entry = {"binary": str(path), "sha": hashlib.sha256(blob).hexdigest()[:16],
             "stamp": stamp}
    if repo:
        head = git(repo, "rev-parse", "--short=8", "HEAD")
        scope = SOURCE_SCOPE.get(name, ".")
        entry["repo"] = repo.name
        entry["scope"] = scope
        entry["repo_head"] = head
        entry["repo_dirty"] = sorted(
            l.split(maxsplit=1)[-1]
            for l in git(repo, "status", "--short", "--", scope).splitlines() if l.strip())
        if stamp and head and stamp != head:
            entry["stale"] = f"compilado en {stamp}, el repo va por {head}"
    return entry


def provenance():
    """What produced this snapshot, and whether anyone was mid-edit.

    A gate that compares ROM without pinning the toolchain measures whoever else
    happened to be building at the time; this campaign lost two verdicts that way
    before the rule became mechanical.
    """
    resolved = {}
    probe = subprocess.run(
        [str(REPO / ".venv" / "bin" / "python"), "-c", PROBE,
         str(REPO / "src"), json.dumps(PROBE_CHIPS)],
        capture_output=True, text=True, cwd="/")
    if probe.returncode == 0:
        resolved = json.loads(probe.stdout)
    tool = {"pymcuc": binary_provenance("pymcuc", REPO / "build" / "bin" / "pymcuc")}
    for name, binary in sorted(resolved.items()):
        tool["pymcuc-" + name] = binary_provenance("pymcuc-" + name, binary)
    return {
        "head": git(REPO, "rev-parse", "--short=8", "HEAD"),
        "compiler_tree_dirty": sorted(
            l.split(maxsplit=1)[-1]
            for l in git(REPO, "status", "--short", "src/compiler").splitlines() if l.strip()),
        "toolchain": tool,
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
                "reason": (reason.group(1).strip() if reason else "unknown")[:110]}
    entry = {"status": "ok", "rom": int(rom.group(1))}
    mir = work / "dist" / "firmware.mir"
    if mir.exists():
        entry["mir"] = hashlib.sha256(mir.read_bytes()).hexdigest()[:16]
    # Where the assembly lands is not uniform: some builds write dist/debug/,
    # others only dist/. Recording which one answered turns a layout change from
    # a silently missing hash into a visible one.
    for name in ("debug/firmware.asm", "firmware.asm"):
        asm = work / "dist" / name
        if asm.exists():
            entry["asm"] = hashlib.sha256(
                canonical_labels(asm.read_text()).encode()).hexdigest()[:16]
            entry["asm_from"] = name
            break
    return entry


def first_that_builds(work, chip, arch, template, candidates, key):
    """Try each pin/channel the architecture might accept, keep the first that builds.

    When none does, report what the compiler actually said about the last try,
    not just that the search failed: "no pin candidate compiled" reads like a
    gap in the facade even when the real answer was an internal compiler error.
    """
    reason = f"no {key} candidate compiled"
    for value in candidates:
        result = build(work, chip, arch, template.format(**{key: value}))
        if result["status"] == "ok":
            result[key] = value
            return result
        reason = f"{key}={value}: {result['reason']}"
    return {"status": "no-build", "reason": reason[:120]}


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
    ("compile_isr()", "backend-roto", "error interno al montar la ISR"),
    ("Unsupported Pin", "sin-mapa-de-pines", "el HAL no conoce los pines de este chip"),
    ("Unknown pin for", "pin-inexistente", "ese pin no existe en este chip: el arnes probo mal"),
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


def report_provenance(prov):
    """Print the toolchain this run is about to measure, warts included."""
    print(f"toolchain @ {prov['head']}:")
    for name, entry in sorted(prov["toolchain"].items()):
        dirty = entry.get("repo_dirty") or []
        notes = []
        if entry.get("stale"):
            notes.append("DESFASADO: " + entry["stale"])
        if dirty:
            notes.append(f"{len(dirty)} sin commitear en {entry.get('repo')}/{entry.get('scope')}")
        print(f"  {name:16s} {entry.get('stamp') or '?':8s} {entry.get('sha') or '-'}"
              f"  {entry['binary']}")
        for note in notes:
            print(f"  {'':16s} ^ {note}")
    if prov["compiler_tree_dirty"]:
        print("AVISO: el arbol del frontend NO esta limpio -- "
              f"{', '.join(prov['compiler_tree_dirty'])}")
        print("       lo que midas incluye trabajo sin commitear de otra persona.")
    print()


def provenance_drift(was, now):
    """Name what moved between two runs, and say whether it can explain a diff.

    A binary relinked from the same commit differs in sha and in nothing else
    that matters; a binary built from a different commit is a different compiler.
    Conflating the two is how a verdict gets attributed to the wrong person.
    """
    out = []
    if was.get("head") != now.get("head"):
        out.append(("distinto", f"HEAD del monorepo {was.get('head')} -> {now.get('head')}"))
    if was.get("compiler_tree_dirty") != now.get("compiler_tree_dirty"):
        out.append(("distinto", "cambio lo que hay sin commitear en src/compiler"))
    old_tool, new_tool = was.get("toolchain", {}), now.get("toolchain", {})
    for name in sorted(set(old_tool) | set(new_tool)):
        a, b = old_tool.get(name, {}), new_tool.get(name, {})
        if a == b:
            continue
        if a.get("stamp") != b.get("stamp"):
            out.append(("distinto", f"{name}: compilado en {a.get('stamp')} -> {b.get('stamp')}"))
        elif a.get("repo_dirty") != b.get("repo_dirty"):
            out.append(("distinto", f"{name}: cambio el trabajo sin commitear de {a.get('repo')}"))
        elif a.get("sha") != b.get("sha"):
            out.append(("inocuo", f"{name}: mismo commit, binario relinkeado (UUID y firma)"))
        else:
            out.append(("distinto", f"{name}: {a} -> {b}"))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("action", choices=["capture", "check", "annotate"])
    ap.add_argument("--file", default=str(REPO / "tests" / "tools" / "rom_snapshot.json"))
    args = ap.parse_args()

    if args.action == "annotate":
        return annotate(Path(args.file))

    prov = provenance()
    report_provenance(prov)

    current = run_corpus()
    path = Path(args.file)

    if args.action == "capture":
        path.write_text(json.dumps(
            {"provenance": prov, "cells": current}, indent=1, sort_keys=True) + "\n")
        ok = sum(1 for v in current.values() if v["status"] == "ok")
        print(f"\ncapturado: {len(current)} celdas, {ok} compilan -> {path}")
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
    drifted = provenance_drift(was, prov) if was else []
    if any(kind == "distinto" for kind, _ in drifted):
        print("PROCEDENCIA DISTINTA de la de la captura:")
        for kind, text in drifted:
            print(f"    {'!' if kind == 'distinto' else ' '} {text}")
        print("    un diff de celdas aqui puede no ser tuyo.\n")
    elif drifted:
        print("procedencia equivalente (" + "; ".join(t for _, t in drifted) + ")\n")
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
    # A cell whose own backend moved cannot be attributed to the change under
    # test. Saying so per cell is the difference between a gate that works while
    # other people build and one that only works when nobody else is around.
    moved = {name for kind, text in drifted if kind == "distinto"
             for name in prov["toolchain"] if text.startswith(name + ":")}
    frontend_moved = any(kind == "distinto" and not text.startswith("pymcuc-")
                         for kind, text in drifted)
    arch_of = chips()

    print(f"\n{len(diffs)} celdas cambiaron:")
    worse = 0
    for key, text, delta in diffs:
        chip = key.split("|", 1)[1]
        backend = ARCH_BACKEND.get(arch_of.get(chip, "?"))
        if backend in moved or frontend_moved:
            flag = f"  <-- NO ES TUYO: se movio {backend if backend in moved else 'el frontend'}"
        elif delta is not None and delta > 0:
            flag = "  <-- ROM SUBE"
            worse += 1
        elif delta is None:
            flag = "  <-- CAMBIA SI COMPILA"
            worse += 1
        else:
            flag = ""
        print(f"  {key:24s} {text}{flag}")
    return 1 if worse else 0


if __name__ == "__main__":
    sys.exit(main())

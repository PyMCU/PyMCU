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
    "print": """from pymcu.types import uint8
from pymcu.hal.uart import UART


def main():
    u = UART(9600)
    n: uint8 = 0
    v: float = 1234.5
    while True:
        u.println("hola")
        u.print_byte(n)
        u.print_float(v)
        n = n + 1
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

PROBE_CHIPS = {"pic": "pic18f45k50", "avr": "atmega328p",
               "rp2040": "rp2040", "riscv": "ch32v003"}

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


SOURCE_SCOPE = {"pymcuc": "src/compiler",
                "pymcuc-riscv": "extensions/pymcu-backend-riscv"}


def binary_provenance(name, path):
    """What a compiler binary is, and what it was built from.

    Three facts, because no two of them are enough. The sha identifies the build
    but over-detects: two links of identical source differ in 210 bytes of UUID,
    MVID and code signature. The stamp names the commit that was checked out,
    but it is SourceRevisionId -- it never sees the working tree, so a binary
    built from an uncommitted fix is labelled with the commit before the fix.
    The hash of the dirty files closes that gap: it is what tells two builds of
    the same commit with different uncommitted work apart.
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
        dirty = sorted(
            l.split(maxsplit=1)[-1]
            for l in git(repo, "status", "--short", "--", scope).splitlines() if l.strip())
        entry["repo_dirty"] = dirty
        entry["dirty_hash"] = dirty_content_hash(repo, dirty)
        if stamp and head and stamp != head:
            entry["stale"] = f"compilado en {stamp}, el repo va por {head}"
    return entry


def dirty_content_hash(repo, dirty):
    """Fingerprint the uncommitted work, not just its filenames.

    A list of names cannot tell two versions of the same dirty file apart, so a
    second edit and a republish leaves the stamp equal, the list equal and only
    the sha moved -- which reads as a harmless relink of a compiler that is in
    fact different. That is the shape of the hole this campaign paid for twice.

    A snapshot taken before this field existed has no hash to compare, which is
    absence of evidence and not evidence of change: the drift check skips the
    comparison rather than reporting the gate's own new field as someone else's
    drift.
    """
    if not dirty:
        return None
    digest = hashlib.sha256()
    for name in dirty:
        digest.update(name.encode())
        f = Path(repo) / name
        digest.update(f.read_bytes() if f.is_file() else b"<ausente>")
    return digest.hexdigest()[:16]


def scope_unchanged_between(repo, old_stamp, new_stamp, scope):
    """True when two commits differ nowhere inside the tree that builds a binary.

    A stdlib commit cannot change the compiler; without this the gate cries
    wolf at every commit landing elsewhere in the monorepo, and a gate that
    cries wolf gets ignored exactly when it is right.
    """
    if not (old_stamp and new_stamp and repo):
        return False
    for stamp in (old_stamp, new_stamp):
        if not git(repo, "cat-file", "-t", stamp):
            return False
    return git(repo, "diff", "--name-only", f"{old_stamp}..{new_stamp}", "--", scope) == ""


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
    tool["stdlib"] = stdlib_provenance()
    return {
        "head": git(REPO, "rev-parse", "--short=8", "HEAD"),
        "compiler_tree_dirty": sorted(
            l.split(maxsplit=1)[-1]
            for l in git(REPO, "status", "--short", "src/compiler").splitlines() if l.strip()),
        "toolchain": tool,
    }


def unreproducible(prov):
    """Why a baseline captured from this toolchain could not be obtained again.

    A snapshot is a claim that these numbers can be reproduced. The provenance block
    already records everything needed to know when that claim is false, and until now
    nothing acted on it: `capture` wrote the file either way.

    Two conditions break it, per binary:

      stale   built from a commit that is not its repo's head, so "check out repo_head
              and rebuild" does not produce this binary
      dirty   built from a working tree. The stamp is SourceRevisionId: it records the
              checkout and never the tree, so once that uncommitted work is committed
              or discarded there is nothing left to rebuild from. The dirty_hash can
              say two such builds differ; it cannot bring either one back.

    Measured 2026-08-30 while retaking the baseline: four of the five backends were
    stale, two of them from dirty trees, and capture would have frozen that without a
    word. The frontend was current only because the run before it had been discarded
    for being stale, which is the same defect one layer up.
    """
    reasons = []
    for name, b in sorted((prov.get("toolchain") or {}).items()):
        if b.get("stale"):
            reasons.append(f"{name}: {b['stale']}")
        if b.get("repo_dirty"):
            reasons.append(
                f"{name}: {len(b['repo_dirty'])} sin commitear en "
                f"{b.get('repo')}/{b.get('scope')} -- el sello no lo ve y no se recupera")
        if b.get("sha") is None:
            reasons.append(f"{name}: no se encontro el binario ({b.get('binary')})")
    return reasons


STDLIB_DIR = REPO / "lib" / "src" / "pymcu"


def stdlib_provenance():
    """The standard library as a toolchain component, because it is one.

    Five compiler binaries were recorded here and the library they all compile was not,
    so a cell that moved because `lib/` moved was pinned on whichever binary happened to
    have moved too, under the heading NO ES TUYO. That happened to 22 cells on
    2026-08-30: the cause was one stdlib commit adding a guard for the ATtiny parts with
    no USART, and every one of them was billed to `pymcuc-avr`.

    The fields are the same ones the binaries carry, and mean the same things, with one
    asymmetry worth stating rather than leaving to be noticed:

      stamp   the last commit that TOUCHED lib/src/pymcu, not the repo head. A commit
              landing anywhere else leaves it alone, which is what keeps this from
              crying wolf at every unrelated commit in the monorepo.
      sha     the content of the tree that will actually be compiled.
      NO stale.  A binary is built once and deployed, so it can lag its own source. The
              stdlib is read from the working tree at compile time, so it is never
              behind itself. There is nothing here for `stale` to mean.
    """
    repo = next((p for p in STDLIB_DIR.parents if (p / ".git").exists()), None)
    scope = "lib/src/pymcu"
    digest = hashlib.sha256()
    for f in sorted(STDLIB_DIR.rglob("*.py")):
        digest.update(str(f.relative_to(STDLIB_DIR)).encode())
        digest.update(f.read_bytes())
    entry = {"binary": str(STDLIB_DIR), "sha": digest.hexdigest()[:16], "stamp": None}
    if repo:
        entry["repo"] = repo.name
        entry["scope"] = scope
        entry["repo_head"] = git(repo, "rev-parse", "--short=8", "HEAD")
        entry["stamp"] = git(repo, "log", "-1", "--format=%h", "--abbrev=8", "--", scope) or None
        dirty = sorted(
            l.split(maxsplit=1)[-1]
            for l in git(repo, "status", "--short", "--", scope).splitlines() if l.strip())
        entry["repo_dirty"] = dirty
        entry["dirty_hash"] = dirty_content_hash(repo, dirty)
    return entry


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



# ── does the image touch a register this chip has? ───────────────────────────
#
# The gate records whether a binary was PRODUCED. It has never said whether the binary is for
# the chip it names, and three bugs shipped through that gap: the ATmega328P USART compiled on
# every AVR (so an ATtiny4313 with 256 bytes of SRAM got UCSR0A at 0xC0 and UDR0 at 0xC6, and
# eight parts with no USART at all got one), a flash table over 256 bytes read only its first
# 256, and a 16-bit compare register written low-byte-only. Every one produced a binary, and
# the gate counted its bytes.
#
# One invariant, the cheapest one that a wrong-chip image cannot satisfy: every absolute
# data-space address in the IR has to be a register THIS chip declares. A `mem` operand comes
# from a chip register; a variable is a `var` with a name and never appears as a raw address,
# so the set to compare against is exactly what the chip file declares.
#
# Note it deliberately does NOT also allow "anywhere in SRAM": 0xC0 falls inside the
# ATtiny4313's RAM (0x0060..0x015F), so a range test passes the very bug this exists to catch.
#
# Chips whose registers are computed from a base (`ptr(UART0_BASE + 0x08)`) rather than written
# as literals are out of scope, because the declared set cannot be read off the file. That is
# the four RP2040/RP2350/CH32V parts; the 26 others, including every AVR and PIC, are covered.
# Two chips with identical register maps cannot be told apart this way, which is correct: for
# this measurement they are the same chip.

LITERAL_PTR = re.compile(r"ptr\(\s*(0x[0-9a-fA-F]+)\s*\)")
ANY_PTR = re.compile(r"ptr\(")


def declared_registers(chip: str):
    """The addresses `chip` declares as literals, or None when it uses computed bases."""
    f = CHIPS_DIR / f"{chip}.py"
    if not f.exists():
        return None
    text = f.read_text()
    literal = LITERAL_PTR.findall(text)
    if not literal or len(literal) != len(ANY_PTR.findall(text)):
        return None
    return {int(a, 16) for a in literal}


def mir_addresses(mir: Path):
    """Every absolute data-space address the IR touches."""
    try:
        doc = json.loads(mir.read_text())
    except (OSError, ValueError):
        return set()
    found = set()

    def walk(node):
        if isinstance(node, dict):
            if node.get("$t") == "mem" and isinstance(node.get("address"), int):
                found.add(node["address"])
            for v in node.values():
                walk(v)
        elif isinstance(node, list):
            for v in node:
                walk(v)

    walk(doc)
    return found


def foreign_registers(mir: Path, chip: str):
    """Addresses the image touches that `chip` does not declare. Empty is the right answer."""
    declared = declared_registers(chip)
    if declared is None:
        return []
    return sorted(a for a in mir_addresses(mir) if a not in declared)


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
        blob = re.sub(r"(/[\w.\-]+)+/", "", blob)
        reason = (re.search(r"(?:error|Codegen failed|CompileError):\s*(.{0,90})", blob)
                  or re.search(r"(Error\[\d+\]\s+.{0,90})", blob)
                  or re.search(r"Error:\s*(.{0,90})", blob))
        return {"status": "no-build",
                "reason": (reason.group(1).strip() if reason else "unknown")[:110]}
    entry = {"status": "ok", "rom": int(rom.group(1))}
    warnings = WARNING.findall(proc.stdout + proc.stderr)
    if warnings:
        entry["warn"] = len(warnings)
        entry["warn_first"] = " ".join(warnings[0].split())[:90]
    mir = work / "dist" / "firmware.mir"
    if mir.exists():
        entry["mir"] = hashlib.sha256(mir.read_bytes()).hexdigest()[:16]
        foreign = foreign_registers(mir, chip)
        if foreign:
            entry["foreign"] = [hex(a) for a in foreign]
    for name in ("debug/firmware.asm", "firmware.asm"):
        asm = work / "dist" / name
        if asm.exists():
            entry["asm"] = hashlib.sha256(
                canonical_labels(asm.read_text()).encode()).hexdigest()[:16]
            entry["asm_from"] = name
            break
    return entry


PIN_SHAPED = re.compile(r"Unsupported Pin|Unknown pin|no such pin", re.I)

WARNING = re.compile(r"Warning\[\d+\][^\n]{0,90}")

DOES_NOT_FIT = re.compile(r"needs \d+ bytes|static data needs|does not fit", re.I)


def first_that_builds(work, chip, arch, template, candidates, key):
    """Try each pin/channel the architecture might accept, keep the first that builds.

    Every attempt's reason is kept, and the summary quotes the first one that is
    not merely "that pin does not exist here". A candidate failing on the pin is
    saying something true and useless; whichever candidate got far enough to hit
    a timebase gap or an internal error is the one worth reading, wherever it
    sits in the list. Only when every reason is about a pin does the summary
    claim that no candidate compiled.
    """
    tried = {}
    for value in candidates:
        result = build(work, chip, arch, template.format(**{key: value}))
        if result["status"] == "ok":
            result[key] = value
            return result
        tried[value] = result["reason"][:110]
    informative = [(v, r) for v, r in tried.items() if not PIN_SHAPED.search(r)]
    if not informative:
        reason = f"no {key} candidate compiled"
    else:
        value, why = informative[0]
        reason = f"{key}={value}: {why}"
    return {"status": "no-build", "reason": reason[:120], "tried": tried}


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


NO_BUILD_KINDS = [
    ("compile_isr()", "backend-roto", "error interno al montar la ISR"),
    ("Unknown opcode", "backend-roto", "el backend emite una instruccion que el core no tiene"),
    ("Symbol not previously defined", "backend-roto", "el backend usa un simbolo que no declara"),
    ("Address label duplicated", "backend-roto", "el backend acuna la misma etiqueta dos veces"),
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


PERIPHERAL_REGISTERS = {
    "uart": ("TXREG", "UDR", "SPBRG", "TXSTA", "UBRR"),
    "adc": ("ADRESH", "ADCON0", "ADMUX", "ADCSRA", "ADRESL"),
}


def chip_lacks(program, chip):
    """Whether the chip has no such peripheral at all, read from its own register map.

    A facade that rejects a chip and a chip that has no USART produce the same
    no-build, and calling both "the facade does not cover it" turns a physical
    fact into a to-do item. The PIC10F200 will never have a UART.
    """
    names = PERIPHERAL_REGISTERS.get(program)
    if not names:
        return False
    source = CHIPS_DIR / f"{chip}.py"
    if not source.exists():
        return False
    text = source.read_text()
    return not any(re.search(rf"^{n}\b", text, re.M) for n in names)


ACCEPTED = {
    "adc|attiny4313": "+12 bytes: importar uart_write_float sin llamarlo arrastra el camino "
                      "uint32 que la eliminacion de codigo muerto no quita; aceptado a cambio "
                      "de que el 4313 tenga los mismos 2 decimales que el resto",
    "float|attiny4313": "+12 bytes, misma causa que adc|attiny4313",
}


def annotate(path):
    import collections
    stored = json.loads(path.read_text())
    cells = stored.get("cells", stored)
    kinds = collections.Counter()
    for key, cell in cells.items():
        if key in ACCEPTED:
            cell["accepted"] = ACCEPTED[key]
        if cell.get("status") == "ok":
            continue
        reason = cell.get("reason", "")
        kind, why = "sin-clasificar", "motivo no reconocido por el arnes"
        for needle, k, w in NO_BUILD_KINDS:
            if needle in reason:
                kind, why = k, w
                break
        program, chip = key.split("|", 1)
        if chip_lacks(program, chip):
            kind = "chip-sin-periferico"
            why = "el chip no tiene ese periferico: no es deuda, es fisica"
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
            notes.append(f"{len(dirty)} sin commitear en {entry.get('repo')}/{entry.get('scope')}"
                         f" (contenido {entry.get('dirty_hash')}) -- el sello NO lo ve")
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
    if was.get("compiler_tree_dirty") != now.get("compiler_tree_dirty"):
        out.append(("distinto", "cambio lo que hay sin commitear en src/compiler"))
    elif was.get("head") != now.get("head"):
        out.append(("inocuo", f"el monorepo avanzo, {was.get('head')} -> {now.get('head')},"
                              " sin tocar ningun binario"))
    old_tool, new_tool = was.get("toolchain", {}), now.get("toolchain", {})
    for name in sorted(set(old_tool) | set(new_tool)):
        a, b = old_tool.get(name, {}), new_tool.get(name, {})
        repo = repo_of(b.get("binary") or a.get("binary"))
        scope = b.get("scope") or a.get("scope") or "."
        if a.get("stamp") != b.get("stamp"):
            if scope_unchanged_between(repo, a.get("stamp"), b.get("stamp"), scope):
                out.append(("inocuo", f"{name}: el repo avanzo {a.get('stamp')} -> {b.get('stamp')}"
                                      f" sin tocar {scope}"))
            else:
                # "compilado en" is the binaries' vocabulary and the stdlib is never
                # compiled ahead of time: it is read from the tree at build time. Saying
                # a library was "compiled at" a commit is the kind of borrowed sentence
                # this harness keeps catching elsewhere.
                verb = "cambio en" if name == "stdlib" else "compilado en"
                out.append(("distinto", f"{name}: {verb} {a.get('stamp')} -> {b.get('stamp')}"))
        elif ("dirty_hash" in a and "dirty_hash" in b
              and a["dirty_hash"] != b["dirty_hash"]):
            out.append(("distinto", f"{name}: mismo commit, pero cambio el trabajo sin commitear"
                                    f" de {a.get('repo')}/{scope}"))
        elif a.get("repo_dirty") != b.get("repo_dirty"):
            out.append(("distinto", f"{name}: cambio la lista de ficheros sin commitear de {a.get('repo')}"))
        elif a.get("sha") != b.get("sha"):
            out.append(("inocuo", f"{name}: mismo commit y mismo arbol, binario relinkeado"))
    return out


def repo_of(binary):
    if not binary:
        return None
    return next((p for p in Path(binary).parents if (p / ".git").exists()), None)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("action", choices=["capture", "check", "annotate"])
    ap.add_argument("--file", default=str(REPO / "tests" / "tools" / "rom_snapshot.json"))
    ap.add_argument("--anyway", action="store_true",
                    help="capturar aunque el toolchain no se pueda reconstruir despues")
    args = ap.parse_args()

    if args.action == "annotate":
        return annotate(Path(args.file))

    prov = provenance()
    report_provenance(prov)

    # Checked BEFORE the corpus runs: refusing after ten minutes of compiling teaches
    # people to pass --anyway to avoid waiting again.
    blockers = unreproducible(prov) if args.action == "capture" else []
    if blockers and not args.anyway:
        print("\nNO SE CAPTURA: este toolchain no se puede reconstruir despues:")
        for r in blockers:
            print(f"    ! {r}")
        print("    un baseline irreproducible es peor que no tenerlo: el dia que alguien")
        print("    discrepe de estas cifras no habra compilador con el que comprobarlo.")
        print("    Limpia la procedencia, o --anyway para capturar de todas formas.")
        return 2

    current = run_corpus()
    path = Path(args.file)

    if args.action == "capture":
        stored = {"provenance": prov, "cells": current}
        # A forced capture labels itself. Otherwise the next reader sees a baseline that
        # looks like every other one and has no way to know it cannot be rebuilt.
        if blockers:
            stored["unreproducible"] = blockers
        path.write_text(json.dumps(stored, indent=1, sort_keys=True) + "\n")
        ok = sum(1 for v in current.values() if v["status"] == "ok")
        noisy = {k: v for k, v in current.items() if v.get("warn")}
        print(f"\ncapturado: {len(current)} celdas, {ok} compilan -> {path}")
        if noisy:
            print(f"  {len(noisy)} compilan CON AVISOS del ensamblador -- ensamblar no es estar bien:")
            for key, cell in sorted(noisy.items()):
                print(f"    {key:24s} {cell['warn']:3d}  {cell.get('warn_first', '')}")
        alien = {k: v for k, v in current.items() if v.get("foreign")}
        if alien:
            print(f"  {len(alien)} tocan REGISTROS QUE EL CHIP NO TIENE -- "
                  "producir un binario no es producir el binario de este chip:")
            for key, cell in sorted(alien.items()):
                print(f"    {key:24s} {', '.join(cell['foreign'][:8])}")
        return 0

    stored = json.loads(path.read_text())
    before = stored.get("cells", stored)

    COMMENTARY = ("reason", "kind", "proves", "tried", "warn_first", "accepted")

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
    # A wrong-chip image is not an outcome to be frozen and diffed like the others: it is
    # wrong on its own, whether or not it changed since the capture. This is the one thing
    # here that fails without reference to the snapshot.
    alien = {k: v for k, v in current.items() if v.get("foreign")}
    if alien:
        print(f"\n{len(alien)} celdas tocan REGISTROS QUE EL CHIP NO TIENE:")
        for key, cell in sorted(alien.items()):
            print(f"  {key:24s} {', '.join(cell['foreign'][:8])}")
        print("  un binario para otro chip tambien pesa bytes; esto no es un diff, es un bug.")

    diffs = []
    for key in sorted(set(before) | set(current)):
        a, b = measured(before.get(key)), measured(current.get(key))
        if a == b:
            continue
        if a and b and a.get("status") == b.get("status") == "ok":
            delta = b["rom"] - a["rom"]
            note = ""
            if b.get("warn") and not a.get("warn"):
                note = f", {b['warn']} AVISOS DEL ENSAMBLADOR nuevos"
            diffs.append((key, f"asm {a.get('asm')} -> {b.get('asm')}, ROM {delta:+d}{note}", delta))
        elif (a and b and a.get("status") == "ok" and b.get("status") == "no-build"
              and a.get("warn") and DOES_NOT_FIT.search(current[key].get("reason", ""))):
            diffs.append((key, f"ok con {a['warn']} avisos -> no-build honesto: "
                               "dejo de compilar un binario roto", "mejora"))
        else:
            diffs.append((key, f"{a} -> {b}", None))
    if not diffs:
        print(f"\nsin cambios: {len(current)} celdas identicas")
        return 1 if alien else 0
    moved = {name for kind, text in drifted if kind == "distinto"
             for name in prov["toolchain"] if text.startswith(name + ":")}
    # The stdlib is compiled into every image, so when it moves it can explain a cell on
    # ANY chip. It has to be named separately or it falls into `frontend_moved` below and
    # the reader is told the frontend moved, which is a different repository's worth of
    # searching. Before this existed the 22 ATtiny cells of #43 were all billed to
    # `pymcuc-avr`, and the cause was one commit under lib/.
    stdlib_moved = "stdlib" in moved
    frontend_moved = any(kind == "distinto" and not text.startswith("pymcuc-")
                         and not text.startswith("stdlib:")
                         for kind, text in drifted)
    arch_of = chips()

    print(f"\n{len(diffs)} celdas cambiaron:")
    worse = 0
    for key, text, delta in diffs:
        chip = key.split("|", 1)[1]
        backend = ARCH_BACKEND.get(arch_of.get(chip, "?"))
        # Everything that moved, not the first one that matched: with both the stdlib and
        # a backend moved, naming only one sends the reader to look in one place for a
        # cause that could be in either.
        culprits = [c for c in (
            "la stdlib" if stdlib_moved else None,
            backend if backend in moved else None,
            "el frontend" if frontend_moved else None) if c]
        if delta == "mejora":
            flag = "  <-- MEJORA"
        elif culprits:
            flag = "  <-- NO ES TUYO: se movio " + " y ".join(culprits)
        elif delta is not None and delta > 0:
            flag = "  <-- ROM SUBE"
            worse += 1
        elif delta is None:
            flag = "  <-- CAMBIA SI COMPILA"
            worse += 1
        else:
            flag = ""
        print(f"  {key:24s} {text}{flag}")
    return 1 if (worse or alien) else 0


if __name__ == "__main__":
    sys.exit(main())

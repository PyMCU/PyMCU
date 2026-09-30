# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
#
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

from pathlib import Path
import ast
import json
import re
import tomlkit
import typer
import os
import sys
import shutil
import importlib.util
from typing import List, Optional
from rich.console import Console
from rich.progress import Progress, SpinnerColumn, TextColumn, BarColumn, TimeElapsedColumn

# New Architecture Imports
from ..toolchains import get_toolchain_for_chip, get_ffi_toolchain_for_chip
from ..backends import binary_for_plugin, get_backend_for_chip, run_backend
from ..core.compiler import (
    PyMCUCompiler,
    ArenaRequiredError,
    StrfmtRequiredError,
    Round2RequiredError,
    map_line,
)
from ..core.project_config import experimental_enabled
from ..core.boards import (
    board_frequency,
    default_frequency,
    load_extension_board_chips,
    resolve_chip_for_board,
    suggest_boards,
)
from ..core.libraries import (
    find_layer_shadowing as library_layer_shadowing,
    include_paths as library_include_paths,
    resolve_for_target,
    search_path_for_project as library_search_path,
)
from ..core.update_check import get_available_updates, get_installed_pymcu_versions
from ..core.upstream_libraries import resolve_upstream_for_target

console = Console()


def _show_update_hint() -> None:
    """Non-blocking: show one-liner if newer pymcu packages are available on PyPI."""
    try:
        installed = get_installed_pymcu_versions()
        updates = get_available_updates(installed)
        if not updates:
            return
        parts = [f"{pkg} {cur} → {new}" for pkg, (cur, new) in updates.items()]
        console.print(
            f"\n[dim]💡 Updates available: {', '.join(parts)}\n"
            "   Run [bold]pymcu upgrade[/bold] to update.[/dim]"
        )
    except Exception:
        pass  # never let update check break a successful build


# CI Diagnostic logger — active only when --verbose / PYMCU_VERBOSE=1.
import sys as _sys_for_diag
def _diag_log(msg: str, verbose: bool = False):
    """Log a diagnostic message to stderr when verbose mode is active."""
    if verbose or os.environ.get("PYMCU_VERBOSE") == "1":
        print(f"[PYMCU_BUILD_DIAG] {msg}", file=_sys_for_diag.stderr, flush=True)

# ---------------------------------------------------------------------------
# Compiler phase → progress mapping
# Must match the phase Names declared in Program.cs / CompilerDriver pipeline.
# Each PHASE_END token advances the build_task by one step within the 10-50 range.
# ---------------------------------------------------------------------------
_COMPILER_PHASES = [
    "Initialization",
    "Bootstrapping",
    "Parsing",
    "Frontend Resolution",
    "IR Generation",
    "Backend Phase",
]
_COMPILER_PHASE_STEP = 40.0 / len(_COMPILER_PHASES)  # spreads 10 -> 50 %

# Flash capacity in bytes for known chips.
# Used for the flash-usage report after assembly.
FLASH_SIZES: dict[str, int] = {
    "atmega328p": 32768, "atmega328": 32768,
    "atmega168p": 16384, "atmega168": 16384,
    "atmega88p":  8192,  "atmega88":  8192,
    "atmega48p":  4096,  "atmega48":  4096,
    "atmega2560": 262144,
    "atmega32u4": 32768,
    "attiny85": 8192,  "attiny45": 4096,  "attiny25": 2048,
    "attiny84": 8192,  "attiny44": 4096,  "attiny24": 2048,
    "attiny13": 1024,  "attiny13a": 1024,
    "attiny2313": 2048, "attiny4313": 4096,
    "rp2040": 2097152,   # 2 MB external QSPI flash (Raspberry Pi Pico default)
    "rp2350": 4194304,   # 4 MB external QSPI flash (Raspberry Pi Pico 2 default)
    "ch32v003": 16384,   # WCH QingKe V2A (RV32EC)
    "ch32v203": 65536,   # WCH QingKe V4B (RV32IMAC)
}


def _make_compiler_output_handler(progress, task, verbose: bool):
    """
    Returns a callback that receives each stdout line from pymcuc and maps
    structured progress tokens to Rich progress updates.

    Token protocol (emitted by Logger in driver mode):
      [PHASE_START] <name>           -> update description
      [PHASE_END]   <name> <ms>      -> advance progress
      [BUILD_INFO]  chip=X freq=Y    -> enrich progress bar description
      [BUILD_OK]    <path>           -> advance to 50 %
      [BUILD_FAIL]  <phase>          -> stop (caller handles exit)
      [INFO]        <text>           -> show in verbose mode
      [VERBOSE]     <text>           -> show in verbose mode
    """
    phase_index = [0]

    def handle(line: str):
        if line.startswith("[PHASE_START] "):
            name = line[len("[PHASE_START] "):]
            progress.update(task, description=f"  [cyan]{name}[/cyan]...")
        elif line.startswith("[PHASE_END] "):
            phase_index[0] += 1
            completed = 10 + phase_index[0] * _COMPILER_PHASE_STEP
            progress.update(task, completed=int(completed))
        elif line.startswith("[BUILD_INFO] "):
            # Parse key=value pairs emitted by Logger.PrintTargetSummary
            info: dict[str, str] = {}
            for part in line[len("[BUILD_INFO] "):].split():
                if "=" in part:
                    k, _, v = part.partition("=")
                    info[k] = v
            chip = info.get("chip", "")
            freq_hz = int(info.get("freq", "0") or "0")
            if chip and freq_hz:
                freq_label = (
                    f"{freq_hz // 1_000_000} MHz" if freq_hz >= 1_000_000
                    else f"{freq_hz // 1_000} kHz" if freq_hz >= 1_000
                    else f"{freq_hz} Hz"
                )
                progress.update(task, description=f"  [cyan]Building[/cyan] {chip} @ {freq_label}...")
            elif chip:
                progress.update(task, description=f"  [cyan]Building[/cyan] {chip}...")
        elif line.startswith("[BUILD_OK] "):
            progress.update(task, completed=50)
        elif line.startswith("[PGO] "):
            # The backend reports what the profile did to it (register-home
            # order today); worth one line whether or not verbose is on.
            progress.console.print(f"  [dim]{line[len('[PGO] '):]}[/dim]")
        elif verbose and line.startswith(("[INFO] ", "[VERBOSE] ")):
            progress.console.print(f"  [dim]{line}[/dim]")

    return handle



# BOARD_CHIPS lives in core.boards, read through resolve_chip_for_board, so `build` and
# `flash` cannot drift apart.
# Extension packages may supplement it via a board_chips.py module
# (see _load_extension_board_chips()).
# ---------------------------------------------------------------------------


def _load_extension_board_chips(flavor: str) -> dict[str, str]:
    """Try to import pymcu_<flavor>.board_chips and return its BOARD_CHIPS dict."""
    return load_extension_board_chips(flavor)


_PRINT_RE    = re.compile(r'\bprint\s*\(')
_UART_RE     = re.compile(r'\bUART\s*\(')
_TICKS_MS_RE = re.compile(r'\b(?:ticks_ms|monotonic|monotonic_ns|ticks_us|micros)\s*\(')
_INPUT_RE    = re.compile(r'\binput\s*\(')
_ASYNC_DEF_RE = re.compile(r'^\s*async\s+def\s', re.MULTILINE)


def _ast_module_or_none(py_file: Path) -> Optional[ast.Module]:
    """Parse a source file with ast; return the tree, or None if it cannot be read
    or parsed (a construct CPython's ast rejects but PyMCU's own parser accepts).
    Callers fall back to a text-based heuristic when this returns None, so an
    unparseable file never silently loses a real usage.
    """
    try:
        text = py_file.read_text(encoding="utf-8", errors="ignore")
    except OSError:
        return None
    try:
        return ast.parse(text)
    except SyntaxError:
        return None


def _call_target_names(node: ast.Call) -> tuple:
    """Return (bare_name, attr_name) for a Call's callee: bare_name is set for a
    plain-name call (`f(...)`), attr_name for an attribute call (`x.f(...)`)."""
    func = node.func
    if isinstance(func, ast.Name):
        return func.id, None
    if isinstance(func, ast.Attribute):
        return None, func.attr
    return None, None


def _source_has_named_call(sources_dir: Path, names: set) -> bool:
    """True if any .py file contains an actual call to one of *names*, as a bare
    name (`millis_init()`) or through an attribute (`timer.millis_init()`).
    Ignores the same spelling sitting in a comment, a string or a docstring --
    those never run, unlike a substring match over the raw text.
    """
    for py_file in sources_dir.rglob("*.py"):
        tree = _ast_module_or_none(py_file)
        if tree is None:
            try:
                text = py_file.read_text(encoding="utf-8", errors="ignore")
            except OSError:
                continue
            if any(re.search(r'\b' + re.escape(n) + r'\s*\(', text) for n in names):
                return True
            continue
        for node in ast.walk(tree):
            if isinstance(node, ast.Call):
                bare, attr = _call_target_names(node)
                if bare in names or attr in names:
                    return True
    return False
# `raise Name(<anything>)` -- an exception raised with a message. The unhandled
# report prints it through the console string writers, which nothing links in
# unless print() (or this) pulled them in.
_RAISE_MSG_RE = re.compile(r'\braise\s+[A-Za-z_]\w*\s*\(\s*[^)\s]')
# `board` is a CircuitPython concept (board.LED, board.GP25). MicroPython code
# addresses pins through machine.Pin and never imports it.
_IMPORT_BOARD_RE = re.compile(r'^\s*(?:import\s+board\b|from\s+board\s+import\b)',
                              re.MULTILINE)


def _detect_raise_with_message(sources_dir: Path) -> bool:
    """Return True if any .py file raises an exception with an argument.

    Same over-inclusive-on-purpose shape as the print() scan: matching `raise X(...)`
    in dead code only links console writers DCE would remove anyway; missing a real
    one just means the unhandled report is `E:<Type>` without the message, the way it
    was before.
    """
    for py_file in sources_dir.rglob("*.py"):
        try:
            lines = py_file.read_text(encoding="utf-8", errors="ignore").splitlines()
            code = "\n".join(line.split("#")[0] for line in lines)
            if _RAISE_MSG_RE.search(code):
                return True
        except OSError:
            pass
    return False


def _imports_board(sources_dir: Path) -> bool:
    """True if any source actually imports `board`."""
    for py_file in sources_dir.rglob("*.py"):
        try:
            lines = py_file.read_text(encoding="utf-8", errors="ignore").splitlines()
            code = "\n".join(line.split("#")[0] for line in lines)
        except OSError:
            continue
        if _IMPORT_BOARD_RE.search(code):
            return True
    return False


def _module_level_shadows(tree: ast.Module, name: str) -> bool:
    """True if *name* is bound at module level (a def, a class, an assignment or
    an import), which shadows the builtin of the same spelling for every call to
    the bare name anywhere in the module."""
    for node in tree.body:
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)) and node.name == name:
            return True
        if isinstance(node, ast.Assign) and any(
            isinstance(t, ast.Name) and t.id == name for t in node.targets
        ):
            return True
        if isinstance(node, ast.AnnAssign) and isinstance(node.target, ast.Name) and node.target.id == name:
            return True
        if isinstance(node, ast.AugAssign) and isinstance(node.target, ast.Name) and node.target.id == name:
            return True
        if isinstance(node, (ast.Import, ast.ImportFrom)):
            for alias in node.names:
                if (alias.asname or alias.name) == name:
                    return True
    return False


def _detect_print_usage(sources_dir: Path) -> tuple[bool, bool, bool]:
    """Scan .py files in sources_dir.

    Returns (has_print, has_uart, has_input):
      has_print -- True if any file calls the builtin print(): a bare-name call
                   (`print(...)`, not `lcd.print(...)`) not shadowed by a
                   module-level `print` of the user's own.
      has_uart  -- True if any file explicitly constructs a UART() instance: by
                   its bare name, by an import alias (`from ... import UART as
                   Serial`), or fully qualified (`pymcu.hal...uart.UART(...)`).
      has_input -- same rule as has_print, for input().
    """
    has_print = False
    has_uart  = False
    has_input = False
    for py_file in sources_dir.rglob("*.py"):
        tree = _ast_module_or_none(py_file)
        if tree is None:
            # Unparseable: fall back to the previous text scan so a construct
            # CPython's ast can't read never silently drops a real usage.
            try:
                lines = py_file.read_text(encoding="utf-8", errors="ignore").splitlines()
                code = "\n".join(line.split("#")[0] for line in lines)
            except OSError:
                continue
            if not has_print and _PRINT_RE.search(code):
                has_print = True
            if not has_uart and _UART_RE.search(code):
                has_uart = True
            if not has_input and _INPUT_RE.search(code):
                has_input = True
            if has_print and has_uart and has_input:
                break
            continue

        print_shadowed = _module_level_shadows(tree, "print")
        input_shadowed = _module_level_shadows(tree, "input")
        uart_aliases = {"UART"}
        for node in ast.walk(tree):
            if isinstance(node, ast.ImportFrom):
                for alias in node.names:
                    if alias.name == "UART":
                        uart_aliases.add(alias.asname or alias.name)
        for node in ast.walk(tree):
            if isinstance(node, ast.Call):
                bare, attr = _call_target_names(node)
                if not has_print and bare == "print" and not print_shadowed:
                    has_print = True
                if not has_input and bare == "input" and not input_shadowed:
                    has_input = True
                if not has_uart and (bare in uart_aliases or attr == "UART"):
                    has_uart = True
        if has_print and has_uart and has_input:
            break
    return has_print, has_uart, has_input


_FSTRING_VALUE_RE = re.compile(r'''=\s*f["']|\.join\s*\(|str\s*\(|repr\s*\(''')


def _detect_fstring_value_usage(sources_dir: Path) -> bool:
    """Return True if any .py file assigns an f-string to a name (`s = f"..."`)
    or calls str.join (a join over a generator/comprehension materializes
    through the same pymcu.strfmt helpers).

    Over-inclusive on purpose (a fully-constant f-string assignment also matches):
    the injected pymcu.strfmt helpers are plain module functions, so anything
    unused is dropped by DCE. hex()/bin()/oct() of a run-time value ALSO needs
    pymcu.strfmt but is not scanned for here (P2 AVR gaps bundle, item 1 / RFC
    0014 decision 5): this regex over the source text cannot tell a real call
    from the same spelling in a comment or a user's own `def hex`, so it used
    to either miss the real usage or inject the helper unasked. The compiler
    itself decides now -- it reports [NEEDS_STRFMT] on its stdout token stream
    once it resolves a call that actually needs the helper and finds the import
    missing; build() answers that below the same way it already answers
    [NEEDS_ARENA] (see _compile_frontend's retry loop).
    """
    for py_file in sources_dir.rglob("*.py"):
        try:
            lines = py_file.read_text(encoding="utf-8", errors="ignore").splitlines()
            code = "\n".join(line.split("#")[0] for line in lines)
            if _FSTRING_VALUE_RE.search(code):
                return True
        except OSError:
            pass
    return False


# docs/rfcs/0004-arena-allocator.md: the arena's default reservation when a program uses
# runtime-sized bytearray(n) and no `arena_size` was set in [tool.pymcu]. Only
# atmega328p is supported in phase 1 (see the RFC, "Targets"). Whether the program
# needs the arena at all is the COMPILER's call -- it folds the size argument itself
# and reports [NEEDS_ARENA] / [ARENA_USED] on the compile's stdout token stream (see
# _compile_frontend below); a lexical scan of the sources cannot tell a foldable
# bytearray(((h // 8) * w) + 1) from a genuinely runtime-sized one.
_ARENA_BOARD_DEFAULT_BYTES = 256


def _inject_arena_shim(generated_dir: Path, arena_size: int) -> None:
    """Write dist/_generated/pymcu/arena.py with ARENA_SIZE set to the real reservation.

    A whole-file replacement of the shipped module (like board.py's board_shim above), not
    a separate imported config module: a cross-module imported constant did not fold as a
    bytearray() size argument when that was tried (see docs/rfcs/0004-arena-allocator.md,
    "The allocator is Python") even though the same name folds fine in an ordinary
    expression in the same file -- a same-file literal sidesteps that gap entirely.
    """
    spec = importlib.util.find_spec("pymcu.arena")
    if spec is None or spec.origin is None:
        raise FileNotFoundError(
            "pymcu.arena (the shipped arena allocator module) was not found on the search "
            "path -- this should be unreachable, please report this as a PyMCU bug.")
    src = Path(spec.origin).read_text(encoding="utf-8")
    replaced, n = re.subn(
        r"^ARENA_SIZE: uint16 = \d+$", f"ARENA_SIZE: uint16 = {arena_size}",
        src, count=1, flags=re.MULTILINE)
    if n != 1:
        raise RuntimeError(
            "pymcu.arena: the 'ARENA_SIZE: uint16 = <N>' line was not found to replace -- "
            "this should be unreachable, please report this as a PyMCU bug.")
    pkg_dir = generated_dir / "pymcu"
    pkg_dir.mkdir(parents=True, exist_ok=True)
    (pkg_dir / "arena.py").write_text(
        "# Auto-generated by pymcu build -- do not edit\n" + replaced, encoding="utf-8")


def _inject_arena_preamble(entry_point: Path, generated_dir: Path) -> tuple[Path, int]:
    """Inject the pymcu.arena import that runtime-sized bytearray(n) lowering resolves.

    `x = bytearray(n)` with a non-constant n expands (in the IR generator) to a call into
    pymcu.arena.alloc(), resolved by import alias exactly like pymcu.strfmt -- the module
    must be loaded for that synthetic call to resolve. See
    docs/rfcs/0004-arena-allocator.md.
    """
    return _inject_preamble(
        entry_point,
        generated_dir,
        comment="# Auto-injected by pymcu build: the arena allocator for bytearray(n)\n",
        import_line="import pymcu.arena as _pymcu_arena\n",
        call_line="pass",
    )


def _inject_strfmt_preamble(entry_point: Path, generated_dir: Path) -> tuple[Path, int]:
    """Inject the pymcu.strfmt import that f-string-as-value lowering resolves.

    `s = f"..."` with runtime interpolations expands (in the IR generator) to
    chained pymcu.strfmt calls into a fixed buffer; the module must be loaded
    for those synthetic calls to resolve.
    """
    return _inject_preamble(
        entry_point,
        generated_dir,
        comment="# Auto-injected by pymcu build: strfmt helpers for f-string values\n",
        import_line="import pymcu.strfmt as _pymcu_strfmt\n",
        call_line="pass",
    )


# round(x, n) on a FLOAT x (P2 AVR gaps bundle, item 2): the IR generator resolves the
# two-argument builtin to a call on pymcu.round2's _pymcu_round2, by import alias exactly
# like pymcu.strfmt above. An integer x folds at compile time and needs no import.
#
# Whether a given round(x, n) call actually needs the helper is NOT decided here (P2 AVR
# gaps bundle, item 1 / RFC 0014 decision 5): a source-text scan cannot tell a real call
# to the builtin from the same spelling in a comment, a string, or a user's own `def
# round` (which CPython -- and now PyMCU -- lets shadow the builtin). The compiler
# decides, from the call it just resolved, and reports [NEEDS_ROUND2] on its stdout
# token stream when the helper is needed and missing; build() answers that below with
# _inject_round2 (see _compile_frontend's retry loop), the same way it already answers
# [NEEDS_ARENA].


def _inject_round2_preamble(entry_point: Path, generated_dir: Path) -> tuple[Path, int]:
    """Inject the pymcu.round2 import that round(x, n) on a float lowering resolves."""
    return _inject_preamble(
        entry_point,
        generated_dir,
        comment="# Auto-injected by pymcu build: round(x, n) helper\n",
        import_line="import pymcu.round2 as _pymcu_round2\n",
        call_line="pass",
    )


def _detect_ticks_ms_usage(sources_dir: Path) -> bool:
    """Return True if any source file actually calls into the Timer0 time base.

    Covers MicroPython ticks_ms()/ticks_us()/micros() and CircuitPython
    time.monotonic()/monotonic_ns()/supervisor.ticks_ms(): all of them read
    the millis/micros counter, which stays frozen at 0 until millis_init()
    arms the overflow ISR -- a monotonic()-scheduled loop then never fires.
    A call is required (ast-checked): the same spelling in a comment does not
    reserve Timer0.
    """
    return _source_has_named_call(
        sources_dir, {"ticks_ms", "ticks_us", "micros", "monotonic", "monotonic_ns"}
    )


def _detect_async_def_usage(sources_dir: Path) -> bool:
    """Return True if any .py file in sources_dir defines an `async def`.

    On AVR the await machinery reads asyncio.ticks(), which is the Timer0
    millis/micros counter -- it only advances once millis_init() has armed the
    overflow ISR, so an async program needs the same preamble as ticks_ms().
    """
    for py_file in sources_dir.rglob("*.py"):
        try:
            lines = py_file.read_text(encoding="utf-8", errors="ignore").splitlines()
            code = "\n".join(line.split("#")[0] for line in lines)
            if _ASYNC_DEF_RE.search(code):
                return True
        except OSError:
            pass
    return False


def _sources_call(sources_dir: Path, name: str) -> bool:
    """Return True if any .py file under sources_dir actually calls *name*.

    Used to skip auto-injecting an init preamble (millis_init(), clock_init())
    the sources already call themselves -- a substring match used to trip on
    the same spelling sitting in a comment, disabling the injection while the
    real init call never ran.
    """
    return _source_has_named_call(sources_dir, {name})


# ---------------------------------------------------------------------------
# RFC 0008 -- embedded files (romfs).
#
# `open("name", mode)` resolves at compile time to a handle over a flash blob;
# there is no filesystem table on the chip. The driver's part is discovery only:
# decide which (name, path) pairs the compiler embeds, hand them over via
# `--embed name=path`, and report them on the build line. The compiler keys its
# table by the name open() is given and refuses anything it cannot resolve.
# ---------------------------------------------------------------------------

# A literal first argument to open(): open("font5x8.bin"), open('data/x.bin', "rb").
# The string may use any Python prefix combination a compiler could accept later
# (b, r, u, and combinations), so the literal is captured with the prefix group.
_OPEN_LITERAL_RE = re.compile(
    r"""\bopen\s*\(\s*(?:[bBrRuU]{0,3})["']([^"'\n]+)["']""")


def _detect_open_literals(sources_dir: Path) -> list[str]:
    """Return every literal filename an open() call in the sources names.

    Pure discovery for the auto-embedding rule: a name found here that exists
    under the source tree gets embedded without the project listing it in
    [tool.pymcu] files. Non-literal arguments (open(self.font_name, ...)) match
    nothing, which is correct -- the compiler still resolves those at compile
    time through constant folding, and the project lists such files explicitly.
    """
    names: list[str] = []
    for py_file in sorted(sources_dir.rglob("*.py")):
        try:
            lines = py_file.read_text(encoding="utf-8", errors="ignore").splitlines()
            code = "\n".join(line.split("#")[0] for line in lines)
        except OSError:
            continue
        for m in _OPEN_LITERAL_RE.finditer(code):
            if m.group(1) not in names:
                names.append(m.group(1))
    return names


def _resolve_embed_files(
    project_root: Path,
    sources_dir: Path,
    entry_point: Path,
    pymcu_config: dict,
) -> list[tuple[str, Path, int]]:
    """Resolve the (name, path, size) triples the compiler embeds.

    Two sources, union on name (first hit wins):
      * [tool.pymcu] files = [...] -- paths or glob patterns, resolved against
        the project root first and the sources dir second, so `files =
        ["font5x8.bin"]` finds `src/font5x8.bin` as well as a top-level file.
        The embedded name is the path relative to the base it matched under.
      * Auto-embed: every literal `open("name")` in the sources whose file
        exists under sources_dir, the project root, or next to the entry file.

    Returns sorted by name for a stable build line.
    """
    embedded: dict[str, Path] = {}

    for pattern in pymcu_config.get("files", []) or []:
        matches: list[Path] = []
        for base in (project_root, sources_dir):
            matches.extend(sorted(base.glob(pattern)))
        matched_any = False
        for m in matches:
            if not m.is_file():
                continue
            matched_any = True
            # sources first: `files = ["font5x8.bin"]` resolving to
            # `src/font5x8.bin` embeds under the name open() names.
            for base in (sources_dir, project_root):
                try:
                    name = m.resolve().relative_to(base.resolve()).as_posix()
                    break
                except ValueError:
                    continue
            else:
                name = m.name
            embedded.setdefault(name, m.resolve())
        if not matched_any:
            console.print(
                f"[yellow]warning:[/yellow] \\[tool.pymcu] files pattern "
                f"'{pattern}' matched no file -- nothing embedded for it")

    for name in _detect_open_literals(sources_dir):
        if name in embedded:
            continue
        for base in (sources_dir, project_root, entry_point.parent):
            candidate = base / name
            if candidate.is_file():
                embedded[name] = candidate.resolve()
                break

    out: list[tuple[str, Path, int]] = []
    for name, path in sorted(embedded.items()):
        try:
            out.append((name, path, path.stat().st_size))
        except OSError:
            continue
    return out


_MAIN_DEF_RE = re.compile(r"^(def main\s*\(\s*\)\s*:)", re.MULTILINE)

# Every line `pymcu build` inserts into the entry file carries this. It is what lets a
# diagnostic be mapped back per LINE instead of by one accumulated offset, which is what
# reported a module-level line one early whenever the program had an explicit
# `def main():` (#311).
INJECTED_MARK = "  # pymcu:injected"


def _inject_preamble(
    entry_point: Path,
    generated_dir: Path,
    comment: str,
    import_line: str,
    call_line: str,
) -> tuple[Path, int]:
    """Write a synthetic entry file injecting import_line + call_line.

    When the source has an explicit ``def main():``, the import is placed at
    the top of the file and the call is inserted as the first statement inside
    ``def main():``.  Otherwise both are prepended at the top level.  This
    avoids the compiler error that fires when top-level executable statements
    coexist with an explicit ``def main()``.

    Returns (synthetic_path, preamble_line_count) so callers can correct
    linemap line numbers that were shifted by the injected preamble.
    """
    generated_dir.mkdir(parents=True, exist_ok=True)
    synthetic = generated_dir / entry_point.name
    existing = entry_point.read_text(encoding="utf-8")
    m = _MAIN_DEF_RE.search(existing)
    if m:
        header = _mark_injected(comment + import_line + "\n")
        modified = (existing[:m.end()] + "\n    " + call_line + INJECTED_MARK
                    + existing[m.end():])
        synthetic.write_text(header + modified, encoding="utf-8")
        # Two insertion points, and therefore two different shifts: a line above
        # `def main():` moves by the header alone, a line at or below the inserted call by
        # the header plus one. A single number cannot say both, and the larger of the two
        # sent every diagnostic about a module-level line one line early (#311). Kept only
        # for callers that still want a rough count; the honest answer is the per-line map
        # _preamble_line_map() reads back off the marks above.
        preamble_lines = header.count("\n") + 1
    else:
        preamble = _mark_injected(comment + import_line + call_line + "\n\n")
        synthetic.write_text(preamble + existing, encoding="utf-8")
        preamble_lines = preamble.count("\n")
    return synthetic, preamble_lines


def _mark_injected(block: str) -> str:
    """Put the sentinel on every line of an injected block, blank lines included."""
    out = []
    for line in block.split("\n")[:-1]:        # the block always ends in a newline
        out.append(line + INJECTED_MARK + "\n")
    return "".join(out)


def _preamble_line_map(synthetic: Path) -> list[int | None]:
    """Generated line (1-based) -> the user's line, or None for a line pymcu injected.

    Read back off the file itself rather than accumulated as the injections run, so the
    four preambles that can stack compose without any of them knowing about the others.
    Index 0 is unused, so a 1-based line number indexes it directly.
    """
    mapping: list[int | None] = [None]
    injected = 0
    try:
        lines = synthetic.read_text(encoding="utf-8").split("\n")
    except OSError:
        return mapping
    if lines and lines[-1] == "":
        lines.pop()
    for generated, line in enumerate(lines, start=1):
        if line.rstrip().endswith(INJECTED_MARK.strip()):
            injected += 1
            mapping.append(None)
        else:
            mapping.append(generated - injected)
    return mapping


def _get_stdout_config(pymcu_config: dict) -> tuple[str, int]:
    """Return (device, baud) for the configured stdout output device.

    Reads optional ``stdout`` and ``stdout_baud`` keys from [tool.pymcu].
    Defaults to uart0 at 115200 baud when not specified.
    """
    device = str(pymcu_config.get("stdout", "uart0"))
    baud   = int(pymcu_config.get("stdout_baud", 115200))
    return device, baud


def _inject_print_preamble(
    entry_point: Path,
    generated_dir: Path,
    device: str = "uart0",
    baud: int = 115200,
) -> tuple[Path, int]:
    """Return a synthetic entry file with a stdout-init preamble prepended.

    Mirrors MicroPython's boot behavior: the configured output device is
    pre-initialized before user code runs, so print() works without an
    explicit UART() constructor in user code.

    Imports print_str from console.py so the IRGenerator resolves string
    output via the arch-dispatched console function rather than the
    uart-specific name.  Integer and float write functions (uart_write_decimal_u8,
    uart_write_float) are kept as-is because they are non-inline and the
    print() handler emits them via direct IR Call nodes rather than VisitCall.
    """
    return _inject_preamble(
        entry_point,
        generated_dir,
        comment=f"# Auto-injected by pymcu build: stdout={device} at {baud} baud for print()\n",
        import_line=(
            "from pymcu.hal.uart import UART as _pymcu_stdout\n"
            "from pymcu.hal.console import print_str\n"
        ),
        call_line=f"_pymcu_stdout({baud})",
    )


def _inject_print_imports_only(entry_point: Path, generated_dir: Path) -> tuple[Path, int]:
    """Inject only the console streaming functions, with NO stdout/UART init.

    Used when the user manages their own UART (so we must not double-initialize it)
    but also calls print(): importing console.print_str loads the streaming value/
    string writers the print() lowering resolves by name.
    """
    return _inject_preamble(
        entry_point,
        generated_dir,
        comment="# Auto-injected by pymcu build: console functions for print() (user-managed UART)\n",
        import_line="from pymcu.hal.console import print_str\n",
        call_line="pass",
    )


def _inject_ticks_ms_preamble(entry_point: Path, generated_dir: Path,
                              reason: str = "ticks_ms()") -> tuple[Path, int]:
    """Return a synthetic entry file with a millis_init() preamble prepended.

    Called when ticks_ms() -- or, on AVR, an `async def` -- is detected in user
    sources and no explicit millis_init() call is present.  Mirrors the print()
    / UART preamble injection pattern: the build driver owns the setup, user
    code stays clean.

    Note: millis_init() configures Timer0 in normal overflow mode at prescaler
    64 (~1 ms resolution at 16 MHz).  Do not use Timer0 for PWM or CTC in the
    same project when the millis counter is active.
    """
    return _inject_preamble(
        entry_point,
        generated_dir,
        comment=f"# Auto-injected by pymcu build: millis timer initialized for {reason}\n",
        import_line="from pymcu.hal.timer import millis_init as _pymcu_millis_init\n",
        call_line="_pymcu_millis_init()",
    )


def _inject_clock_init_preamble(entry_point: Path, generated_dir: Path) -> tuple[Path, int]:
    """Return a synthetic entry file that calls clock_init() first thing in main().

    The RP2350 bootrom leaves clk_sys on the low boot clock and the system TIMER tick
    sourced from the imprecise ROSC, so a bare-metal program runs ~12x slow on the CPU
    and ~2x slow on every delay_ms/asyncio timer. The pico-sdk fixes this in its runtime
    (runtime_init_clocks, before main); PyMCU mirrors that by auto-injecting clock_init()
    -- which starts XOSC, locks PLL_SYS at 150 MHz and gives the timer an exact 1 MHz tick
    -- so user code stays clean (no manual clock setup, just like the SDK / MicroPython).

    Injected last so clock_init() lands ahead of any stdout/ticks preamble, which need the
    final clk_sys / clk_peri to be in effect before they configure their peripherals.
    """
    return _inject_preamble(
        entry_point,
        generated_dir,
        comment="# Auto-injected by pymcu build: RP2350 clocks brought up to 150 MHz / 1 MHz tick\n",
        import_line="from pymcu.hal.rp2350.clocks import clock_init as _pymcu_clock_init\n",
        call_line="_pymcu_clock_init()",
    )


def _correct_linemap(linemap_path: Path, filename: str, offset) -> None:
    """Map every linemap entry whose File == *filename* back to the user's line.

    *offset* is the per-line map _preamble_line_map() builds, or a plain count for callers
    that still hand one over. Entries that land on a line pymcu injected are dropped: they
    have no counterpart in the original source file. The single count is what put the
    debugger's line map one off over the region above `def main():` (#311).
    """
    entries = json.loads(linemap_path.read_text(encoding="utf-8"))
    corrected = []
    for e in entries:
        if e.get("File") == filename:
            new_line = map_line(offset, e["Line"])
            if new_line:
                corrected.append({**e, "Line": new_line})
        else:
            corrected.append(e)
    linemap_path.write_text(json.dumps(corrected), encoding="utf-8")


def _resolve_chip_for_board(board: str, extra: dict[str, str]) -> str | None:
    """Return the chip name for *board*, checking extension-supplied entries first."""
    return resolve_chip_for_board(board, extra)


def _install_deps_hint(project_root: Path) -> str:
    """
    The command that installs this project's dependencies.

    Picks by what the project actually carries, so the advice matches the layout
    `pymcu new` produced rather than assuming a package manager.
    """
    if (project_root / "uv.lock").exists() or shutil.which("uv"):
        return "uv sync"
    if (project_root / "poetry.lock").exists():
        return "poetry install"
    if (project_root / "requirements.txt").exists():
        return "pip install -r requirements.txt"
    return "uv sync   (or: pip install -r requirements.txt)"


def _parse_hex_flash_bytes(hex_file: Path) -> int:
    """
    Parse an Intel HEX file and return the total number of data bytes.
    Only counts type-00 (data) records; ignores EOF (01) and extended (02/04) records.
    Returns 0 if the file cannot be read.
    """
    total = 0
    try:
        with open(hex_file, "r") as f:
            for line in f:
                line = line.strip()
                if not line.startswith(":"):
                    continue
                rec_len  = int(line[1:3], 16)
                rec_type = int(line[7:9], 16)
                if rec_type == 0x00:   # data record
                    total = total + rec_len
    except Exception:
        pass

    return total


# Last resort only, for a caller holding no artifacts, and it is the ATmega328P's preamble
# and nobody else's. An AVR vector-table slot is 4 bytes on the parts with JMP/CALL (avr5/avr6,
# the ATmega328P among them) and 2 bytes on the parts without them (avr25, every ATtiny), and
# the last slot is not padded because nothing follows it. So the ATmega's 26 slots occupy
# 25*4 + 2 = 102 bytes and an ATtiny's occupy 25*2 + 2 = 52, and the `__bad_interrupt`
# stub that trails the table adds its relaxed RJMP: 104 and 54 with it counted.
#
# The table alone was 104 once, which is 26*4 and two bytes more than the image has:
# `__bad_interrupt` sits at 0x66 in the linked ELF. And the constant was applied to both
# families, because the guard below tests `startswith("at")`, so an attiny13 whose table
# is 52 and whose whole flash is 1024 was told "8 bytes of your code" for a program with 60.
# Measured from the linked ELF wherever it exists, and from the emitted assembly otherwise,
# which together is every real build.
_AVR_PREAMBLE_BYTES = 104


def _avr_vector_table_bytes(artifacts_dir) -> int | None:
    """The size of the vector table the backend actually emitted, or None.

    Read rather than assumed, so it stays right when the table changes: the count is
    hardcoded at 26 slots today (pymcu-avr#16) and the slot width already varies by core.
    The table is the run of `.org` directives before `__bad_interrupt`, and its size is the
    last of those plus the two bytes of the RJMP that fills it, so neither the slot count nor
    the slot width appears here.
    """
    if artifacts_dir is None:
        return None
    try:
        text = (Path(artifacts_dir) / "firmware.gas.asm").read_text()
    except OSError:
        return None

    orgs: list[int] = []
    for line in text.splitlines():
        s = line.strip()
        if s.startswith("__bad_interrupt"):
            break
        if s.startswith(".org"):
            try:
                orgs.append(int(s.split()[1], 0))
            except (IndexError, ValueError):
                return None
    if not orgs:
        return None
    # The LAST slot holds only its RJMP. Every earlier one is padded out to the stride, but
    # nothing follows the last, so the assembler puts `__bad_interrupt` immediately after the
    # two bytes of that jump. `orgs[-1] + stride` is the table's arithmetic size and two bytes
    # more than the image's on any part whose slots are wider than an RJMP. Verified against
    # the linked ELF: `__bad_interrupt` is at 0x66 on the ATmega328P, so the table is 102 and
    # not 104, and at 0x34 on the ATtiny13, where the two agree because the slot IS an RJMP.
    return orgs[-1] + 2


def _avr_stub_bytes(artifacts_dir) -> int | None:
    """The width the backend emitted for the `__bad_interrupt` jump, or None.

    Fallback for a caller with no ELF to measure: the emitted mnemonic is RJMP on
    the parts without JMP/CALL (2 bytes) and JMP on the rest (4). The linker then
    relaxes JMP back to RJMP wherever main is in range -- which is nearly always,
    since only interrupt handlers sit between the stub and it -- so a build that
    produced an ELF takes that figure instead of this one.
    """
    try:
        text = (Path(artifacts_dir) / "firmware.gas.asm").read_text()
    except OSError:
        return None
    seen_label = False
    for line in text.splitlines():
        s = line.strip()
        if not seen_label:
            if s.startswith("__bad_interrupt"):
                seen_label = True
            continue
        if s and not s.startswith(";"):
            return 4 if s.split(None, 1)[0].upper() in ("JMP", "CALL") else 2
    return None


def _elf_text_symbol_addrs(elf_file) -> dict | None:
    """{name: byte address} for the .text symbols of a little-endian ELF32, or None.

    Read straight from the section and symbol tables so the report does not depend
    on an avr-nm the WASI toolchain does not ship. Anything that is not a 32-bit
    little-endian ELF with a findable .text and symtab returns None and the caller
    falls back to the assembly.
    """
    import struct
    try:
        data = Path(elf_file).read_bytes()
        if len(data) < 52 or data[:6] != b"\x7fELF\x01\x01":
            return None
        hdr = struct.unpack_from("<HHIIIIIHHHHHH", data, 16)
        e_shoff, e_shentsize, e_shnum, e_shstrndx = hdr[5], hdr[10], hdr[11], hdr[12]
        if not e_shoff or e_shentsize < 40 or not 0 < e_shstrndx < e_shnum:
            return None
        sections = [struct.unpack_from("<10I", data, e_shoff + i * e_shentsize)
                    for i in range(e_shnum)]

        def cstring(offset: int, base: int) -> bytes:
            start = base + offset
            return data[start:data.index(b"\0", start)]

        shstr_base = sections[e_shstrndx][4]
        text_idx = next(
            (i for i, sh in enumerate(sections)
             if cstring(sh[0], shstr_base) == b".text"),
            None,
        )
        if text_idx is None:
            return None
        addrs = {}
        for sh in sections:
            if sh[1] != 2 or sh[9] < 16:            # SHT_SYMTAB, Elf32_Sym
                continue
            str_base = sections[sh[6]][4]           # sh_link -> its string table
            for off in range(sh[4], sh[4] + sh[5], sh[9]):
                st_name, st_value, _sz, _info, _other, st_shndx = \
                    struct.unpack_from("<IIIBBH", data, off)
                if st_name and st_shndx == text_idx:
                    addrs[cstring(st_name, str_base).decode("utf-8", "replace")] = st_value
        return addrs
    except (IndexError, TypeError, ValueError, struct.error, OSError):
        return None


def _resolve_blockmap(blockmap_path: Path, elf_syms: dict[str, int]) -> None:
    """Fill in WordAddr fields of a backend-emitted blockmap from the linked ELF.

    The backend emits labels with WordAddr=null: link-time relaxation changes
    instruction widths, so only the ELF symtab knows where a label landed.
    *elf_syms* is {name: byte address} from _elf_text_symbol_addrs; a label the
    linker dropped (peephole-deleted, unreachable) stays null and the profiler
    merges its PCs into the preceding block.
    """
    raw = json.loads(blockmap_path.read_text())
    for section in ("Blocks", "Branches"):
        for rec in raw.get(section, []):
            name = rec.get("Label") or rec.get("Sym")
            if name and name in elf_syms:
                rec["WordAddr"] = elf_syms[name] // 2
    blockmap_path.write_text(json.dumps(raw, indent=2))


def _splice_avr_math_runtime(asm_path: Path, asm_content: str, avr_math_path: Path) -> None:
    """Splice the AVR math runtime sources into firmware.asm before assembling.

    The backend emits CALLs to the ``__div*``/``__mod*``/``__mul*`` helpers but
    their bodies live in ``lib/src/pymcu/math/avr/*.S``; pull in exactly the
    files the asm references, inserted before the first function label so the
    helpers stay within RCALL range of their callers. Shared by ``build`` and
    ``profile --pgo`` -- anything assembled straight from the backend output
    needs it or ``avr-ld`` reports the helpers undefined.
    """
    # The signed floor div/mod routines (__divs*/__mods*) build on the unsigned
    # core, so a reference to one of them pulls in the core's file as well.
    runtime_funcs = ["__div8", "__mod8", "__mul8", "__div16", "__mod16", "__div32", "__mod32",
                     "__divs8", "__mods8", "__divs16", "__mods16", "__divs32", "__mods32",
                     "__mul32"]
    needed_funcs = [f for f in runtime_funcs if f in asm_content]
    if not needed_funcs:
        return

    # Which source files each entry point needs. A whole file is spliced in, so
    # anything sharing a file is paid for whether or not it is called: the 32-bit
    # signed pair sits in its own file for that reason, and an unsigned-only
    # program -- the decimal printer among them -- no longer carries its 226
    # bytes. __mod32 is split out to mod32.S for the same reason (PyMCU/PyMCU#408).
    func_map = {
        "__div8": ("div.S",),
        "__mod8": ("div.S",),
        "__divs8": ("div.S",),
        "__mods8": ("div.S",),
        "__mul8": ("mul.S",),
        "__div16": ("div16.S",),
        "__mod16": ("div16.S",),
        "__divs16": ("div16.S",),
        "__mods16": ("div16.S",),
        "__div32": ("div32.S",),
        "__mod32": ("div32.S", "mod32.S"),
        "__divs32": ("div32.S", "div32s.S"),
        "__mods32": ("div32.S", "div32s.S"),
        "__mul32": ("mul32.S",),
    }
    math_runtime_text = "\n; --- PyMCU AVR Math Runtime ---\n"
    included_files = set()
    for func in [f for f in needed_funcs if not f.startswith("__fp")]:
        for fname in func_map.get(func, ()):
            if fname in included_files:
                continue
            src_path = avr_math_path / fname
            if src_path.exists():
                with open(src_path, "r") as lib_f:
                    math_runtime_text += lib_f.read() + "\n"
                included_files.add(fname)
            else:
                console.print(f"[bold yellow]Warning:[/bold yellow] Runtime file {fname} not found")

    # Insert math runtime BEFORE the first function label so that __div8/__mod8
    # are at a low word address, within RCALL range (±2047 words) of any call
    # site in large firmware images.
    with open(asm_path, "r") as f:
        lines = f.readlines()

    insert_idx = len(lines)  # fallback: append
    past_vector_table = False
    org_line_idx = -1
    for i, line in enumerate(lines):
        stripped = line.strip()
        if stripped.startswith(".org"):
            past_vector_table = True
            org_line_idx = i
        elif past_vector_table and stripped and not stripped.startswith(";") \
                and not stripped.startswith(".") \
                and stripped.endswith(":"):
            # First function label after the vector table
            insert_idx = i
            break

    # The peephole optimiser removes "RJMP main" when main: is the very next
    # label in the compiler's internal list (programs with no ISRs). If we are
    # about to insert the math runtime before main: and the reset-vector jump
    # is gone, re-add it so the CPU jumps past the runtime to main at reset.
    if insert_idx < len(lines):
        first_label = lines[insert_idx].strip().rstrip(":")
        if first_label == "main" and org_line_idx >= 0:
            has_reset_jump = any(
                "RJMP\tmain" in lines[j] or "JMP\tmain" in lines[j]
                for j in range(org_line_idx + 1, insert_idx)
            )
            if not has_reset_jump:
                math_runtime_text = "\tRJMP\tmain\n" + math_runtime_text

    lines.insert(insert_idx, math_runtime_text + "\n")
    with open(asm_path, "w") as f:
        f.writelines(lines)


def _avr_preamble_bytes(artifacts_dir) -> int | None:
    """Vector table plus the `__bad_interrupt` stub, or None.

    The stub is the soft-reset jump every unused vector points at -- runtime
    scaffolding, not user code -- so it counts with the table. The linked ELF
    carries its real size: `__bad_interrupt` ends where the first .text symbol
    after it begins (`main` whenever no ISR was emitted ahead of it), which is
    104 on the ATmega328P blink -- table 102, stub a relaxed RJMP of 2 -- and two
    more on a part whose JMP could not relax. With no ELF the assembly gives the
    table and the stub's emitted width instead.
    """
    if artifacts_dir is None:
        return None
    addrs = _elf_text_symbol_addrs(Path(artifacts_dir) / "debug" / "firmware.elf")
    if addrs is not None and "__bad_interrupt" in addrs:
        following = min((a for a in addrs.values() if a > addrs["__bad_interrupt"]),
                        default=None)
        if following is not None:
            return following
    table = _avr_vector_table_bytes(artifacts_dir)
    stub = _avr_stub_bytes(artifacts_dir)
    if table is None or stub is None:
        return None
    return table + stub


def _check_flash_capacity(flash_bytes: int, flash_total: int, target: str) -> None:
    """Refuse to call an over-capacity image a successful build.

    The generated linker script declares no MEMORY regions, so ld never
    errors on overflow; without this gate a 33 KB image for a 32 KB chip
    reported "100%" and built "successfully", then failed mysteriously at
    flash time - on an Uno it would also invade the bootloader section.
    """
    if flash_total and flash_bytes > flash_total:
        over = flash_bytes - flash_total
        console.print(
            f"[bold red]Error:[/bold red] firmware is {flash_bytes} bytes but "
            f"{target} has {flash_total} bytes of flash ({over} bytes over). "
            "Reduce code size or pick a larger chip."
        )
        raise typer.Exit(code=1)


def _flash_report_lines(flash_bytes: int, flash_total: int, target: str,
                        artifacts_dir=None) -> list[str]:
    """Render the size report: the whole image, and how much of it is user code."""
    if flash_total:
        pct = flash_bytes * 100 // flash_total
        head = (f"[dim]Flash:[/dim] {flash_bytes} / {flash_total} bytes "
                f"({pct}% of program storage)")
    else:
        head = f"[dim]Flash:[/dim] {flash_bytes} bytes"

    lines = [head]
    if target.lower().startswith("at"):
        preamble = _avr_preamble_bytes(artifacts_dir)
        if preamble is None:
            preamble = _AVR_PREAMBLE_BYTES
        if flash_bytes > preamble:
            lines.append(
                f"[dim]       {flash_bytes - preamble} bytes of your code + "
                f"{preamble} bytes of interrupt vector table[/dim]"
            )
    return lines

def _print_explain(output_dir) -> None:
    """--explain: everything the build did on the user's behalf, as one summary.

    Two sources, both already produced by a normal build:
      - the "# Auto-injected by pymcu build: ..." comments in the synthetic entry
        (dist/_generated/main.py) document each preamble injection at the moment
        it happens;
      - dist/firmware.mir carries the compiled facts (ISR registrations with their
        vectors, ISR-shared globals given volatile semantics).
    """
    import json as _json
    from pathlib import Path as _Path

    lines: list[str] = []

    entry = _Path(output_dir) / "_generated" / "main.py"
    if entry.exists():
        for raw in entry.read_text().splitlines():
            if raw.startswith("# Auto-injected by pymcu build:"):
                lines.append(raw.removeprefix("# Auto-injected by pymcu build:").strip())

    mir = _Path(output_dir) / "firmware.mir"
    if mir.exists():
        try:
            prog = _json.loads(mir.read_text())
        except Exception:
            prog = {}
        for fn in prog.get("functions", []):
            if fn.get("isInterrupt"):
                lines.append(
                    f"'{fn.get('originalName', fn['name'])}' compiled as an ISR "
                    f"(interrupt vector {fn.get('interruptVector')}, word address); "
                    "keep it short and don't block"
                )
        shared = prog.get("isrSharedGlobals", [])
        if shared:
            lines.append(
                "shared between ISR and main, made volatile (GPIOR-promoted when "
                "possible): " + ", ".join(shared)
            )

    if not lines:
        console.print("\n[bold]Implicit in this build:[/bold] nothing -- everything "
                      "your firmware does is written in your source.")
        return

    console.print("\n[bold]Implicit in this build:[/bold]")
    for entry_line in lines:
        console.print(f"  [cyan]-[/cyan] {entry_line}")


def build(
    verbose: bool = typer.Option(False, "--verbose", "-v", help="Enable verbose logging"),
    stdlib_override: Optional[List[str]] = typer.Option(
        None, "--stdlib",
        help="Override stdlib flavor(s) from pyproject.toml (e.g. --stdlib micropython). "
             "Can be specified multiple times.",
    ),
    debug: bool = typer.Option(False, "--debug", help="Emit debug symbols and line map for the emulator debugger"),
    profile: Optional[str] = typer.Option(
        None, "--profile",
        help="Compile with a PGO profile (a profile.json from 'pymcu profile --pgo'). "
             "Also read from PYMCU_PROFILE.",
    ),
    explain: bool = typer.Option(
        False, "--explain",
        help="After the build, list everything that happened implicitly: injected "
             "setup (stdout UART, millis timer, clocks), ISR registrations with their "
             "vectors, and ISR-shared globals. Turns the build's magic into a lesson.",
    ),
):
    is_verbose = verbose or os.environ.get("PYMCU_VERBOSE") == "1"
    _diag_log("=== BUILD COMMAND STARTED ===", verbose=is_verbose)
    _diag_log(f"Working directory: {os.getcwd()}", verbose=is_verbose)
    _diag_log(f"sys.executable: {sys.executable}", verbose=is_verbose)
    _diag_log(f"sys.prefix: {sys.prefix}", verbose=is_verbose)
    _diag_log(f"sys.version: {sys.version}", verbose=is_verbose)
    _diag_log(f"sys.path: {sys.path}", verbose=is_verbose)
    _diag_log(f"VIRTUAL_ENV: {os.environ.get('VIRTUAL_ENV', 'NOT SET')}", verbose=is_verbose)
    _diag_log(f"PATH: {os.environ.get('PATH', 'NOT SET')}", verbose=is_verbose)
    _diag_log(f"PYTHONPATH: {os.environ.get('PYTHONPATH', 'NOT SET')}", verbose=is_verbose)

    if is_verbose:
        console.print("\\[debug] === Build command started ===", style="dim cyan")
        console.print(f"\\[debug] Current working directory: {os.getcwd()}", style="dim")
        console.print(f"\\[debug] sys.executable: {sys.executable}", style="dim")
        console.print(f"\\[debug] sys.prefix: {sys.prefix}", style="dim")
        console.print(f"\\[debug] VIRTUAL_ENV: {os.environ.get('VIRTUAL_ENV', 'NOT SET')}", style="dim")
        console.print(f"\\[debug] PATH: {os.environ.get('PATH', 'NOT SET')}", style="dim")

    pyproject_path = Path("pyproject.toml")
    _diag_log(f"Looking for pyproject.toml at: {pyproject_path.absolute()}", verbose=is_verbose)
    _diag_log(f"pyproject.toml exists: {pyproject_path.exists()}", verbose=is_verbose)
    if not pyproject_path.exists():
        _diag_log("ERROR: pyproject.toml NOT FOUND", verbose=is_verbose)
        console.print("[red]No pyproject.toml found. Are you in a pymcu project?[/red]")
        raise typer.Exit(code=1)

    try:
        _diag_log("Reading pyproject.toml...", verbose=is_verbose)
        with open(pyproject_path, "r") as f:
            config = tomlkit.load(f)

        _diag_log("pyproject.toml loaded successfully", verbose=is_verbose)
        pymcu_config = config.get("tool", {}).get("pymcu", {})
        _diag_log(f"pymcu_config keys: {list(pymcu_config.keys())}", verbose=is_verbose)

        # PGO is experimental (RFC 0010): 'pgo = true' under
        # [tool.pymcu.experimental], or PYMCU_EXPERIMENTAL_PGO=1. Asking for a
        # profiled build with the flag off stops here, before anything is built.
        # --profile wins over PYMCU_PROFILE; a named file that does not exist is
        # an error rather than a silent unprofiled build, for the same reason
        # PYMCU_BACKEND_BINARY refuses to fall back.
        pgo_enabled = experimental_enabled(pymcu_config, "pgo")
        profile_path = profile or os.environ.get("PYMCU_PROFILE") or None
        if profile_path is not None:
            if not pgo_enabled:
                console.print(
                    "[bold red]Error:[/bold red] PGO is experimental: set 'pgo = true' "
                    "under \\[tool.pymcu.experimental] in pyproject.toml "
                    "(or PYMCU_EXPERIMENTAL_PGO=1) to use --profile/PYMCU_PROFILE.")
                raise typer.Exit(code=1)
            if not Path(profile_path).exists():
                console.print(f"[bold red]Error:[/bold red] profile not found: {profile_path}")
                raise typer.Exit(code=1)

        target_key   = pymcu_config.get("target", None)
        _diag_log(f"target_key from config: {target_key}", verbose=is_verbose)

        # Compatibility: accept legacy "chip" key with a deprecation warning
        if target_key is None and pymcu_config.get("chip"):
            target_key = pymcu_config.get("chip")
            _diag_log(f"Using legacy 'chip' key: {target_key}", verbose=is_verbose)
            console.print(
                "[bold yellow]Deprecation:[/bold yellow] 'chip' in \\[tool.pymcu] is deprecated. "
                "Rename it to 'target'."
            )
        board_key    = pymcu_config.get("board", None)
        _diag_log(f"board_key from config: {board_key}", verbose=is_verbose)
        # Resolved once the target is known: a fixed 4 MHz was the PIC16F84A's
        # default and silently ran an Arduino at a quarter speed, skewing every
        # delay and UART divisor. None means "decide from the target", below.
        freq         = pymcu_config.get("frequency", None)
        src_path     = pymcu_config.get("sources", "src")
        _diag_log(f"freq: {freq}, src_path: {src_path}", verbose=is_verbose)

        # Resolve stdlib flavors: CLI --stdlib overrides pyproject.toml
        stdlib_flavors: list[str] = (
            list(stdlib_override)
            if stdlib_override
            else list(pymcu_config.get("stdlib", []))
        )
        # Two flavors at once used to "work": both directories went on the
        # include path and whichever came first in the list won every clash.
        # They do clash -- `time.sleep` takes a uint16 in the MicroPython layer
        # and a float in the CircuitPython one, and boards/arduino_uno.py
        # defines D0 as 0 in one and "PD0" in the other -- so the winner
        # silently decided the semantics of the program.
        if len(stdlib_flavors) > 1:
            console.print(
                f"[bold red]Error:[/bold red] stdlib declares more than one compat "
                f"layer ({', '.join(stdlib_flavors)}).\n"
                "  The layers are not interoperable: they define the same module "
                "names with different APIs.\n"
                "  Pick one in \\[tool.pymcu], or pass a single --stdlib."
            )
            raise typer.Exit(code=1)

        extension_board_chips: dict[str, str] = {}
        extra_includes: list[str] = []
        extension_board_dirs: dict[str, Path] = {}  # flavor -> boards/ dir
        flavor_dirs: dict[str, Path] = {}           # flavor -> package dir

        # stdlib_path: inject a local stdlib directory before any installed package
        stdlib_path_override: str | None = pymcu_config.get("stdlib_path", None)
        if stdlib_path_override:
            resolved_stdlib_path = (pyproject_path.parent / stdlib_path_override).resolve()
            if resolved_stdlib_path.is_dir():
                extra_includes.append(str(resolved_stdlib_path))
                _diag_log(f"stdlib_path override: {resolved_stdlib_path}", verbose=is_verbose)
            else:
                console.print(
                    f"[bold yellow]Warning:[/bold yellow] stdlib_path '{stdlib_path_override}' "
                    f"not found at {resolved_stdlib_path}."
                )

        for flavor in stdlib_flavors:
            spec = importlib.util.find_spec(f"pymcu_{flavor}")
            if spec and spec.submodule_search_locations:
                pkg_dir = Path(list(spec.submodule_search_locations)[0])
                pkg_parent = pkg_dir.parent
                # The layer's own directory before its parent: site-packages
                # must stay on the path for `import pymcu_<flavor>.sub`, but a
                # flat module there shadows the layer's names if it wins the
                # clash. Adafruit-Blinka's top-level board.py / digitalio.py /
                # busio.py -- pulled in by every adafruit-circuitpython-* dist
                # -- used to be picked over the CircuitPython layer's own
                # modules, and the build then failed inside the shim's
                # `import json` rather than in anything the program wrote.
                extra_includes.append(str(pkg_dir))
                extra_includes.append(str(pkg_parent))
                flavor_dirs[flavor] = pkg_dir
                # Collect board_chips supplements
                extension_board_chips.update(_load_extension_board_chips(flavor))
                # Record boards/ dir for shim generation
                boards_dir = pkg_dir / "boards"
                if boards_dir.is_dir():
                    extension_board_dirs[flavor] = boards_dir
            else:
                # The flavor is imported from the project's environment, not
                # from wherever the CLI happens to live. Under pipx those are
                # different interpreters, and `pip install pymcu-<flavor>`
                # installs into neither of them -- the reported symptom was
                # doing exactly that and seeing the build fail unchanged.
                console.print(
                    f"[bold yellow]Warning:[/bold yellow] stdlib flavor "
                    f"'pymcu_{flavor}' is not installed in this project's environment.\n"
                    f"  Install the project's dependencies, then build again:\n"
                    f"    [bold]{_install_deps_hint(pyproject_path.parent)}[/bold]"
                )

        # Derive target from board or fall back to explicit target / default
        if board_key:
            target = _resolve_chip_for_board(board_key, extension_board_chips)
            if target is None:
                near = suggest_boards(board_key, extension_board_chips)
                hint = (f" Did you mean '{near[0]}'?" if len(near) == 1
                        else f" Close names: {', '.join(near)}." if near
                        else "")
                console.print(
                    f"[bold red]Error:[/bold red] Unknown board '{board_key}'.{hint}\n"
                    "  [dim]`pymcu boards` lists what this installation supports.[/dim]\n"
                    "  [dim]An extension package adds its own in board_chips.py.[/dim]"
                )
                raise typer.Exit(code=1)

            # Both keys set. Checked here rather than where they are read, because the implied
            # target is the whole content of the sentence and it is not known until the
            # extension board tables above are loaded. Printed as "?" it sent the reader to
            # delete the `target` line that was correct, and the real error, a board name that
            # resolves to nothing, appeared only after they had (#198).
            if target_key:
                console.print(
                    f"[bold red]Error:[/bold red] Cannot set both 'target' and 'board' in \\[tool.pymcu].\n"
                    f"  'board = \"{board_key}\"' implies target = \"{target}\". Remove the 'target' key."
                )
                raise typer.Exit(code=1)
        elif target_key:
            target = target_key
        else:
            # Defaulting here used to produce a PIC16F84A image. That is a
            # leftover from when this compiler only had a PIC backend, and it is
            # the worst failure mode available: the build succeeds, prints a
            # flash figure, and hands over firmware for hardware nobody named.
            # An unknown board already exits; an absent one now does too.
            console.print(
                "[bold red]Error:[/bold red] No 'board' or 'target' in \\[tool.pymcu].\n"
                "  Add [bold]board = \"arduino_uno\"[/bold] for a known board, or\n"
                "  [bold]target = \"atmega328p\"[/bold] for a bare chip.\n"
                "  [dim]`pymcu boards` lists what this installation supports.[/dim]"
            )
            raise typer.Exit(code=1)

        if freq is None:
            freq = board_frequency(board_key) if board_key else default_frequency(target)
            _diag_log(f"freq defaulted from target: {freq}", verbose=is_verbose)

        # Third-party libraries, discovered through the pymcu.libraries entry
        # point.  Added after the flavor packages so no library can shadow
        # `machine` or `digitalio`, and only when they apply to this target: a
        # library that cannot serve this chip is skipped with a reason instead
        # of failing later inside the compiler.
        # PYMCU_LIBRARY_FILTER=0 puts every installed library on the include
        # path regardless of what it declares.  The index generator sets it so
        # the compiler, not the manifest, decides what builds where.
        libs, skipped_libs, lib_errors = resolve_for_target(
            target, stdlib_flavors,
            search_path=library_search_path(pyproject_path.parent.absolute()),
            enforce=os.environ.get("PYMCU_LIBRARY_FILTER") != "0",
        )
        if lib_errors:
            # Reported, not fatal. These are other people's packages: one
            # library with a broken manifest used to stop every build in the
            # environment, including builds that never import it. The library
            # is left off the include path instead, so a project that does
            # import it fails on the import itself -- with this warning
            # already on screen to say why.
            console.print("[bold yellow]Warning:[/bold yellow] installed libraries "
                          "the compiler cannot use:")
            for problem in lib_errors:
                console.print(f"  {problem}")

        for note in skipped_libs:
            console.print(f"[bold yellow]Skipping library[/bold yellow] {note}")

        for note in library_layer_shadowing(libs, flavor_dirs):
            console.print(f"[bold yellow]Warning:[/bold yellow] {note}")

        extra_includes.extend(library_include_paths(libs, stdlib_flavors))
        for lib in libs:
            _diag_log(
                f"library {lib.name} {lib.version} -> {', '.join(lib.modules)}",
                verbose=is_verbose,
            )

        project_root = pyproject_path.parent.absolute()
        sources_dir = (project_root / src_path).resolve()

        entry_file_name = pymcu_config.get("entry", "main.py")
        entry_point = (sources_dir / entry_file_name).resolve()

        output_dir = project_root / "dist"
        output_file = output_dir / "firmware.asm"

        if not entry_point.exists():
            console.print(f"[red]Entry point not found at: {entry_point}[/red]")
            console.print(f"[yellow]Check 'sources' and 'entry' in pyproject.toml (current: sources={src_path}, entry={entry_file_name})[/yellow]")
            raise typer.Exit(code=1)
        
        config_map = {}
        tool_config = pymcu_config.get("config", {})
        for key, val in tool_config.items():
            config_map[str(key)] = str(val)

        # Read vector configuration for bootloader support
        vectors_config = pymcu_config.get("vectors", {})
        reset_vector = vectors_config.get("reset", None)
        interrupt_vector = vectors_config.get("interrupt", None)

        if not output_dir.exists():
            output_dir.mkdir(parents=True)

        # Upstream libraries: a plain PyPI distribution the (cached) library
        # index vouches for and measures, with no pymcu.toml of its own. Added
        # after the manifest libraries above, so neither can shadow `board`,
        # `digitalio`, `pulseio` or a curated library, and staged into
        # dist/_upstream rather than pointed at site-packages directly (see
        # core/upstream_libraries.py for why).
        upstream_includes, upstream_skipped, upstream_errors = resolve_upstream_for_target(
            search_path=library_search_path(pyproject_path.parent.absolute()),
            flavors=stdlib_flavors,
            stage_root=output_dir / "_upstream",
            enforce=os.environ.get("PYMCU_LIBRARY_FILTER") != "0",
        )
        for note in upstream_skipped:
            console.print(f"[bold yellow]Skipping upstream library[/bold yellow] {note}")
        for problem in upstream_errors:
            console.print(f"[bold yellow]Warning:[/bold yellow] upstream library {problem}")
        extra_includes.extend(upstream_includes)

        # Shared generated-files directory (board shim + print preamble).
        generated_dir = output_dir / "_generated"

        # Generate dist/_generated/board.py shim when board= is set.
        # This shim is prepended to -I so `import board` finds it first.
        if board_key:
            generated_dir.mkdir(parents=True, exist_ok=True)
            board_shim = generated_dir / "board.py"

            # Find which extension (if any) has boards/<board>.py.
            # We copy the board file content directly into board.py so that
            # `import board` works without star-import (not supported by pymcuc).
            src_board_file = None
            for flavor, boards_dir in extension_board_dirs.items():
                candidate = boards_dir / f"{board_key}.py"
                if candidate.exists():
                    src_board_file = candidate
                    break

            if src_board_file:
                board_shim_content = (
                    f"# Auto-generated by pymcu build -- do not edit\n"
                    + src_board_file.read_text()
                )
            else:
                # Vanilla fallback: copy the stdlib board file directly. Located
                # via find_spec because `pymcu` is a namespace package whose
                # __file__ is None (several distributions contribute pymcu.*).
                try:
                    from importlib.util import find_spec
                    spec = find_spec(f"pymcu.boards.{board_key}")
                    if spec is None or spec.origin is None:
                        raise FileNotFoundError(board_key)
                    board_shim_content = (
                        "# Auto-generated by pymcu build -- do not edit\n"
                        + Path(spec.origin).read_text()
                    )
                except Exception:
                    # Only worth saying when the program actually imports
                    # `board`. A MicroPython project on the Pico addresses pins
                    # through machine.Pin and never touches it, so this fired
                    # on every single build of a perfectly good project --
                    # board files exist for the CircuitPython flavor and not
                    # the MicroPython one. A warning that is always there is a
                    # warning people learn to scroll past.
                    if _imports_board(sources_dir):
                        console.print(
                            f"[bold yellow]Warning:[/bold yellow] No board file found for "
                            f"'{board_key}'.\n"
                            f"  `import board` will not resolve its pin names. This board "
                            f"has no definition for the stdlib flavor in use."
                        )
                    board_shim_content = f"# Auto-generated by pymcu build -- no board file found for {board_key}\n"

            board_shim.write_text(board_shim_content)
            # Prepend generated dir so `import board` finds the shim first
            extra_includes.insert(0, str(generated_dir))

        # Auto-inject stdout preamble when print() or input() is used without an
        # explicit UART() constructor in user sources.  This mirrors MicroPython's
        # REPL behaviour where the output device is pre-initialized before user
        # code runs, so print()/input() work out of the box with no extra imports.
        # The output device is configurable via [tool.pymcu] stdout / stdout_baud.
        _linemap_preamble_offset = 0
        _original_entry_point = entry_point

        _has_print, _has_uart, _has_input = _detect_print_usage(sources_dir)
        if (_has_print or _has_input) and not _has_uart:
            _stdout_device, _stdout_baud = _get_stdout_config(pymcu_config)
            entry_point, _n = _inject_print_preamble(
                entry_point, generated_dir, _stdout_device, _stdout_baud
            )
            _linemap_preamble_offset += _n
            if str(generated_dir) not in extra_includes:
                extra_includes.insert(0, str(generated_dir))
            _trigger = "print()" if _has_print else "input()"
            if _has_print and _has_input:
                _trigger = "print() and input()"
            _diag_log(
                f"{_trigger} detected without UART() — injecting stdout preamble "
                f"({_stdout_device} at {_stdout_baud} baud)",
                verbose=is_verbose,
            )
            if is_verbose:
                console.print(
                    f"\\[debug] {_trigger} without UART — stdout preamble injected "
                    f"({_stdout_device} at {_stdout_baud} baud)",
                    style="dim",
                )
        elif _has_print and _has_uart:
            # User drives their own UART but also calls print(): load the console
            # streaming functions (no init -- the user's UART() owns the hardware).
            entry_point, _n = _inject_print_imports_only(entry_point, generated_dir)
            _linemap_preamble_offset += _n
            if str(generated_dir) not in extra_includes:
                extra_includes.insert(0, str(generated_dir))
            _diag_log("print() + user UART() — injecting console functions (no init)",
                      verbose=is_verbose)
        elif _detect_raise_with_message(sources_dir):
            # No print()/input() anywhere, but some raise carries a message: the
            # unhandled report prints `E:<Type>: <msg>` through the same console
            # string writers, so they must be linked even though nothing calls
            # print. No UART init -- the exception runtime programs the
            # transmitter itself when the program does not own it (uart_owned
            # stays false), and must not reprogram a live one when it does.
            entry_point, _n = _inject_print_imports_only(entry_point, generated_dir)
            _linemap_preamble_offset += _n
            if str(generated_dir) not in extra_includes:
                extra_includes.insert(0, str(generated_dir))
            _diag_log("raise with message detected — injecting console functions "
                      "(no init)", verbose=is_verbose)

        # Auto-inject the strfmt helpers when an f-string is assigned to a variable
        # (f-string-as-value lowering resolves pymcu.strfmt by import alias).
        if _detect_fstring_value_usage(sources_dir):
            entry_point, _n = _inject_strfmt_preamble(entry_point, generated_dir)
            _linemap_preamble_offset += _n
            if str(generated_dir) not in extra_includes:
                extra_includes.insert(0, str(generated_dir))
            _diag_log("f-string value assignment detected — injecting pymcu.strfmt import",
                      verbose=is_verbose)

        # round(x, n) on a run-time float: NOT detected here (see the comment on
        # _inject_round2_preamble above) -- the compiler's own [NEEDS_ROUND2] token
        # drives the injection, in _compile_frontend's retry loop below.

        # Auto-inject millis_init() preamble when ticks_ms() is used, or when an
        # ATmega program uses async/await (asyncio.ticks() is the same Timer0
        # micros counter and reads a frozen 0 until the overflow ISR is armed).
        # millis_init() must run before the first read; injecting it here mirrors
        # how UART is set up for print().  Skipped when the sources already call
        # millis_init() themselves -- registering the OVF vector twice is an error.
        _millis_reason = ""
        if _detect_ticks_ms_usage(sources_dir):
            _millis_reason = "ticks_ms()"
        elif target.lower().startswith("atmega") and _detect_async_def_usage(sources_dir):
            _millis_reason = "async def (asyncio.ticks)"
        # Either way the time base runs, and the compiler is told so (see
        # PymcuCompiler.compile(timebase=...)).
        _timebase = bool(_millis_reason) or _sources_call(sources_dir, "millis_init")
        if _millis_reason and not _sources_call(sources_dir, "millis_init"):
            entry_point, _n = _inject_ticks_ms_preamble(entry_point, generated_dir, _millis_reason)
            _linemap_preamble_offset += _n
            if str(generated_dir) not in extra_includes:
                extra_includes.insert(0, str(generated_dir))
            _diag_log(
                f"{_millis_reason} detected — injecting millis_init() preamble "
                "(Timer0 OVF @ prescaler 64)",
                verbose=is_verbose,
            )
            if is_verbose:
                console.print(
                    f"\\[debug] {_millis_reason} detected — millis_init() preamble injected",
                    style="dim",
                )

        # Auto-inject clock_init() for the RP2350 so clk_sys is 150 MHz and the system
        # timer ticks at an exact 1 MHz -- mirrors the pico-sdk runtime (runtime_init_clocks)
        # which does the same before main(). Injected LAST so clock_init() runs first, ahead
        # of any stdout/ticks preamble that depends on the final clk_sys / clk_peri. Skipped
        # if the user already calls clock_init() (idempotent, but avoids a redundant pass).
        if target == "rp2350" and not _sources_call(sources_dir, "clock_init"):
            entry_point, _n = _inject_clock_init_preamble(entry_point, generated_dir)
            _linemap_preamble_offset += _n
            if str(generated_dir) not in extra_includes:
                extra_includes.insert(0, str(generated_dir))
            _diag_log("rp2350 target — injecting clock_init() (150 MHz + 1 MHz timer tick)",
                      verbose=is_verbose)

        # What the compiler will see versus what the user wrote. Every preamble injection
        # above replaced entry_point with a synthetic file under dist/_generated and shifted
        # the line numbers; without this map a diagnostic sends the reader into their own
        # build output, at a line that says something else.
        # Read back off the generated file, which is the only place that knows where every
        # one of the four possible preambles actually landed.
        _preamble_map = (
            _preamble_line_map(entry_point)
            if _linemap_preamble_offset > 0 and str(entry_point) != str(_original_entry_point)
            else None
        )
        _diagnostic_source = (
            (str(entry_point), str(_original_entry_point), _preamble_map)
            if _preamble_map is not None
            else None
        )

        # RFC 0008 -- resolve which files open() can name at compile time. Runs
        # against the ORIGINAL entry point: discovery scans the user's sources,
        # not any synthetic preamble staged into dist/_generated.
        _embedded_files = _resolve_embed_files(
            project_root, sources_dir, _original_entry_point, pymcu_config)
        for _name, _path, _size in _embedded_files:
            console.print(f"Embedded: {_name}, {_size} bytes")
            _diag_log(f"romfs: {_name} <- {_path} ({_size} B)", verbose=is_verbose)

        # Detect C interop: [tool.pymcu.ffi] sources = [...]
        ffi_config = pymcu_config.get("ffi", {})
        ffi_sources_raw: list[str] = list(ffi_config.get("sources", []))
        use_ffi = bool(ffi_sources_raw)

        # 1. Factory: Get the appropriate toolchain strategy.
        # When [tool.pymcu.ffi] sources are declared the GNU binutils pipeline
        # (avr-as + avr-ld + avr-objcopy) is used.
        if use_ffi:
            try:
                toolchain = get_ffi_toolchain_for_chip(target, console)
            except ValueError as e:
                console.print(f"[bold red]Error:[/bold red] {e}")
                raise typer.Exit(code=1)
        else:
            toolchain = get_toolchain_for_chip(target, console)

        # 2. Interactive Install Check (BEFORE Progress Bar)
        if not toolchain.is_cached():
            try:
                toolchain.install()
            except RuntimeError as e:
                console.print(f"[bold red]Toolchain installation failed:[/bold red] {e}")
                raise typer.Exit(code=1)

        # 3. Core Compiler Wrapper
        compiler = PyMCUCompiler(console)

        with Progress(
            SpinnerColumn(),
            TextColumn("[progress.description]{task.description}"),
            BarColumn(),
            TimeElapsedColumn(),
            transient=False,
            console=console
        ) as progress:
            
            build_task = progress.add_task(description=f"  [cyan]Building[/cyan] {target}...", total=100)

            # Step 1: Compilation (Python -> ASM)
            # When a backend plugin is installed for this chip, use the two-phase
            # approach: pymcuc --emit-ir (frontend only) then backend binary (codegen).
            # Otherwise fall back to single-step compilation (non-AVR backends).
            # Progress 10-50% is driven by PHASE_START/PHASE_END tokens from pymcuc.
            progress.update(build_task, description="  [cyan]Compiling[/cyan]...", completed=10)
            compiler_handler = _make_compiler_output_handler(progress, build_task, verbose)
            backend_plugin = get_backend_for_chip(target)
            blockmap_path: Path | None = None

            # docs/rfcs/0004-arena-allocator.md: whether the program allocates from the
            # arena is the COMPILER's call, not a source-text scan's -- only the IR
            # generator knows whether the bytearray() size argument actually folded (a
            # foldable `bytearray(((h // 8) * w) + 1)` is a fixed SRAM array and reserves
            # nothing). pymcuc reports on its stdout token stream:
            #   [NEEDS_ARENA] -- a runtime-sized allocation met a missing pymcu.arena
            #                    import; the compile fails (ArenaRequiredError)
            #   [ARENA_USED]  -- a runtime-sized allocation lowered against an import the
            #                    program wrote itself; the compile succeeds but ran with
            #                    the shipped module's ARENA_SIZE of 0
            # Either way the answer is the same: stage the shim + import and run the
            # frontend once more. The retry re-reads entry_point / extra_includes /
            # _diagnostic_source, which the injection updates in place.
            _arena_size_override = pymcu_config.get("arena_size", None)

            def _inject_arena() -> None:
                nonlocal entry_point, _linemap_preamble_offset
                nonlocal _preamble_map, _diagnostic_source
                _reserved = _arena_size_override or _ARENA_BOARD_DEFAULT_BYTES
                _inject_arena_shim(generated_dir, _reserved)
                entry_point, _n = _inject_arena_preamble(entry_point, generated_dir)
                _linemap_preamble_offset += _n
                if str(generated_dir) not in extra_includes:
                    extra_includes.insert(0, str(generated_dir))
                _preamble_map = _preamble_line_map(entry_point)
                _diagnostic_source = (
                    str(entry_point), str(_original_entry_point), _preamble_map)
                _diag_log(
                    "compiler reported an arena allocation — injecting pymcu.arena "
                    f"import (reserving {_reserved} B)", verbose=is_verbose)
                if _arena_size_override is not None:
                    console.print(
                        f"Arena: reserved {_reserved} B (arena_size override in "
                        "\\[tool.pymcu])")
                else:
                    console.print(
                        f"Arena: reserved {_reserved} B (board default; at least one "
                        "allocation is runtime-sized and could not be sized exactly -- "
                        "set arena_size in \\[tool.pymcu] to reserve a precise amount)")

            # RFC 0014 decision 5: pymcu.strfmt (a run-time string build -- an f-string
            # value, str()/repr()/hex()/bin()/oct() of a run-time value) and pymcu.round2
            # (round(x, n) on a run-time float) are the same story as the arena above --
            # only the IR generator knows a given call actually needs the helper, so it
            # reports [NEEDS_STRFMT] / [NEEDS_ROUND2] on the token stream and the driver
            # injects the import and compiles again. Neither needs a generated shim (no
            # per-build parameter the way ARENA_SIZE is), so there is no "_USED" token
            # to answer on an already-successful compile -- unlike the arena.

            def _inject_strfmt() -> None:
                nonlocal entry_point, _linemap_preamble_offset
                nonlocal _preamble_map, _diagnostic_source
                entry_point, _n = _inject_strfmt_preamble(entry_point, generated_dir)
                _linemap_preamble_offset += _n
                if str(generated_dir) not in extra_includes:
                    extra_includes.insert(0, str(generated_dir))
                _preamble_map = _preamble_line_map(entry_point)
                _diagnostic_source = (
                    str(entry_point), str(_original_entry_point), _preamble_map)
                _diag_log(
                    "compiler reported a run-time string build -- injecting "
                    "pymcu.strfmt import", verbose=is_verbose)

            def _inject_round2() -> None:
                nonlocal entry_point, _linemap_preamble_offset
                nonlocal _preamble_map, _diagnostic_source
                entry_point, _n = _inject_round2_preamble(entry_point, generated_dir)
                _linemap_preamble_offset += _n
                if str(generated_dir) not in extra_includes:
                    extra_includes.insert(0, str(generated_dir))
                _preamble_map = _preamble_line_map(entry_point)
                _diagnostic_source = (
                    str(entry_point), str(_original_entry_point), _preamble_map)
                _diag_log(
                    "compiler reported round(x, n) on a float -- injecting "
                    "pymcu.round2 import", verbose=is_verbose)

            # One injector per requirement the compiler can ask for. Each fires at most
            # once per build (the retry re-resolves the same call against the now-present
            # import, which does not ask again), so the loop below is bounded by the
            # injector count plus the final successful attempt -- a program that somehow
            # needed the arena, then strfmt, then round2 still converges in four tries.
            _injectors = {
                ArenaRequiredError: _inject_arena,
                StrfmtRequiredError: _inject_strfmt,
                Round2RequiredError: _inject_round2,
            }

            def _compile_frontend(with_ir: bool, ir_file: Path | None = None) -> None:
                def _run() -> None:
                    compiler.compile(
                        input_file=entry_point,
                        output_file=str(output_file),
                        target=target,
                        freq=freq,
                        configs=config_map,
                        search_path=sources_dir,
                        verbose=verbose,
                        reset_vector=reset_vector,
                        interrupt_vector=interrupt_vector,
                        extra_includes=extra_includes or None,
                        on_output=compiler_handler,
                        timebase=_timebase,
                        stdlib_flavor=stdlib_flavors[0] if stdlib_flavors else "",
                        embed_files=_embedded_files,
                        profile_path=profile_path,
                        **({"emit_ir_path": str(ir_file),
                            "diagnostic_source": _diagnostic_source} if with_ir else {}),
                    )
                for _attempt in range(len(_injectors) + 1):
                    try:
                        _run()
                    except tuple(_injectors) as exc:
                        _injectors[type(exc)]()
                        continue
                    if compiler.last_compile_used_arena:
                        _inject_arena()
                        _run()
                    return
                # Unreachable unless two injectors keep undoing each other's fix, which
                # is a compiler bug, not a program to diagnose here -- run once more and
                # let the real exception surface rather than swallowing it silently.
                _run()

            try:
                if backend_plugin is not None:
                    ir_file = output_dir / "firmware.mir"
                    _compile_frontend(with_ir=True, ir_file=ir_file)
                    progress.update(build_task, description="  [cyan]Code Generation[/cyan]...", completed=40)
                    linemap_path: Path | None = None
                    varmap_path: Path | None = None
                    if debug:
                        debug_dir = output_dir / "_debug"
                        debug_dir.mkdir(parents=True, exist_ok=True)
                        linemap_path = debug_dir / "linemap.json"
                        varmap_path  = debug_dir / "varmap.json"
                        # The block map feeds `pymcu profile --pgo`; with the
                        # experimental flag off the backend never sees the flag.
                        if pgo_enabled:
                            blockmap_path = debug_dir / "blockmap.json"
                    run_backend(
                        backend_binary=binary_for_plugin(backend_plugin),
                        ir_file=ir_file,
                        output_file=output_file,
                        target=target,
                        freq=freq,
                        configs=config_map,
                        reset_vector=reset_vector,
                        interrupt_vector=interrupt_vector,
                        verbose=verbose,
                        on_output=compiler_handler,
                        emit_linemap_path=linemap_path,
                        emit_varmap_path=varmap_path,
                        emit_blockmap_path=blockmap_path,
                        profile_path=Path(profile_path) if profile_path else None,
                        stdout_baud=_get_stdout_config(pymcu_config)[1],
                        uart_owned=_has_uart or _has_print or _has_input,
                    )
                    # Correct linemap line numbers when preamble was injected.
                    # The compiler saw the synthetic file (with prepended lines),
                    # so all recorded line numbers are shifted by the preamble size.
                    if linemap_path and linemap_path.exists() and _preamble_map is not None:
                        _correct_linemap(linemap_path, "main.py", _preamble_map)
                else:
                    _compile_frontend(with_ir=False)
            except RuntimeError as e:
                progress.stop()
                console.print(f"[bold red]Compilation Error:[/bold red] {e}")
                raise typer.Exit(code=1)
                
            progress.update(build_task, completed=50)
            
            # Step 1.5: Library Injection (Float Support & AVR Math)
            with open(output_file, "r") as asm_f:
                asm_content = asm_f.read()
            
            spec = importlib.util.find_spec("pymcu.math")
            
            if spec and spec.origin:
                math_lib_path = Path(spec.origin).parent
                
                # PIC Float Support
                if '#include "float.inc"' in asm_content:
                    progress.update(build_task, description="Injecting Float Library...")
                    pic_arch = "pic16" # Default for PIC10/12/16
                    if target.lower().startswith("pic18"):
                        pic_arch = "pic18"

                    src_inc = math_lib_path / pic_arch / "float.inc"
                    dst_inc = output_dir / "float.inc"
                    
                    if src_inc.exists():
                        shutil.copy(str(src_inc), str(dst_inc))
                    else:
                        console.print(f"[bold yellow]Warning:[/bold yellow] float.inc not found for {pic_arch}")

                # AVR Math Runtime Injection
                # If we are targeting AVR, we need to assemble and link the math runtime.
                # Append the math assembly source directly to the output file
                # if the compiler emitted calls to __div8, __mod8, etc.
                if toolchain.get_name() == "avr-as":
                    progress.update(build_task, description="Injecting AVR Math Runtime...")
                    _splice_avr_math_runtime(output_file, asm_content, math_lib_path / "avr")

            else:
                console.print("[bold yellow]Warning:[/bold yellow] pymcu-stdlib not installed, math operations may fail.")

            # Step 2: Assembly (ASM -> HEX)
            progress.update(build_task, description="  [cyan]Assembling[/cyan]...", completed=60)
            hex_file: Path | None = None
            try:
                if use_ffi:
                    # ── FFI pipeline: avr-as + avr-gcc + avr-ld + avr-objcopy ──────────
                    ffi_tc = toolchain  # type: ignore[assignment]

                    # 2a. Assemble firmware.asm → firmware.o (ELF)
                    firmware_obj = ffi_tc.assemble(output_file)

                    # 2b. Compile C sources declared in [tool.pymcu.ffi]
                    progress.update(build_task, description="  [cyan]Compiling C sources[/cyan]...", completed=65)
                    c_source_paths = [
                        (project_root / p).resolve() for p in ffi_sources_raw
                    ]
                    include_dirs_raw: list[str] = list(ffi_config.get("include_dirs", []))
                    include_dirs = [
                        (project_root / d).resolve() for d in include_dirs_raw
                    ]
                    cflags: list[str] = list(ffi_config.get("cflags", []))
                    c_objects = ffi_tc.compile_c(
                        c_source_paths, include_dirs, cflags, output_dir
                    )

                    # 2c. Link firmware.o + C objects → firmware.elf
                    progress.update(build_task, description="  [cyan]Linking[/cyan]...", completed=75)
                    linker_script_rel: str | None = ffi_config.get("linker_script", None)
                    linker_script_path = (
                        (project_root / linker_script_rel).resolve()
                        if linker_script_rel else None
                    )
                    elf_file = ffi_tc.link(
                        firmware_obj, c_objects, output_dir, linker_script_path
                    )

                    # 2d. ELF → Intel HEX
                    progress.update(build_task, description="  [cyan]Generating HEX[/cyan]...", completed=85)
                    hex_file = ffi_tc.elf_to_hex(elf_file)
                    _diag_log(f"FFI: Generated hex_file: {hex_file}", verbose=is_verbose)
                    _diag_log(f"FFI: hex_file exists: {hex_file.exists() if hex_file else 'None'}", verbose=is_verbose)
                    if hex_file and hex_file.exists():
                        _diag_log(f"FFI: hex_file size: {hex_file.stat().st_size} bytes", verbose=is_verbose)

                    # Move ELF to dist/debug/
                    debug_dir = output_dir / "debug"
                    debug_dir.mkdir(parents=True, exist_ok=True)
                    shutil.move(str(elf_file), str(debug_dir / elf_file.name))
                    # Clean up intermediate objects
                    for obj in [firmware_obj] + c_objects:
                        if obj.exists():
                            obj.unlink()

                elif toolchain.get_name() == "avr-as":
                    # ── avr-as pipeline (non-FFI): assemble → link → objcopy ───────────
                    # Same as FFI but without C compilation.
                    gas_tc = toolchain  # type: ignore[assignment]

                    firmware_obj = gas_tc.assemble(output_file)
                    progress.update(build_task, description="  [cyan]Linking[/cyan]...", completed=75)
                    elf_file = gas_tc.link(firmware_obj, [], output_dir)
                    if blockmap_path is not None and blockmap_path.exists():
                        _resolve_blockmap(blockmap_path,
                                          _elf_text_symbol_addrs(elf_file) or {})
                    progress.update(build_task, description="  [cyan]Generating HEX[/cyan]...", completed=85)
                    hex_file = gas_tc.elf_to_hex(elf_file)

                    debug_dir = output_dir / "debug"
                    debug_dir.mkdir(parents=True, exist_ok=True)
                    shutil.move(str(elf_file), str(debug_dir / elf_file.name))
                    if firmware_obj.exists():
                        firmware_obj.unlink()

                elif toolchain.get_name() == "riscv-as":
                    # ── riscv-as pipeline: assemble → link → objcopy ───────────────────
                    # pymcuc emits a self-contained .asm (reset vector, helpers and
                    # ISA attributes included), so there is no crt0 or libgcc to link.
                    gas_tc = toolchain  # type: ignore[assignment]

                    firmware_obj = gas_tc.assemble(output_file)
                    progress.update(build_task, description="  [cyan]Linking[/cyan]...", completed=75)
                    elf_file = gas_tc.link(firmware_obj, [], output_dir)
                    progress.update(build_task, description="  [cyan]Generating HEX[/cyan]...", completed=85)
                    hex_file = gas_tc.elf_to_hex(elf_file)
                    # WCH-Link flashes a flat image, so ship both.
                    gas_tc.elf_to_bin(elf_file)

                    debug_dir = output_dir / "debug"
                    debug_dir.mkdir(parents=True, exist_ok=True)
                    shutil.move(str(elf_file), str(debug_dir / elf_file.name))
                    if firmware_obj.exists():
                        firmware_obj.unlink()

                elif toolchain.get_name() == "llvm-rp2040":
                    # ── LLVM pipeline (RP2040): opt -> llc -> llvm-mc -> lld -> objcopy ─
                    # The backend wrote LLVM IR (.ll) into output_file; the toolchain
                    # optimises it, links it against the boot2/crt0 runtime and emits a
                    # flat flash image (firmware.bin) with boot2 at offset 0.
                    progress.update(build_task, description="  [cyan]LLVM build[/cyan]...", completed=75)
                    bin_file = toolchain.assemble(output_file)
                    hex_file = None  # RP2040 ships a raw flash binary, not Intel HEX

                    debug_dir = output_dir / "debug"
                    debug_dir.mkdir(parents=True, exist_ok=True)
                    for inter in ["firmware.elf", "firmware.o", "boot2.o",
                                  "crt0.o", "picobin.o", "firmware.opt.ll"]:
                        p = output_dir / inter
                        if p.exists():
                            shutil.move(str(p), str(debug_dir / p.name))

                    flash_total = FLASH_SIZES.get(target.lower(), 0)
                    flash_bytes = bin_file.stat().st_size
                    for line in _flash_report_lines(flash_bytes, flash_total, target,
                                                    output_dir):
                        console.print(line)
                    _check_flash_capacity(flash_bytes, flash_total, target)

                else:
                    # ── Generic toolchain assembly (e.g. gputils/PIC) ──────────────────
                    last_exc = None
                    try:
                        hex_file = toolchain.assemble(output_file)
                    except RuntimeError as e:
                        last_exc = e
                    if hex_file is None:
                        progress.stop()
                        console.print(f"[bold red]Assembly Error:[/bold red] {last_exc}")
                        raise typer.Exit(code=1)

            except typer.Exit:
                raise
            except Exception as e:
                progress.stop()
                console.print(f"[bold red]Assembly Error:[/bold red] {e}")
                raise typer.Exit(code=1)

            progress.update(build_task, completed=90)

            # Step 2.5: Flash size report (HEX parse)
            if hex_file is not None:
                progress.update(build_task, description="Reporting size...")
                flash_bytes = _parse_hex_flash_bytes(hex_file)
                if flash_bytes > 0:
                    flash_total = FLASH_SIZES.get(target.lower(), 0)
                    for line in _flash_report_lines(flash_bytes, flash_total, target,
                                                    output_dir):
                        console.print(line)
                    _check_flash_capacity(flash_bytes, flash_total, target)

            # Step 3: Cleanup
            progress.update(build_task, description="Cleaning up...")
            
            # Move extra files to dist/debug
            debug_dir = output_dir / "debug"
            for ext in [".lst", ".cod", ".map", ".asm", ".obj", ".cof"]: # Added .obj, .cof for AVRA
                f = output_file.with_suffix(ext)
                if f.exists():
                    if not debug_dir.exists():
                        debug_dir.mkdir(parents=True)
                    shutil.move(str(f), str(debug_dir / f.name))
            
            progress.update(build_task, description="Done!", completed=100)

        _diag_log(f"Build completed! Output directory: {output_dir}", verbose=is_verbose)
        _diag_log(f"Output directory exists: {output_dir.exists()}", verbose=is_verbose)
        if is_verbose and output_dir.exists():
            files = list(output_dir.glob("*"))
            _diag_log(f"Files in output directory ({len(files)}):", verbose=is_verbose)
            for f in files:
                _diag_log(f"  - {f.name} ({f.stat().st_size} bytes)", verbose=is_verbose)

            hex_file = output_dir / "firmware.hex"
            _diag_log(f"firmware.hex exists: {hex_file.exists()}", verbose=is_verbose)
            if hex_file.exists():
                _diag_log(f"firmware.hex size: {hex_file.stat().st_size} bytes", verbose=is_verbose)

        console.print(f"[bold green]Build successful![/bold green] Artifacts in: [blue]{output_dir}[/blue]")
        if explain:
            _print_explain(output_dir)
        _show_update_hint()

    except typer.Exit:
        # An inner handler already printed a specific diagnostic and asked to exit;
        # re-raise without the generic "Error:" line (which prints empty for Exit).
        raise
    except Exception as e:
        _diag_log(f"BUILD FAILED with exception: {type(e).__name__}: {e}", verbose=is_verbose)
        if is_verbose:
            import traceback
            _diag_log(f"Traceback:\n{traceback.format_exc()}", verbose=is_verbose)
        console.print(f"[bold red]Error:[/bold red] {e}")
        raise typer.Exit(code=1)

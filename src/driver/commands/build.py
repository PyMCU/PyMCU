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
from ..backends import get_backend_for_chip, run_backend
from ..core.compiler import PyMCUCompiler
from ..core.boards import BOARD_CHIPS
from ..core.update_check import get_available_updates, get_installed_pymcu_versions

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
        elif verbose and line.startswith(("[INFO] ", "[VERBOSE] ")):
            progress.console.print(f"  [dim]{line}[/dim]")

    return handle



# BOARD_CHIPS is imported from core.boards to avoid duplication with flash.py.
# Extension packages may supplement it via a board_chips.py module
# (see _load_extension_board_chips()).
# ---------------------------------------------------------------------------


def _load_extension_board_chips(flavor: str) -> dict[str, str]:
    """
    Try to import pymcu_<flavor>.board_chips and return its BOARD_CHIPS dict.
    Returns an empty dict if the module or attribute does not exist.
    """
    try:
        mod = importlib.import_module(f"pymcu_{flavor}.board_chips")
        return dict(getattr(mod, "BOARD_CHIPS", {}))
    except Exception:
        return {}


_PRINT_RE    = re.compile(r'\bprint\s*\(')
_UART_RE     = re.compile(r'\bUART\s*\(')
_TICKS_MS_RE = re.compile(r'\bticks_ms\s*\(')
_INPUT_RE    = re.compile(r'\binput\s*\(')


def _detect_print_usage(sources_dir: Path) -> tuple[bool, bool, bool]:
    """Scan .py files in sources_dir.

    Returns (has_print, has_uart, has_input):
      has_print -- True if any file contains a print() call
      has_uart  -- True if any file explicitly constructs a UART() instance
      has_input -- True if any file contains an input() call
    """
    has_print = False
    has_uart  = False
    has_input = False
    for py_file in sources_dir.rglob("*.py"):
        try:
            # Strip inline comments from each line before matching to avoid
            # false positives from comment text (e.g. "# Output on UART (9600 baud)")
            lines = py_file.read_text(encoding="utf-8", errors="ignore").splitlines()
            code = "\n".join(line.split("#")[0] for line in lines)
            if not has_print and _PRINT_RE.search(code):
                has_print = True
            if not has_uart and _UART_RE.search(code):
                has_uart = True
            if not has_input and _INPUT_RE.search(code):
                has_input = True
        except OSError:
            pass
        if has_print and has_uart and has_input:
            break
    return has_print, has_uart, has_input


def _detect_ticks_ms_usage(sources_dir: Path) -> bool:
    """Return True if any .py file in sources_dir calls ticks_ms()."""
    for py_file in sources_dir.rglob("*.py"):
        try:
            text = py_file.read_text(encoding="utf-8", errors="ignore")
            if _TICKS_MS_RE.search(text):
                return True
        except OSError:
            pass
    return False


def _sources_contain(sources_dir: Path, token: str) -> bool:
    """Return True if any .py file under sources_dir mentions *token* (substring match)."""
    for py_file in sources_dir.rglob("*.py"):
        try:
            if token in py_file.read_text(encoding="utf-8", errors="ignore"):
                return True
        except OSError:
            pass
    return False


_MAIN_DEF_RE = re.compile(r"^(def main\s*\(\s*\)\s*:)", re.MULTILINE)


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
        header = comment + import_line + "\n"
        modified = existing[:m.end()] + "\n    " + call_line + existing[m.end():]
        synthetic.write_text(header + modified, encoding="utf-8")
        # Lines before def main() shifted by header_lines; lines inside def main()
        # shifted by header_lines + 1 (the inserted call_line).  Use the larger
        # value so breakpoints inside the function body (the common case) resolve
        # correctly.
        preamble_lines = header.count("\n") + 1
    else:
        preamble = comment + import_line + call_line + "\n\n"
        synthetic.write_text(preamble + existing, encoding="utf-8")
        preamble_lines = preamble.count("\n")
    return synthetic, preamble_lines


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


def _inject_ticks_ms_preamble(entry_point: Path, generated_dir: Path) -> tuple[Path, int]:
    """Return a synthetic entry file with a millis_init() preamble prepended.

    Called when ticks_ms() is detected in user sources and no explicit
    millis_init() call is present.  Mirrors the print() / UART preamble
    injection pattern: the build driver owns the setup, user code stays clean.

    Note: millis_init() configures Timer0 in normal overflow mode at prescaler
    64 (~1 ms resolution at 16 MHz).  Do not use Timer0 for PWM or CTC in the
    same project when ticks_ms() is active.
    """
    return _inject_preamble(
        entry_point,
        generated_dir,
        comment="# Auto-injected by pymcu build: millis timer initialized for ticks_ms()\n",
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


def _correct_linemap(linemap_path: Path, filename: str, offset: int) -> None:
    """Subtract *offset* from every linemap entry whose File == *filename*.

    Entries that would have a corrected line number <= 0 (i.e. they point into
    the injected preamble itself) are dropped — they have no counterpart in the
    original source file.
    """
    entries = json.loads(linemap_path.read_text(encoding="utf-8"))
    corrected = []
    for e in entries:
        if e.get("File") == filename:
            new_line = e["Line"] - offset
            if new_line > 0:
                corrected.append({**e, "Line": new_line})
        else:
            corrected.append(e)
    linemap_path.write_text(json.dumps(corrected), encoding="utf-8")


def _resolve_chip_for_board(board: str, extra: dict[str, str]) -> str | None:
    """Return the chip name for *board*, checking extension-supplied entries first."""
    return extra.get(board) or BOARD_CHIPS.get(board)


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

    # Deduct the constant startup preamble that every PyMCU binary carries:
    #   - 26 interrupt vector slots x 4 bytes (RJMP + NOP padding) = 104 bytes
    #   - __bad_interrupt: RJMP main                                =   2 bytes
    # Total preamble = 106 bytes. This matches avr-libc's crt0 footprint
    # (26 x JMP = 104 bytes + __bad_interrupt: JMP = 4 bytes = 108 bytes),
    # keeping the differential comparison fair.
    PREAMBLE_SIZE = 106
    if total >= PREAMBLE_SIZE:
        total -= PREAMBLE_SIZE

    return total

def build(
    verbose: bool = typer.Option(False, "--verbose", "-v", help="Enable verbose logging"),
    stdlib_override: Optional[List[str]] = typer.Option(
        None, "--stdlib",
        help="Override stdlib flavor(s) from pyproject.toml (e.g. --stdlib micropython). "
             "Can be specified multiple times.",
    ),
    debug: bool = typer.Option(False, "--debug", help="Emit debug symbols and line map for the emulator debugger"),
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
        console.print("[debug] === Build command started ===", style="dim cyan")
        console.print(f"[debug] Current working directory: {os.getcwd()}", style="dim")
        console.print(f"[debug] sys.executable: {sys.executable}", style="dim")
        console.print(f"[debug] sys.prefix: {sys.prefix}", style="dim")
        console.print(f"[debug] VIRTUAL_ENV: {os.environ.get('VIRTUAL_ENV', 'NOT SET')}", style="dim")
        console.print(f"[debug] PATH: {os.environ.get('PATH', 'NOT SET')}", style="dim")

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

        target_key   = pymcu_config.get("target", None)
        _diag_log(f"target_key from config: {target_key}", verbose=is_verbose)

        # Compatibility: accept legacy "chip" key with a deprecation warning
        if target_key is None and pymcu_config.get("chip"):
            target_key = pymcu_config.get("chip")
            _diag_log(f"Using legacy 'chip' key: {target_key}", verbose=is_verbose)
            console.print(
                "[bold yellow]Deprecation:[/bold yellow] 'chip' in [tool.pymcu] is deprecated. "
                "Rename it to 'target'."
            )
        board_key    = pymcu_config.get("board", None)
        _diag_log(f"board_key from config: {board_key}", verbose=is_verbose)
        freq         = pymcu_config.get("frequency", 4000000)
        src_path     = pymcu_config.get("sources", "src")
        _diag_log(f"freq: {freq}, src_path: {src_path}", verbose=is_verbose)

        # board and target are mutually exclusive
        if target_key and board_key:
            implied = BOARD_CHIPS.get(board_key, "?")
            console.print(
                f"[bold red]Error:[/bold red] Cannot set both 'target' and 'board' in \\[tool.pymcu].\n"
                f"  'board = \"{board_key}\"' implies target = \"{implied}\". Remove the 'target' key."
            )
            raise typer.Exit(code=1)

        # Resolve stdlib flavors: CLI --stdlib overrides pyproject.toml
        stdlib_flavors: list[str] = (
            list(stdlib_override)
            if stdlib_override
            else list(pymcu_config.get("stdlib", []))
        )
        extension_board_chips: dict[str, str] = {}
        extra_includes: list[str] = []
        extension_board_dirs: dict[str, Path] = {}  # flavor -> boards/ dir

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
                extra_includes.append(str(pkg_parent))
                extra_includes.append(str(pkg_dir))
                # Collect board_chips supplements
                extension_board_chips.update(_load_extension_board_chips(flavor))
                # Record boards/ dir for shim generation
                boards_dir = pkg_dir / "boards"
                if boards_dir.is_dir():
                    extension_board_dirs[flavor] = boards_dir
            else:
                console.print(
                    f"[bold yellow]Warning:[/bold yellow] stdlib flavor 'pymcu_{flavor}' not found. "
                    f"Install it with: pip install pymcu-{flavor}"
                )

        # Derive target from board or fall back to explicit target / default
        if board_key:
            target = _resolve_chip_for_board(board_key, extension_board_chips)
            if target is None:
                console.print(
                    f"[bold red]Error:[/bold red] Unknown board '{board_key}'. "
                    f"Add it to BOARD_CHIPS in core/boards.py or provide a board_chips.py in your extension package."
                )
                raise typer.Exit(code=1)
        else:
            target = target_key or "pic16f84a"

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
                # Vanilla fallback: copy the vanilla board file directly
                try:
                    import pymcu as _pymcu_pkg
                    vanilla_board = Path(_pymcu_pkg.__file__).parent / "boards" / f"{board_key}.py"
                    if vanilla_board.exists():
                        board_shim_content = (
                            "# Auto-generated by pymcu build -- do not edit\n"
                            + vanilla_board.read_text()
                        )
                    else:
                        raise FileNotFoundError
                except Exception:
                    console.print(f"[bold yellow]Warning:[/bold yellow] No board file found for '{board_key}'.")
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
                    f"[debug] {_trigger} without UART — stdout preamble injected "
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

        # Auto-inject millis_init() preamble when ticks_ms() is used.
        # millis_init() must run before any ticks_ms() call; injecting it here
        # mirrors how UART is set up for print().
        if _detect_ticks_ms_usage(sources_dir):
            entry_point, _n = _inject_ticks_ms_preamble(entry_point, generated_dir)
            _linemap_preamble_offset += _n
            if str(generated_dir) not in extra_includes:
                extra_includes.insert(0, str(generated_dir))
            _diag_log(
                "ticks_ms() detected — injecting millis_init() preamble (Timer0 OVF @ prescaler 64)",
                verbose=is_verbose,
            )
            if is_verbose:
                console.print(
                    "[debug] ticks_ms() detected — millis_init() preamble injected",
                    style="dim",
                )

        # Auto-inject clock_init() for the RP2350 so clk_sys is 150 MHz and the system
        # timer ticks at an exact 1 MHz -- mirrors the pico-sdk runtime (runtime_init_clocks)
        # which does the same before main(). Injected LAST so clock_init() runs first, ahead
        # of any stdout/ticks preamble that depends on the final clk_sys / clk_peri. Skipped
        # if the user already calls clock_init() (idempotent, but avoids a redundant pass).
        if target == "rp2350" and not _sources_contain(sources_dir, "clock_init"):
            entry_point, _n = _inject_clock_init_preamble(entry_point, generated_dir)
            _linemap_preamble_offset += _n
            if str(generated_dir) not in extra_includes:
                extra_includes.insert(0, str(generated_dir))
            _diag_log("rp2350 target — injecting clock_init() (150 MHz + 1 MHz timer tick)",
                      verbose=is_verbose)

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
            try:
                if backend_plugin is not None:
                    ir_file = output_dir / "firmware.mir"
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
                        emit_ir_path=str(ir_file),
                    )
                    progress.update(build_task, description="  [cyan]Code Generation[/cyan]...", completed=40)
                    linemap_path: Path | None = None
                    varmap_path: Path | None = None
                    if debug:
                        debug_dir = output_dir / "_debug"
                        debug_dir.mkdir(parents=True, exist_ok=True)
                        linemap_path = debug_dir / "linemap.json"
                        varmap_path  = debug_dir / "varmap.json"
                    run_backend(
                        backend_binary=backend_plugin.get_backend_binary(),
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
                    )
                    # Correct linemap line numbers when preamble was injected.
                    # The compiler saw the synthetic file (with prepended lines),
                    # so all recorded line numbers are shifted by the preamble size.
                    if linemap_path and linemap_path.exists() and _linemap_preamble_offset > 0:
                        _correct_linemap(linemap_path, "main.py", _linemap_preamble_offset)
                else:
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
                    )
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
                    avr_math_path = math_lib_path / "avr"
                    
                    # List of runtime functions to check. The signed floor div/mod
                    # routines (__divs*/__mods*) wrap the unsigned core, which lives in
                    # the same .S file, so pulling in their file also brings the core.
                    runtime_funcs = ["__div8", "__mod8", "__mul8", "__div16", "__mod16", "__div32", "__mod32",
                                     "__divs8", "__mods8", "__divs16", "__mods16", "__divs32", "__mods32",
                                     "__mul32"]
                    needed_funcs = [f for f in runtime_funcs if f in asm_content]

                    if needed_funcs:
                        # Build the math runtime text
                        func_map = {
                            "__div8": "div.S",
                            "__mod8": "div.S",
                            "__divs8": "div.S",
                            "__mods8": "div.S",
                            "__mul8": "mul.S",
                            "__div16": "div16.S",
                            "__mod16": "div16.S",
                            "__divs16": "div16.S",
                            "__mods16": "div16.S",
                            "__div32": "div32.S",
                            "__mod32": "div32.S",
                            "__divs32": "div32.S",
                            "__mods32": "div32.S",
                            "__mul32": "mul32.S",
                        }
                        math_runtime_text = "\n; --- PyMCU AVR Math Runtime ---\n"
                        included_files = set()
                        for func in [f for f in needed_funcs if not f.startswith("__fp")]:
                            fname = func_map.get(func)
                            if fname and fname not in included_files:
                                src_path = avr_math_path / fname
                                if src_path.exists():
                                    with open(src_path, "r") as lib_f:
                                        math_runtime_text += lib_f.read() + "\n"
                                    included_files.add(fname)
                                else:
                                    console.print(f"[bold yellow]Warning:[/bold yellow] Runtime file {fname} not found")
                        # Insert math runtime BEFORE the first function label so that
                        # __div8/__mod8 are at a low word address, within RCALL range
                        # (±2047 words) of any call site in large firmware images.
                        with open(output_file, "r") as f:
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

                        # The peephole optimiser removes "RJMP main" when main: is the
                        # very next label in the compiler's internal list (programs with
                        # no ISRs).  If we are about to insert the math runtime before
                        # main: and the reset-vector jump is gone, re-add it so the CPU
                        # jumps past the runtime to main at reset.
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
                        with open(output_file, "w") as f:
                            f.writelines(lines)

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
                    progress.update(build_task, description="  [cyan]Generating HEX[/cyan]...", completed=85)
                    hex_file = gas_tc.elf_to_hex(elf_file)

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
                    if flash_total:
                        pct = flash_bytes * 100 // flash_total
                        console.print(
                            f"[dim]Flash:[/dim] {flash_bytes} / {flash_total} bytes "
                            f"({pct}% of program storage)"
                        )
                    else:
                        console.print(f"[dim]Flash:[/dim] {flash_bytes} bytes")

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
                    if flash_total:
                        pct = flash_bytes * 100 // flash_total
                        console.print(
                            f"[dim]Flash:[/dim] {flash_bytes} / {flash_total} bytes "
                            f"({pct}% of program storage)"
                        )
                    else:
                        console.print(f"[dim]Flash:[/dim] {flash_bytes} bytes")

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

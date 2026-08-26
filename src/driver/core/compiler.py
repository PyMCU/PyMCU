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

import sys
import os
import shutil
import re
import subprocess
import time
from pathlib import Path
from rich.console import Console

_DIAG_HEADER_RE = re.compile(r"^(\S+?):(\d+)(:.*)$")

# `12 |     x: uint8 = +seed` -- a source line in the snippet the compiler draws under the
# header. The caret line carries no leading number and so never matches.
_DIAG_GUTTER_RE = re.compile(r"^(\s*)(\d+)( \| .*)$")


def _remap_diagnostics(text: str, diagnostic_source) -> str:
    """Point every diagnostic at the file the user wrote, header AND snippet.

    `diagnostic_source` is (synthetic_path, real_path, preamble_lines). The compiler is
    handed a synthetic entry with an injected preamble, so it reports
    `dist/_generated/main.py:13:1` for a line the user wrote at 7 in `src/main.py`.

    The snippet under the header is rendered by the compiler against that same synthetic
    file, so its gutter carries the offset too. Rewriting only the header left one message
    stating two different line numbers for the same line. The gutter follows the header and
    never the reverse: the header is what the editor integrations parse and what the reader
    opens their editor at.
    """
    synthetic, real, offset = diagnostic_source
    syn_name = os.path.basename(str(synthetic))

    def is_synthetic(path: str) -> bool:
        return (os.path.basename(path) == syn_name
                and "_generated" in path.replace("\\", "/"))

    out: list[str] = []
    # Whether the snippet currently being read belongs to the entry file. Decided per block
    # rather than per line: a diagnostic reported against an imported module has numbering of
    # its own, and shifting it by the ENTRY file's preamble would invent a line.
    renumber = False

    for line in text.split("\n"):
        header = _DIAG_HEADER_RE.match(line)
        if header:
            path, num, rest = header.group(1), int(header.group(2)), header.group(3)
            if is_synthetic(path):
                # A line at or below the offset is inside the preamble, so the generated file
                # really is where it went wrong. Its frame stays as the compiler drew it;
                # renumbering would send the reader to a line of their own source that is not
                # the one that failed.
                renumber = num > offset
                out.append(f"{real}:{max(1, num - offset)}{rest}")
            else:
                renumber = False
                out.append(line)
            continue

        gutter = _DIAG_GUTTER_RE.match(line) if renumber else None
        if gutter:
            pad, digits, rest = gutter.group(1), gutter.group(2), gutter.group(3)
            mapped = int(digits) - offset
            if mapped < 1:
                # A context line from inside the preamble. There is no number of the user's
                # that fits it, and clamping it to 1 would label injected code as the first
                # line they wrote, so it is dropped instead.
                continue
            # Right-justified into the width the compiler already used. The caret line was
            # padded against that width, so preserving it keeps the arrow under its character
            # without this code needing to know how the caret line was built. The mapped
            # number is never longer than the original, so the field never overflows.
            out.append(f"{str(mapped).rjust(len(pad) + len(digits))}{rest}")
            continue

        out.append(line)

    return "\n".join(out)


class PyMCUCompiler:
    """
    Wrapper for the core C++ build tool (pymcuc).
    Handles path resolution, stdlib detection, and binary invocation.
    """
    
    def __init__(self, console: Console):
        self.console = console

    def _get_start_path(self) -> Path:
        """Helper to allow easier mocking or inheritance if needed"""
        # We start searching relative to *this file* (src/driver/core/compiler.py)
        # So we likely want to go up to src/driver or src context.
        return Path(__file__).parent.parent 

    def get_compiler_path(self) -> Path:
        # compiler.py is in src/driver/core/
        # toolchain.py was in src/driver/
        # Compiler usually sits near the package root or in bin/
        
        base_path = self._get_start_path() 
        
        candidates = ["pymcuc"]
        if sys.platform == "win32":
            candidates.insert(0, "pymcuc.exe")

        # 1. Check adjacent to src/driver/ (standard wheel layout)
        for name in candidates:
            local_compiler = base_path / name
            if local_compiler.exists():
                return local_compiler
            bin_compiler = base_path / "bin" / name
            if bin_compiler.exists():
                return bin_compiler

        # 2. Development environment fallback (dotnet publish target)
        project_root = base_path.parent.parent
        for name in candidates:
            p = project_root / "build" / "bin" / name
            if p.exists():
                return p

        # 3. System PATH
        which_result = shutil.which("pymcuc")
        if which_result:
            return Path(which_result)

        return Path("pymcuc")  # Last-resort relative fallback

    def get_stdlib_path(self, verbose: bool = False) -> str:
        """
        Resolves the PyMCU Standard Library path.
        """
        is_verbose = verbose or os.environ.get("PYMCU_VERBOSE") == "1"
        try:
            if is_verbose:
                self.console.print(f"\\[debug] sys.executable: {sys.executable}", style="dim")
                self.console.print(f"\\[debug] sys.prefix: {sys.prefix}", style="dim")
                self.console.print(f"\\[debug] sys.path ({len(sys.path)} entries):", style="dim")
                for i, path_entry in enumerate(sys.path):
                    self.console.print(f"\\[debug]   [{i}] {path_entry}", style="dim")
                self.console.print(f"\\[debug] VIRTUAL_ENV env var: {os.environ.get('VIRTUAL_ENV', 'NOT SET')}", style="dim")
                self.console.print(f"\\[debug] PATH env var: {os.environ.get('PATH', 'NOT SET')}", style="dim")

            import pymcu
            if is_verbose:
                self.console.print(f"\\[debug] pymcu namespace __path__: {list(pymcu.__path__)}", style="dim green")
            for _p in pymcu.__path__:
                chips_dir = Path(_p) / "chips"
                if chips_dir.is_dir():
                    return str(Path(_p))
            if is_verbose:
                self.console.print(f"\\[debug] chips/ not found in any pymcu.__path__ entry", style="yellow")
        except ImportError as e:
            if is_verbose:
                self.console.print(f"\\[debug] Failed to import pymcu: {e}", style="dim")
                self.console.print(f"\\[debug] sys.path was: {sys.path}", style="dim")
        except Exception as e:
            if is_verbose:
                self.console.print(f"\\[debug] Error in get_stdlib_path: {e}", style="dim")
        return ""

    def compile(self, input_file: str, output_file: str, target: str, freq: int, configs: dict, search_path: str = None, verbose: bool = False, reset_vector: int = None, interrupt_vector: int = None, extra_includes: list = None, on_output=None, emit_ir_path: str = None, diagnostic_source: tuple = None):
        compiler = self.get_compiler_path()
        input_path = Path(input_file).absolute()
        cmd = [str(compiler), input_file, "-o", output_file, "--target", target, "--freq", str(freq)]

        if emit_ir_path:
            cmd.extend(["--emit-ir", emit_ir_path])

        if reset_vector is not None:
            cmd.extend(["--reset-vector", str(reset_vector)])
        if interrupt_vector is not None:
            cmd.extend(["--interrupt-vector", str(interrupt_vector)])

        working_dir = search_path if search_path else input_path.parent
        cmd.extend(["-I", str(working_dir.absolute())])
        # The project's own source directory. Modules loaded from inside it are the user's, and
        # only those have their module level executed on import. It is passed explicitly rather
        # than inferred, because the entry file is staged into dist/_generated while the imports
        # still resolve out of the original source tree.
        cmd.extend(["--project-root", str(working_dir.absolute())])

        # Extra include paths (generated board shim, extension packages) — prepended
        # before stdlib so they shadow any same-named modules in the vanilla stdlib.
        if extra_includes:
            for inc in extra_includes:
                cmd.extend(["-I", str(inc)])
                if verbose:
                    self.console.print(f"\\[debug] Extra include: {inc}", style="dim")

        stdlib = self.get_stdlib_path(verbose=verbose)
        if stdlib:
            # Resolving path is critical for C++ compiler if CWD varies or if path is relative
            include_path = str(Path(stdlib).parent.resolve())
            stdlib_abs = str(Path(stdlib).resolve())

            if verbose:
                self.console.print(f"\\[debug] Stdlib found at: {stdlib_abs}", style="dim")
                self.console.print(f"\\[debug] Adding include path: {include_path}", style="dim")

            # Only the stdlib's parent directory is added as an include path, so
            # imports must go through the `pymcu.*` namespace (e.g. `from pymcu.time
            # import delay_ms`). Shadowing bare ecosystem names such as `time`,
            # `machine`, or `board` is the responsibility of opt-in compat packages
            # (`pymcu-circuitpython`, `pymcu-micropython`), which the driver adds via
            # `extra_includes` above. See docs/docs/compat/ for the design rationale.
            cmd.extend(["-I", include_path])
            
        for key, val in configs.items():
            cmd.extend(["-C", f"{key}={val}"])

        try:
            # stdout is captured so the driver can parse structured progress tokens:
            #   [PHASE_START] <name>
            #   [PHASE_END]   <name> <elapsedMs>
            #   [BUILD_OK]    <outputPath>
            #   [BUILD_FAIL]  <phaseName>
            #   [INFO]        [<component>] <message>
            #   [VERBOSE]     [<component>] <message>
            #
            # stderr is left to pass through directly so VS Code's problem matcher
            # can parse diagnostic lines (file:line:col: severity: msg).
            #
            # A NEGATIVE returncode means the frontend died on a signal, which is never a
            # statement about the program: -9 is jetsam killing it under heavy parallel-build
            # load, and the others are the compiler crashing. Neither produces a diagnostic,
            # so both used to surface as "Compilation failed (see diagnostics above)" with
            # nothing above -- a message that reads as a rejected program and sends the
            # reader looking for an error that was never printed.
            #
            # Retrying is right for any of them: a signal death is not reproducible from the
            # program's side, and a crash that survives four attempts is a real crash. Only
            # -9 was retried before, so a jetsam kill delivered as anything else fell straight
            # through. This is POSIX-only; on Windows negative return codes do not map to
            # signals, so the retry is inert there. Output is buffered and only emitted for
            # the attempt we keep.
            max_signal_retries = 3
            for attempt in range(max_signal_retries + 1):
                buffered: list[str] = []
                # encoding is pinned to utf-8 because pymcuc always emits utf-8; without
                # it Popen(text=True) decodes with the locale codepage (cp1252 on
                # Windows), raising UnicodeDecodeError on non-ASCII diagnostics.
                # stderr is captured ONLY when there is a synthetic entry to map back:
                # the compiler sees dist/_generated/main.py and reports against it, at a
                # line shifted by the injected preamble, which sends the reader into their
                # own build output at a line that says something else. Rewriting the path
                # and the number makes the problem matcher point at the real file, so this
                # helps the editor integration rather than working against it.
                capture_stderr = subprocess.PIPE if diagnostic_source else None
                with subprocess.Popen(
                    cmd,
                    stdout=subprocess.PIPE,
                    stderr=capture_stderr,
                    text=True,
                    encoding="utf-8",
                    errors="replace",
                    bufsize=1,
                ) as proc:
                    if proc.stdout:
                        buffered = [raw.rstrip("\r\n") for raw in proc.stdout]
                    err_text = proc.stderr.read() if proc.stderr else ""
                    proc.wait()

                if err_text:
                    sys.stderr.write(_remap_diagnostics(err_text, diagnostic_source))
                    sys.stderr.flush()

                if proc.returncode < 0 and attempt < max_signal_retries:
                    time.sleep(0.25 * (attempt + 1))
                    continue
                break

            if on_output:
                for line in buffered:
                    on_output(line)

            if proc.returncode < 0:
                # Still dead on a signal after every retry. Say what happened: the compiler
                # was killed, the program was never judged, and "see diagnostics above" would
                # be pointing at an empty screen.
                import signal as _signal
                try:
                    signame = _signal.Signals(-proc.returncode).name
                except ValueError:
                    signame = f"signal {-proc.returncode}"
                raise RuntimeError(
                    f"the compiler was killed by {signame} after {max_signal_retries + 1} "
                    "attempts, so it never reported on this program. On macOS this is "
                    "usually the OS reclaiming memory from parallel builds; build fewer "
                    "projects at once, or re-run. It is not an error in your code.")

            if proc.returncode != 0:
                raise RuntimeError("Compilation failed (see diagnostics above)")
        except FileNotFoundError:
            raise RuntimeError(f"Compiler '{compiler}' not found.")

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

import json
import sys
import os
import shutil
import re
import subprocess
import threading
import time
from importlib.machinery import PathFinder
from importlib.metadata import Distribution, distributions
from pathlib import Path
from urllib.parse import unquote, urlparse
from urllib.request import url2pathname
from rich.console import Console

_DIAG_HEADER_RE = re.compile(r"^(\S+?):(\d+)(:.*)$")

# `12 |     x: uint8 = +seed` -- a source line in the snippet the compiler draws under the
# header. The caret line carries no leading number and so never matches.
_DIAG_GUTTER_RE = re.compile(r"^(\s*)(\d+)( \| .*)$")


def map_line(offset, generated: int):
    """The user's line for a generated one, or None when pymcu wrote that line itself.

    `offset` is either a per-line map (index = generated line, value = the user's line or
    None) or, for callers that still hand one over, a single count of injected lines.
    One number cannot describe a preamble inserted in TWO places -- a header at the top and
    a call inside `def main():` -- which is why a diagnostic about a module-level line came
    out one line early (#311).
    """
    if isinstance(offset, int):
        mapped = generated - offset
        return mapped if mapped >= 1 else None
    if 1 <= generated < len(offset):
        return offset[generated]
    # Past the end of the map: the file grew after it was built, so shift by whatever the
    # map's own tail says rather than inventing a number.
    return None


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

    # `main.py:43` written INSIDE a message, not as its header. A diagnostic that refuses a
    # site because of an earlier one cites that earlier site in its own sentence, and the
    # compiler numbers it against the synthetic file like everything else. Only the header and
    # the snippet were mapped, so one message stated a line the reader's file does not have:
    # "already 3 for PD6 at line 43" in a 41-line program (#303). The citation now carries the
    # file name, which is what makes it recognisable here -- a bare number could not be told
    # from a duty cycle or a prescaler in the same sentence.
    cited = re.compile(r"(?<![\w./\\])" + re.escape(syn_name) + r":(\d+)")

    def map_citations(text: str) -> str:
        return cited.sub(lambda m: f"{syn_name}:{map_line(offset, int(m.group(1))) or 1}", text)

    out: list[str] = []
    # Whether the snippet currently being read belongs to the entry file. Decided per block
    # rather than per line: a diagnostic reported against an imported module has numbering of
    # its own, and shifting it by the ENTRY file's preamble would invent a line.
    renumber = False

    for line in text.split("\n"):
        header = _DIAG_HEADER_RE.match(line)
        if header:
            path, num, rest = header.group(1), int(header.group(2)), header.group(3)
            # The cited site belongs to the entry file whichever file the header names: a
            # refusal raised inside an imported HAL can still quote the caller's line.
            rest = map_citations(rest)
            if is_synthetic(path):
                # A line pymcu injected has no counterpart in the user's file, so the
                # generated file really is where it went wrong. Its frame stays as the
                # compiler drew it; renumbering would send the reader to a line of their own
                # source that is not the one that failed.
                mapped_header = map_line(offset, num)
                renumber = mapped_header is not None
                out.append(f"{real}:{mapped_header or 1}{rest}")
            else:
                renumber = False
                out.append(f"{path}:{num}{rest}")
            continue

        gutter = _DIAG_GUTTER_RE.match(line) if renumber else None
        if gutter:
            pad, digits, rest = gutter.group(1), gutter.group(2), gutter.group(3)
            mapped = map_line(offset, int(digits))
            if mapped is None:
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


class ArenaRequiredError(RuntimeError):
    """pymcuc reported [NEEDS_ARENA]: the program has a runtime-sized bytearray(n)
    but no pymcu.arena import. `pymcu build` answers by injecting the allocator
    shim + import and running the frontend once more."""


class StrfmtRequiredError(RuntimeError):
    """pymcuc reported [NEEDS_STRFMT]: the program builds a string from a run-time
    value (an f-string value, str()/repr()/hex()/bin()/oct() of a run-time value...)
    but has no pymcu.strfmt import. `pymcu build` answers by injecting the import
    and running the frontend once more (RFC 0014 decision 5: the compiler decides
    this from the call it resolved, not a source-text scan)."""


class Round2RequiredError(RuntimeError):
    """pymcuc reported [NEEDS_ROUND2]: the program calls round(x, n) on a run-time
    float but has no pymcu.round2 import. `pymcu build` answers by injecting the
    import and running the frontend once more (RFC 0014 decision 5)."""


class PyMCUCompiler:
    """
    Wrapper for the core C++ build tool (pymcuc).
    Handles path resolution, stdlib detection, and binary invocation.
    """

    def __init__(self, console: Console, package_search_path: list[str] | None = None):
        self.console = console
        self.package_search_path = package_search_path
        self.compiler_candidates: list[str] = []
        self._stdlib_distribution: Distribution | None = None
        # Set by the most recent compile(): True when pymcuc emitted [ARENA_USED],
        # i.e. a runtime-sized bytearray(n) was lowered against a pymcu.arena import
        # that resolved. The compile SUCCEEDED, but if the driver had not staged its
        # generated shim the arena ran with the shipped module's ARENA_SIZE of 0 --
        # the flag is how build.py knows to inject and compile once more anyway.
        self.last_compile_used_arena = False

    def _get_start_path(self) -> Path:
        """Helper to allow easier mocking or inheritance if needed"""
        # We start searching relative to *this file* (src/driver/core/compiler.py)
        # So we likely want to go up to src/driver or src context.
        return Path(__file__).parent.parent 

    def get_compiler_path(self) -> Path:
        # compiler.py is in src/driver/core/
        # toolchain.py was in src/driver/
        # Compiler usually sits near the package root or in bin/
        #
        # Every candidate tried is recorded in self.compiler_candidates, because "which binary
        # is this actually running" was the first question in all five stale-artifact
        # incidents, and answering it took a `find` each time. Reported under PYMCU_VERBOSE.

        base_path = self._get_start_path() 
        
        candidates = ["pymcuc"]
        if sys.platform == "win32":
            candidates.insert(0, "pymcuc.exe")

        tried: list[str] = []
        self.compiler_candidates = tried

        # 1. Check adjacent to src/driver/ (standard wheel layout)
        for name in candidates:
            local_compiler = base_path / name
            tried.append(str(local_compiler))
            if local_compiler.exists():
                return local_compiler
            bin_compiler = base_path / "bin" / name
            tried.append(str(bin_compiler))
            if bin_compiler.exists():
                return bin_compiler

        # 2. Development environment fallback (dotnet publish target)
        project_root = base_path.parent.parent
        for name in candidates:
            p = project_root / "build" / "bin" / name
            tried.append(str(p))
            if p.exists():
                return p

        # 3. System PATH
        tried.append("PATH: pymcuc")
        which_result = shutil.which("pymcuc")
        if which_result:
            return Path(which_result)

        return Path("pymcuc")  # Last-resort relative fallback

    def get_stdlib_path(self, verbose: bool = False) -> str:
        """
        Resolve the PyMCU standard library without importing ``pymcu``.

        Importing the namespace is unsafe here: another installed distribution
        can contribute a regular ``pymcu/__init__.py`` and run it while a build
        is only trying to locate compiler input files.
        """
        is_verbose = verbose or os.environ.get("PYMCU_VERBOSE") == "1"
        self._stdlib_distribution = None
        try:
            if is_verbose:
                self.console.print(f"\\[debug] sys.executable: {sys.executable}", style="dim")
                self.console.print(f"\\[debug] sys.prefix: {sys.prefix}", style="dim")
                self.console.print(f"\\[debug] sys.path ({len(sys.path)} entries):", style="dim")
                for i, path_entry in enumerate(sys.path):
                    self.console.print(f"\\[debug]   [{i}] {path_entry}", style="dim")
                self.console.print(f"\\[debug] VIRTUAL_ENV env var: {os.environ.get('VIRTUAL_ENV', 'NOT SET')}", style="dim")
                self.console.print(f"\\[debug] PATH env var: {os.environ.get('PATH', 'NOT SET')}", style="dim")

            found = (
                distributions(path=self.package_search_path)
                if self.package_search_path is not None
                else distributions()
            )
            for dist in found:
                name = str(dist.metadata.get("Name", "")).lower().replace("_", "-")
                if name != "pymcu-stdlib":
                    continue
                for entry in dist.files or ():
                    if entry.parts[:2] == ("pymcu", "chips"):
                        package = Path(dist.locate_file("pymcu"))
                        if (package / "chips").is_dir():
                            self._stdlib_distribution = dist
                            return str(package)

                raw = dist.read_text("direct_url.json")
                if raw:
                    direct = json.loads(raw)
                    parsed = urlparse(str(direct.get("url", "")))
                    if (direct.get("dir_info", {}).get("editable")
                            and parsed.scheme == "file"):
                        path = url2pathname(unquote(parsed.path))
                        if parsed.netloc:
                            path = f"//{parsed.netloc}{path}"
                        root = Path(path)
                        for package in (root / "pymcu", root / "src" / "pymcu"):
                            if (package / "chips").is_dir():
                                self._stdlib_distribution = dist
                                return str(package)

            spec = PathFinder.find_spec(
                "pymcu",
                self.package_search_path
                if self.package_search_path is not None else sys.path,
            )
            if spec and spec.submodule_search_locations:
                for location in spec.submodule_search_locations:
                    package = Path(location)
                    if (package / "chips").is_dir():
                        return str(package)
            if is_verbose:
                self.console.print("\\[debug] pymcu stdlib metadata has no chips/", style="yellow")
        except Exception as e:
            if is_verbose:
                self.console.print(f"\\[debug] Error in get_stdlib_path: {e}", style="dim")
        return ""

    def isolate_stdlib(self, stdlib: str, output_dir: Path) -> Path:
        """Expose the pymcu package without exposing its site-packages peers."""
        source = Path(stdlib).resolve()
        root = output_dir / "_stdlib"
        package = root / "pymcu"
        if package.is_symlink():
            try:
                if package.resolve() == source:
                    return root
            except OSError:
                pass
            package.unlink()
        elif package.exists():
            shutil.rmtree(package)

        root.mkdir(parents=True, exist_ok=True)
        owned = []
        if self._stdlib_distribution is not None:
            owned = [
                entry for entry in self._stdlib_distribution.files or ()
                if entry.parts and entry.parts[0] == "pymcu"
            ]
        if owned:
            package.mkdir()
            for entry in owned:
                source_file = Path(self._stdlib_distribution.locate_file(entry))
                if not source_file.is_file():
                    continue
                target = root.joinpath(*entry.parts)
                target.parent.mkdir(parents=True, exist_ok=True)
                try:
                    target.symlink_to(source_file)
                except OSError:
                    shutil.copy2(source_file, target)
            return root

        # Editable installs normally record only their finder and metadata,
        # not every source file. Their located source package is already a
        # distribution-specific tree, so isolating that directory is safe.
        try:
            package.symlink_to(source, target_is_directory=True)
        except OSError:
            shutil.copytree(source, package)
        return root

    def compile(self, input_file: str, output_file: str, target: str, freq: int, configs: dict, search_path: str = None, verbose: bool = False, reset_vector: int = None, interrupt_vector: int = None, extra_includes: list = None, on_output=None, emit_ir_path: str = None, diagnostic_source: tuple = None, timebase: bool = False, library: bool = False, stdlib_flavor: str = "", embed_files: list = None, profile_path: str = None):
        compiler = self.get_compiler_path()
        input_path = Path(input_file).absolute()
        cmd = [str(compiler), input_file, "-o", output_file, "--target", target, "--freq", str(freq)]

        if emit_ir_path:
            cmd.extend(["--emit-ir", emit_ir_path])
        # PGO: the profile JSON produced by `pymcu profile --pgo`. Consumed by the
        # optimizer inside pymcuc, so it goes on the frontend invocation -- the
        # backend binary only ever sees the resulting .mir.
        if profile_path:
            cmd.extend(["--profile", str(profile_path)])
        # Which CircuitPython/MicroPython compat layer the project builds against, if any --
        # a single build-wide fact, same as target/freq. Folds sys.implementation.name/
        # .version, sys.platform and os.uname() the same way __CHIP__ is folded
        # (docs/rfcs/0007 in the PyMCU repo). The caller already refuses a project naming
        # two flavors at once, so this is always at most one string.
        if stdlib_flavor:
            cmd.extend(["--stdlib", stdlib_flavor])
        # The program runs the millisecond time base (injected or explicit millis_init()):
        # the compiler binds __TIMEBASE__ = 1 and the PWM HAL refuses, at compile time, a
        # Timer0 frequency that would reprogram the prescaler under the clock (PyMCU#295).
        if timebase:
            cmd.append("--timebase")
        # Library mode: the unit has no entry point, so every top-level function is a root
        # and nothing is injected around them. `pymcu natmod` compiles this way, because the
        # entry point of a native module is the loader's mpy_init, not a main.
        if library:
            cmd.append("--library")

        # RFC 0008 -- each (name, path, size) triple becomes --embed name=path. The
        # compiler keys its romfs table by the name open() receives; the path is
        # where the blob's bytes are read from on the host.
        for name, path, _size in (embed_files or []):
            cmd.extend(["--embed", f"{name}={path}"])

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

        # Is the artifact about to run the one the sources expect? Nothing here can fail a
        # build: every check is a warning, and the mtime comparison only speaks at all when the
        # artifacts are part of a checkout. See core/staleness.py for why each one exists.
        try:
            from .staleness import resolution_report, warnings_for  # noqa: PLC0415

            if os.environ.get("PYMCU_VERBOSE") == "1" or verbose:
                for line in resolution_report(compiler, stdlib, self.compiler_candidates):
                    self.console.print(f"\\[debug] {line}", style="dim")

            source_roots = [Path(search_path) if search_path else input_path.parent]
            source_roots.extend(Path(i) for i in (extra_includes or []))
            for warning in warnings_for(compiler, stdlib, source_roots):
                self.console.print(f"warning: {warning}", style="yellow")
        except Exception:
            # A freshness check that breaks a build would be worse than the staleness it
            # exists to report.
            pass

        if stdlib:
            # A wheel installs pymcu beside every project dependency. Adding
            # that shared site-packages directory would bypass the library
            # metadata filter, so expose only pymcu through an isolated root.
            include_path = str(self.isolate_stdlib(
                stdlib, Path(output_file).absolute().parent
            ))
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
            #   [NEEDS_ARENA] runtime-sized bytearray(n), pymcu.arena not imported
            #   [ARENA_USED]  an arena allocation was lowered (see ArenaRequiredError
            #                 and last_compile_used_arena for how build.py uses these)
            #   [NEEDS_STRFMT] a run-time string build, pymcu.strfmt not imported
            #   [NEEDS_ROUND2] round(x, n) on a run-time float, pymcu.round2 not imported
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
                # stderr is always captured now: remapped when there is a synthetic
                # entry to map back (the compiler sees dist/_generated/main.py and
                # reports against it, at a line shifted by the injected preamble,
                # which sends the reader into their own build output at a line that
                # says something else -- rewriting path+number makes the problem
                # matcher point at the real file), and held back entirely when the
                # attempt ended in [NEEDS_ARENA]: the caller answers that token by
                # injecting the allocator and retrying, so showing the missing-import
                # diagnostic would be reporting an error that is about to be fixed.
                capture_stderr = subprocess.PIPE
                with subprocess.Popen(
                    cmd,
                    stdout=subprocess.PIPE,
                    stderr=capture_stderr,
                    text=True,
                    encoding="utf-8",
                    errors="replace",
                    bufsize=1,
                ) as proc:
                    # stderr must drain WHILE stdout is being read: pymcuc writes
                    # diagnostics there (every [Warning] line), and a volume past the
                    # pipe's 64K buffer blocks the write, so stdout never reaches EOF
                    # and the read below never returns -- a deadlock measured live
                    # with PYMCU_VERIFY_IR=1 on a fixture that emits ~200 warnings.
                    err_parts: list[str] = []
                    def _drain_stderr() -> None:
                        if proc.stderr:
                            err_parts.append(proc.stderr.read())
                    stderr_reader = threading.Thread(target=_drain_stderr, daemon=True)
                    stderr_reader.start()
                    if proc.stdout:
                        buffered = [raw.rstrip("\r\n") for raw in proc.stdout]
                    stderr_reader.join()
                    err_text = err_parts[0] if err_parts else ""
                    proc.wait()

                needs_arena = "[NEEDS_ARENA]" in buffered
                needs_strfmt = "[NEEDS_STRFMT]" in buffered
                needs_round2 = "[NEEDS_ROUND2]" in buffered
                if err_text and not (needs_arena or needs_strfmt or needs_round2):
                    sys.stderr.write(
                        _remap_diagnostics(err_text, diagnostic_source)
                        if diagnostic_source else err_text)
                    sys.stderr.flush()

                if proc.returncode < 0 and attempt < max_signal_retries:
                    time.sleep(0.25 * (attempt + 1))
                    continue
                break

            if on_output:
                for line in buffered:
                    on_output(line)

            self.last_compile_used_arena = "[ARENA_USED]" in buffered

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
                if needs_arena:
                    raise ArenaRequiredError(
                        "Compilation failed (see diagnostics above)")
                if needs_strfmt:
                    raise StrfmtRequiredError(
                        "Compilation failed (see diagnostics above)")
                if needs_round2:
                    raise Round2RequiredError(
                        "Compilation failed (see diagnostics above)")
                raise RuntimeError("Compilation failed (see diagnostics above)")
        except FileNotFoundError:
            raise RuntimeError(f"Compiler '{compiler}' not found.")

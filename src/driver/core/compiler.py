# -----------------------------------------------------------------------------
# Whipsnake CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
#
# Permission is hereby granted, free of charge, to any person obtaining a copy
# of this software and associated documentation files (the "Software"), to deal
# in the Software without restriction, including without limitation the rights
# to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
# copies of the Software, and to permit persons to whom the Software is
# furnished to do so, subject to the following conditions:
#
# The above copyright notice and this permission notice shall be included in
# all copies or substantial portions of the Software.
#
# THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
# IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
# FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
# AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
# LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
# OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
# THE SOFTWARE.
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

import sys
import os
import subprocess
from pathlib import Path
from rich.console import Console

class WhipCompiler:
    """
    Wrapper for the core C++ build tool (whipc).
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
        # Standard lookup logic
        # compiler.py is in src/driver/core/
        # toolchain.py was in src/driver/
        # Compiler usually sits near the package root or in bin/
        
        # We'll search relative to src/driver (parent of this file's dir)
        base_path = self._get_start_path() 
        
        candidates = ["whipc"]
        if sys.platform == "win32":
            candidates.insert(0, "whipc.exe")

        # 1. Check adjacent (standard wheel layout)
        for name in candidates:
            local_compiler = base_path / name
            if local_compiler.exists(): return local_compiler
            
            # Check in bin/ subdirectory
            bin_compiler = base_path / "bin" / name
            if bin_compiler.exists(): return bin_compiler

        # 2. Development environment fallbacks (build/bin)
        project_root = base_path.parent.parent # If base is src/driver -> project is src/../
        for d in ["build/bin", "cmake-build-debug/bin", "cmake-build-release/bin"]:
             for name in candidates:
                p = project_root / d / name
                if p.exists(): return p
            
        return Path("whipc") # Fallback to PATH

    def get_stdlib_path(self, verbose: bool = False) -> str:
        """
        Resolves the Whipsnake Standard Library path.
        """
        try:
            # Diagnostic for debugging import failure
            is_verbose = verbose or os.environ.get("WHIP_VERBOSE") == "1"
            if is_verbose:
                self.console.print(f"[debug] sys.executable: {sys.executable}", style="dim")
                self.console.print(f"[debug] sys.path: {sys.path}", style="dim")

            import whipsnake
            if hasattr(whipsnake, "__file__") and whipsnake.__file__:
                p = Path(whipsnake.__file__).parent / "chips"
                if p.is_dir():
                    # Return the package directory itself
                    return str(Path(whipsnake.__file__).parent)
        except ImportError as e:
            self.console.print(f"[debug] Failed to import whipsnake: {e}", style="red")
        except Exception as e:
            self.console.print(f"[debug] Error in get_stdlib_path: {e}", style="red")
        return ""

    def compile(self, input_file: str, output_file: str, arch: str, freq: int, configs: dict, search_path: str = None, verbose: bool = False, reset_vector: int = None, interrupt_vector: int = None, extra_includes: list = None):
        compiler = self.get_compiler_path()
        input_path = Path(input_file).absolute()
        cmd = [str(compiler), input_file, "-o", output_file, "--arch", arch, "--chip", arch, "--freq", str(freq)]

        if reset_vector is not None:
            cmd.extend(["--reset-vector", str(reset_vector)])
        if interrupt_vector is not None:
            cmd.extend(["--interrupt-vector", str(interrupt_vector)])

        working_dir = search_path if search_path else input_path.parent
        cmd.extend(["-I", str(working_dir.absolute())])

        # Extra include paths (generated board shim, extension packages) — prepended
        # before stdlib so they shadow any same-named modules in the vanilla stdlib.
        if extra_includes:
            for inc in extra_includes:
                cmd.extend(["-I", str(inc)])
                if verbose:
                    self.console.print(f"[debug] Extra include: {inc}", style="dim")

        stdlib = self.get_stdlib_path(verbose=verbose)
        if stdlib:
            # Resolving path is critical for C++ compiler if CWD varies or if path is relative
            include_path = str(Path(stdlib).parent.resolve())
            stdlib_abs = str(Path(stdlib).resolve())

            if verbose:
                self.console.print(f"[debug] Stdlib found at: {stdlib_abs}", style="dim")
                self.console.print(f"[debug] Adding include path: {include_path}", style="dim")

            cmd.extend(["-I", include_path])
            cmd.extend(["-I", stdlib_abs]) # Add package dir itself as fallback
            
        for key, val in configs.items():
            cmd.extend(["-C", f"{key}={val}"])
        
        try:
            # Let stderr flow through to the terminal so VS Code's problem
            # matcher can parse diagnostic lines (file:line:col: severity: msg).
            result = subprocess.run(cmd)
            if result.returncode != 0:
                raise RuntimeError("Compilation failed (see diagnostics above)")
        except FileNotFoundError:
            raise RuntimeError(f"Compiler '{compiler}' not found.")

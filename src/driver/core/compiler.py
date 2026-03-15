# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# This file is part of the PyMCU Development Ecosystem.
#
# PyMCU is free software: you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# PyMCU is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with PyMCU.  If not, see <https://www.gnu.org/licenses/>.
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
import subprocess
from pathlib import Path
from rich.console import Console

class PyMcuCompiler:
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
        # Standard lookup logic
        # compiler.py is in src/driver/core/
        # toolchain.py was in src/driver/
        # Compiler usually sits near the package root or in bin/
        
        # We'll search relative to src/driver (parent of this file's dir)
        base_path = self._get_start_path() 
        
        candidates = ["pymcuc"]
        if sys.platform == "win32":
            candidates.insert(0, "pymcuc.exe")

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
            
        return Path("pymcuc") # Fallback to PATH

    def get_stdlib_path(self, verbose: bool = False) -> str:
        """
        Resolves the PyMCU Standard Library path.
        """
        try:
            # Diagnostic for debugging import failure
            is_verbose = verbose or os.environ.get("PYMCU_VERBOSE") == "1"
            if is_verbose:
                self.console.print(f"[debug] sys.executable: {sys.executable}", style="dim")
                self.console.print(f"[debug] sys.path: {sys.path}", style="dim")
            
            import pymcu
            if hasattr(pymcu, "__file__") and pymcu.__file__:
                p = Path(pymcu.__file__).parent / "chips"
                if p.is_dir():
                    # Return the package directory itself
                    return str(Path(pymcu.__file__).parent)
        except ImportError as e:
            self.console.print(f"[debug] Failed to import pymcu: {e}", style="red")
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

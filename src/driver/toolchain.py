import os
import subprocess
import sys
import shutil
from pathlib import Path
from typing import Optional, List
from rich.console import Console
from rich.progress import Progress, SpinnerColumn, TextColumn, BarColumn

console = Console()

class Toolchain:
    @staticmethod
    def get_compiler_path() -> Path:
        # If running from a package, it should be in the same directory or bin
        base_path = Path(__file__).parent
        
        # Check if pymcuc is in the same directory (bundled by scikit-build)
        local_compiler = base_path / "pymcuc"
        if local_compiler.exists():
            return local_compiler
        
        # Check in a 'bin' subdirectory
        bin_compiler = base_path / "bin" / "pymcuc"
        if bin_compiler.exists():
            return bin_compiler

        # Development fallback: look for build directories
        project_root = base_path.parent.parent # base_path is src/driver/
        build_dirs = ["build/bin", "cmake-build-debug/bin", "cmake-build-release/bin"]
        for d in build_dirs:
            p = project_root / d / "pymcuc"
            if p.exists():
                return p
            
        # Fallback to PATH
        return Path("pymcuc")

    @staticmethod
    def get_stdlib_path() -> str:
        # Try to find 'pymcu' package (which should be provided by pymcu-stdlib)
        try:
            import pymcu
            # Check if this pymcu has chips
            if hasattr(pymcu, "__file__") and pymcu.__file__ and (Path(pymcu.__file__).parent / "chips").is_dir():
                return str(Path(pymcu.__file__).parent)
        except ImportError:
            pass
            
        return ""

    @staticmethod
    def run_compiler(input_file: str, output_file: str, arch: str, freq: int, configs: dict):
        compiler = Toolchain.get_compiler_path()
        
        cmd = [str(compiler), input_file, "-o", output_file, "--arch", arch, "--freq", str(freq)]
        
        stdlib = Toolchain.get_stdlib_path()
        if stdlib:
            # We want the parent of 'pymcu' directory as include path
            include_path = str(Path(stdlib).parent)
            cmd.extend(["-I", include_path])
            
        for key, val in configs.items():
            cmd.extend(["-C", f"{key}={val}"])
        
        try:
            subprocess.run(cmd, check=True, capture_output=True)
        except subprocess.CalledProcessError as e:
            error_msg = e.stderr.decode() if e.stderr else e.stdout.decode()
            raise RuntimeError(f"Compilation failed:\n{error_msg}")
        except FileNotFoundError:
            raise RuntimeError(f"Compiler '{compiler}' not found. Make sure it is installed and in your PATH.")

    @staticmethod
    def find_tool(name: str) -> Optional[Path]:
        path = shutil.which(name)
        return Path(path) if path else None

    @staticmethod
    def install_tool(name: str):
        console.print(f"[yellow]{name} not found. Attempting automatic installation...[/yellow]")
        
        system = sys.platform
        package_map = {
            "gpasm": "gputils",
            "avra": "avra",
            "pioasm": "pioasm"
        }
        
        pkg = package_map.get(name, name)
        
        if system == "darwin":
            # macOS
            if name == "pioasm":
                # pioasm is often not in core, but available in pico-sdk or some taps
                # For simplicity, we'll try a common tap or tell the user
                console.print("[yellow]pioasm might require 'pico-sdk' or a specific homebrew tap.[/yellow]")
            Toolchain._run_install_cmd(["brew", "install", pkg], f"{pkg} (via Homebrew)")
        elif system == "linux":
            # Assume Debian/Ubuntu for now
            Toolchain._run_install_cmd(["sudo", "apt-get", "update"], "Updating package list")
            Toolchain._run_install_cmd(["sudo", "apt-get", "install", "-y", pkg], f"{pkg} (via apt)")
        elif system == "win32":
            # Try winget (standard on modern Windows)
            # Use --accept-source-agreements and --accept-package-agreements to avoid interactive prompts
            common_args = ["--accept-source-agreements", "--accept-package-agreements"]
            if name == "gpasm":
                # gputils is the package name for gpasm
                Toolchain._run_install_cmd(["winget", "install", "--id", "GPUTILS.GPUTILS", "-e", "--source", "winget"] + common_args, "gputils (via winget)")
            elif name == "avra":
                Toolchain._run_install_cmd(["winget", "install", "--id", "Ro5bert.avra", "-e", "--source", "winget"] + common_args, "avra (via winget)")
            else:
                # Generic attempt
                Toolchain._run_install_cmd(["winget", "install", pkg] + common_args, f"{pkg} (via winget)")
        else:
            raise RuntimeError(f"Automatic installation of {name} not supported on {system}. Please install it manually.")

    @staticmethod
    def _run_install_cmd(cmd: List[str], desc: str):
        # On Windows, we need to ensure we can find winget and that it doesn't block
        shell = sys.platform == "win32"

        with Progress(
            SpinnerColumn(),
            TextColumn("[progress.description]{task.description}"),
            BarColumn(),
            transient=True,
        ) as progress:
            progress.add_task(description=f"Installing {desc}...", total=None)
            try:
                # On Windows, shell=True uses cmd.exe.
                subprocess.run(cmd, check=True, capture_output=True, shell=shell)
            except subprocess.CalledProcessError as e:
                # Merge stdout and stderr for checking messages
                output = (e.stdout.decode(errors='replace') if e.stdout else "") + \
                         (e.stderr.decode(errors='replace') if e.stderr else "")
                
                # Some successful installs might return non-zero or have output in stderr
                # Winget sometimes returns 0xA0A0016 for "already installed" or similar
                if "Successfully installed" in output or "already installed" in output.lower():
                    return
                
                # Check for common winget exit codes that are not errors
                # 0x0 is success, but CallProcessError means it wasn't 0.
                
                console.print(f"[red]Installation failed:[/red]\n{output}")
                raise RuntimeError(f"Failed to install {desc}")

    @staticmethod
    def run_assembler(arch: str, input_asm: str, assembler_override: Optional[str] = None):
        # Determine which assembler to use
        tool = assembler_override
        if not tool:
            if arch.startswith("pic") or arch in ["baseline", "midrange", "advanced"]:
                tool = "gpasm"
            elif arch == "avr" or arch == "atmega328p":
                tool = "avra"
            elif arch == "pio" or arch == "rp2040-pio":
                tool = "pioasm"
        
        if not tool:
            console.print("[yellow]No assembler configured for this architecture. Stopping at ASM generation.[/yellow]")
            return

        tool_path = Toolchain.find_tool(tool)
        if not tool_path:
            try:
                Toolchain.install_tool(tool)
                tool_path = Toolchain.find_tool(tool)
            except Exception as e:
                console.print(f"[red]Error during installation: {e}[/red]")
            
            if not tool_path:
                raise RuntimeError(f"Assembler '{tool}' not found. Please install it manually.")

        # Run the assembler
        cmd = []
        if tool == "gpasm":
            # gpasm produce hex/cod/lst by default
            cmd = [str(tool_path), input_asm]
        elif tool == "avra":
            # avra <input>
            cmd = [str(tool_path), input_asm]
        elif tool == "pioasm":
            # pioasm <input> <output>
            output_h = str(Path(input_asm).with_suffix(".h"))
            cmd = [str(tool_path), input_asm, output_h]
        elif tool == "mpasm":
            # MPASM logic (usually Windows only)
            console.print("[yellow]MPASM support is limited. Ensure it is in your PATH.[/yellow]")
            cmd = [str(tool_path), "/q", "/p" + arch.upper(), input_asm]
        
        if cmd:
            console.print(f"[pymcu] Executing assembler ([bold]{tool}[/bold])...")
            try:
                subprocess.run(cmd, check=True, capture_output=True)
            except subprocess.CalledProcessError as e:
                error_msg = e.stderr.decode() if e.stderr else e.stdout.decode()
                console.print(f"[red]Assembler failed:[/red]\n{error_msg}")
                raise RuntimeError("Assembly failed.")

from abc import ABC, abstractmethod
from pathlib import Path
import sys
import hashlib
import urllib.request
import tarfile
import zipfile
from typing import Optional
from rich.console import Console
from rich.progress import (
    Progress,
    SpinnerColumn,
    TextColumn,
    BarColumn,
    DownloadColumn,
    TransferSpeedColumn,
    TimeRemainingColumn
)

class ExternalToolchain(ABC):
    """
    Abstract base class for managing external compiler/assembler toolchains.
    Handles caching, installation, and invocation strategies.
    Enforces strict security checks.
    """
    
    def __init__(self, console: Console):
        self.console = console
        self.base_dir = Path.home() / ".pymcu" / "tools" / sys.platform
        
        if not self.base_dir.exists():
            self.base_dir.mkdir(parents=True, exist_ok=True)

    @classmethod
    @abstractmethod
    def supports(cls, chip: str) -> bool:
        """
        Determines if this toolchain supports the given chip family.
        """
        pass

    @abstractmethod
    def get_name(self) -> str:
        """Returns the robust name of the toolchain (e.g. 'gputils')."""
        pass

    @abstractmethod
    def is_cached(self) -> bool:
        """Checks if the toolchain is already installed and ready."""
        pass

    @abstractmethod
    def install(self) -> Path:
        """
        Interactively installs the toolchain.
        Must download, verify, and verify integrity strictly.
        Returns the path to the main executable.
        """
        pass

    @abstractmethod
    def assemble(self, asm_file: Path, output_file: Optional[Path] = None) -> Path:
        """
        Runs the assembler on the generated ASM file.
        Returns the path to the generated artifact (HEX/ELF).
        """
        pass

    def _get_tool_dir(self) -> Path:
        return self.base_dir / self.get_name()

    def verify_sha256(self, file_path: Path, expected_hash: str) -> bool:
        """
        Strictly verifies the SHA-256 hash of a file.
        Returns True only if the hash EXACTLY matches.
        """
        if not file_path.exists():
            return False
            
        sha256_hash = hashlib.sha256()
        with open(file_path, "rb") as f:
            # Read in 4K chunks to be memory efficient
            for byte_block in iter(lambda: f.read(4096), b""):
                sha256_hash.update(byte_block)
        
        calculated_hash = sha256_hash.hexdigest()
        
        # Security: Strict comparison, no placeholders allowed in production code.
        return calculated_hash.lower() == expected_hash.lower()

    def _download_file(self, url: str, dest_path: Path, description: str):
        """Helper to download a file with a rich progress bar."""
        try:
            with Progress(
                SpinnerColumn(),
                TextColumn("[progress.description]{task.description}"),
                BarColumn(),
                DownloadColumn(),
                TransferSpeedColumn(),
                TimeRemainingColumn(),
                transient=True,
                console=self.console
            ) as progress:
                task_id = progress.add_task(description, total=None)
                
                def reporthook(block_num, block_size, total_size):
                    progress.update(task_id, total=total_size, completed=block_num * block_size)

                urllib.request.urlretrieve(url, dest_path, reporthook=reporthook)
        except Exception as e:
            # Cleanup partial download
            if dest_path.exists():
                dest_path.unlink()
            raise RuntimeError(f"Download failed: {e}")

    def _extract_archive(self, archive_path: Path, target_dir: Path, archive_type: str):
        """Helper to extract tar.gz, tar.bz2, or zip."""
        self.console.print(f"Extracting to {target_dir}...")
        
        try:
            if archive_type == "tar.gz":
                with tarfile.open(archive_path, "r:gz") as tar:
                    tar.extractall(path=target_dir)
            elif archive_type == "tar.bz2":
                with tarfile.open(archive_path, "r:bz2") as tar:
                    tar.extractall(path=target_dir)
            elif archive_type == "zip":
                with zipfile.ZipFile(archive_path, 'r') as zip_ref:
                    zip_ref.extractall(target_dir)
            else:
                raise ValueError(f"Unsupported archive type: {archive_type}")
        except Exception as e:
            raise RuntimeError(f"Extraction failed: {e}")

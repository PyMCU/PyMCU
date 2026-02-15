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

from abc import ABC, abstractmethod
from pathlib import Path
import sys
import hashlib
import urllib.request
import tarfile
import zipfile
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

class CacheableTool(ABC):
    """
    Abstract base class for any tool that needs to be downloaded, verified, 
    and cached in the ~/.pymcu/tools directory.
    """
    
    def __init__(self, console: Console):
        self.console = console
        self.base_dir = Path.home() / ".pymcu" / "tools" / sys.platform
        
        if not self.base_dir.exists():
            self.base_dir.mkdir(parents=True, exist_ok=True)

    @abstractmethod
    def get_name(self) -> str:
        """Returns the directory name for this tool."""
        pass

    def _get_tool_dir(self) -> Path:
        return self.base_dir / self.get_name()

    @abstractmethod
    def is_cached(self) -> bool:
        """Checks if the tool is already installed."""
        pass

    @abstractmethod
    def install(self) -> Path:
        """Installs the tool."""
        pass

    def verify_sha256(self, file_path: Path, expected_hash: str) -> bool:
        """
        Strictly verifies the SHA-256 hash of a file.
        Returns True only if the hash EXACTLY matches.
        """
        if not file_path.exists():
            return False
            
        sha256_hash = hashlib.sha256()
        with open(file_path, "rb") as f:
            for byte_block in iter(lambda: f.read(4096), b""):
                sha256_hash.update(byte_block)
        
        calculated_hash = sha256_hash.hexdigest()
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

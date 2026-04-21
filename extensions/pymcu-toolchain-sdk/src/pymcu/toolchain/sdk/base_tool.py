# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# This program is free software: you can redistribute it and/or modify
# it under the terms of the GNU Affero General Public License as published
# by the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# This program is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU Affero General Public License for more details.
#
# You should have received a copy of the GNU Affero General Public License
# along with this program.  If not, see <https://www.gnu.org/licenses/>.
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

from abc import ABC, abstractmethod
from pathlib import Path
import contextlib
import hashlib
import os
import platform
import sys
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


def _default_platform_key() -> str:
    """
    Return a normalised platform key of the form ``{os}-{arch}``
    (e.g. ``linux-x86_64``, ``darwin-arm64``, ``win32-x86_64``).

    This is the canonical cache-directory discriminator used by all
    CacheableTool subclasses.
    """
    machine = platform.machine().lower()
    if machine in ("amd64", "x86_64"):
        arch = "x86_64"
    elif machine in ("arm64", "aarch64"):
        arch = "arm64"
    else:
        arch = machine

    os_name = sys.platform if not sys.platform.startswith("linux") else "linux"
    return f"{os_name}-{arch}"


def _is_non_interactive() -> bool:
    """
    Return True when the process is running in a non-interactive context
    (CI environment, piped stdin, or explicit opt-out via env var).
    Callers should auto-accept install prompts when this returns True.
    """
    if os.environ.get("CI") == "true":
        return True
    if os.environ.get("PYMCU_NO_INTERACTIVE") == "1":
        return True
    try:
        return not sys.stdin.isatty()
    except Exception:
        return True


@contextlib.contextmanager
def _tool_lock(lock_file: Path):
    """
    POSIX advisory lock on *lock_file* to prevent concurrent installs
    from corrupting the toolchain cache directory.  On Windows the
    context manager is a no-op (file locking semantics differ but the
    risk of parallel installs is lower on Windows).
    """
    lock_file.parent.mkdir(parents=True, exist_ok=True)
    if sys.platform == "win32":
        yield
        return
    import fcntl
    fh = open(lock_file, "w")
    try:
        fcntl.flock(fh, fcntl.LOCK_EX)
        yield
    finally:
        fcntl.flock(fh, fcntl.LOCK_UN)
        fh.close()


class CacheableTool(ABC):
    """
    Abstract base class for any tool that needs to be downloaded, verified,
    and cached in the ~/.pymcu/tools directory.

    Environment variables
    ---------------------
    PYMCU_TOOLS_DIR
        Override the root cache directory.  Defaults to
        ``~/.pymcu/tools/{platform_key}``.
    PYMCU_SKIP_HASH_CHECK
        Set to ``1`` to skip SHA-256 verification (for development only).
    PYMCU_NO_INTERACTIVE
        Set to ``1`` to suppress all interactive prompts (auto-accept).
    CI
        Set to ``true`` (standard GitHub Actions / most CI systems) to
        suppress all interactive prompts (auto-accept).
    """

    def __init__(self, console: Console):
        self.console = console
        platform_key = _default_platform_key()
        tools_root_env = os.environ.get("PYMCU_TOOLS_DIR")
        if tools_root_env:
            resolved = Path(tools_root_env).resolve()
            # Require an absolute path to prevent accidental relative traversal
            if not resolved.is_absolute():
                raise ValueError(
                    f"PYMCU_TOOLS_DIR must be an absolute path, got: {tools_root_env!r}"
                )
            self.base_dir = resolved / platform_key
        else:
            self.base_dir = Path.home() / ".pymcu" / "tools" / platform_key

        if not self.base_dir.exists():
            self.base_dir.mkdir(parents=True, exist_ok=True)

    @abstractmethod
    def get_name(self) -> str:
        """Returns the directory name for this tool."""
        pass

    def _get_tool_dir(self) -> Path:
        return self.base_dir / self.get_name()

    def _version_file(self) -> Path:
        return self._get_tool_dir() / ".version"

    def _read_cached_version(self) -> str:
        """Return the version string recorded in the tool cache, or '' if absent."""
        vf = self._version_file()
        try:
            return vf.read_text().strip() if vf.exists() else ""
        except Exception:
            return ""

    def _write_cached_version(self, version: str) -> None:
        """Record *version* in the tool cache directory."""
        vf = self._version_file()
        vf.parent.mkdir(parents=True, exist_ok=True)
        vf.write_text(version)

    def _lock_file(self) -> Path:
        return self.base_dir / f"{self.get_name()}.lock"

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
        """
        Extract tar.gz, tar.bz2, or zip to *target_dir*.

        Path-traversal (zip-slip) protection is applied: any member whose
        resolved path escapes *target_dir* is silently skipped and a warning
        is printed.
        """
        self.console.print(f"Extracting to {target_dir}...")
        resolved_target = target_dir.resolve()

        def _safe_tar_members(tar: tarfile.TarFile):
            for member in tar.getmembers():
                dest = (resolved_target / member.name).resolve()
                if not str(dest).startswith(str(resolved_target)):
                    self.console.print(
                        f"[yellow]Skipping unsafe archive member: {member.name}[/yellow]"
                    )
                    continue
                yield member

        def _safe_zip_members(zf: zipfile.ZipFile) -> list[str]:
            safe = []
            for name in zf.namelist():
                dest = (resolved_target / name).resolve()
                if not str(dest).startswith(str(resolved_target)):
                    self.console.print(
                        f"[yellow]Skipping unsafe archive member: {name}[/yellow]"
                    )
                    continue
                safe.append(name)
            return safe

        try:
            if archive_type == "tar.gz":
                with tarfile.open(archive_path, "r:gz") as tar:
                    tar.extractall(path=target_dir, members=_safe_tar_members(tar))
            elif archive_type == "tar.bz2":
                with tarfile.open(archive_path, "r:bz2") as tar:
                    tar.extractall(path=target_dir, members=_safe_tar_members(tar))
            elif archive_type == "zip":
                with zipfile.ZipFile(archive_path, "r") as zf:
                    for name in _safe_zip_members(zf):
                        zf.extract(name, target_dir)
            else:
                raise ValueError(f"Unsupported archive type: {archive_type}")
        except (ValueError, RuntimeError):
            raise
        except Exception as e:
            raise RuntimeError(f"Extraction failed: {e}")

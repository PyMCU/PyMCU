
import sys
import os
import unittest
from unittest.mock import patch, MagicMock
from pathlib import Path
from driver.main import _ensure_venv
from driver.commands.new import new
import tomlkit
import importlib.metadata

class TestVersioningLogic(unittest.TestCase):
    
    @patch("sys.prefix", "/usr")
    @patch("sys.base_prefix", "/usr")
    @patch("sys.argv", ["pymcu", "build"])
    @patch("sys.platform", "linux")
    @patch("pathlib.Path.cwd")
    @patch("os.execv")
    def test_venv_switch_triggered(self, mock_execv, mock_cwd):
        # Setup: Filesystem
        fake_cwd = MagicMock(spec=Path)
        mock_cwd.return_value = fake_cwd
        
        fake_venv = MagicMock(spec=Path)
        fake_cwd.__truediv__.return_value = fake_venv # cwd / ".venv"
        # We need careful handling of chained calls:
        # cwd / ".venv" -> fake_venv
        # fake_venv.exists() -> True
        # fake_venv.is_dir() -> True
        # fake_venv / "bin" -> fake_bin
        # fake_bin / "pymcu" -> fake_exe
        
        # When .exists() is called, we need to return True for venv, bin, exe
        # But for other paths?
        
        # Let's configure the mock objects specific to the path flow
        
        # 1. cwd / ".venv"
        fake_venv = MagicMock()
        fake_venv.exists.return_value = True
        fake_venv.is_dir.return_value = True
        
        # 2. .venv / "bin" (linux)
        fake_bin = MagicMock()
        
        # 3. bin / "pymcu"
        fake_exe = MagicMock()
        fake_exe.exists.return_value = True
        fake_exe.__str__.return_value = "/fake/cwd/.venv/bin/pymcu"
        
        # Chain them
        # cwd / ".venv"
        def cwd_div(arg):
            if arg == ".venv": return fake_venv
            return MagicMock()
        fake_cwd.__truediv__.side_effect = cwd_div
        
        # .venv / "bin"
        def venv_div(arg):
            if arg == "bin": return fake_bin
            return MagicMock()
        fake_venv.__truediv__.side_effect = venv_div
        
        # bin / "pymcu"
        def bin_div(arg):
            if arg == "pymcu": return fake_exe
            return MagicMock()
        fake_bin.__truediv__.side_effect = bin_div

        # Execute
        _ensure_venv()

        # Verify
        mock_execv.assert_called_once()
        args = mock_execv.call_args[0]
        self.assertEqual(args[0], "/fake/cwd/.venv/bin/pymcu")
        
    @patch("sys.prefix", "/path/to/venv")
    @patch("sys.base_prefix", "/usr")
    @patch("os.execv")
    def test_venv_switch_skipped_if_already_in_venv(self, mock_execv):
        # Execute
        _ensure_venv()
        
        # Verify
        mock_execv.assert_not_called()

    @patch("importlib.metadata.version")
    @patch("driver.commands.new.open", new_callable=MagicMock)
    @patch("driver.commands.new.Path")
    @patch("driver.commands.new.Prompt")
    @patch("driver.commands.new.console") # Suppress console output
    def test_new_command_pins_version(self, mock_console, mock_prompt, mock_path, mock_open, mock_version):
        # Setup
        mock_version.return_value = "1.2.3"
        mock_prompt.ask.side_effect = ["uv"] # pkg_manager only (mcu passed as arg)
        
        mock_proj_dir = MagicMock()
        mock_path.return_value = mock_proj_dir
        mock_proj_dir.exists.return_value = False
        
        # Mock open context manager
        mock_file_handle = MagicMock()
        mock_open.return_value.__enter__.return_value = mock_file_handle
        
        # Execute
        try:
           new("myproj", mcu="pic16f84a")
        except:
           pass

        # Verify
        # Check that importlib.metadata.version was called with correct package
        mock_version.assert_called_with("pymcu-compiler")

if __name__ == "__main__":
    unittest.main()

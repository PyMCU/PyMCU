import unittest
from pathlib import Path
from unittest.mock import patch, MagicMock

# `src.driver`, not `driver`: tests/driver/ is itself a package named driver,
# so a bare `driver` import resolves to whichever of the two pytest collected
# first. Alone this file passed; in a full run it failed to import and its
# tests were skipped silently. Every other test module spells it this way.
from src.driver.commands.new import new
from src.driver.main import _ensure_venv


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
        fake_cwd.__truediv__.return_value = fake_venv  # cwd / ".venv"
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

    # sys.prefix is the project's own .venv, so _ensure_venv() must find nothing
    # to switch to. Path.cwd/exists/is_dir are patched so the check runs against
    # this fake project instead of whatever directory pytest was started from.
    @patch("sys.prefix", "/path/to/project/.venv")
    @patch("sys.base_prefix", "/usr")
    @patch("pathlib.Path.is_dir", return_value=True)
    @patch("pathlib.Path.exists", return_value=True)
    @patch("pathlib.Path.cwd", return_value=Path("/path/to/project"))
    @patch("os.execv")
    def test_venv_switch_skipped_if_already_in_venv(
        self, mock_execv, mock_cwd, mock_exists, mock_is_dir
    ):
        # Execute
        _ensure_venv()

        # Verify
        mock_execv.assert_not_called()

    @patch("importlib.metadata.version")
    @patch("src.driver.commands.new.open", new_callable=MagicMock)
    @patch("src.driver.commands.new.Path")
    @patch("src.driver.commands.new.Prompt")
    @patch("src.driver.commands.new.console")  # Suppress console output
    def test_new_command_pins_version(self, mock_console, mock_prompt, mock_path, mock_open, mock_version):
        # Setup
        mock_version.return_value = "1.2.3"
        mock_prompt.ask.side_effect = ["uv"]  # unused: chip/freq/stdlib passed as args

        mock_proj_dir = MagicMock()
        mock_path.return_value = mock_proj_dir
        mock_proj_dir.exists.return_value = False

        # Mock open context manager
        mock_file_handle = MagicMock()
        mock_open.return_value.__enter__.return_value = mock_file_handle

        # Execute
        # --chip selects advanced mode; freq/stdlib/pkg_manager are supplied so the
        # scaffold runs straight through without any interactive prompt.
        try:
            new(
                "myproj",
                chip="pic16f84a",
                freq=4_000_000,
                stdlib=[],
                pkg_manager="uv",
                no_git=True,
            )
        except:
            pass

        # Verify
        # Check that importlib.metadata.version was called with correct package
        mock_version.assert_called_with("pymcu-compiler")


if __name__ == "__main__":
    unittest.main()

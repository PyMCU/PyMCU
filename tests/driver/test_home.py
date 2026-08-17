# tests/driver/test_home.py
#
# Tests for the local server behind `pymcu home`. It installs packages, so the
# checks that matter most here are the ones that decide who is allowed to ask.
# A real server is started on an ephemeral loopback port; nothing is installed.

import json
import threading
import urllib.error
import urllib.request
from pathlib import Path

import pytest
import tomlkit

from src.driver.commands import libraries as lib_cmd
from src.driver.home.server import serve


PROJECT = """\
[project]
name = "demo"
version = "0.1.0"
dependencies = []

[tool.pymcu]
board = "arduino_uno"
frequency = 16000000
sources = "src"
entry = "main.py"
"""


@pytest.fixture
def server(tmp_path, monkeypatch):
    """A running home server for a throwaway project, with no index behind it."""
    path = tmp_path / "pyproject.toml"
    path.write_text(PROJECT)
    (tmp_path / "src").mkdir()
    (tmp_path / "src" / "main.py").write_text("def main():\n    pass\n")

    # No network, and no cache from the developer's own machine.
    monkeypatch.setattr(lib_cmd, "CACHE_FILE", tmp_path / "cache.json")
    monkeypatch.setattr(lib_cmd, "CACHE_DIR", tmp_path / "cache")
    monkeypatch.setattr(lib_cmd, "_download_index", lambda url: {"v": 1, "libraries": []})
    monkeypatch.setattr(lib_cmd, "_installed_libraries", lambda project: ([], []))

    project = lib_cmd.Project(path, tomlkit.loads(path.read_text()))
    httpd, token = serve(project, port=0)
    thread = threading.Thread(target=httpd.serve_forever, daemon=True)
    thread.start()

    yield f"http://127.0.0.1:{httpd.server_port}", token, tmp_path

    httpd.shutdown()
    httpd.server_close()


def _get(url, token=None, headers=None):
    request = urllib.request.Request(url)
    if token:
        request.add_header("X-PyMCU-Token", token)
    for key, value in (headers or {}).items():
        request.add_header(key, value)
    with urllib.request.urlopen(request, timeout=5) as response:
        return response.status, json.loads(response.read().decode())


def _post(url, token, payload, headers=None):
    request = urllib.request.Request(
        url, data=json.dumps(payload).encode(), method="POST")
    request.add_header("Content-Type", "application/json")
    if token:
        request.add_header("X-PyMCU-Token", token)
    for key, value in (headers or {}).items():
        request.add_header(key, value)
    with urllib.request.urlopen(request, timeout=5) as response:
        return response.status, json.loads(response.read().decode())


class TestWhoMayAsk:
    """The server installs packages; these are the checks that gate that."""

    def test_no_token_is_refused(self, server):
        base, _token, _root = server
        with pytest.raises(urllib.error.HTTPError) as exc:
            _get(f"{base}/api/project")
        assert exc.value.code == 403

    def test_a_wrong_token_is_refused(self, server):
        base, _token, _root = server
        with pytest.raises(urllib.error.HTTPError) as exc:
            _get(f"{base}/api/project", token="not-the-token")
        assert exc.value.code == 403

    def test_a_foreign_origin_is_refused(self, server):
        """Another tab must not be able to drive this, token or not."""
        base, token, _root = server
        with pytest.raises(urllib.error.HTTPError) as exc:
            _get(f"{base}/api/project", token=token,
                 headers={"Origin": "https://example.test"})
        assert exc.value.code == 403

    def test_the_page_itself_needs_no_token(self, server):
        """
        The shell is public; the data behind it is not. Asserted on the app's
        own mount point rather than on any wording, so a rewrite of the copy is
        not a failing test.
        """
        base, _token, _root = server
        with urllib.request.urlopen(base, timeout=5) as response:
            body = response.read()
        assert response.status == 200
        assert b'id="view"' in body and b"PyMCU" in body

    def test_an_unknown_route_is_a_404(self, server):
        base, token, _root = server
        with pytest.raises(urllib.error.HTTPError) as exc:
            _get(f"{base}/api/nope", token=token)
        assert exc.value.code == 404


class TestPayloads:
    def test_the_project_carries_its_flash_budget(self, server):
        base, token, _root = server
        status, body = _get(f"{base}/api/project", token=token)
        assert status == 200
        assert body["chip"] == "atmega328p"
        # Without this the byte figures are numbers with nothing to compare to.
        assert body["flash_total"] == 32768
        assert body["layer"] == "native"

    def test_boards_are_grouped_for_the_picker(self, server):
        base, token, _root = server
        _status, body = _get(f"{base}/api/boards", token=token)
        names = [b["name"] for g in body["groups"] for b in g["boards"]]
        assert "arduino_uno" in names and "raspberry_pi_pico" in names

    def test_an_empty_index_is_not_an_error(self, server):
        base, token, _root = server
        _status, body = _get(f"{base}/api/index", token=token)
        assert body["libraries"] == []
        assert body["error"] == ""


class TestConfig:
    def test_changing_the_board_rewrites_pyproject(self, server):
        base, token, root = server
        _status, body = _post(f"{base}/api/config", token,
                              {"board": "raspberry_pi_pico", "layer": "micropython"})
        assert body["ok"]
        assert body["project"]["chip"] == "rp2040"
        assert body["project"]["flash_total"] == 2097152

        text = (root / "pyproject.toml").read_text()
        assert 'board = "raspberry_pi_pico"' in text
        assert 'stdlib = ["micropython"]' in text
        # The board's own clock came with it.
        assert "frequency = 125000000" in text

    def test_a_bad_board_is_refused_and_nothing_is_written(self, server):
        base, token, root = server
        before = (root / "pyproject.toml").read_text()
        with pytest.raises(urllib.error.HTTPError) as exc:
            _post(f"{base}/api/config", token, {"board": "teensy41"})
        assert exc.value.code == 400
        assert (root / "pyproject.toml").read_text() == before

    def test_installing_without_a_name_is_refused(self, server):
        base, token, _root = server
        with pytest.raises(urllib.error.HTTPError) as exc:
            _post(f"{base}/api/install", token, {})
        assert exc.value.code == 400


class TestAssembly:
    """
    The assembly is compiled on request, not stored: it is only true for one
    chip and one compiler version, which are exactly the two things that move
    underneath a published file.
    """

    def test_an_unknown_library_says_to_install_it_first(self, server):
        base, token, _root = server
        _status, body = _get(f"{base}/api/assembly?name=nothing", token=token)
        assert body["ok"] is False
        assert "install" in body["error"].lower()

    def test_no_name_is_refused(self, server):
        base, token, _root = server
        _status, body = _get(f"{base}/api/assembly?name=", token=token)
        assert body["ok"] is False

    def test_it_needs_the_token_like_everything_else(self, server):
        base, _token, _root = server
        with pytest.raises(urllib.error.HTTPError) as exc:
            _get(f"{base}/api/assembly?name=neopixel")
        assert exc.value.code == 403

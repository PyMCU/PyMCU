# -----------------------------------------------------------------------------
# PyMCU CLI Driver -- `pymcu home`
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

"""
The local package manager: a browser UI that installs into *this* project.

A page on pymcu.org can list libraries but cannot touch a .venv, which is the
one thing worth doing from a catalogue. Hence a server on loopback.

That server installs packages, so it executes things, and everything below
follows from taking that seriously: loopback only, an ephemeral port, a session
token required on every call, and Origin/Host checked so a page in another tab
cannot drive it. None of that is optional.

The handlers are thin on purpose. Every one of them calls the same functions
`pymcu install` calls -- the UI knows nothing the CLI does not, which is what
keeps the two from drifting apart.
"""

from __future__ import annotations

import json
import secrets
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

from ..commands import libraries as lib_cmd
from ..core.libraries import read_description, site_packages_of
from ..core.project_config import apply_changes, available_boards, describe

STATIC_DIR = Path(__file__).parent / "static"
MAX_BODY = 64 * 1024


class HomeState:
    """What the server needs to answer, resolved once at startup."""

    def __init__(self, project: lib_cmd.Project, token: str):
        self.project = project
        self.token = token
        self.lock = threading.Lock()

    def reload_project(self) -> None:
        """Re-read pyproject.toml after a change written by an install."""
        import tomlkit

        self.project = lib_cmd.Project(
            self.project.path,
            tomlkit.loads(self.project.path.read_text(encoding="utf-8")),
        )


def _json_bytes(payload) -> bytes:
    return json.dumps(payload).encode("utf-8")


class HomeHandler(BaseHTTPRequestHandler):
    server_version = "pymcu-home"
    state: HomeState

    # ------------------------------------------------------------------
    # Plumbing
    # ------------------------------------------------------------------

    def log_message(self, fmt, *args):  # noqa: D401 - silence the default stderr log
        """Requests are not interesting; failures are reported in the response."""

    def _send(self, status: int, body: bytes, content_type: str) -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        # The page is served from this same origin; nothing else may embed or
        # read it.
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(body)

    def _send_json(self, payload, status: int = 200) -> None:
        self._send(status, _json_bytes(payload), "application/json; charset=utf-8")

    def _error(self, status: int, message: str) -> None:
        self._send_json({"error": message}, status=status)

    def _authorised(self, query: dict) -> bool:
        """
        A request is ours only if it carries the session token and comes from us.

        The Host check is what stops DNS rebinding: a name that resolves to
        127.0.0.1 would otherwise let any page on the internet talk to this
        server with the browser's own network access.
        """
        host = (self.headers.get("Host") or "").split(":")[0]
        if host not in ("127.0.0.1", "localhost"):
            return False

        origin = self.headers.get("Origin")
        if origin is not None:
            hostname = urlparse(origin).hostname
            if hostname not in ("127.0.0.1", "localhost"):
                return False

        sent = self.headers.get("X-PyMCU-Token") or (query.get("token", [""])[0])
        return secrets.compare_digest(sent or "", self.state.token)

    def _read_body(self) -> dict:
        length = int(self.headers.get("Content-Length") or 0)
        if length <= 0 or length > MAX_BODY:
            return {}
        try:
            return json.loads(self.rfile.read(length).decode("utf-8"))
        except (ValueError, UnicodeDecodeError):
            return {}

    # ------------------------------------------------------------------
    # Routes
    # ------------------------------------------------------------------

    def do_GET(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler's naming
        parsed = urlparse(self.path)
        query = parse_qs(parsed.query)

        if parsed.path in ("/", "/index.html"):
            self._serve_page()
            return

        if not parsed.path.startswith("/api/"):
            self._error(404, "not found")
            return

        if not self._authorised(query):
            self._error(403, "bad or missing session token")
            return

        if parsed.path == "/api/project":
            self._send_json(self._project_payload())
        elif parsed.path == "/api/boards":
            self._send_json({"groups": available_boards(self.state.project.flavors)})
        elif parsed.path == "/api/installed":
            self._send_json({"libraries": self._installed_payload()})
        elif parsed.path == "/api/index":
            self._send_json(self._index_payload(refresh=query.get("refresh") == ["1"]))
        else:
            self._error(404, "not found")

    def do_POST(self) -> None:  # noqa: N802
        parsed = urlparse(self.path)
        query = parse_qs(parsed.query)

        if not self._authorised(query):
            self._error(403, "bad or missing session token")
            return

        body = self._read_body()

        if parsed.path == "/api/config":
            self._apply_config(body)
            return

        name = str(body.get("name", "")).strip()
        if not name:
            self._error(400, "no library name given")
            return

        if parsed.path == "/api/install":
            self._run_change(name, install=True, verify=bool(body.get("verify", True)))
        elif parsed.path == "/api/uninstall":
            self._run_change(name, install=False, verify=False)
        else:
            self._error(404, "not found")

    # ------------------------------------------------------------------
    # Payloads -- the same shapes `pymcu libraries --json` returns
    # ------------------------------------------------------------------

    def _serve_page(self) -> None:
        try:
            html = (STATIC_DIR / "index.html").read_bytes()
        except OSError:
            self._error(500, "the UI is missing from this installation")
            return
        self._send(200, html, "text/html; charset=utf-8")

    def _apply_config(self, body: dict) -> None:
        """
        Change [tool.pymcu], then say what the project looks like now.

        Retargeting can strand an installed library -- a driver for AVR on a
        board that is suddenly RP2040 -- so the installed list comes back with
        the answer, re-judged against the new chip. Nothing is uninstalled
        behind the user's back; the row simply starts saying it does not fit.
        """
        if not self.state.lock.acquire(blocking=False):
            self._error(409, "another change is already running")
            return
        try:
            project = self.state.project
            frequency = body.get("frequency")
            result = apply_changes(
                project.path, project.doc,
                board=body.get("board"),
                frequency=int(frequency) if frequency not in (None, "") else None,
                layer=body.get("layer"),
                sources=body.get("sources"),
                entry=body.get("entry"),
            )
            if not result.ok:
                self._error(400, result.message)
                return

            self.state.reload_project()
            self._send_json({
                "ok": True,
                "message": result.message,
                "changed": result.changed,
                "project": self._project_payload(),
                "installed": self._installed_payload(),
            })
        except (OSError, ValueError) as exc:
            self._error(500, f"{type(exc).__name__}: {exc}")
        finally:
            self.state.lock.release()

    def _project_payload(self) -> dict:
        from ..commands.build import FLASH_SIZES

        project = self.state.project
        return {
            **describe(project.doc, project.root),
            "name": str(project.doc.get("project", {}).get("name", "") or project.root.name),
            "root": str(project.root.resolve()),
            "venv": str(project.venv) if project.venv.exists() else "",
            # The part's own flash, which is what turns a byte count into a
            # proportion someone can act on.
            "flash_total": FLASH_SIZES.get(project.chip.lower(), 0),
        }

    def _installed_payload(self) -> list[dict]:
        """
        Installed libraries, each with what it measured on this chip.

        The measurement lives in the index, not in the wheel -- a figure baked
        into a release would be frozen to the compiler of that day -- so the two
        are joined here by distribution name.
        """
        installed, _ = lib_cmd._installed_libraries(self.state.project)
        search = site_packages_of(self.state.project.venv) if self.state.project.venv.exists() else None
        index, _source = lib_cmd.fetch_index()
        by_distribution = {
            str(entry.get("distribution", "")).lower(): entry
            for entry in lib_cmd._entries(index)
        }

        payload = []
        for lib in installed:
            item = lib_cmd._installed_json(lib, self.state.project)
            entry = by_distribution.get(lib.distribution.lower())
            item["measured"] = (lib_cmd.measured_for(entry, self.state.project.chip)
                                if entry else
                                {"targets": {}, "flash": None, "ram": None,
                                 "compiler": "", "date": ""})
            # Read from the installed wheel rather than the index: it is the
            # copy this project actually has, and it needs no network.
            readme, kind = read_description(lib.distribution, search)
            item["readme"] = readme or str((entry or {}).get("readme", ""))
            item["readme_type"] = kind or str((entry or {}).get("readme_type", ""))
            payload.append(item)
        return payload

    def _index_payload(self, refresh: bool) -> dict:
        index, source = lib_cmd.fetch_index(refresh=refresh)
        project = self.state.project
        installed = {
            lib.distribution.lower()
            for lib in lib_cmd._installed_libraries(project)[0]
        }

        entries = []
        for entry in lib_cmd._entries(index):
            reasons = (lib_cmd.entry_verdict(entry, project.chip, project.flavors)
                       if project.chip else ["no board or target declared"])
            entries.append(lib_cmd._entry_json(entry, reasons, installed, project.chip))

        entries.sort(key=lambda e: (not e["fits"], e["name"]))
        return {
            "source": source,
            "error": lib_cmd.last_index_error(),
            "libraries": entries,
        }

    # ------------------------------------------------------------------
    # Changes
    # ------------------------------------------------------------------

    def _run_change(self, name: str, *, install: bool, verify: bool) -> None:
        """
        Install or remove, then report what the project looks like afterwards.

        Serialised: two installs at once would race on the same .venv and on
        pyproject.toml, and the UI makes that easy to trigger by double-clicking.
        """
        if not self.state.lock.acquire(blocking=False):
            self._error(409, "another change is already running")
            return
        try:
            result = (lib_cmd.install_library(self.state.project, name, verify=verify)
                      if install else
                      lib_cmd.uninstall_library(self.state.project, name))
            self.state.reload_project()
            self._send_json({
                "ok": result.ok,
                "message": result.message,
                "log": result.log,
                "project": self._project_payload(),
                "installed": self._installed_payload(),
            })
        except Exception as exc:                       # pragma: no cover - defensive
            self._error(500, f"{type(exc).__name__}: {exc}")
        finally:
            self.state.lock.release()


def serve(project: lib_cmd.Project, port: int = 0) -> tuple[ThreadingHTTPServer, str]:
    """Start the server on loopback and return it with its session token."""
    token = secrets.token_urlsafe(24)
    handler = type("BoundHomeHandler", (HomeHandler,), {"state": HomeState(project, token)})
    httpd = ThreadingHTTPServer(("127.0.0.1", port), handler)
    return httpd, token

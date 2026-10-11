# -----------------------------------------------------------------------------
# PyMCU native-module support: a minimal reader for Unix `ar` archives
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
"""
Just enough of the PyPI ``ar`` package for CircuitPython's ``tools/ar_util.py``.

``mpy_ld.py`` links ``libgcc.a`` (a Cortex-M0 has no divide instruction, so even
``x // 3`` becomes a call into it) and reads the archive through ``ar_util``, which
does ``from ar import Archive`` and silently falls back to ``None`` when the package
is absent, then fails with "Please run 'pip install ar'". The archive format is a
handful of fixed-width header fields, so this reads it directly and a user no longer
has to install a package for it.

``pymcu natmod`` puts this directory on PYTHONPATH of the ``mpy_ld.py`` it runs, and
only when the interpreter it picked cannot ``import ar`` itself. The surface is
exactly what ``ar_util`` calls: ``Archive(fileobj)``, iterating it for entries that
have a ``.name``, and ``Archive.open(name, "rb")``.

Handled: the GNU and SysV long-name table (``//`` and ``/123``), the trailing slash
GNU puts on names, the BSD ``#1/len`` form, and the symbol-table members (``/``,
``__.SYMDEF``), which are skipped.
"""

import io

_MAGIC = b"!<arch>\n"
_HEADER = 60


class _Entry:
    def __init__(self, name, data):
        self.name = name
        self._data = data

    @property
    def size(self):
        return len(self._data)


class Archive:
    def __init__(self, fileobj):
        raw = fileobj.read()
        if raw[:8] != _MAGIC:
            raise ValueError("not an ar archive")
        self._entries = {}
        self._order = []
        names_table = b""
        pos = 8
        while pos + _HEADER <= len(raw):
            h = raw[pos:pos + _HEADER]
            if h[58:60] != b"`\n":
                raise ValueError("corrupt ar header at offset %d" % pos)
            name = h[0:16].decode("ascii", "replace").rstrip()
            size = int(h[48:58].decode("ascii").strip() or "0")
            data = raw[pos + _HEADER:pos + _HEADER + size]
            pos += _HEADER + size + (size & 1)

            if name == "//":                      # GNU long-name table
                names_table = data
                continue
            if name in ("/", "/SYM64/", "__.SYMDEF", "__.SYMDEF SORTED"):
                continue                          # symbol table
            if name.startswith("#1/"):            # BSD: the name leads the data
                n = int(name[3:])
                name = data[:n].rstrip(b"\0").decode("ascii", "replace")
                data = data[n:]
            elif name.startswith("/") and name[1:].isdigit():
                start = int(name[1:])
                end = names_table.index(b"\n", start)
                name = names_table[start:end].decode("ascii", "replace").rstrip("/")
            else:
                name = name.rstrip("/")
            if name not in self._entries:
                self._order.append(name)
            self._entries[name] = _Entry(name, data)

    def __iter__(self):
        return (self._entries[n] for n in self._order)

    def open(self, entry, mode="rb"):
        name = entry if isinstance(entry, str) else entry.name
        return io.BytesIO(self._entries[name]._data)

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False

# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
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

"""
Read the sections and symbols of a linked 32-bit little-endian ELF.

The profiler and the build report used to shell out to avr-nm and avr-size. Neither
ships with the WASI toolchain, and avr-nm was found at three hard-coded paths. The
linked firmware carries everything they read, so the driver reads it directly.

The letters returned for symbols are the ones `nm --format=bsd` prints, so code that
was written against avr-nm keeps its filters unchanged: t/T for code, d/D and b/B for
data and BSS, a/A for absolute symbols.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass
from pathlib import Path

SHT_PROGBITS = 1
SHT_SYMTAB = 2
SHT_NOBITS = 8
SHF_WRITE = 0x1
SHF_ALLOC = 0x2
SHF_EXECINSTR = 0x4
SHN_UNDEF = 0
SHN_LORESERVE = 0xFF00
SHN_ABS = 0xFFF1
SHN_COMMON = 0xFFF2
STB_LOCAL = 0
STB_WEAK = 2
STT_OBJECT = 1
STT_SECTION = 3
STT_FILE = 4


@dataclass(frozen=True)
class Section:
    name: str
    address: int
    size: int
    flags: int
    kind: int


@dataclass(frozen=True)
class Symbol:
    name: str
    value: int
    size: int
    kind: str       # the letter nm prints for it: t T d D b B a A r R n N c W V
    section: str    # the section it is defined in, "*ABS*" for absolute symbols


@dataclass(frozen=True)
class Elf:
    sections: tuple[Section, ...]
    symbols: tuple[Symbol, ...]


def _nm_kind(bind: int, sym_type: int, shndx: int, section: Section | None) -> str:
    """The nm letter for a defined symbol, from its binding, type and section."""
    if bind == STB_WEAK:
        return "V" if sym_type == STT_OBJECT else "W"
    if shndx == SHN_ABS:
        letter = "A"
    elif shndx == SHN_COMMON:
        letter = "C"
    elif section is None:
        letter = "N"
    elif section.flags & SHF_EXECINSTR:
        letter = "T"
    elif section.kind == SHT_NOBITS:
        letter = "B"
    elif section.flags & SHF_WRITE:
        letter = "D"
    elif section.flags & SHF_ALLOC:
        letter = "R"
    else:
        letter = "N"
    return letter if bind != STB_LOCAL else letter.lower()


def read_elf(path: Path | str) -> Elf | None:
    """Parse the section and symbol tables of *path*, or None when it is not one.

    Anything that is not a 32-bit little-endian ELF, or whose tables are damaged,
    returns None; callers decide what a missing answer means for them.
    """
    try:
        data = Path(path).read_bytes()
        if len(data) < 52 or data[:6] != b"\x7fELF\x01\x01":
            return None
        hdr = struct.unpack_from("<HHIIIIIHHHHHH", data, 16)
        e_shoff, e_shentsize, e_shnum, e_shstrndx = hdr[5], hdr[10], hdr[11], hdr[12]
        if not e_shoff or e_shentsize < 40 or not 0 < e_shstrndx < e_shnum:
            return None
        raw = [struct.unpack_from("<10I", data, e_shoff + i * e_shentsize)
               for i in range(e_shnum)]

        def cstring(offset: int, base: int) -> str:
            start = base + offset
            return data[start:data.index(b"\0", start)].decode("utf-8", "replace")

        shstr_base = raw[e_shstrndx][4]
        sections = tuple(
            Section(cstring(sh[0], shstr_base), sh[3], sh[5], sh[2], sh[1])
            for sh in raw
        )

        symbols = []
        for sh in raw:
            if sh[1] != SHT_SYMTAB or sh[9] < 16:       # Elf32_Sym is 16 bytes
                continue
            str_base = raw[sh[6]][4]                    # sh_link -> its string table
            for off in range(sh[4], sh[4] + sh[5], sh[9]):
                st_name, st_value, st_size, st_info, _other, st_shndx = \
                    struct.unpack_from("<IIIBBH", data, off)
                sym_type, bind = st_info & 0xF, st_info >> 4
                if not st_name or sym_type in (STT_SECTION, STT_FILE):
                    continue
                if st_shndx == SHN_UNDEF:
                    continue
                if st_shndx == SHN_ABS:
                    section_name, section = "*ABS*", None
                elif 0 < st_shndx < min(len(sections), SHN_LORESERVE):
                    section_name, section = sections[st_shndx].name, sections[st_shndx]
                else:
                    section_name, section = "", None
                symbols.append(Symbol(
                    name=cstring(st_name, str_base),
                    value=st_value,
                    size=st_size,
                    kind=_nm_kind(bind, sym_type, st_shndx, section),
                    section=section_name,
                ))
        return Elf(sections=sections, symbols=tuple(symbols))
    except (IndexError, TypeError, ValueError, struct.error, OSError):
        return None


def symbols(path: Path | str) -> list[Symbol] | None:
    """Every defined symbol, sorted by name the way nm sorts them, or None."""
    elf = read_elf(path)
    if elf is None:
        return None
    return sorted(elf.symbols, key=lambda s: s.name)


def section_sizes(path: Path | str) -> dict[str, int] | None:
    """{section: size} for the sections `avr-size -A` lists, or None.

    That is every allocated section plus the PROGBITS ones that carry contents
    without being allocated -- .avr.prop on AVR is one of those, and it is part of
    the size report. Symbol and string tables are neither, so they stay out.
    """
    elf = read_elf(path)
    if elf is None:
        return None
    return {s.name: s.size for s in elf.sections
            if s.flags & SHF_ALLOC or s.kind == SHT_PROGBITS}


def text_symbol_addrs(path: Path | str) -> dict[str, int] | None:
    """{name: byte address} for the symbols defined in .text, or None.

    The map the build report and the blockmap resolver use: one address per name,
    the one the linker assigned.
    """
    elf = read_elf(path)
    if elf is None:
        return None
    return {s.name: s.value for s in elf.symbols if s.section == ".text"}

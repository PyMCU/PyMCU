# -----------------------------------------------------------------------------
# PyMCU fs -- the romfs file protocol (RFC 0008, phase 1).
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
#
# There is no filesystem on the target and no file table either. A file is a blob
# the driver embedded in flash at build time ([tool.pymcu] files = [...] or the
# auto-embed of a literal open("name")), and a handle is a compile-time view over
# it -- (base, length, pos) -- that the compiler lowers directly.
#
#   f = open("font5x8.bin", "rb")     # resolved while compiling, not at run time
#   hdr = f.read(2)                   # a view; hdr[0] is a flash load
#   f.seek(off)                       # pos = off
#   f.close()
#
# Everything in this module is a declaration for tools and for the CPython oracle:
# the compiler answers open() and the handle's methods itself, before module
# resolution. Bodies raise CompileError so a use that somehow reaches them is a
# compile-time diagnostic, not silently-wrong firmware.

from pymcu.exceptions import CompileError
from pymcu.types import const


class RomFile:
    # The file protocol the compiler implements: read(n) with n known at compile
    # time, readinto(buf), readline(max) with a bound, seek/tell, close, and with.
    # name is the embedded name as open() spelled it.
    name: str = ""

    def read(self, n: const[int]) -> bytes:
        raise CompileError("romfs read() is answered by the compiler")

    def readinto(self, buf: bytearray) -> int:
        raise CompileError("romfs readinto() is answered by the compiler")

    def readline(self, max: const[int]) -> bytes:
        raise CompileError("romfs readline() is answered by the compiler")

    def seek(self, off: int, whence: const[int] = 0) -> int:
        raise CompileError("romfs seek() is answered by the compiler")

    def tell(self) -> int:
        raise CompileError("romfs tell() is answered by the compiler")

    def close(self) -> None:
        raise CompileError("romfs close() is answered by the compiler")

    def __enter__(self) -> "RomFile":
        return self

    def __exit__(self, exception_type, exception_value, traceback) -> None:
        self.close()

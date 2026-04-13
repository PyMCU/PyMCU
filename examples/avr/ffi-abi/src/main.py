# PyMCU -- ffi-abi: AVR calling-convention (ABI) validation for @extern FFI
#
# Each @extern function is an ABI probe that echoes one of its arguments back
# to the caller. By calling f(10, 20, 30) and verifying which value is returned
# we prove that PyMCU placed each argument in the correct AVR register:
#
#   arg0 -> R24    arg1 -> R22    arg2 -> R20    arg3 -> R18
#   uint8 return -> R24
#
# The non-commutative abi_sub8(a, b) = a - b verifies argument ORDER: swapped
# args would yield (30 - 100) mod 256 = 186 (0xBA) instead of 70 (0x46).
#
# The post-call test allocates a local variable (stored in a callee-saved
# register R4-R15 by the PyMCU codegen), executes a C call that clobbers
# working registers, and verifies the local value is unchanged afterwards.
# This validates that avr-gcc correctly saves/restores callee-saved registers
# across the C function boundary.
#
# Expected UART output (9600 baud, 16 MHz):
#   "ABI\n"      -- boot banner
#   "0:0A\n"     -- abi_echo_arg0(10, 20, 30)   = 10 = 0x0A  (arg0 via R24)
#   "1:14\n"     -- abi_echo_arg1(10, 20, 30)   = 20 = 0x14  (arg1 via R22)
#   "2:1E\n"     -- abi_echo_arg2(10, 20, 30)   = 30 = 0x1E  (arg2 via R20)
#   "3:04\n"     -- abi_echo_arg3(1, 2, 3, 4)   =  4 = 0x04  (arg3 via R18)
#   "S:46\n"     -- abi_sub8(100, 30)            = 70 = 0x46  (order: not 0xBA)
#   "K:AA\n"     -- local 0xAA survives a C call (callee-saved reg preserved)
#   "OK\n"       -- done
#
from pymcu.types import uint8, inline
from pymcu.hal.uart import UART
from pymcu.ffi import extern


@extern("abi_echo_arg0")
def abi_echo_arg0(a: uint8, b: uint8, c: uint8) -> uint8:
    pass

@extern("abi_echo_arg1")
def abi_echo_arg1(a: uint8, b: uint8, c: uint8) -> uint8:
    pass

@extern("abi_echo_arg2")
def abi_echo_arg2(a: uint8, b: uint8, c: uint8) -> uint8:
    pass

@extern("abi_echo_arg3")
def abi_echo_arg3(a: uint8, b: uint8, c: uint8, d: uint8) -> uint8:
    pass

@extern("abi_sub8")
def abi_sub8(a: uint8, b: uint8) -> uint8:
    pass


@inline
def write_hex(uart: UART, tag: uint8, val: uint8):
    uart.write(tag)
    uart.write(':')
    uart.write_hex(val)
    uart.write('\n')


def main():
    uart = UART(9600)
    uart.println("ABI")

    # arg0 (R24): pass (10, 20, 30), expect 10 = 0x0A
    r0: uint8 = abi_echo_arg0(10, 20, 30)
    write_hex(uart, '0', r0)

    # arg1 (R22): pass (10, 20, 30), expect 20 = 0x14
    r1: uint8 = abi_echo_arg1(10, 20, 30)
    write_hex(uart, '1', r1)

    # arg2 (R20): pass (10, 20, 30), expect 30 = 0x1E
    r2: uint8 = abi_echo_arg2(10, 20, 30)
    write_hex(uart, '2', r2)

    # arg3 (R18): pass (1, 2, 3, 4), expect 4 = 0x04
    r3: uint8 = abi_echo_arg3(1, 2, 3, 4)
    write_hex(uart, '3', r3)

    # arg order -- non-commutative: 100 - 30 = 70 = 0x46
    # if args were swapped: 30 - 100 = 186 = 0xBA (uint8 wrap)
    rs: uint8 = abi_sub8(100, 30)
    write_hex(uart, 'S', rs)

    # post-call register preservation: local 0xAA must survive the C call
    saved: uint8 = 0xAA
    dummy: uint8 = abi_sub8(50, 50)   # = 0, exercises the call path
    write_hex(uart, 'K', saved)        # must still be 0xAA

    uart.println("OK")

    while True:
        pass

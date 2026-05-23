# PyMCU -- gc-basic: GC runtime activation and heap allocation
#
# Verifies that:
#   - gc_alloc(n) activates program.NeedsGc, injects gc_init call in main
#   - Three consecutive gc_alloc calls all return non-null (bump allocator works)
#   - GcRoot/GcUnroot injected automatically for named GC_REF locals
#   - Shadow stack and heap layout are correct (no crash, no OOM on small heap)
#
# Output on UART (9600 baud):
#   "GC\n"   -- boot banner (proves gc_init ran without crashing)
#   "A:01\n" -- gc_alloc(8)  returned non-null
#   "B:01\n" -- gc_alloc(16) returned non-null
#   "C:01\n" -- gc_alloc(4)  returned non-null
#   "DONE\n" -- all allocations complete, no crash
#
from pymcu.types import uint16
from pymcu.hal.uart import UART

def main():
    uart = UART(9600)
    uart.println("GC")

    ptr1 = gc_alloc(8)
    addr1: uint16 = bitcast(uint16, ptr1)
    if addr1 != 0:
        uart.println("A:01")
    else:
        uart.println("A:00")

    ptr2 = gc_alloc(16)
    addr2: uint16 = bitcast(uint16, ptr2)
    if addr2 != 0:
        uart.println("B:01")
    else:
        uart.println("B:00")

    ptr3 = gc_alloc(4)
    addr3: uint16 = bitcast(uint16, ptr3)
    if addr3 != 0:
        uart.println("C:01")
    else:
        uart.println("C:00")

    uart.println("DONE")

    while True:
        pass

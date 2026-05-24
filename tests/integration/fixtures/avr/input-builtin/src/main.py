# PyMCU -- input-builtin: input() builtin echo fixture
#
# Output on serial at 115200 baud:
#   "IN\n"    -- boot banner
#   ">"       -- prompt emitted before each read
#   <count>\n -- decimal byte count of the received line
#
# The test injects a line over the virtual serial port and verifies
# that input() emits the prompt, reads until newline, and the buffer
# contains the expected bytes.
#
from pymcu.types import uint8

print("IN")
while True:
    line: bytearray = input(">")
    n: uint8 = 0
    while n < 64:
        if line[n] == 0:
            break
        n = n + 1
    print(n)

# expect: match
# doc: docs/language/limitations.md:377
from typing import Optional
from pymcu.types import uint8
class Dev:
    def __init__(self, pin: Optional[uint8] = None):
        self.pin = pin
    def read(self):
        if self.pin is None:
            return 99
        return self.pin
a = Dev()
b = Dev(7)
print(a.read())
print(b.read())
print("END")

# expect: match
# doc: docs/language/roadmap.md:56
class Pin:
    def __init__(self, n):
        self.n = n
    def read(self):
        return self.n
class Bus:
    def __init__(self, pins):
        self.pins = pins
    def total(self):
        s = 0
        for p in self.pins:
            s = s + p.read()
        return s
bus = Bus([Pin(1), Pin(2), Pin(3)])
print(bus.total())
print("END")

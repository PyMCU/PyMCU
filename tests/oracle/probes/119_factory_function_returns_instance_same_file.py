# expect: match
# doc: docs/language/roadmap.md:24
# tracked: #429
class Sensor:
    def __init__(self, base):
        self.base = base
    def read(self):
        return self.base + 1

def make_sensor(base: int) -> Sensor:
    return Sensor(base)

s = make_sensor(9)
print(s.read())
print("END")

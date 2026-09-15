# expect: match
# doc: docs/language/limitations.md:278
class Sensor:
    def __init__(self):
        self.count = 0
    def begin(self):
        self.count = self.count + 1

def reset(dev: Sensor) -> None:
    dev.begin()
    dev.count = 0

class Controller:
    def __init__(self):
        self.s = Sensor()
    def run(self):
        reset(self.s)
        print(self.s.count)

s = Sensor()
reset(s)
print(s.count)
c = Controller()
c.run()
print("END")

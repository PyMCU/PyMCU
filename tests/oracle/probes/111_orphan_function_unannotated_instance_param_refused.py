# expect: refuse 'dev' is an integer
# doc: docs/language/limitations.md:281
class Sensor:
    def __init__(self):
        self.count = 0
    def begin(self):
        self.count = self.count + 1

def reset(dev):
    dev.begin()
    dev.count = 0

s = Sensor()
reset(s)
print(s.count)
print("END")

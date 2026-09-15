# expect: match
# doc: docs/language/roadmap.md:24
# tracked: #426
class Sensor:
    def __init__(self):
        self.count = 0

def read(self):
    return self.count

Sensor.read = read

s = Sensor()
print(s.read())
print("END")

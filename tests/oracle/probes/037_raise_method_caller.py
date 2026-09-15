# expect: match
# doc: docs/language/roadmap.md:33
class Sensor:
    def read(self, raw):
        if raw > 5:
            raise ValueError("range")
        return raw
s = Sensor()
try:
    print(s.read(9))
except ValueError:
    print(42)
print("END")

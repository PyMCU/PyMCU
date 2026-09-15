# expect: match
# doc: docs/language/roadmap.md:26
# tracked: #391
class Device:
    SCALE = 5
    def read(self):
        return self.SCALE + 1
d = Device()
print(d.SCALE)
print(d.read())
print("END")

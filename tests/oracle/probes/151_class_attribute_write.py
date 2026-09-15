# expect: match
# doc: docs/language/roadmap.md:26
class Device:
    SCALE = 5
d = Device()
d.SCALE = 9
print(d.SCALE)
print("END")

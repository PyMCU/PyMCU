# expect: match
# doc: docs/language/roadmap.md:55
# tracked: #394
class Pin:
    def __init__(self, n):
        self.n = n
    def read(self):
        return self.n + 1
for p in [Pin(n) for n in (2, 3, 4)]:
    print(p.read())
print("END")

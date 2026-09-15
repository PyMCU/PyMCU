# expect: match
# doc: docs/language/roadmap.md:30
class Gate:
    def __init__(self, v):
        self.v = v
    def __enter__(self):
        return self
    def __exit__(self, typ, val, tb):
        self.v = self.v + 10
        return False
a = Gate(1)
b = Gate(2)
with a as x, b as y:
    print(x.v + y.v)
print(a.v)
print(b.v)
print("END")

# expect: match
# doc: docs/language/roadmap.md:61
# tracked: #395
class Num:
    def __init__(self, v):
        self.v = v
    def __eq__(self, other):
        return self.v == other.v
    def __le__(self, other):
        return self.v <= other.v
    def __sub__(self, other):
        return Num(self.v - other.v)
    def __mul__(self, other):
        return Num(self.v * other.v)
a = Num(5)
b = Num(3)
print(a == Num(5))
print(a <= b)
print((a - b).v)
print((a * b).v)
print("END")

# expect: match
# doc: docs/language/roadmap.md:61
class Num:
    def __init__(self, v):
        self.v = v
    def __add__(self, other):
        return Num(self.v + other.v)
    def __lt__(self, other):
        return self.v < other.v
c = Num(2) + Num(5)
print(c.v)
print(Num(2) < Num(5))
print("END")

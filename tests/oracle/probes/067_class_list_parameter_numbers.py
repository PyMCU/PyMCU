# expect: match
# doc: docs/language/roadmap.md:57
class Table:
    def __init__(self, values):
        self.values = values
    def total(self):
        s = 0
        for v in self.values:
            s = s + v
        return s
print(Table([4, 5, 6]).total())
print("END")

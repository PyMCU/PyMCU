# expect: match
# doc: docs/language/roadmap.md:26
class Cell:
    def __init__(self):
        self._value = 1
    @property
    def value(self):
        return self._value
    @value.setter
    def value(self, new):
        self._value = new + 1
c = Cell()
c.value = 9
print(c.value)
print("END")

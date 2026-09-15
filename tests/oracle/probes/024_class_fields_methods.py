# expect: match
# doc: docs/language/roadmap.md:26
class Box:
    def __init__(self, value):
        self.value = value
    def bump(self, n):
        self.value = self.value + n
        return self.value
b = Box(4)
print(b.bump(3))
print(b.value)
print("END")

# expect: match
# doc: docs/language/roadmap.md:28
class Base:
    def __init__(self, a=2):
        self.a = a
class Child(Base):
    def __init__(self, b=5, a=3):
        super().__init__(a)
        self.b = b
    def total(self):
        return self.a + self.b
c = Child()
print(c.total())
print("END")

# expect: refuse no runtime class object
# doc: docs/language/roadmap.md:92
class Util:
    @classmethod
    def make(cls):
        return cls()
    def __init__(self):
        self.value = 5

m = Util.make()
print(m.value)
print("END")

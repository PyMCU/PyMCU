# expect: refuse requires a list, tuple, set or dict literal
# doc: LANGUAGE_ROADMAP.md:40
class Box:
    def __init__(self, v):
        self.v = v
    def __contains__(self, item):
        return item == self.v
b = Box(5)
print(5 in b)
print("END")

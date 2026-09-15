# expect: match
# doc: docs/language/roadmap.md:61
class Flag:
    def __init__(self, v):
        self.v = v
    def __bool__(self):
        return self.v != 0
class Holder:
    def __init__(self):
        self.flag = Flag(1)
h = Holder()
if h.flag:
    print(7)
else:
    print(8)
print("END")

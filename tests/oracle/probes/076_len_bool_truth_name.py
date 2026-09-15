# expect: match
# doc: docs/language/roadmap.md:61
class Bag:
    def __init__(self, n):
        self.n = n
    def __len__(self):
        return self.n
b = Bag(2)
if b:
    print(1)
else:
    print(0)
print(len(b))
print("END")

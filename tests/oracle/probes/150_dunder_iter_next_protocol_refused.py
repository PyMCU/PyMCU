# expect: refuse does not run the iterator protocol
# doc: docs/language/roadmap.md:19
class Counter:
    def __init__(self, n):
        self.n = n
        self.i = 0
    def __iter__(self):
        return self
    def __next__(self):
        if self.i >= self.n:
            raise StopIteration
        v = self.i
        self.i = self.i + 1
        return v
for x in Counter(4):
    print(x)
print("END")

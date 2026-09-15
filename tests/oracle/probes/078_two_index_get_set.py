# expect: match
# doc: docs/language/roadmap.md:61
# tracked: #397
class Matrix:
    def __init__(self):
        self.value = 0
    def __setitem__(self, key, value):
        x, y = key
        self.value = x * 10 + y + value
    def __getitem__(self, key):
        x, y = key
        return x * 10 + y + self.value
m = Matrix()
m[2, 3] = 4
print(m[1, 2])
print("END")

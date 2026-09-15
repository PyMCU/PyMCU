# expect: match
# doc: docs/language/roadmap.md:37
# tracked: #392
class Buf:
    def __init__(self):
        self.data = bytearray(b"abcd")
    def __len__(self):
        return 4
    def __getitem__(self, i):
        return self.data[i]
b = Buf()
print(b[1:3])
print("END")

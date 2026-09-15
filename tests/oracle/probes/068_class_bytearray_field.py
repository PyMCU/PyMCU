# expect: match
# doc: docs/language/roadmap.md:57
class Holder:
    def __init__(self, buf):
        self.buf = buf
    def set(self, i, v):
        self.buf[i] = v
buf = bytearray(b"abc")
h = Holder(buf)
h.set(1, 90)
print(buf)
print("END")

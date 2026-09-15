# expect: match
# doc: docs/language/roadmap.md:59
buf = bytearray(b"abcdef")
buf[1:4] = b"XYZ"
print(buf)
print("END")

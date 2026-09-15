# expect: match
# doc: docs/language/roadmap.md:59
buf = bytearray(b"abcdef")
print(buf[1:5:2])
print("END")

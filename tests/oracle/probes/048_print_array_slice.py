# expect: match
# doc: docs/language/roadmap.md:37
buf = bytearray(b"\xcc\x10\xca\xfe")
print(buf[0:2])
print("END")

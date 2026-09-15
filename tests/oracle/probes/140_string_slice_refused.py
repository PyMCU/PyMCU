# expect: refuse Slice indexing is only supported on named fixed-size arrays
# doc: docs/language/roadmap.md:60
s = "hello"
print(s[1:3])
print("END")

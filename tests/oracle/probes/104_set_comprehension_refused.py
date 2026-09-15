# expect: refuse set comprehensions
# doc: docs/language/limitations.md:501
s = {x + 1 for x in [1, 2, 3]}
print(2 in s)
print("END")

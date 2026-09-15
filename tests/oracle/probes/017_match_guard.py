# expect: match
# doc: docs/language/roadmap.md:22
x = 12
match x:
    case n if n > 10:
        print(n)
    case _:
        print(0)
print("END")

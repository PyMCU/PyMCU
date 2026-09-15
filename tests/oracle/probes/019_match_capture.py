# expect: match
# doc: docs/language/roadmap.md:22
x = 9
match x:
    case value:
        print(value + 1)
print("END")

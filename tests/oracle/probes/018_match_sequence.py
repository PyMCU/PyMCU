# expect: match
# doc: docs/language/roadmap.md:22
# tracked: #401
pair = [4, 5]
match pair:
    case [4, y]:
        print(y)
    case _:
        print(0)
print("END")

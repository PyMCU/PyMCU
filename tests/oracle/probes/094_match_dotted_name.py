# expect: match
# doc: docs/language/roadmap.md:22
class Codes:
    OK = 2
x = 2
match x:
    case Codes.OK:
        print("ok")
    case _:
        print("bad")
print("END")

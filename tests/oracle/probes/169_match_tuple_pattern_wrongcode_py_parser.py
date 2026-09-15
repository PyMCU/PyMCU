# expect: match
# doc: https://github.com/PyMCU/PyMCU/issues/439
# frontend: py-parser
# tracked: #439
pair = (4, 5)
match pair:
    case (4, y):
        print(y)
    case _:
        print(0)
print("END")

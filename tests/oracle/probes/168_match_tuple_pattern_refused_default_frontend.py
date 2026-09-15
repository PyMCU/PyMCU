# expect: refuse tuples are not supported as runtime values
# doc: https://github.com/PyMCU/PyMCU/issues/439
# frontend: default
pair = (4, 5)
match pair:
    case (4, y):
        print(y)
    case _:
        print(0)
print("END")

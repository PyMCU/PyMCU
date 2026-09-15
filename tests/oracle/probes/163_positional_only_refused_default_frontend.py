# expect: refuse Expected parameter name
# doc: https://github.com/PyMCU/PyMCU/issues/389
# frontend: default
def add(x, y, /):
    return x + y
print(add(2, 3))
print("END")

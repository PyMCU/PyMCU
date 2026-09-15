# expect: refuse round() is a Python builtin that PyMCU does not provide
# doc: src/compiler/IR/IRGenerator/Call.cs:4511
def make(x: float) -> float:
    return round(x)
print(make(3.7))
print("END")

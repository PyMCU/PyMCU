# expect: match
# doc: docs/language/roadmap.md:35
def quot(a, b):
    return a // b
try:
    print(quot(8, 0))
except ZeroDivisionError:
    print(100)
print("END")

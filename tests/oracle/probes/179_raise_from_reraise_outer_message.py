# expect: match
# doc: docs/language/limitations.md:217
def risky():
    raise ValueError("bad reading")

def wrap():
    try:
        risky()
    except ValueError as err:
        raise TypeError("wrapped") from err

try:
    wrap()
except TypeError as e:
    print(e.args[0])
print("END")

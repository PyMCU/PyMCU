# expect: match
# doc: docs/language/roadmap.md:33
def risky():
    raise ValueError("bad")
try:
    risky()
except ValueError as e:
    print(e.args[0])
print("END")

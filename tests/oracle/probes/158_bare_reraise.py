# expect: match
# doc: docs/language/roadmap.md:33
def risky():
    raise ValueError("boom")
def wrapper():
    try:
        risky()
    except ValueError:
        print("caught, reraising")
        raise
try:
    wrapper()
except ValueError:
    print("outer caught")
print("END")

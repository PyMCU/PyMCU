# expect: match
# doc: docs/language/roadmap.md:33
def fail():
    raise ValueError("boom")
fail()

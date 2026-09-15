# expect: match
# doc: docs/language/roadmap.md:23
def show(**kwargs):
    return kwargs['a'] + kwargs['b']
print(show(a=1, b=2))
print("END")

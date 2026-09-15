# expect: match
# doc: docs/language/roadmap.md:32
count = 1
def inc():
    global count
    count = count + 2
inc()
print(count)
print("END")

# expect: match
# doc: docs/language/roadmap.md:33
class SensorError(Exception):
    pass
def risky(x):
    if x > 5:
        raise SensorError("too high")
    return x
try:
    print(risky(9))
except SensorError:
    print(-1)
print("END")

# expect: match
# doc: docs/rfcs/0005-raise-message-deferred-print.md
def risky(temp: float) -> int:
    if temp > 100.0:
        raise ValueError(f"too hot: {temp}")
    return 1

try:
    v = risky(200.5)
except ValueError as e:
    print(e)
print("END")

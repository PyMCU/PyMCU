# expect: match
# doc: docs/rfcs/0005-raise-message-deferred-print.md
def risky(code: int, temp: float) -> int:
    if code > 100:
        raise ValueError(f"bad code {code} at {temp}")
    return code

try:
    v = risky(200, 36.5)
except ValueError as e:
    print(e)
print("END")

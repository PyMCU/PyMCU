# expect: match
# doc: docs/rfcs/0005-raise-message-deferred-print.md
def risky(code: int) -> int:
    if code > 100:
        raise ValueError(f"bad code {code}")
    return code

try:
    v = risky(200)
except ValueError as e:
    print(e)
print("END")

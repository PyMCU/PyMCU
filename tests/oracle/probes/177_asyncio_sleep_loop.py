# expect: match
# doc: docs/language/roadmap.md:70
import asyncio
async def worker(n):
    total = 0
    for i in range(n):
        await asyncio.sleep(0)
        total = total + i
    print(total)
asyncio.run(worker(4))
print("END")

# expect: refuse only `await asyncio.sleep(n)`
# doc: docs/language/roadmap.md:70
import asyncio
async def worker(n, tag):
    for i in range(n):
        await asyncio.sleep(0)
async def main():
    await asyncio.gather(worker(2, 1), worker(2, 2))
asyncio.run(main())
print("END")

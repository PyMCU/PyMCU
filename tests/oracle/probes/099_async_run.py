# expect: match
# doc: LANGUAGE_ROADMAP.md:370
import asyncio
async def job():
    await asyncio.sleep(0)
    print(5)
asyncio.run(job())
print("END")

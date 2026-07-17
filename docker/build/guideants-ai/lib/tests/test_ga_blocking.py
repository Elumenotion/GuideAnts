import asyncio
import time
import unittest

from ga_blocking import await_blocking


class GaBlockingTests(unittest.IsolatedAsyncioTestCase):
    async def test_await_blocking_does_not_starve_event_loop(self) -> None:
        completed = asyncio.Event()

        def slow_blocking() -> int:
            time.sleep(0.05)
            return 42

        async def heartbeat() -> None:
            await asyncio.sleep(0.01)
            completed.set()

        generation = asyncio.create_task(await_blocking(slow_blocking))
        heartbeat_task = asyncio.create_task(heartbeat())
        result, _ = await asyncio.gather(generation, heartbeat_task)
        self.assertTrue(completed.is_set())
        self.assertEqual(result, 42)


if __name__ == "__main__":
    unittest.main()

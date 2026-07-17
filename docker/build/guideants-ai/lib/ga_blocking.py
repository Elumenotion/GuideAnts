"""Run blocking inference/engine work off the asyncio event loop."""

from __future__ import annotations

import asyncio
from collections.abc import Callable
from typing import ParamSpec, TypeVar

P = ParamSpec("P")
R = TypeVar("R")


async def await_blocking(func: Callable[P, R], /, *args: P.args, **kwargs: P.kwargs) -> R:
    return await asyncio.to_thread(func, *args, **kwargs)

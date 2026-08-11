"""Public ScriptExecutionAgent client for talking-head video jobs."""

from .client import (
    VideoClientError,
    cancel_talking_head_job,
    get_talking_head_job,
    materialize_talking_head_result,
    submit_talking_head,
)

__all__ = [
    "VideoClientError",
    "cancel_talking_head_job",
    "get_talking_head_job",
    "materialize_talking_head_result",
    "submit_talking_head",
]

"""Public ScriptExecutionAgent client for talking-head video jobs."""

from .client import (
    VideoClientError,
    cancel_image_job,
    cancel_talking_head_job,
    get_image_job,
    get_talking_head_job,
    materialize_image_result,
    materialize_talking_head_result,
    submit_image_edit,
    submit_image_generate,
    submit_talking_head,
)

__all__ = [
    "VideoClientError",
    "cancel_image_job",
    "cancel_talking_head_job",
    "get_image_job",
    "get_talking_head_job",
    "materialize_image_result",
    "materialize_talking_head_result",
    "submit_image_edit",
    "submit_image_generate",
    "submit_talking_head",
]

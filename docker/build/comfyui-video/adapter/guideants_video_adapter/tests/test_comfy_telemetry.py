from __future__ import annotations

from guideants_video_adapter.comfy_telemetry import (
    ComfyProgressListener,
    format_progress,
    merge_progress,
    queue_state_for_prompt,
)


def test_merge_progress_updates_timestamp_and_fields() -> None:
    current = {"phase": "queued", "message": "queued", "updated_at": 1.0}
    merged = merge_progress(current, phase="sampling", step=3, max_steps=20, percent=15.0)
    assert merged["phase"] == "sampling"
    assert merged["step"] == 3
    assert merged["max_steps"] == 20
    assert merged["percent"] == 15.0
    assert merged["updated_at"] > 1.0


def test_format_progress_includes_sampling_details() -> None:
    rendered = format_progress(
        {
            "phase": "sampling",
            "message": "sampling 5/20",
            "node_id": "7",
            "node_class": "WanVideoSampler",
            "step": 5,
            "max_steps": 20,
            "percent": 25.0,
        }
    )
    assert "sampling" in rendered
    assert "WanVideoSampler" in rendered
    assert "5/20" in rendered


def test_queue_state_for_prompt_reports_running_and_pending_positions() -> None:
    payload = {
        "queue_running": [[1, "running-prompt", {}, {}, []]],
        "queue_pending": [[2, "pending-prompt", {}, {}, []], [3, "other", {}, {}, []]],
    }
    assert queue_state_for_prompt(payload, "running-prompt") == {
        "queue_position": 0,
        "queue_remaining": 2,
    }
    assert queue_state_for_prompt(payload, "pending-prompt") == {
        "queue_position": 1,
        "queue_remaining": 2,
    }


def test_should_log_progress_skips_routing_noise() -> None:
    from guideants_video_adapter.comfy_telemetry import should_log_progress

    base = {
        "phase": "sampling",
        "message": "sampling 5/20",
        "node_class": "WanVideoSampler",
        "step": 5,
        "max_steps": 20,
        "queue_position": 0,
        "queue_remaining": 1,
        "last_event": "progress",
        "seed": 42,
    }
    assert should_log_progress(None, base) is True
    noisy = dict(base)
    noisy["queue_remaining"] = 0
    noisy["last_event"] = "status"
    noisy["updated_at"] = 999.0
    assert should_log_progress(base, noisy) is False
    stepped = dict(base)
    stepped["step"] = 6
    stepped["message"] = "sampling 6/20"
    assert should_log_progress(base, stepped) is True


def test_listener_maps_progress_events() -> None:
    updates: list[dict] = []
    listener = ComfyProgressListener(
        "http://127.0.0.1:8188",
        "client-1",
        "prompt-1",
        {"7": {"class_type": "WanVideoSampler"}},
        updates.append,
    )
    listener._handle_message(
        '{"type":"progress","data":{"value":4,"max":20,"prompt_id":"prompt-1","node":"7"}}'
    )
    assert updates[-1]["phase"] == "sampling"
    assert updates[-1]["node_class"] == "WanVideoSampler"
    assert updates[-1]["step"] == 4
    assert updates[-1]["max_steps"] == 20

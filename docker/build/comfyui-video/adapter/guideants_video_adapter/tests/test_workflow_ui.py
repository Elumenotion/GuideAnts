from __future__ import annotations

import json
from pathlib import Path

import pytest

from guideants_video_adapter.workflow_ui import api_prompt_to_ui_workflow, is_api_prompt


def test_is_api_prompt_detects_api_graph() -> None:
    assert is_api_prompt({"3": {"class_type": "KSampler", "inputs": {}}})
    assert not is_api_prompt({"nodes": [], "links": []})


def test_api_prompt_to_ui_workflow_builds_nodes_and_links() -> None:
    api_prompt = {
        "1": {
            "class_type": "CheckpointLoaderSimple",
            "inputs": {"ckpt_name": "model.safetensors"},
        },
        "2": {
            "class_type": "CLIPTextEncode",
            "inputs": {"text": "hello", "clip": ["1", 1]},
        },
    }
    object_info = {
        "CheckpointLoaderSimple": {
            "input": {"required": {"ckpt_name": ["COMBO", {"options": []}]}},
            "output": ["MODEL", "CLIP", "VAE"],
            "output_name": ["MODEL", "CLIP", "VAE"],
        },
        "CLIPTextEncode": {
            "input": {
                "required": {
                    "text": ["STRING", {"multiline": True}],
                    "clip": ["CLIP"],
                }
            },
            "output": ["CONDITIONING"],
            "output_name": ["CONDITIONING"],
        },
    }

    ui = api_prompt_to_ui_workflow(api_prompt, object_info)
    assert ui["version"] == 0.4
    assert len(ui["nodes"]) == 2
    assert len(ui["links"]) == 1
    assert ui["links"][0][1:4] == [1, 1, 2]
    assert "id" not in ui

    labeled = api_prompt_to_ui_workflow(
        api_prompt,
        object_info,
        workflow_id="qwen-image-v1__abc",
        filename="guideants-jobs/qwen-image-v1__abc.json",
    )
    assert labeled["id"] == "qwen-image-v1__abc"
    assert labeled["filename"] == "guideants-jobs/qwen-image-v1__abc.json"


def test_api_prompt_to_ui_workflow_from_inpaint_template() -> None:
    template_path = (
        Path(__file__).resolve().parents[3]
        / "workflows"
        / "qwen-image-edit-bf16-inpaint-v1.json"
    )
    api_prompt = json.loads(template_path.read_text(encoding="utf-8"))
    object_info = {
        "VAEDecode": {
            "input": {"required": {"samples": ["LATENT"], "vae": ["VAE"]}},
            "output": ["IMAGE"],
        },
        "SaveImage": {
            "input": {"required": {"filename_prefix": ["STRING"], "images": ["IMAGE"]}},
            "output": [],
        },
        "LoadImage": {
            "input": {"required": {"image": ["COMBO", {"options": []}]}},
            "output": ["IMAGE", "MASK"],
        },
        "KSampler": {
            "input": {
                "required": {
                    "model": ["MODEL"],
                    "seed": ["INT"],
                    "steps": ["INT"],
                    "cfg": ["FLOAT"],
                    "sampler_name": ["COMBO", {"options": []}],
                    "scheduler": ["COMBO", {"options": []}],
                    "positive": ["CONDITIONING"],
                    "negative": ["CONDITIONING"],
                    "latent_image": ["LATENT"],
                    "denoise": ["FLOAT"],
                }
            },
            "output": ["LATENT"],
        },
    }
    ui = api_prompt_to_ui_workflow(api_prompt, object_info)
    assert len(ui["nodes"]) == len(api_prompt)


def test_publish_job_ui_workflow_writes_job_and_running(tmp_path: Path) -> None:
    from guideants_video_adapter.workflow_ui import publish_job_ui_workflow

    ui = {"nodes": [{"id": 1}], "links": [], "version": 0.4}
    path = publish_job_ui_workflow(tmp_path, "qwen-image-v1", "abc123", ui)
    assert path.name == "qwen-image-v1__abc123.json"
    assert path.is_file()
    assert (tmp_path / "_RUNNING__qwen-image-v1.json").is_file()
    assert json.loads(path.read_text(encoding="utf-8"))["nodes"][0]["id"] == 1


def test_publish_job_ui_workflow_rejects_unsafe_names(tmp_path: Path) -> None:
    from guideants_video_adapter.workflow_ui import publish_job_ui_workflow

    ui = {"nodes": [], "links": []}
    with pytest.raises(ValueError, match="unsafe workflow_version"):
        publish_job_ui_workflow(tmp_path, "../escape", "job1", ui)
    with pytest.raises(ValueError, match="unsafe job_id"):
        publish_job_ui_workflow(tmp_path, "qwen-image-v1", "bad/id", ui)

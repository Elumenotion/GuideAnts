import unittest

from guideants_hf.vision_token_preset import (
    GEMMA_IMAGE_MAX_TOKENS,
    GEMMA_IMAGE_MIN_TOKENS,
    QWEN_IMAGE_MIN_TOKENS,
    apply_alias_vision_token_preset,
    normalize_router_ini_text,
)


class VisionTokenPresetTests(unittest.TestCase):
    def test_qwen_alias_gets_min_tokens_only(self) -> None:
        preset = apply_alias_vision_token_preset(
            "qwen3.6-35b-a3b",
            {"ctx-size": "32768", "image-min-tokens": "512", "image-max-tokens": "2048"},
        )
        self.assertEqual(preset["image-min-tokens"], QWEN_IMAGE_MIN_TOKENS)
        self.assertNotIn("image-max-tokens", preset)
        self.assertEqual(preset["ctx-size"], "32768")

    def test_gemma_alias_gets_280_min_and_max(self) -> None:
        preset = apply_alias_vision_token_preset(
            "gemma-4-E4B-it-GGUF",
            {"ctx-size": "65536", "image-min-tokens": "1024"},
        )
        self.assertEqual(preset["image-min-tokens"], GEMMA_IMAGE_MIN_TOKENS)
        self.assertEqual(preset["image-max-tokens"], GEMMA_IMAGE_MAX_TOKENS)

    def test_other_aliases_strip_vision_token_keys(self) -> None:
        preset = apply_alias_vision_token_preset(
            "llava-7b",
            {"ctx-size": "8192", "image-min-tokens": "1024", "image-max-tokens": "2048"},
        )
        self.assertNotIn("image-min-tokens", preset)
        self.assertNotIn("image-max-tokens", preset)

    def test_no_mmproj_skips_qwen_vision_token_preset(self) -> None:
        preset = apply_alias_vision_token_preset(
            "qwen3.6-35b-a3b",
            {"ctx-size": "32768", "no-mmproj": ""},
        )
        self.assertNotIn("image-min-tokens", preset)
        self.assertEqual(preset["ctx-size"], "32768")

    def test_normalize_router_ini_text_rewrites_sections(self) -> None:
        source = """version = 1

[gemma-4-E4B-it-GGUF]
model = /models-local/llama/gemma-4-E4B-it-GGUF/model.gguf
mmproj = /models-local/llama/gemma-4-E4B-it-GGUF/mmproj-F16.gguf
ctx-size = 65536
image-min-tokens = 1024

[qwen3.6-35b-a3b]
model = /models-local/llama/qwen/model.gguf
mmproj = /models-local/llama/qwen/mmproj.gguf
ctx-size = 32768
"""
        normalized = normalize_router_ini_text(source)
        gemma_section = normalized.split("[gemma-4-E4B-it-GGUF]")[1].split("[qwen3.6-35b-a3b]")[0]
        self.assertIn("image-min-tokens = 280", normalized)
        self.assertIn("image-max-tokens = 280", normalized)
        self.assertNotIn("image-min-tokens = 1024", gemma_section)
        self.assertIn("[qwen3.6-35b-a3b]", normalized)
        qwen_section = normalized.split("[qwen3.6-35b-a3b]")[1]
        self.assertIn("image-min-tokens = 1024", qwen_section)


if __name__ == "__main__":
    unittest.main()

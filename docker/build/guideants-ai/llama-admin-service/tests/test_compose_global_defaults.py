import re
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[5]

COMPOSE_FILES = {
    "cpu": [
        REPO_ROOT / "docker/docker-compose.cpu.yml",
        REPO_ROOT / "docker/docker-compose.ghcr-cpu.yml",
        REPO_ROOT / "docker/docker-compose.cpu.api-only-local-build.yml",
        REPO_ROOT / "installer/docker/docker-compose.cpu.yml",
        REPO_ROOT / "installer/docker/docker-compose.ghcr-cpu.yml",
        REPO_ROOT / "installer/docker/compose/ai-cpu.yml",
    ],
    "cuda": [
        REPO_ROOT / "docker/docker-compose.cuda.yml",
        REPO_ROOT / "docker/docker-compose.ghcr-cuda13.yml",
        REPO_ROOT / "docker/docker-compose.cuda.api-only-local-build.yml",
        REPO_ROOT / "installer/docker/docker-compose.cuda.yml",
        REPO_ROOT / "installer/docker/docker-compose.ghcr-cuda13.yml",
        REPO_ROOT / "installer/docker/compose/ai-cuda13.yml",
    ],
    "rocm": [
        REPO_ROOT / "docker/docker-compose.rocm.yml",
        REPO_ROOT / "docker/docker-compose.ghcr-rocm.yml",
        REPO_ROOT / "installer/docker/docker-compose.rocm.yml",
        REPO_ROOT / "installer/docker/docker-compose.ghcr-rocm.yml",
        REPO_ROOT / "installer/docker/compose/ai-rocm.yml",
    ],
    "vulkan": [
        REPO_ROOT / "docker/docker-compose.vulkan.yml",
        REPO_ROOT / "docker/docker-compose.ghcr-vulkan.yml",
        REPO_ROOT / "installer/docker/docker-compose.vulkan.yml",
        REPO_ROOT / "installer/docker/docker-compose.ghcr-vulkan.yml",
        REPO_ROOT / "installer/docker/compose/ai-vulkan.yml",
    ],
}

COMMON_PINNED = {
    "GA_LLAMA_MODELS_MAX": "${GA_LLAMA_MODELS_MAX:-1}",
    "GA_LLAMA_NO_AUTOLOAD": "${GA_LLAMA_NO_AUTOLOAD:-1}",
    "GA_LLAMA_JINJA": "${GA_LLAMA_JINJA:-1}",
    "GA_LLAMA_CONT_BATCH": "${GA_LLAMA_CONT_BATCH:-1}",
}

COMMON_AUTO = {
    "GA_LLAMA_THREADS": "${GA_LLAMA_THREADS:-}",
    "GA_LLAMA_PARALLEL": "${GA_LLAMA_PARALLEL:-}",
    "GA_LLAMA_GPU_LAYERS": "${GA_LLAMA_GPU_LAYERS:-}",
    "GA_LLAMA_KV_OFFLOAD": "${GA_LLAMA_KV_OFFLOAD:-}",
    "GA_LLAMA_CACHE_TYPE_K": "${GA_LLAMA_CACHE_TYPE_K:-}",
    "GA_LLAMA_CACHE_TYPE_V": "${GA_LLAMA_CACHE_TYPE_V:-}",
    "GA_LLAMA_TENSOR_SPLIT": "${GA_LLAMA_TENSOR_SPLIT:-}",
}

PROFILE_TUNED = {
    "cpu": {
        "GA_LLAMA_KV_UNIFIED": "${GA_LLAMA_KV_UNIFIED:-1}",
        "GA_LLAMA_FLASH_ATTN": "${GA_LLAMA_FLASH_ATTN:-on}",
        "GA_LLAMA_NO_MMAP": "${GA_LLAMA_NO_MMAP:-0}",
    },
    "cuda": {
        "GA_LLAMA_KV_UNIFIED": "${GA_LLAMA_KV_UNIFIED:-1}",
        "GA_LLAMA_FLASH_ATTN": "${GA_LLAMA_FLASH_ATTN:-on}",
        "GA_LLAMA_NO_MMAP": "${GA_LLAMA_NO_MMAP:-0}",
    },
    "rocm": {
        "GA_LLAMA_KV_UNIFIED": "${GA_LLAMA_KV_UNIFIED:-1}",
        "GA_LLAMA_FLASH_ATTN": "${GA_LLAMA_FLASH_ATTN:-on}",
        "GA_LLAMA_NO_MMAP": "${GA_LLAMA_NO_MMAP:-1}",
    },
    "vulkan": {
        "GA_LLAMA_KV_UNIFIED": "${GA_LLAMA_KV_UNIFIED:-0}",
        "GA_LLAMA_FLASH_ATTN": "${GA_LLAMA_FLASH_ATTN:-off}",
        "GA_LLAMA_NO_MMAP": "${GA_LLAMA_NO_MMAP:-0}",
    },
}

AUDIO_BACKENDS = {
    "cpu": "cpu",
    "cuda": "cuda",
    "rocm": "rocm",
    "vulkan": "vulkan",
}

ENV_LINE = re.compile(r"^\s+- (GA_(?:LLAMA|ASR|TTS|SD)_[A-Z0-9_]+)=(.*)$")


def parse_env_map(text: str) -> dict[str, str]:
    values: dict[str, str] = {}
    for line in text.splitlines():
        match = ENV_LINE.match(line)
        if match:
            values[match.group(1)] = match.group(2)
    return values


class ComposeGlobalDefaultsTests(unittest.TestCase):
    def test_compose_templates_expose_homogeneous_global_defaults(self) -> None:
        for profile, paths in COMPOSE_FILES.items():
            expected = {
                **COMMON_PINNED,
                **COMMON_AUTO,
                **PROFILE_TUNED[profile],
                "GA_ASR_BACKEND": AUDIO_BACKENDS[profile],
                "GA_TTS_BACKEND": AUDIO_BACKENDS[profile],
                "GA_SD_OFFLOAD_TO_CPU": "${GA_SD_OFFLOAD_TO_CPU:-0}",
            }
            for path in paths:
                with self.subTest(profile=profile, path=str(path.relative_to(REPO_ROOT))):
                    self.assertTrue(path.is_file(), msg=f"missing compose file: {path}")
                    env_map = parse_env_map(path.read_text(encoding="utf-8"))
                    for key, value in expected.items():
                        self.assertEqual(env_map.get(key), value, msg=f"{key} mismatch in {path}")

    def test_rocm_audio_build_enables_first_class_hip_backend(self) -> None:
        dockerfile = REPO_ROOT / "docker/build/guideants-ai/Dockerfile.rocm"
        text = dockerfile.read_text(encoding="utf-8")

        self.assertIn("-DENGINE_ENABLE_HIP=ON", text)
        self.assertIn("-DENGINE_ENABLE_CUDA=OFF", text)
        self.assertNotIn("-DGGML_HIP=ON", text)

    def test_repository_env_does_not_force_sd_parameters_into_cpu_ram(self) -> None:
        env_file = REPO_ROOT / "docker/.env"
        env_map = dict(
            line.split("=", 1)
            for line in env_file.read_text(encoding="utf-8").splitlines()
            if line and not line.startswith("#") and "=" in line
        )

        self.assertEqual(env_map.get("GA_SD_OFFLOAD_TO_CPU"), "0")
        self.assertEqual(env_map.get("GA_SD_BACKEND"), "gpu")


if __name__ == "__main__":
    unittest.main()

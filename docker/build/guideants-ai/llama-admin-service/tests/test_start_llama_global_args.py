import os
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[5]
START_LLAMA = REPO_ROOT / "docker/build/guideants-ai/start-llama.sh"


class StartLlamaGlobalArgsTests(unittest.TestCase):
    def _run_start_llama_argv(self, *, env: dict[str, str]) -> list[str]:
        if not START_LLAMA.is_file():
            self.skipTest(f"start-llama.sh not found at {START_LLAMA}")

        with tempfile.TemporaryDirectory() as tmp:
            tmp_path = Path(tmp)
            bin_dir = tmp_path / "bin"
            bin_dir.mkdir()
            fake_server = bin_dir / "llama-server"
            fake_server.write_text("#!/bin/sh\nprintf '%s\\n' \"$@\"\n", encoding="utf-8")
            fake_server.chmod(fake_server.stat().st_mode | stat.S_IEXEC)

            script_copy = tmp_path / "start-llama.sh"
            script_copy.write_text(
                START_LLAMA.read_text(encoding="utf-8").replace(
                    "exec /app/llama-server",
                    f"exec {fake_server}",
                ),
                encoding="utf-8",
            )
            script_copy.chmod(script_copy.stat().st_mode | stat.S_IEXEC)

            router_canonical = tmp_path / "router-models.ini"
            router_runtime = tmp_path / "router-models.runtime.ini"
            router_canonical.write_text(
                "version = 1\n\n[alias]\nmodel = /models-local/llama/a/model.gguf\nmmproj = \n",
                encoding="utf-8",
            )

            run_env = os.environ.copy()
            run_env.update(env)
            lib_root = REPO_ROOT / "docker/build/guideants-ai/lib"
            admin_root = REPO_ROOT / "docker/build/guideants-ai/llama-admin-service"
            run_env["PYTHONPATH"] = f"{lib_root}:{admin_root}"
            run_env.setdefault("GA_LLAMA_MODELS_PRESET", str(router_canonical))
            run_env.setdefault("GA_LLAMA_MODELS_RUNTIME_PRESET", str(router_runtime))

            completed = subprocess.run(
                ["bash", str(script_copy)],
                env=run_env,
                capture_output=True,
                text=True,
                check=False,
            )
            if completed.returncode != 0:
                self.fail(
                    "start-llama failed\n"
                    f"stdout:\n{completed.stdout}\n"
                    f"stderr:\n{completed.stderr}"
                )
            return completed.stdout.strip().splitlines()

    def test_parent_argv_is_bootstrap_only(self) -> None:
        argv = self._run_start_llama_argv(
            env={
                "GA_LLAMA_JINJA": "1",
                "GA_LLAMA_KV_UNIFIED": "1",
                "GA_LLAMA_CONT_BATCH": "1",
                "GA_LLAMA_NO_MMAP": "1",
                "GA_LLAMA_FLASH_ATTN": "on",
            },
        )
        self.assertIn("--models-preset", argv)
        self.assertNotIn("--jinja", argv)
        self.assertNotIn("--kv-unified", argv)
        self.assertNotIn("--cont-batching", argv)
        self.assertNotIn("--no-mmap", argv)
        self.assertNotIn("--flash-attn", argv)

    def test_materializes_env_defaults_into_runtime_ini(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_path = Path(tmp)
            router_canonical = tmp_path / "router-models.ini"
            router_runtime = tmp_path / "router-models.runtime.ini"
            router_canonical.write_text(
                "version = 1\n\n[alias]\nmodel = /models-local/llama/a/model.gguf\nmmproj = \n",
                encoding="utf-8",
            )

            bin_dir = tmp_path / "bin"
            bin_dir.mkdir()
            fake_server = bin_dir / "llama-server"
            fake_server.write_text("#!/bin/sh\nprintf '%s\\n' \"$@\"\n", encoding="utf-8")
            fake_server.chmod(fake_server.stat().st_mode | stat.S_IEXEC)
            script_copy = tmp_path / "start-llama.sh"
            script_copy.write_text(
                START_LLAMA.read_text(encoding="utf-8").replace(
                    "exec /app/llama-server",
                    f"exec {fake_server}",
                ),
                encoding="utf-8",
            )
            script_copy.chmod(script_copy.stat().st_mode | stat.S_IEXEC)

            run_env = os.environ.copy()
            run_env.update(
                {
                    "GA_LLAMA_JINJA": "1",
                    "GA_LLAMA_NO_MMAP": "1",
                    "GA_LLAMA_MODELS_PRESET": str(router_canonical),
                    "GA_LLAMA_MODELS_RUNTIME_PRESET": str(router_runtime),
                },
            )
            lib_root = REPO_ROOT / "docker/build/guideants-ai/lib"
            admin_root = REPO_ROOT / "docker/build/guideants-ai/llama-admin-service"
            run_env["PYTHONPATH"] = f"{lib_root}:{admin_root}"

            completed = subprocess.run(
                ["bash", str(script_copy)],
                env=run_env,
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(completed.returncode, 0, completed.stderr)
            runtime_text = router_runtime.read_text(encoding="utf-8")
            self.assertIn("jinja = true", runtime_text)
            self.assertIn("no-mmap = true", runtime_text)

    def test_excludes_alias_scoped_ctx_and_cache_from_parent_argv(self) -> None:
        argv = self._run_start_llama_argv(
            env={
                "GA_LLAMA_JINJA": "1",
                "GA_LLAMA_CTX_SIZE": "131072",
                "GA_LLAMA_CACHE_RAM": "8192",
            },
        )
        self.assertNotIn("--ctx-size", argv)
        self.assertNotIn("--cache-ram", argv)

    def test_omits_env_default_keys_from_parent_argv_when_unset(self) -> None:
        argv = self._run_start_llama_argv(env={"GA_LLAMA_JINJA": "1"})
        self.assertNotIn("--threads", argv)
        self.assertNotIn("--parallel", argv)
        self.assertNotIn("--n-gpu-layers", argv)

    def test_fails_when_router_preset_missing(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_path = Path(tmp)
            missing = tmp_path / "missing.ini"
            bin_dir = tmp_path / "bin"
            bin_dir.mkdir()
            fake_server = bin_dir / "llama-server"
            fake_server.write_text("#!/bin/sh\nprintf '%s\\n' \"$@\"\n", encoding="utf-8")
            fake_server.chmod(fake_server.stat().st_mode | stat.S_IEXEC)
            script_copy = tmp_path / "start-llama.sh"
            script_copy.write_text(
                START_LLAMA.read_text(encoding="utf-8").replace(
                    "exec /app/llama-server",
                    f"exec {fake_server}",
                ),
                encoding="utf-8",
            )
            script_copy.chmod(script_copy.stat().st_mode | stat.S_IEXEC)

            run_env = os.environ.copy()
            run_env["GA_LLAMA_MODELS_PRESET"] = str(missing)
            lib_root = REPO_ROOT / "docker/build/guideants-ai/lib"
            admin_root = REPO_ROOT / "docker/build/guideants-ai/llama-admin-service"
            run_env["PYTHONPATH"] = f"{lib_root}:{admin_root}"

            completed = subprocess.run(
                ["bash", str(script_copy)],
                env=run_env,
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertNotEqual(completed.returncode, 0)
            self.assertIn("router preset not found", completed.stderr)


if __name__ == "__main__":
    unittest.main()

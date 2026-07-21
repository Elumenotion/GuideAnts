import os
import shutil
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[5]
START_LLAMA = REPO_ROOT / "docker/build/guideants-ai/start-llama.sh"


class StartLlamaGlobalArgsTests(unittest.TestCase):
    def _run_start_llama_argv(self, *, env: dict[str, str], router_ini: bool = True) -> list[str]:
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
            if router_ini:
                router_canonical.write_text(
                    "version = 1\n\n[alias]\nmodel = /models-local/llama/a/model.gguf\nmmproj = \n",
                    encoding="utf-8",
                )
                router_runtime.write_text(
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

    def test_routed_mode_includes_jinja_from_env(self) -> None:
        argv = self._run_start_llama_argv(
            env={
                "GA_LLAMA_JINJA": "1",
                "GA_LLAMA_KV_UNIFIED": "1",
                "GA_LLAMA_CONT_BATCH": "1",
            },
        )
        self.assertIn("--models-preset", argv)
        self.assertIn("--jinja", argv)
        self.assertIn("--kv-unified", argv)
        self.assertIn("--cont-batching", argv)

    def test_routed_mode_excludes_alias_scoped_ctx_and_cache(self) -> None:
        argv = self._run_start_llama_argv(
            env={
                "GA_LLAMA_JINJA": "1",
                "GA_LLAMA_CTX_SIZE": "131072",
                "GA_LLAMA_CACHE_RAM": "8192",
            },
        )
        self.assertNotIn("--ctx-size", argv)
        self.assertNotIn("--cache-ram", argv)

    def test_standalone_mode_includes_alias_scoped_ctx_and_cache(self) -> None:
        argv = self._run_start_llama_argv(
            env={
                "GA_LLAMA_JINJA": "1",
                "GA_LLAMA_CTX_SIZE": "131072",
                "GA_LLAMA_CACHE_RAM": "8192",
                "GA_LLAMA_MODELS_PRESET": "",
            },
            router_ini=False,
        )
        self.assertNotIn("--models-preset", argv)
        self.assertIn("--ctx-size", argv)
        self.assertIn("131072", argv)
        self.assertIn("--cache-ram", argv)
        self.assertIn("8192", argv)

    def test_routed_mode_omits_llama_auto_keys_when_unset(self) -> None:
        argv = self._run_start_llama_argv(env={"GA_LLAMA_JINJA": "1"})
        self.assertNotIn("--threads", argv)
        self.assertNotIn("--parallel", argv)
        self.assertNotIn("--n-gpu-layers", argv)


if __name__ == "__main__":
    unittest.main()

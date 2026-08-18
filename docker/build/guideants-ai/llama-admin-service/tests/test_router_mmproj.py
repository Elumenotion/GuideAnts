import os
import tempfile
import unittest
from unittest import mock

import llama_router_ini as router
from guideants_hf.env_default_preset import (
    apply_env_defaults_to_extras,
    resolve_env_default_extras,
)
from guideants_hf.router_mmproj import (
    materialize_router_extras_for_runtime,
    materialize_router_ini_text,
    preset_disables_mmproj,
    resolve_router_runtime_config_path,
)


class RouterMmprojTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmpdir = tempfile.TemporaryDirectory()
        self._ini_dir = self._tmpdir.name

    def tearDown(self) -> None:
        self._tmpdir.cleanup()

    def _ini_path(self, name: str) -> str:
        return os.path.join(self._ini_dir, name)

    def test_preset_disables_mmproj_accepts_boolean_flag(self) -> None:
        self.assertTrue(preset_disables_mmproj({"no-mmproj": ""}))
        self.assertTrue(preset_disables_mmproj({"nommproj": ""}))
        self.assertTrue(preset_disables_mmproj({"mmproj-auto": "false"}))
        self.assertFalse(preset_disables_mmproj({"spec-type": "draft-mtp"}))
        self.assertFalse(preset_disables_mmproj({"mmproj-auto": "true"}))

    def test_materialize_translates_hyphenless_nommproj(self) -> None:
        canonical = (
            "version = 1\n\n"
            "[alias]\n"
            "model = /models-local/llama/a/model.gguf\n"
            "mmproj = /models-local/llama/a/mmproj-F16.gguf\n"
            "nommproj = \n"
        )
        runtime = materialize_router_ini_text(
            canonical,
            parse_router_ini=router.parse_router_ini,
            serialize_router_ini_for_runtime=router.serialize_router_ini_for_runtime,
        )
        self.assertIn("mmproj-auto = false", runtime)
        self.assertNotIn("nommproj", runtime)
        self.assertNotIn("mmproj =", runtime)

    def test_materialize_translates_no_mmproj_to_mmproj_auto_false(self) -> None:
        canonical = (
            "version = 1\n\n"
            "[alias]\n"
            "model = /models-local/llama/a/model.gguf\n"
            "mmproj = /models-local/llama/a/mmproj-F16.gguf\n"
            "no-mmproj = \n"
            "spec-type = draft-mtp\n"
        )
        runtime = materialize_router_ini_text(
            canonical,
            parse_router_ini=router.parse_router_ini,
            serialize_router_ini_for_runtime=router.serialize_router_ini_for_runtime,
        )

        self.assertIn("mmproj-auto = false", runtime)
        self.assertNotIn("no-mmproj", runtime)
        self.assertNotIn("mmproj =", runtime)

        canonical_entries = router.parse_router_ini(canonical)
        self.assertEqual(
            "/models-local/llama/a/mmproj-F16.gguf",
            canonical_entries["alias"].mmproj,
        )

    def test_materialize_restores_mmproj_when_override_removed(self) -> None:
        canonical = (
            "version = 1\n\n"
            "[alias]\n"
            "model = /models-local/llama/a/model.gguf\n"
            "mmproj = /models-local/llama/a/mmproj-F16.gguf\n"
            "spec-type = draft-mtp\n"
        )
        runtime = materialize_router_ini_text(
            canonical,
            parse_router_ini=router.parse_router_ini,
            serialize_router_ini_for_runtime=router.serialize_router_ini_for_runtime,
        )
        self.assertIn(
            "mmproj = /models-local/llama/a/mmproj-F16.gguf",
            runtime,
        )
        self.assertNotIn("mmproj-auto = false", runtime)

    def test_materialize_router_extras_for_runtime(self) -> None:
        extras = materialize_router_extras_for_runtime(
            {"no-mmproj": "", "spec-type": "draft-mtp"},
        )
        self.assertEqual(extras["mmproj-auto"], "false")
        self.assertEqual(extras["spec-type"], "draft-mtp")
        self.assertNotIn("no-mmproj", extras)

    def test_env_defaults_fill_missing_keys(self) -> None:
        env = {
            "GA_LLAMA_JINJA": "1",
            "GA_LLAMA_NO_MMAP": "1",
            "GA_LLAMA_GPU_LAYERS": "999",
        }
        resolved = resolve_env_default_extras({"spec-type": "draft-mtp"}, environ=env)
        self.assertEqual(resolved["jinja"], "true")
        self.assertEqual(resolved["no-mmap"], "true")
        self.assertEqual(resolved["n-gpu-layers"], "999")
        self.assertNotIn("spec-type", resolved)

    def test_env_defaults_do_not_override_alias_keys(self) -> None:
        env = {"GA_LLAMA_JINJA": "1", "GA_LLAMA_GPU_LAYERS": "999"}
        resolved = resolve_env_default_extras(
            {"jinja": "false", "n-gpu-layers": "42"},
            environ=env,
        )
        self.assertEqual(resolved, {})

    def test_runtime_ini_includes_env_defaults(self) -> None:
        canonical = (
            "version = 1\n\n"
            "[alias]\n"
            "model = /models-local/llama/a/model.gguf\n"
            "mmproj = \n"
            "spec-type = draft-mtp\n"
        )
        env = {"GA_LLAMA_JINJA": "1", "GA_LLAMA_NO_MMAP": "1"}
        with mock.patch.dict(os.environ, env, clear=False):
            runtime = materialize_router_ini_text(
                canonical,
                parse_router_ini=router.parse_router_ini,
                serialize_router_ini_for_runtime=router.serialize_router_ini_for_runtime,
            )
        self.assertIn("jinja = true", runtime)
        self.assertIn("no-mmap = true", runtime)
        self.assertIn("spec-type = draft-mtp", runtime)

    def test_runtime_ini_keeps_alias_override_over_env(self) -> None:
        canonical = (
            "version = 1\n\n"
            "[alias]\n"
            "model = /models-local/llama/a/model.gguf\n"
            "mmproj = \n"
            "n-gpu-layers = 42\n"
        )
        env = {"GA_LLAMA_GPU_LAYERS": "999", "GA_LLAMA_JINJA": "1"}
        with mock.patch.dict(os.environ, env, clear=False):
            runtime = materialize_router_ini_text(
                canonical,
                parse_router_ini=router.parse_router_ini,
                serialize_router_ini_for_runtime=router.serialize_router_ini_for_runtime,
            )
        self.assertIn("n-gpu-layers = 42", runtime)
        self.assertIn("jinja = true", runtime)
        self.assertNotIn("n-gpu-layers = 999", runtime)

    def test_apply_env_defaults_to_extras_merges(self) -> None:
        merged = apply_env_defaults_to_extras(
            {"spec-type": "draft-mtp"},
            environ={"GA_LLAMA_JINJA": "1"},
        )
        self.assertEqual(merged["spec-type"], "draft-mtp")
        self.assertEqual(merged["jinja"], "true")

    def test_upsert_writes_runtime_ini_alongside_canonical(self) -> None:
        router.ROUTER_CONFIG_PATH = self._ini_path("canonical.ini")
        runtime_path = resolve_router_runtime_config_path(router.ROUTER_CONFIG_PATH)

        router.upsert_router_entry(
            "alias",
            "/models-local/llama/a/model.gguf",
            "/models-local/llama/a/mmproj-F16.gguf",
            preset={"no-mmproj": "", "spec-type": "draft-mtp"},
            preset_mode="merge",
            trigger_reload=False,
        )

        with open(router.ROUTER_CONFIG_PATH, "r", encoding="utf-8") as handle:
            canonical = handle.read()
        with open(runtime_path, "r", encoding="utf-8") as handle:
            runtime = handle.read()

        self.assertIn("mmproj = /models-local/llama/a/mmproj-F16.gguf", canonical)
        self.assertIn("mmproj-auto = false", canonical)
        self.assertNotIn("no-mmproj", canonical)
        self.assertIn("mmproj-auto = false", runtime)
        self.assertNotIn("no-mmproj", runtime)
        self.assertNotIn("mmproj =", runtime)


class UnrecognizedPresetTests(unittest.TestCase):
    def test_parse_nommproj_error(self) -> None:
        from guideants_hf.unrecognized_preset import parse_unrecognized_option_error

        parsed = parse_unrecognized_option_error(
            "failed to initialize router models: option 'nommproj' not recognized in preset 'Qwen3.8-27B-GGUF'"
        )
        self.assertEqual(parsed, ("nommproj", "Qwen3.8-27B-GGUF"))

    def test_parse_uses_latest_unrecognized_option(self) -> None:
        from guideants_hf.unrecognized_preset import parse_unrecognized_option_error

        parsed = parse_unrecognized_option_error(
            "option 'nommproj' not recognized in preset 'Qwen3.8-27B-GGUF'\n"
            "option 'foobar' not recognized in preset 'alias'\n"
        )
        self.assertEqual(parsed, ("foobar", "alias"))

    def test_drop_nommproj_rewrites_to_mmproj_auto(self) -> None:
        from guideants_hf.unrecognized_preset import drop_unrecognized_option_from_entries

        entries = router.parse_router_ini(
            "version = 1\n\n[Qwen3.8-27B-GGUF]\nmodel = /m/a.gguf\nmmproj = /m/p.gguf\nnommproj = \nctx-size = 8\n"
        )
        changed = drop_unrecognized_option_from_entries(
            entries, option_token="nommproj", alias="Qwen3.8-27B-GGUF"
        )
        self.assertTrue(changed)
        extras = entries["Qwen3.8-27B-GGUF"].extras
        self.assertEqual(extras["mmproj-auto"], "false")
        self.assertNotIn("nommproj", extras)
        self.assertEqual(extras["ctx-size"], "8")

    def test_drop_unknown_key_by_hyphenless_token(self) -> None:
        from guideants_hf.unrecognized_preset import drop_unrecognized_option_from_entries

        entries = router.parse_router_ini(
            "version = 1\n\n[alias]\nmodel = /m/a.gguf\nmmproj = \nfoo-bar = 1\nctx-size = 8\n"
        )
        changed = drop_unrecognized_option_from_entries(
            entries, option_token="foobar", alias="alias"
        )
        self.assertTrue(changed)
        self.assertNotIn("foo-bar", entries["alias"].extras)
        self.assertEqual(entries["alias"].extras["ctx-size"], "8")

    def test_sanitize_canonical_from_log_rewrites_file(self) -> None:
        from guideants_hf.unrecognized_preset import sanitize_canonical_from_log

        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        canonical_path = os.path.join(tmp.name, "router-models.ini")
        log_path = os.path.join(tmp.name, "llama-server.log")
        with open(canonical_path, "w", encoding="utf-8") as handle:
            handle.write(
                "version = 1\n\n[Qwen3.8-27B-GGUF]\nmodel = /m/a.gguf\nmmproj = /m/p.gguf\nno-mmproj = \n"
            )
        with open(log_path, "w", encoding="utf-8") as handle:
            handle.write(
                "E srv  llama_server: failed to initialize router models: option 'nommproj' not recognized in preset 'Qwen3.8-27B-GGUF'\n"
            )
        self.assertTrue(
            sanitize_canonical_from_log(
                canonical_path,
                log_path,
                parse_router_ini=router.parse_router_ini,
                serialize_router_ini=router.serialize_router_ini,
            )
        )
        with open(canonical_path, encoding="utf-8") as handle:
            rewritten = handle.read()
        self.assertIn("mmproj-auto = false", rewritten)
        self.assertNotIn("no-mmproj", rewritten)


if __name__ == "__main__":
    unittest.main()

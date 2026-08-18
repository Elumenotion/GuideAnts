import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

_SERVICE_ROOT = Path(__file__).resolve().parents[1]
if str(_SERVICE_ROOT) not in sys.path:
    sys.path.insert(0, str(_SERVICE_ROOT))

from warmup_plan import (
    SERVICE_LLAMA,
    WarmupPlanDocument,
    WarmupServiceSection,
)
import warmup_orchestrator
from warmup_orchestrator import (
    _aux_services_to_drain_before_llama_with_state,
    _reconcile_aux,
    _run_reconcile_loop,
    _store_plan,
    compute_transitions,
    derive_plan_commands,
    initialize_warmup_executor_on_startup,
    request_warmup_apply,
)
from warmup_state import (
    APPLY_STATUS_PENDING,
    APPLY_STATUS_APPLYING,
    APPLY_STATUS_APPLIED,
    atomic_write_warmup_state,
    build_initial_state_from_plan,
    build_warmup_state_document,
    read_warmup_state,
)


def _sample_document(revision: int = 1, sections: dict | None = None) -> WarmupPlanDocument:
    default_sections = {
        SERVICE_LLAMA: WarmupServiceSection(enabled=True, router_alias="Qwen-Test"),
        "SpeechTranscription": WarmupServiceSection(enabled=False),
        "Embeddings": WarmupServiceSection(enabled=False),
        "SpeechSynthesis": WarmupServiceSection(enabled=False),
        "ImageGeneration": WarmupServiceSection(enabled=False),
    }
    if sections is not None:
        default_sections.update(sections)
    return WarmupPlanDocument(
        schema_version=1,
        revision=revision,
        services=default_sections,
    )


def _sample_document_with_sections(**overrides):
    return _sample_document(revision=1, sections=dict(overrides))


def _mark_loaded(state: dict, service: str, *, ref_key: str, ref_value: str) -> None:
    entry = state["services"][service]
    entry["phase"] = "ready"
    entry[ref_key] = ref_value


class WarmupOrchestratorTests(unittest.TestCase):
    def test_derive_plan_commands_explicit_off_is_always_unload(self) -> None:
        document = _sample_document_with_sections(
            SpeechTranscription=WarmupServiceSection(enabled=False, model_id="asr-model"),
        )
        commands = derive_plan_commands(document)
        self.assertEqual(commands["SpeechTranscription"], "unload")
        self.assertEqual(commands[SERVICE_LLAMA], "load")
        self.assertEqual(commands["Embeddings"], "unload")

    def test_derive_plan_commands_enabled_service_is_load(self) -> None:
        document = _sample_document_with_sections(
            Embeddings=WarmupServiceSection(enabled=True, model_id="new-emb"),
        )
        commands = derive_plan_commands(document)
        self.assertEqual(commands["Embeddings"], "load")

    def test_compute_transitions_legacy_helper_matches_derive_plan_commands(self) -> None:
        document = _sample_document_with_sections(
            Embeddings=WarmupServiceSection(enabled=True, model_id="new-emb"),
        )
        state = build_initial_state_from_plan(document, desired_sha256=document.content_fingerprint())
        transitions = compute_transitions(document, state)
        by_service = {item.service: item.action for item in transitions}
        self.assertEqual(by_service["Embeddings"], "load")

    @mock.patch("warmup_orchestrator._start_apply_worker_if_needed", return_value=True)
    def test_request_apply_starts_worker_even_when_revision_already_applied(self, _mock_start) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            state_path = os.path.join(tmp, ".warmup-state.json")
            os.environ["GA_WARMUP_STATE_PATH"] = state_path
            document = _sample_document_with_sections()
            atomic_write_warmup_state(
                build_warmup_state_document(
                    desired_revision=1,
                    applied_revision=1,
                    apply_status=APPLY_STATUS_APPLIED,
                    apply_error=None,
                    desired_sha256=document.content_fingerprint(),
                    services={},
                )
            )
            result = request_warmup_apply(document)
            self.assertFalse(result["noop"])
            self.assertTrue(result["started"])

    def test_startup_purges_legacy_ini_and_stays_idle(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            state_path = os.path.join(tmp, ".warmup-state.json")
            ini_path = os.path.join(tmp, "warmup-desired.ini")
            os.environ["GA_WARMUP_STATE_PATH"] = state_path
            os.environ["GA_WARMUP_DESIRED_PATH"] = ini_path
            with open(ini_path, "w", encoding="utf-8") as handle:
                handle.write("revision = 99\n[ImageGeneration]\nbundle_id = stale\n")

            initialize_warmup_executor_on_startup()

            self.assertFalse(os.path.exists(ini_path))
            state = read_warmup_state()
            assert state is not None
            self.assertEqual(state["applyStatus"], "idle")
            self.assertEqual(state["services"], {})

    def test_startup_initializes_empty_idle_state_without_loading(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            state_path = os.path.join(tmp, ".warmup-state.json")
            os.environ["GA_WARMUP_STATE_PATH"] = state_path
            atomic_write_warmup_state(
                build_warmup_state_document(
                    desired_revision=1,
                    applied_revision=1,
                    apply_status=APPLY_STATUS_APPLIED,
                    apply_error=None,
                    desired_sha256="stale-plan",
                    services={
                        SERVICE_LLAMA: {
                            "phase": "ready",
                            "routerAlias": "Qwen-Test",
                        }
                    },
                )
            )

            initialize_warmup_executor_on_startup()

            state = read_warmup_state()
            self.assertIsNotNone(state)
            assert state is not None
            self.assertEqual(state["desiredRevision"], 0)
            self.assertEqual(state["appliedRevision"], 0)
            self.assertEqual(state["applyStatus"], "idle")
            self.assertEqual(state["services"], {})

    @mock.patch("warmup_orchestrator._start_apply_worker_if_needed", return_value=True)
    def test_request_apply_starts_worker_when_pending(self, _mock_start) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            state_path = os.path.join(tmp, ".warmup-state.json")
            os.environ["GA_WARMUP_STATE_PATH"] = state_path
            document = _sample_document_with_sections()
            initialize_warmup_executor_on_startup()
            result = request_warmup_apply(document)
            self.assertFalse(result["noop"])
            self.assertTrue(result["started"])

    @mock.patch("warmup_orchestrator.aux_engine_reports_loaded", return_value=True)
    def test_llama_change_drains_engine_reported_warm_aux(self, _mock_loaded) -> None:
        document = _sample_document_with_sections(
            Embeddings=WarmupServiceSection(enabled=True, model_id="qwen3_embedding_0_6b"),
        )
        document.services[SERVICE_LLAMA] = WarmupServiceSection(enabled=True, router_alias="New-Alias")
        drained = _aux_services_to_drain_before_llama_with_state(document, state={})
        self.assertIn("Embeddings", drained)


class FakeEngine:
    """Records engine admin calls so tests can assert GPU drain order."""

    def __init__(self, loaded_llama_aliases=None, loaded_aux=None):
        self.calls: list[tuple] = []
        self._loaded_llama = list(loaded_llama_aliases or [])
        self._loaded_aux = dict(loaded_aux or {})

    def list_llama_models(self):
        return [
            {"id": alias, "status": {"value": "loaded"}}
            for alias in self._loaded_llama
        ]

    def llama_engine_loaded_aliases(self):
        return list(self._loaded_llama)

    def aux_engine_reports_loaded(self, service):
        return service in self._loaded_aux

    def aux_engine_loaded_ref(self, service):
        return self._loaded_aux.get(service)

    def post_aux_load(self, service, model_ref=None, load_field="model_path"):
        self.calls.append(("aux-load", service, model_ref, load_field))
        if model_ref:
            self._loaded_aux[service] = model_ref
        return True

    def post_aux_unload(self, service):
        self.calls.append(("aux-unload", service))
        self._loaded_aux.pop(service, None)
        return True

    def wait_aux_ready(self, service, timeout_seconds=None, expected_model_ref=None):
        return True

    def wait_aux_unloaded(self, service, timeout_seconds=None):
        return True

    def post_llama_load(self, alias):
        self.calls.append(("llama-load", alias))
        self._loaded_llama = [alias]
        return True

    def post_llama_unload(self, alias):
        self.calls.append(("llama-unload", alias))
        self._loaded_llama = [a for a in self._loaded_llama if a != alias]
        return True

    def wait_llama_loaded(self, alias, timeout_seconds=None):
        return True

    def wait_llama_unloaded(self, alias, timeout_seconds=None):
        return True


class WarmupReconcileExecutionTests(unittest.TestCase):
    """End-to-end apply loop with a fake engine (no real HTTP)."""

    def _patch_engine(self, engine: FakeEngine):
        names = [
            "list_llama_models",
            "llama_engine_loaded_aliases",
            "aux_engine_reports_loaded",
            "aux_engine_loaded_ref",
            "post_aux_load",
            "post_aux_unload",
            "wait_aux_ready",
            "wait_aux_unloaded",
            "post_llama_load",
            "post_llama_unload",
            "wait_llama_loaded",
            "wait_llama_unloaded",
        ]
        patchers = [
            mock.patch.object(warmup_orchestrator, name, getattr(engine, name))
            for name in names
        ]
        for patcher in patchers:
            patcher.start()
            self.addCleanup(patcher.stop)

    def _all_loaded_aux(self):
        return {
            "SpeechTranscription": WarmupServiceSection(enabled=True, model_id="asr-model"),
            "Embeddings": WarmupServiceSection(enabled=True, model_id="emb-model"),
            "SpeechSynthesis": WarmupServiceSection(enabled=True, model_id="tts-model"),
            "ImageGeneration": WarmupServiceSection(enabled=True, bundle_id="sd-bundle"),
        }

    def test_llama_change_drains_all_warm_aux_then_restores_in_d11_order(self) -> None:
        engine = FakeEngine(
            loaded_llama_aliases=["Old-Alias"],
            loaded_aux={
                "SpeechTranscription": "asr-model",
                "Embeddings": "emb-model",
                "SpeechSynthesis": "tts-model",
                "ImageGeneration": "sd-bundle",
            },
        )
        self._patch_engine(engine)

        with tempfile.TemporaryDirectory() as tmp:
            os.environ["GA_WARMUP_STATE_PATH"] = os.path.join(tmp, ".warmup-state.json")

            document = _sample_document(revision=2, sections=self._all_loaded_aux())
            document.services[SERVICE_LLAMA] = WarmupServiceSection(enabled=True, router_alias="New-Alias")

            state = build_warmup_state_document(
                desired_revision=1,
                applied_revision=1,
                apply_status=APPLY_STATUS_APPLIED,
                apply_error=None,
                desired_sha256="prior-plan",
                services=build_initial_state_from_plan(
                    document,
                    desired_sha256=document.content_fingerprint(),
                )["services"],
            )
            atomic_write_warmup_state(state)
            _store_plan(document)

            _run_reconcile_loop()

        unloads = [call[1] for call in engine.calls if call[0] == "aux-unload"]
        self.assertEqual(
            unloads,
            ["ImageGeneration", "SpeechSynthesis", "Embeddings", "SpeechTranscription"],
        )

        loads = [call[1] for call in engine.calls if call[0] == "aux-load"]
        self.assertEqual(
            loads,
            ["SpeechTranscription", "Embeddings", "SpeechSynthesis", "ImageGeneration"],
        )

        llama_load_index = next(i for i, call in enumerate(engine.calls) if call[0] == "llama-load")
        last_unload_index = max(i for i, call in enumerate(engine.calls) if call[0] == "aux-unload")
        first_reload_index = min(i for i, call in enumerate(engine.calls) if call[0] == "aux-load")
        self.assertLess(last_unload_index, llama_load_index, "aux must drain before llama load")
        self.assertLess(llama_load_index, first_reload_index, "aux must reload after llama load")

        self.assertIn(("llama-unload", "Old-Alias"), engine.calls)
        self.assertIn(("llama-load", "New-Alias"), engine.calls)

    def test_single_aux_routing_change_does_not_touch_others(self) -> None:
        engine = FakeEngine(
            loaded_llama_aliases=["Primary"],
            loaded_aux={
                "SpeechTranscription": "asr-model",
                "Embeddings": "emb-model",
                "SpeechSynthesis": "tts-model",
                "ImageGeneration": "sd-bundle",
            },
        )
        self._patch_engine(engine)

        with tempfile.TemporaryDirectory() as tmp:
            os.environ["GA_WARMUP_STATE_PATH"] = os.path.join(tmp, ".warmup-state.json")

            sections = self._all_loaded_aux()
            sections["SpeechTranscription"] = WarmupServiceSection(enabled=False, model_id="asr-model")
            document = _sample_document(revision=2, sections=sections)
            document.services[SERVICE_LLAMA] = WarmupServiceSection(enabled=True, router_alias="Primary")

            state = build_warmup_state_document(
                desired_revision=1,
                applied_revision=1,
                apply_status=APPLY_STATUS_APPLIED,
                apply_error=None,
                desired_sha256="prior-plan",
                services=build_initial_state_from_plan(
                    document,
                    desired_sha256=document.content_fingerprint(),
                )["services"],
            )
            atomic_write_warmup_state(state)
            _store_plan(document)

            _run_reconcile_loop()
            final_state = read_warmup_state()

        unloads = [call[1] for call in engine.calls if call[0] == "aux-unload"]
        self.assertEqual(unloads, ["SpeechTranscription"])
        self.assertEqual([call[1] for call in engine.calls if call[0] == "aux-load"], [])
        self.assertEqual([c for c in engine.calls if c[0].startswith("llama")], [])
        self.assertEqual(final_state["applyStatus"], APPLY_STATUS_APPLIED)
        self.assertEqual(final_state["appliedRevision"], 2)

    def test_startup_discards_stale_status_and_does_not_call_engines(self) -> None:
        engine = FakeEngine()
        self._patch_engine(engine)

        with tempfile.TemporaryDirectory() as tmp:
            os.environ["GA_WARMUP_STATE_PATH"] = os.path.join(tmp, ".warmup-state.json")

            document = _sample_document(revision=1, sections=self._all_loaded_aux())
            atomic_write_warmup_state(
                build_warmup_state_document(
                    desired_revision=1,
                    applied_revision=1,
                    apply_status=APPLY_STATUS_APPLIED,
                    apply_error=None,
                    desired_sha256=document.content_fingerprint(),
                    services={
                        SERVICE_LLAMA: {
                            "phase": "ready",
                            "routerAlias": "Primary",
                        }
                    },
                )
            )

            initialize_warmup_executor_on_startup()
            final_state = read_warmup_state()

        self.assertEqual(engine.calls, [])
        assert final_state is not None
        self.assertEqual(final_state["desiredRevision"], 0)
        self.assertEqual(final_state["applyStatus"], "idle")
        self.assertEqual(final_state["appliedRevision"], 0)
        self.assertEqual(final_state["services"], {})

    def test_first_cloud_plan_after_reset_explicitly_unloads_every_aux_engine(self) -> None:
        engine = FakeEngine(
            loaded_aux={
                "SpeechTranscription": "asr-model",
                "Embeddings": "emb-model",
                "SpeechSynthesis": "tts-model",
                "ImageGeneration": "sd-bundle",
            }
        )
        self._patch_engine(engine)

        with tempfile.TemporaryDirectory() as tmp:
            os.environ["GA_WARMUP_STATE_PATH"] = os.path.join(tmp, ".warmup-state.json")
            initialize_warmup_executor_on_startup()
            cloud_plan = _sample_document(
                sections={
                    SERVICE_LLAMA: WarmupServiceSection(enabled=False),
                }
            )
            _store_plan(cloud_plan)

            _run_reconcile_loop()
            final_state = read_warmup_state()

        self.assertEqual(
            [call[1] for call in engine.calls if call[0] == "aux-unload"],
            ["ImageGeneration", "SpeechSynthesis", "Embeddings", "SpeechTranscription"],
        )
        self.assertEqual([call for call in engine.calls if call[0] == "aux-load"], [])
        assert final_state is not None
        self.assertEqual(final_state["applyStatus"], APPLY_STATUS_APPLIED)

    def test_image_generation_load_without_bundle_id_fails_without_engine_calls(self) -> None:
        engine = FakeEngine()
        self._patch_engine(engine)

        with tempfile.TemporaryDirectory() as tmp:
            os.environ["GA_WARMUP_STATE_PATH"] = os.path.join(tmp, ".warmup-state.json")
            document = _sample_document(revision=1)
            atomic_write_warmup_state(
                build_initial_state_from_plan(document, desired_sha256=document.content_fingerprint())
            )
            ok = _reconcile_aux("ImageGeneration", WarmupServiceSection(enabled=False), "load")
            final_state = read_warmup_state()

        self.assertFalse(ok)
        self.assertEqual(engine.calls, [])
        assert final_state is not None
        self.assertEqual(final_state["services"]["ImageGeneration"]["phase"], "failed")
        self.assertIn("bundle_id", final_state["services"]["ImageGeneration"]["error"])


if __name__ == "__main__":
    unittest.main()

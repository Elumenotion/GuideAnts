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
    WarmupPlanDocument,
    WarmupServiceSection,
)
import warmup_orchestrator
from warmup_orchestrator import (
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
            legacy_ini = "warmup" + "-" + "desired" + ".ini"
            ini_path = os.path.join(tmp, legacy_ini)
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
                    services={},
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

class FakeEngine:
    """Records engine admin calls so tests can assert GPU drain order."""

    def __init__(self, loaded_aux=None):
        self.calls: list[tuple] = []
        self._loaded_aux = dict(loaded_aux or {})

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


class WarmupReconcileExecutionTests(unittest.TestCase):
    """End-to-end apply loop with a fake engine (no real HTTP)."""

    def _patch_engine(self, engine: FakeEngine):
        names = [
            "aux_engine_reports_loaded",
            "aux_engine_loaded_ref",
            "post_aux_load",
            "post_aux_unload",
            "wait_aux_ready",
            "wait_aux_unloaded",
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

    def test_aux_off_then_on_unloads_in_order_then_reloads(self) -> None:
        engine = FakeEngine(
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

            # first plan: everything off -> unloads in frozen order
            off_plan = _sample_document(revision=2)
            state = build_warmup_state_document(
                desired_revision=1,
                applied_revision=1,
                apply_status=APPLY_STATUS_APPLIED,
                apply_error=None,
                desired_sha256="prior-plan",
                services=build_initial_state_from_plan(
                    off_plan,
                    desired_sha256=off_plan.content_fingerprint(),
                )["services"],
            )
            atomic_write_warmup_state(state)
            _store_plan(off_plan)

            _run_reconcile_loop()

        unloads = [call[1] for call in engine.calls if call[0] == "aux-unload"]
        self.assertEqual(
            unloads,
            ["ImageGeneration", "SpeechSynthesis", "Embeddings", "SpeechTranscription"],
        )
        self.assertEqual([call[1] for call in engine.calls if call[0] == "aux-load"], [])

    def test_single_aux_routing_change_does_not_touch_others(self) -> None:
        engine = FakeEngine(
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
                    services={},
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
            cloud_plan = _sample_document()
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

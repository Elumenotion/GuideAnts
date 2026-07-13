import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

_SERVICE_ROOT = Path(__file__).resolve().parents[1]
if str(_SERVICE_ROOT) not in sys.path:
    sys.path.insert(0, str(_SERVICE_ROOT))

from warmup_desired_ini import (
    SERVICE_LLAMA,
    WarmupDesiredDocument,
    WarmupServiceSection,
    write_warmup_desired,
)
import warmup_orchestrator
from warmup_orchestrator import (
    _aux_services_to_drain_before_llama,
    _run_reconcile_loop,
    compute_transitions,
    request_warmup_apply,
)
from warmup_state import (
    APPLY_STATUS_APPLYING,
    APPLY_STATUS_APPLIED,
    atomic_write_warmup_state,
    build_initial_state_from_desired,
    build_warmup_state_document,
    read_warmup_state,
)


def _sample_document(revision: int = 1, sections: dict | None = None) -> WarmupDesiredDocument:
    default_sections = {
        SERVICE_LLAMA: WarmupServiceSection(desired="warm", router_alias="Qwen-Test"),
        "SpeechTranscription": WarmupServiceSection(desired="idle"),
        "Embeddings": WarmupServiceSection(desired="idle"),
        "SpeechSynthesis": WarmupServiceSection(desired="idle"),
        "ImageGeneration": WarmupServiceSection(desired="idle"),
    }
    if sections is not None:
        default_sections.update(sections)
    return WarmupDesiredDocument(
        version=1,
        revision=revision,
        updated_at_utc="2026-07-12T19:00:00Z",
        sections=default_sections,
    )


def _sample_document_with_sections(**overrides):
    return _sample_document(revision=1, sections=dict(overrides))


class WarmupOrchestratorTests(unittest.TestCase):
    def test_compute_transitions_only_asr_to_idle(self) -> None:
        document = _sample_document_with_sections(
            SpeechTranscription=WarmupServiceSection(desired="idle", model_id="asr-model"),
        )
        state = build_initial_state_from_desired(document, desired_sha256=document.content_fingerprint())
        state["services"]["SpeechTranscription"]["applied"] = "warm"
        state["services"][SERVICE_LLAMA]["applied"] = "warm"
        transitions = compute_transitions(document, state)
        by_service = {item.service: item.action for item in transitions}
        self.assertEqual(by_service["SpeechTranscription"], "unload")
        self.assertEqual(by_service.get(SERVICE_LLAMA), "noop")
        self.assertEqual(by_service.get("Embeddings"), "noop")

    def test_compute_transitions_model_change_is_load(self) -> None:
        document = _sample_document_with_sections(
            Embeddings=WarmupServiceSection(desired="warm", model_id="new-emb"),
        )
        state = build_initial_state_from_desired(document, desired_sha256=document.content_fingerprint())
        state["services"]["Embeddings"]["applied"] = "warm"
        state["services"]["Embeddings"]["modelId"] = "old-emb"
        transitions = compute_transitions(document, state)
        by_service = {item.service: item.action for item in transitions}
        self.assertEqual(by_service["Embeddings"], "load")

    def test_request_apply_noop_when_revision_applied(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            ini_path = os.path.join(tmp, "warmup-desired.ini")
            state_path = os.path.join(tmp, ".warmup-state.json")
            os.environ["GA_WARMUP_DESIRED_PATH"] = ini_path
            os.environ["GA_WARMUP_STATE_PATH"] = state_path
            document = _sample_document_with_sections()
            write_warmup_desired(document)
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
            result = request_warmup_apply()
            self.assertTrue(result["noop"])
            self.assertEqual(result["appliedRevision"], 1)

    @mock.patch("warmup_orchestrator._start_apply_worker_if_needed", return_value=True)
    def test_request_apply_starts_worker_when_pending(self, _mock_start) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            ini_path = os.path.join(tmp, "warmup-desired.ini")
            state_path = os.path.join(tmp, ".warmup-state.json")
            os.environ["GA_WARMUP_DESIRED_PATH"] = ini_path
            os.environ["GA_WARMUP_STATE_PATH"] = state_path
            document = _sample_document_with_sections()
            write_warmup_desired(document)
            atomic_write_warmup_state(
                build_initial_state_from_desired(document, desired_sha256=document.content_fingerprint())
            )
            result = request_warmup_apply()
            self.assertFalse(result["noop"])
            self.assertTrue(result["started"])


    def test_compute_transitions_llama_change_drains_applied_warm_aux(self) -> None:
        document = _sample_document_with_sections(
            Embeddings=WarmupServiceSection(desired="warm", model_id="qwen3_embedding_0_6b"),
        )
        state = build_initial_state_from_desired(document, desired_sha256=document.content_fingerprint())
        state["services"][SERVICE_LLAMA]["applied"] = "warm"
        state["services"]["Embeddings"]["applied"] = "warm"
        document.sections[SERVICE_LLAMA] = WarmupServiceSection(
            desired="warm",
            router_alias="New-Alias",
        )
        state["services"][SERVICE_LLAMA]["routerAlias"] = "Old-Alias"
        transitions = compute_transitions(document, state)
        by_service = {item.service: item.action for item in transitions}
        self.assertEqual(by_service[SERVICE_LLAMA], "load")
        self.assertEqual(by_service["Embeddings"], "noop")
        drained = _aux_services_to_drain_before_llama(document, state)
        self.assertIn("Embeddings", drained)


class FakeEngine:
    """Records engine admin calls so tests can assert D11 order + GPU drain/restore."""

    def __init__(self, loaded_llama_aliases=None):
        self.calls: list[tuple[str, str]] = []
        self._loaded_llama = list(loaded_llama_aliases or [])

    def list_llama_models(self):
        return [
            {"id": alias, "status": {"value": "loaded"}}
            for alias in self._loaded_llama
        ]

    def post_aux_load(self, service, model_ref=None):
        self.calls.append(("aux-load", service))
        return True

    def post_aux_unload(self, service):
        self.calls.append(("aux-unload", service))
        return True

    def wait_aux_ready(self, service, timeout_seconds=None):
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
    """End-to-end reconcile loop with a fake engine (no real HTTP)."""

    def _patch_engine(self, engine: FakeEngine):
        names = [
            "list_llama_models",
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

    def _all_warm_aux(self):
        return {
            "SpeechTranscription": WarmupServiceSection(desired="warm", model_id="asr-model"),
            "Embeddings": WarmupServiceSection(desired="warm", model_id="emb-model"),
            "SpeechSynthesis": WarmupServiceSection(desired="warm", model_id="tts-model"),
            "ImageGeneration": WarmupServiceSection(desired="warm", bundle_id="sd-bundle"),
        }

    def test_llama_change_drains_all_warm_aux_then_restores_in_d11_order(self) -> None:
        engine = FakeEngine(loaded_llama_aliases=["Old-Alias"])
        self._patch_engine(engine)

        with tempfile.TemporaryDirectory() as tmp:
            os.environ["GA_WARMUP_DESIRED_PATH"] = os.path.join(tmp, "warmup-desired.ini")
            os.environ["GA_WARMUP_STATE_PATH"] = os.path.join(tmp, ".warmup-state.json")

            # Desired: llama warm on a NEW alias, every aux warm.
            document = _sample_document(revision=2, sections=self._all_warm_aux())
            document.sections[SERVICE_LLAMA] = WarmupServiceSection(desired="warm", router_alias="New-Alias")
            write_warmup_desired(document)

            # Applied state: llama warm on Old-Alias, every aux already warm, revision behind.
            state = build_initial_state_from_desired(document, desired_sha256=document.content_fingerprint())
            state["appliedRevision"] = 1
            state["services"][SERVICE_LLAMA]["applied"] = "warm"
            state["services"][SERVICE_LLAMA]["routerAlias"] = "Old-Alias"
            for aux in ("SpeechTranscription", "Embeddings", "SpeechSynthesis", "ImageGeneration"):
                state["services"][aux]["applied"] = "warm"
            atomic_write_warmup_state(state)

            _run_reconcile_loop()

        # GPU drain unloads every warm aux in D11 unload order.
        unloads = [svc for kind, svc in engine.calls if kind == "aux-unload"]
        self.assertEqual(
            unloads,
            ["ImageGeneration", "SpeechSynthesis", "Embeddings", "SpeechTranscription"],
        )

        # Aux restored in D11 load order after the llama reconcile.
        loads = [svc for kind, svc in engine.calls if kind == "aux-load"]
        self.assertEqual(
            loads,
            ["SpeechTranscription", "Embeddings", "SpeechSynthesis", "ImageGeneration"],
        )

        # All aux drained before the llama load; all aux reloaded after it.
        llama_load_index = next(i for i, (kind, _) in enumerate(engine.calls) if kind == "llama-load")
        last_unload_index = max(i for i, (kind, _) in enumerate(engine.calls) if kind == "aux-unload")
        first_reload_index = min(i for i, (kind, _) in enumerate(engine.calls) if kind == "aux-load")
        self.assertLess(last_unload_index, llama_load_index, "aux must drain before llama load")
        self.assertLess(llama_load_index, first_reload_index, "aux must reload after llama load")

        # The stale llama alias is unloaded and the new alias loaded.
        self.assertIn(("llama-unload", "Old-Alias"), engine.calls)
        self.assertIn(("llama-load", "New-Alias"), engine.calls)

    def test_single_aux_routing_change_does_not_touch_others(self) -> None:
        engine = FakeEngine(loaded_llama_aliases=["Primary"])
        self._patch_engine(engine)

        with tempfile.TemporaryDirectory() as tmp:
            os.environ["GA_WARMUP_DESIRED_PATH"] = os.path.join(tmp, "warmup-desired.ini")
            os.environ["GA_WARMUP_STATE_PATH"] = os.path.join(tmp, ".warmup-state.json")

            # Only SpeechTranscription flips to idle; llama + other aux unchanged.
            sections = self._all_warm_aux()
            sections["SpeechTranscription"] = WarmupServiceSection(desired="idle", model_id="asr-model")
            document = _sample_document(revision=2, sections=sections)
            document.sections[SERVICE_LLAMA] = WarmupServiceSection(desired="warm", router_alias="Primary")
            write_warmup_desired(document)

            state = build_initial_state_from_desired(document, desired_sha256=document.content_fingerprint())
            state["appliedRevision"] = 1
            state["services"][SERVICE_LLAMA]["applied"] = "warm"
            state["services"][SERVICE_LLAMA]["routerAlias"] = "Primary"
            for aux in ("SpeechTranscription", "Embeddings", "SpeechSynthesis", "ImageGeneration"):
                state["services"][aux]["applied"] = "warm"
            atomic_write_warmup_state(state)

            _run_reconcile_loop()
            final_state = read_warmup_state()

        # No GPU drain: llama is unchanged, so only the single aux is unloaded.
        unloads = [svc for kind, svc in engine.calls if kind == "aux-unload"]
        self.assertEqual(unloads, ["SpeechTranscription"])
        self.assertEqual([svc for kind, svc in engine.calls if kind == "aux-load"], [])
        self.assertEqual([c for c in engine.calls if c[0].startswith("llama")], [])
        self.assertEqual(final_state["applyStatus"], APPLY_STATUS_APPLIED)
        self.assertEqual(final_state["appliedRevision"], 2)


if __name__ == "__main__":
    unittest.main()

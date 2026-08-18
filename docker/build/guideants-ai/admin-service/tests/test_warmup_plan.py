import os
import sys
import tempfile
import unittest
from pathlib import Path

_SERVICE_ROOT = Path(__file__).resolve().parents[1]
if str(_SERVICE_ROOT) not in sys.path:
    sys.path.insert(0, str(_SERVICE_ROOT))

from warmup_plan import (
    AUX_SERVICES,
    SERVICE_LLAMA,
    WarmupPlanValidationError,
    parse_warmup_plan,
    plan_to_payload,
    section_execution_ref,
)
from warmup_state import (
    APPLY_STATUS_APPLIED,
    atomic_write_warmup_state,
    build_initial_state_from_plan,
    build_warmup_state_document,
    read_warmup_state,
    sync_state_after_plan_submission,
)


def _plan_payload() -> dict:
    return {
        "schemaVersion": 1,
        "services": {
            SERVICE_LLAMA: {"enabled": False},
            "SpeechTranscription": {"enabled": False},
            "Embeddings": {
                "enabled": True,
                "modelId": "qwen3_embedding_0_6b",
            },
            "SpeechSynthesis": {"enabled": False},
            "ImageGeneration": {
                "enabled": False,
                "bundleId": "flux2-klein-4b",
            },
        },
    }


class WarmupPlanTests(unittest.TestCase):
    def test_parse_requires_explicit_state_for_every_service(self) -> None:
        payload = _plan_payload()
        del payload["services"]["ImageGeneration"]

        with self.assertRaisesRegex(WarmupPlanValidationError, "missing: ImageGeneration"):
            parse_warmup_plan(payload)

    def test_enabled_service_requires_execution_reference(self) -> None:
        payload = _plan_payload()
        payload["services"]["SpeechSynthesis"] = {"enabled": True}

        with self.assertRaisesRegex(WarmupPlanValidationError, "has no execution reference"):
            parse_warmup_plan(payload)

    def test_disabled_service_preserves_inventory_ref_without_requesting_load(self) -> None:
        plan = parse_warmup_plan(_plan_payload())
        image = plan.services["ImageGeneration"]

        self.assertEqual(image.bundle_id, "flux2-klein-4b")
        self.assertIsNone(section_execution_ref("ImageGeneration", image))

    def test_payload_round_trip_is_structured_json_without_ini_fields(self) -> None:
        plan = parse_warmup_plan(_plan_payload()).with_revision(7)
        payload = plan_to_payload(plan)

        self.assertEqual(payload["revision"], 7)
        self.assertEqual(payload["services"]["Embeddings"]["modelId"], "qwen3_embedding_0_6b")
        self.assertNotIn("desired", str(payload))
        self.assertNotIn("router_alias", str(payload))

    def test_plan_submission_preserves_loaded_refs_and_advances_status(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            os.environ["GA_WARMUP_STATE_PATH"] = os.path.join(tmp, ".warmup-state.json")
            first = parse_warmup_plan(_plan_payload()).with_revision(1)
            initial = build_initial_state_from_plan(
                first,
                desired_sha256=first.content_fingerprint(),
            )
            initial["appliedRevision"] = 1
            initial["applyStatus"] = APPLY_STATUS_APPLIED
            initial["services"]["Embeddings"]["phase"] = "ready"
            initial["services"]["Embeddings"]["modelId"] = "qwen3_embedding_0_6b"
            atomic_write_warmup_state(initial)

            payload = _plan_payload()
            payload["services"]["Embeddings"] = {"enabled": False}
            second = parse_warmup_plan(payload).with_revision(2)
            sync_state_after_plan_submission(
                second,
                desired_sha256=second.content_fingerprint(),
                changed=True,
            )
            state = read_warmup_state()

        assert state is not None
        self.assertEqual(state["desiredRevision"], 2)
        self.assertEqual(state["services"]["Embeddings"]["modelId"], "qwen3_embedding_0_6b")
        self.assertNotIn("planRef", state["services"]["Embeddings"])

    def test_idle_status_document_contains_no_plan(self) -> None:
        state = build_warmup_state_document(
            desired_revision=0,
            applied_revision=0,
            apply_status="idle",
            apply_error=None,
            desired_sha256="",
            services={},
        )

        self.assertEqual(state["services"], {})
        self.assertEqual(state["desiredRevision"], 0)
        self.assertEqual(set(AUX_SERVICES), {
            "SpeechTranscription",
            "Embeddings",
            "SpeechSynthesis",
            "ImageGeneration",
        })


if __name__ == "__main__":
    unittest.main()

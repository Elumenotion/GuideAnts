import os
import sys
import tempfile
import unittest
from pathlib import Path

_SERVICE_ROOT = Path(__file__).resolve().parents[1]
if str(_SERVICE_ROOT) not in sys.path:
    sys.path.insert(0, str(_SERVICE_ROOT))

from warmup_desired_ini import (
    WarmupDesiredDocument,
    WarmupDesiredValidationError,
    WarmupServiceSection,
    aux_section_load_request,
    parse_warmup_desired_ini,
    put_warmup_desired_text,
    read_warmup_desired,
    section_execution_ref,
    serialize_warmup_desired_ini,
    write_warmup_desired,
)
from warmup_orchestrator import compute_transitions
from warmup_state import (
    atomic_write_warmup_state,
    build_initial_state_from_desired,
    read_warmup_state,
    sync_state_after_desired_write,
)


def _sample_document(revision: int = 1) -> WarmupDesiredDocument:
    return WarmupDesiredDocument(
        version=1,
        revision=revision,
        updated_at_utc="2026-07-12T19:00:00Z",
        sections={
            "llama": WarmupServiceSection(router_alias="Qwen3.6-35B-A3B-MTP-GGUF"),
            "SpeechTranscription": WarmupServiceSection(model_id="qwen3_asr_0_6b"),
            "Embeddings": WarmupServiceSection(enabled=False),
            "SpeechSynthesis": WarmupServiceSection(model_path="OmniVoice"),
            "ImageGeneration": WarmupServiceSection(enabled=False),
        },
    )


SAMPLE_INI = """\
version = 1
revision = 1
updated_at_utc = 2026-07-12T19:00:00Z

[llama]
router_alias = Qwen3.6-35B-A3B-MTP-GGUF

[SpeechTranscription]
model_id = qwen3_asr_0_6b

[SpeechSynthesis]
model_path = chatterbox
"""

LEGACY_INI = """\
version = 1
revision = 1
updated_at_utc = 2026-07-12T19:00:00Z

[llama]
desired = warm
router_alias = Qwen3.6-35B-A3B-MTP-GGUF

[SpeechTranscription]
desired = warm
model_id = qwen3_asr_0_6b

[SpeechSynthesis]
desired = warm
model_path = chatterbox
"""


class WarmupDesiredIniTests(unittest.TestCase):
    def test_round_trip_parses_plan_fixture(self) -> None:
        document = parse_warmup_desired_ini(SAMPLE_INI)
        self.assertEqual(document.version, 1)
        self.assertEqual(document.sections["llama"].router_alias, "Qwen3.6-35B-A3B-MTP-GGUF")
        reserialized = serialize_warmup_desired_ini(document)
        self.assertNotIn("desired = warm", reserialized)
        self.assertIn("model_path = chatterbox", reserialized)
        reparsed = parse_warmup_desired_ini(reserialized)
        self.assertEqual(
            section_execution_ref("SpeechSynthesis", reparsed.sections["SpeechSynthesis"]),
            "chatterbox",
        )

    def test_legacy_desired_warm_still_parses_on_read(self) -> None:
        document = parse_warmup_desired_ini(LEGACY_INI)
        self.assertEqual(
            section_execution_ref("SpeechSynthesis", document.sections["SpeechSynthesis"]),
            "chatterbox",
        )

    def test_model_path_section_round_trip(self) -> None:
        ini = """\
version = 1
revision = 1
updated_at_utc = 2026-07-12T19:00:00Z

[SpeechSynthesis]
model_path = OmniVoice
"""
        document = parse_warmup_desired_ini(ini)
        self.assertEqual(document.sections["SpeechSynthesis"].model_path, "OmniVoice")
        ref, field = aux_section_load_request(document.sections["SpeechSynthesis"])
        self.assertEqual(ref, "OmniVoice")
        self.assertEqual(field, "model_path")
        self.assertNotIn("desired =", serialize_warmup_desired_ini(document))

    def test_enabled_off_preserves_bundle_id_on_serialize(self) -> None:
        document = parse_warmup_desired_ini(
            """\
version = 1
revision = 1
updated_at_utc = 2026-07-12T19:00:00Z

[ImageGeneration]
enabled = off
bundle_id = flux2-klein-4b
"""
        )
        reserialized = serialize_warmup_desired_ini(document)
        self.assertIn("enabled = off", reserialized)
        self.assertIn("bundle_id = flux2-klein-4b", reserialized)
        self.assertIsNone(section_execution_ref("ImageGeneration", document.sections["ImageGeneration"]))

    def test_legacy_model_id_still_validates_and_loads_via_model_id(self) -> None:
        section = WarmupServiceSection(model_id="omnivoice")
        ref, field = aux_section_load_request(section)
        self.assertEqual(ref, "omnivoice")
        self.assertEqual(field, "model_id")

    def test_section_without_load_ref_is_invalid_on_write(self) -> None:
        bad = """\
version = 1
revision = 1
updated_at_utc = 2026-07-12T19:00:00Z

[SpeechTranscription]
desired = warm
"""
        with self.assertRaises(WarmupDesiredValidationError):
            parse_warmup_desired_ini(bad)

    def test_image_generation_without_bundle_id_is_invalid_on_write(self) -> None:
        ini = """\
version = 1
revision = 1
updated_at_utc = 2026-07-12T19:00:00Z

[ImageGeneration]
desired = warm
"""
        with self.assertRaises(WarmupDesiredValidationError):
            parse_warmup_desired_ini(ini)

    def test_read_warmup_desired_does_not_validate_legacy_disk_state(self) -> None:
        ini = """\
version = 1
revision = 4
updated_at_utc = 2026-07-12T19:00:00Z

[ImageGeneration]
desired = warm
"""
        with tempfile.TemporaryDirectory() as tmp:
            ini_path = os.path.join(tmp, "warmup-desired.ini")
            os.environ["GA_WARMUP_DESIRED_PATH"] = ini_path
            with open(ini_path, "w", encoding="utf-8") as handle:
                handle.write(ini)

            loaded = read_warmup_desired()
            assert loaded is not None
            self.assertIsNone(section_execution_ref("ImageGeneration", loaded.sections["ImageGeneration"]))

    def test_atomic_write_bumps_revision(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            ini_path = os.path.join(tmp, "warmup-desired.ini")
            os.environ["GA_WARMUP_DESIRED_PATH"] = ini_path
            first = write_warmup_desired(_sample_document(revision=0))
            self.assertEqual(first.revision, 1)

            second_doc = _sample_document(revision=0)
            second_doc.sections["SpeechTranscription"] = WarmupServiceSection(enabled=False)
            second = write_warmup_desired(second_doc)
            self.assertEqual(second.revision, 2)

            loaded = read_warmup_desired()
            assert loaded is not None
            self.assertTrue(loaded.sections["SpeechTranscription"].enabled is False)

    def test_identical_content_does_not_bump_revision(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            ini_path = os.path.join(tmp, "warmup-desired.ini")
            os.environ["GA_WARMUP_DESIRED_PATH"] = ini_path
            write_warmup_desired(_sample_document(revision=0))
            repeat = write_warmup_desired(_sample_document(revision=0))
            self.assertEqual(repeat.revision, 1)
            self.assertFalse(repeat.changed)

    def test_expected_revision_mismatch_raises(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            ini_path = os.path.join(tmp, "warmup-desired.ini")
            os.environ["GA_WARMUP_DESIRED_PATH"] = ini_path
            write_warmup_desired(_sample_document(revision=0))
            with self.assertRaises(ValueError):
                write_warmup_desired(_sample_document(revision=0), expected_revision=99)

    def test_put_warmup_desired_text_writes_file(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            ini_path = os.path.join(tmp, "warmup-desired.ini")
            os.environ["GA_WARMUP_DESIRED_PATH"] = ini_path
            written, result = put_warmup_desired_text(SAMPLE_INI)
            self.assertEqual(result.revision, 1)
            self.assertEqual(written.sections["SpeechSynthesis"].model_path, "chatterbox")


class WarmupStateTests(unittest.TestCase):
    def test_build_initial_state_tracks_plan_ref(self) -> None:
        document = _sample_document(revision=3)
        state = build_initial_state_from_desired(document, desired_sha256="abc123")
        self.assertEqual(state["desiredRevision"], 3)
        self.assertEqual(state["services"]["llama"]["planRef"], "Qwen3.6-35B-A3B-MTP-GGUF")
        self.assertNotIn("applied", state["services"]["llama"])

    def test_atomic_write_and_read_round_trip(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            state_path = os.path.join(tmp, ".warmup-state.json")
            os.environ["GA_WARMUP_STATE_PATH"] = state_path
            document = _sample_document(revision=2)
            state = build_initial_state_from_desired(document, desired_sha256="deadbeef")
            atomic_write_warmup_state(state)
            loaded = read_warmup_state()
            assert loaded is not None
            self.assertEqual(loaded["desiredRevision"], 2)

    def test_sync_state_preserves_loaded_ref_when_plan_turns_off(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            state_path = os.path.join(tmp, ".warmup-state.json")
            os.environ["GA_WARMUP_STATE_PATH"] = state_path

            document = _sample_document(revision=1)
            initial = build_initial_state_from_desired(document, desired_sha256="sha1")
            initial["services"]["SpeechTranscription"]["modelId"] = "qwen3_asr_0_6b"
            initial["services"]["SpeechTranscription"]["phase"] = "ready"
            atomic_write_warmup_state(initial)

            updated = _sample_document(revision=2)
            updated.sections["SpeechTranscription"] = WarmupServiceSection(enabled=False)
            synced = sync_state_after_desired_write(
                updated,
                desired_sha256="sha2",
                changed=True,
            )
            entry = synced["services"]["SpeechTranscription"]
            self.assertEqual(entry["modelId"], "qwen3_asr_0_6b")
            self.assertNotIn("planRef", entry)

            transitions = compute_transitions(updated, synced)
            by_service = {item.service: item.action for item in transitions}
            self.assertEqual(by_service["SpeechTranscription"], "unload")

    def test_sync_state_model_path_change_preserves_loaded_ref(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            state_path = os.path.join(tmp, ".warmup-state.json")
            os.environ["GA_WARMUP_STATE_PATH"] = state_path

            document = _sample_document(revision=1)
            initial = build_initial_state_from_desired(document, desired_sha256="sha1")
            tts = dict(initial["services"]["SpeechSynthesis"])
            tts["modelId"] = "chatterbox"
            tts["phase"] = "ready"
            initial["services"]["SpeechSynthesis"] = tts
            atomic_write_warmup_state(initial)

            updated = _sample_document(revision=2)
            updated.sections["SpeechSynthesis"] = WarmupServiceSection(model_path="OmniVoice")
            synced = sync_state_after_desired_write(
                updated,
                desired_sha256="sha2",
                changed=True,
            )
            entry = synced["services"]["SpeechSynthesis"]
            self.assertEqual(entry["modelId"], "chatterbox")
            self.assertEqual(entry["planRef"], "OmniVoice")
            self.assertEqual(entry["phase"], "idle")

            transitions = compute_transitions(updated, synced)
            by_service = {item.service: item.action for item in transitions}
            self.assertEqual(by_service["SpeechSynthesis"], "load")


if __name__ == "__main__":
    unittest.main()

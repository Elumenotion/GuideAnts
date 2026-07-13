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
    parse_warmup_desired_ini,
    put_warmup_desired_text,
    read_warmup_desired,
    serialize_warmup_desired_ini,
    write_warmup_desired,
)
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
            "llama": WarmupServiceSection(
                desired="warm",
                router_alias="Qwen3.6-35B-A3B-MTP-GGUF",
            ),
            "SpeechTranscription": WarmupServiceSection(
                desired="warm",
                model_id="qwen3_asr_0_6b",
            ),
            "Embeddings": WarmupServiceSection(
                desired="idle",
            ),
            "SpeechSynthesis": WarmupServiceSection(
                desired="warm",
                model_id="chatterbox",
            ),
            "ImageGeneration": WarmupServiceSection(
                desired="idle",
            ),
        },
    )


SAMPLE_INI = """\
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
model_id = chatterbox
"""


class WarmupDesiredIniTests(unittest.TestCase):
    def test_round_trip_parses_plan_fixture(self) -> None:
        document = parse_warmup_desired_ini(SAMPLE_INI)
        self.assertEqual(document.version, 1)
        self.assertEqual(document.revision, 1)
        self.assertEqual(document.sections["llama"].router_alias, "Qwen3.6-35B-A3B-MTP-GGUF")
        self.assertEqual(document.sections["SpeechTranscription"].model_id, "qwen3_asr_0_6b")
        reserialized = serialize_warmup_desired_ini(document)
        reparsed = parse_warmup_desired_ini(reserialized)
        self.assertEqual(reparsed.sections["llama"].desired, "warm")
        self.assertEqual(reparsed.sections["SpeechTranscription"].model_id, "qwen3_asr_0_6b")

    def test_warm_section_requires_model_ref(self) -> None:
        bad = """\
version = 1
revision = 1
updated_at_utc = 2026-07-12T19:00:00Z

[SpeechTranscription]
desired = warm
"""
        with self.assertRaises(WarmupDesiredValidationError):
            parse_warmup_desired_ini(bad)

    def test_image_generation_warm_without_bundle_id_is_valid(self) -> None:
        ini = """\
version = 1
revision = 1
updated_at_utc = 2026-07-12T19:00:00Z

[ImageGeneration]
desired = warm
"""
        document = parse_warmup_desired_ini(ini)
        self.assertEqual(document.sections["ImageGeneration"].desired, "warm")
        self.assertIsNone(document.sections["ImageGeneration"].bundle_id)

    def test_atomic_write_bumps_revision(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            ini_path = os.path.join(tmp, "warmup-desired.ini")
            os.environ["GA_WARMUP_DESIRED_PATH"] = ini_path
            first = write_warmup_desired(_sample_document(revision=0))
            self.assertEqual(first.revision, 1)
            self.assertTrue(first.changed)

            second_doc = _sample_document(revision=0)
            second_doc.sections["SpeechTranscription"] = WarmupServiceSection(
                desired="idle",
            )
            second = write_warmup_desired(second_doc)
            self.assertEqual(second.revision, 2)
            self.assertTrue(second.changed)

            loaded = read_warmup_desired()
            assert loaded is not None
            self.assertEqual(loaded.revision, 2)
            self.assertEqual(loaded.sections["SpeechTranscription"].desired, "idle")

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
            self.assertEqual(written.sections["SpeechSynthesis"].model_id, "chatterbox")


class WarmupStateTests(unittest.TestCase):
    def test_build_initial_state_tracks_revision(self) -> None:
        document = _sample_document(revision=3)
        state = build_initial_state_from_desired(document, desired_sha256="abc123")
        self.assertEqual(state["desiredRevision"], 3)
        self.assertEqual(state["appliedRevision"], 0)
        self.assertEqual(state["applyStatus"], "pending")
        self.assertEqual(state["services"]["llama"]["desired"], "warm")
        self.assertEqual(state["services"]["llama"]["applied"], "idle")

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
            self.assertIn("SpeechTranscription", loaded["services"])

    def test_sync_state_preserves_applied_on_desired_change(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            state_path = os.path.join(tmp, ".warmup-state.json")
            ini_path = os.path.join(tmp, "warmup-desired.ini")
            os.environ["GA_WARMUP_STATE_PATH"] = state_path
            os.environ["GA_WARMUP_DESIRED_PATH"] = ini_path

            document = _sample_document(revision=1)
            initial = build_initial_state_from_desired(document, desired_sha256="sha1")
            initial["services"]["SpeechTranscription"]["applied"] = "warm"
            atomic_write_warmup_state(initial)

            updated = _sample_document(revision=2)
            updated.sections["SpeechTranscription"] = WarmupServiceSection(desired="idle")
            synced = sync_state_after_desired_write(
                updated,
                desired_sha256="sha2",
                changed=True,
            )
            self.assertEqual(synced["desiredRevision"], 2)
            self.assertEqual(synced["services"]["SpeechTranscription"]["desired"], "idle")
            self.assertEqual(synced["services"]["SpeechTranscription"]["applied"], "warm")
            self.assertEqual(synced["applyStatus"], "pending")


if __name__ == "__main__":
    unittest.main()

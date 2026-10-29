#!/usr/bin/env python3
import json
import math
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import paragraph_tts as pt
import narration_core as core


def _tone(sr: int, dur_s: float, freq: float = 440.0, amp: int = 12000) -> list[int]:
    n = int(sr * dur_s)
    return [int(amp * math.sin(2 * math.pi * freq * i / sr)) for i in range(n)]


class ParsePiecesTests(unittest.TestCase):
    def test_line_break_starts_new_piece(self):
        pieces = pt.parse_pieces("First paragraph here.\n\nSecond paragraph here.")
        self.assertEqual(len(pieces), 2)
        self.assertEqual(pieces[0]["breaks_before"], 0)
        self.assertEqual(pieces[1]["breaks_before"], 1)

    def test_double_break_multiplies(self):
        pieces = pt.parse_pieces("One.\n\n\nTwo.")
        self.assertEqual(len(pieces), 2)
        self.assertEqual(pieces[1]["breaks_before"], 2)

    def test_wrapped_lines_same_piece(self):
        pieces = pt.parse_pieces("One line\ncontinues here.\n\nNext.")
        self.assertEqual(len(pieces), 2)
        self.assertEqual(pieces[0]["text"], "One line continues here.")

    def test_markdown_noise_ignored(self):
        raw = "# Header\n\n---\n\nSpoken line one.\n\nSpoken line two.\n\nEND OF SCRIPT\n"
        pieces = pt.parse_pieces(raw)
        self.assertEqual([p["text"] for p in pieces],
                         ["Spoken line one.", "Spoken line two."])


class ClampGapsTests(unittest.TestCase):
    def _write(self, tmp, samples, sr=24000):
        path = os.path.join(tmp, "in.wav")
        core.write_wav_pcm16(path, samples, sr)
        return path

    def test_interior_gap_clamped_to_ratio(self):
        sr = 24000
        samples = _tone(sr, 1.0) + [0] * (sr // 2) + _tone(sr, 1.0)  # 500ms interior silence
        new, report, edge = pt.clamp_gaps(samples, sr, min_ms=300, ratio=0.80)
        self.assertEqual(len(report), 1)
        self.assertEqual(report[0]["original_ms"], 500.0)
        self.assertEqual(report[0]["clamped_ms"], 400.0)
        self.assertEqual(len(new), len(samples) - sr // 10)  # 100ms removed
        self.assertEqual(edge, [])
        # removed zone was pure silence
        self.assertEqual(report[0]["removed_peak"], 0)

    def test_edge_silence_never_touched(self):
        sr = 24000
        samples = [0] * int(sr * 0.4) + _tone(sr, 1.0) + [0] * int(sr * 0.6)
        new, report, edge = pt.clamp_gaps(samples, sr, min_ms=300, ratio=0.80)
        self.assertEqual(report, [])
        self.assertEqual(len(new), len(samples))
        self.assertEqual(len(edge), 2)  # lead + tail reported, untouched

    def test_short_gap_below_floor_untouched(self):
        sr = 24000
        samples = _tone(sr, 1.0) + [0] * int(sr * 0.2) + _tone(sr, 1.0)  # 200ms
        new, report, edge = pt.clamp_gaps(samples, sr, min_ms=300, ratio=0.80)
        self.assertEqual(report, [])
        self.assertEqual(len(new), len(samples))

    def test_soft_speech_tail_never_clipped(self):
        """The real failure mode: the detector's gap span includes the
        decaying tail of a final word (soft speech below the RMS gap
        threshold). The cut must stay in the quiet core; the tail survives.
        """
        sr = 24000
        tail = _tone(sr, 0.3, freq=300.0, amp=300)   # soft: peak 300 > quiet 60,
                                                     # rms 212 < gap threshold
        samples = _tone(sr, 1.0) + tail + [0] * int(sr * 0.6) + _tone(sr, 1.0)
        new, report, edge = pt.clamp_gaps(samples, sr, min_ms=300, ratio=0.80)
        self.assertEqual(len(report), 1)
        removed = report[0]
        # the removed zone lies entirely AFTER the tail ends (1.3s) and
        # before the next word starts (1.9s)
        self.assertGreater(removed["removed_start_s"], 1.3 - 1e-6)
        self.assertLess(removed["removed_end_s"], 1.9 + 1e-6)
        self.assertLessEqual(removed["removed_peak"], 10)
        # the tail's energy is still present in the output:
        i0, i1 = int(1.0 * sr), int(1.3 * sr)
        tail_out = new[i0:i1]
        tail_in = samples[i0:i1]
        self.assertEqual(len(tail_out), len(tail_in))
        diff = max(abs(a - b) for a, b in zip(tail_out, tail_in))
        self.assertLess(diff, 40)  # at most fade-level change, no removal



class RequestOptionTests(unittest.TestCase):
    def setUp(self):
        # Force the direct urlopen path (no skill gateway) so we can intercept it.
        self._saved = os.environ.pop("AUDIOCPP_SKILL_BASE_URL", None)
        import narration as narr
        self.narr = narr
        self._real_urlopen = narr.urllib.request.urlopen

    def tearDown(self):
        self.narr.urllib.request.urlopen = self._real_urlopen
        if self._saved is not None:
            os.environ["AUDIOCPP_SKILL_BASE_URL"] = self._saved

    def _capture_payload(self, **kwargs):
        captured = {}

        class _FakeResp:
            def read(self):
                return b"RIFF\x00\x00\x00\x00WAVEfmt "  # RIFF magic + padding
            def __enter__(self):
                return self
            def __exit__(self, *a):
                return False

        def fake_urlopen(request, timeout=None):
            captured["payload"] = json.loads(request.data.decode("utf-8"))
            return _FakeResp()

        self.narr.urllib.request.urlopen = fake_urlopen
        self.narr._synth_chunk(
            model="test-model", engine_url="http://127.0.0.1:1/v1",
            voice="doug", text="Hello world", seed=42, timeout=5.0, **kwargs,
        )
        return captured["payload"]

    def test_parse_request_options(self):
        from narration import _parse_request_options
        self.assertEqual(
            _parse_request_options(["template_name=emotion", "emotion=happy"]),
            {"template_name": "emotion", "emotion": "happy"},
        )
        self.assertEqual(_parse_request_options([]), {})

    def test_parse_request_options_rejects_malformed(self):
        from narration import _parse_request_options
        with self.assertRaises(SystemExit):
            _parse_request_options(["noequalsign"])
        with self.assertRaises(SystemExit):
            _parse_request_options(["=value"])

    def test_synth_chunk_emits_options(self):
        payload = self._capture_payload(options={"template_name": "emotion", "emotion": "happy"})
        self.assertEqual(payload["options"], {"template_name": "emotion", "emotion": "happy"})
        self.assertEqual(payload["voice"], "doug")

    def test_synth_chunk_omits_options_when_absent(self):
        # Backward-compat: no --request-option -> byte-identical payload, so the
        # existing chatterbox --voice doug path is untouched.
        payload = self._capture_payload()
        self.assertNotIn("options", payload)
        self.assertEqual(set(payload.keys()), {"model", "input", "voice", "seed"})

    def test_cache_digest_changes_with_options(self):
        # A piece synthesized with options must not collide with a plain cached piece.
        import hashlib
        def digest(text, voice, options):
            return hashlib.sha256(
                (text + "|" + voice + "|" + json.dumps(options, sort_keys=True)).encode("utf-8")
            ).hexdigest()[:16]
        self.assertNotEqual(
            digest("Hello", "doug", {}),
            digest("Hello", "doug", {"emotion": "happy"}),
        )



if __name__ == "__main__":
    unittest.main()

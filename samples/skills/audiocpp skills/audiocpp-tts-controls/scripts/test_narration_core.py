#!/usr/bin/env python3
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import narration_core as core


class NarrationCoreTests(unittest.TestCase):
    def test_strip_script_markdown(self) -> None:
        raw = """# Script 1

## TTS Input

---

Hi, my name is Alex Narrator.

END OF SCRIPT 1 V3
"""
        self.assertEqual(core.strip_script_markdown(raw), "Hi, my name is Alex Narrator.")

    def test_plan_chunks_on_sentence_boundaries(self) -> None:
        sentences = [f"Line {index}." for index in range(1, 11)]
        text = " ".join(sentences)
        plans = core.plan_chunks(text, max_chunk_seconds=2.0, words_per_second=2.0)
        self.assertGreater(len(plans), 1)
        for plan in plans:
            self.assertLessEqual(plan.words, 4)
            self.assertNotIn("  ", plan.text)

    def test_needs_chunking_uses_words_seconds_heuristic(self) -> None:
        short = "One two three four five."
        long = "word " * 400
        self.assertFalse(
            core.needs_chunking(short, max_chunk_seconds=95, words_per_second=3.1)
        )
        self.assertTrue(
            core.needs_chunking(long.strip(), max_chunk_seconds=95, words_per_second=3.1)
        )

    def test_estimate_audio_seconds(self) -> None:
        self.assertAlmostEqual(core.estimate_audio_seconds(310, 3.1), 100.0, places=1)

    def test_oversized_sentence_splits_on_clauses(self) -> None:
        text = (
            "This is a very long first clause with many many words; "
            "and this is another very long second clause with many many words; "
            "and yet another clause that keeps going and going."
        )
        plans = core.plan_chunks(text, max_chunk_seconds=3.0, words_per_second=2.0)
        self.assertGreaterEqual(len(plans), 2)

    def test_concat_inserts_inter_chunk_pause(self) -> None:
        import tempfile

        tone = [1000] * 2400  # 100ms tone at 24kHz
        with tempfile.TemporaryDirectory() as tmp:
            a_path = os.path.join(tmp, "a.wav")
            b_path = os.path.join(tmp, "b.wav")
            out_path = os.path.join(tmp, "out.wav")
            core.write_wav_pcm16(a_path, tone, 24000)
            core.write_wav_pcm16(b_path, tone, 24000)
            result = core.concat_wavs([a_path, b_path], out_path, inter_chunk_pause_ms=150)
            samples, sr = core.read_wav_pcm16(out_path)
            self.assertEqual(sr, 24000)
            expected = len(tone) * 2 + int(24000 * 0.150)
            self.assertEqual(len(samples), expected)
            self.assertEqual(result["inter_chunk_pause_ms"], 150)


if __name__ == "__main__":
    unittest.main()

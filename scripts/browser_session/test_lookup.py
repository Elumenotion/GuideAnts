"""Unit tests for browser session lookup helpers."""

from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from scripts.browser_session.clock import parse_timecode, video_ms  # noqa: E402
from scripts.browser_session.crop import screen_to_crop  # noqa: E402
from scripts.browser_session.lookup import lookup_at  # noqa: E402
from scripts.browser_session.schema import MonitorGeometry, ScreenRect, SessionClock, write_json  # noqa: E402


class ParseTimecodeTests(unittest.TestCase):
    def test_minutes_seconds_fraction(self) -> None:
        self.assertEqual(parse_timecode("1:23.4"), 83400)

    def test_seconds_fraction(self) -> None:
        self.assertEqual(parse_timecode("83.4"), 83400)

    def test_raw_milliseconds(self) -> None:
        self.assertEqual(parse_timecode("83400"), 83400)


class CropMathTests(unittest.TestCase):
    def test_monitor_relative_crop(self) -> None:
        monitor = MonitorGeometry(index=1, left=100, top=50, width=1920, height=1080)
        screen = ScreenRect(left=100, top=50, width=1280, height=800)
        crop, visible, clamped = screen_to_crop(screen, monitor)
        self.assertTrue(visible)
        self.assertFalse(clamped)
        self.assertEqual((crop.x, crop.y, crop.w, crop.h), (0, 0, 1280, 800))

    def test_clamp_partial_off_monitor(self) -> None:
        monitor = MonitorGeometry(index=1, left=0, top=0, width=1000, height=800)
        screen = ScreenRect(left=-100, top=0, width=500, height=400)
        crop, visible, clamped = screen_to_crop(screen, monitor)
        self.assertTrue(visible)
        self.assertTrue(clamped)
        self.assertEqual((crop.x, crop.y, crop.w, crop.h), (0, 0, 400, 400))


class LookupTests(unittest.TestCase):
    def _write_fixture(self, root: Path) -> None:
        write_json(
            root / "session.json",
            {
                "schema_version": 1,
                "session_id": "test",
                "clock": {
                    "t0_epoch_ms": 1_000_000,
                    "recording_started_epoch_ms": 1_000_000,
                    "recording_lead_in_ms": 0,
                    "fps": 30,
                },
                "monitor": {
                    "index": 1,
                    "left": 0,
                    "top": 0,
                    "width": 1920,
                    "height": 1080,
                },
                "paths": {"video": str((root / "video.mp4").resolve())},
                "capture_browser_hwnd": "0xabc",
            },
        )
        write_json(
            root / "index.json",
            {
                "tabs": {
                    "tab-1": {
                        "tab_id": "tab-1",
                        "opened_at_ms": 0,
                        "closed_at_ms": None,
                        "last_url": "https://a.example",
                        "last_title": "A",
                    },
                    "tab-2": {
                        "tab_id": "tab-2",
                        "opened_at_ms": 0,
                        "closed_at_ms": 20_000,
                        "last_url": "https://b.example",
                        "last_title": "B",
                    },
                },
                "checkpoints": [
                    {
                        "id": "000001",
                        "t_ms": 1000,
                        "tab_id": "tab-1",
                        "foreground": True,
                        "trigger": "navigate",
                        "url": "https://a.example/one",
                        "title": "A one",
                        "has_mhtml": True,
                    },
                    {
                        "id": "000002",
                        "t_ms": 5000,
                        "tab_id": "tab-2",
                        "foreground": False,
                        "trigger": "navigate",
                        "url": "https://b.example/two",
                        "title": "B two",
                        "has_mhtml": True,
                    },
                    {
                        "id": "000003",
                        "t_ms": 8000,
                        "tab_id": "tab-1",
                        "foreground": True,
                        "trigger": "navigate",
                        "url": "https://a.example/two",
                        "title": "A two",
                        "has_mhtml": True,
                    },
                ],
            },
        )
        cp1 = root / "checkpoints" / "000001"
        cp1.mkdir(parents=True)
        (cp1 / "meta.json").write_text("{}", encoding="utf-8")
        (cp1 / "page.mhtml").write_text("<mhtml one>", encoding="utf-8")
        cp3 = root / "checkpoints" / "000003"
        cp3.mkdir(parents=True)
        (cp3 / "meta.json").write_text("{}", encoding="utf-8")
        (cp3 / "page.mhtml").write_text("<mhtml two>", encoding="utf-8")

        windows = [
            {
                "t_ms": 1000,
                "hwnd": "0x1",
                "process": "chrome.exe",
                "title": "A one",
                "is_capture_browser": True,
                "screen": {"left": 0, "top": 0, "width": 1200, "height": 800},
                "crop": {"x": 0, "y": 0, "w": 1200, "h": 800},
                "visible_on_monitor": True,
                "clamped": False,
            },
            {
                "t_ms": 5500,
                "hwnd": "0x1",
                "process": "chrome.exe",
                "title": "A one",
                "is_capture_browser": True,
                "screen": {"left": 0, "top": 0, "width": 1200, "height": 800},
                "crop": {"x": 0, "y": 0, "w": 1200, "h": 800},
                "visible_on_monitor": True,
                "clamped": False,
            },
            {
                "t_ms": 6000,
                "hwnd": "0x2",
                "process": "Code.exe",
                "title": "notes.md",
                "is_capture_browser": False,
                "screen": {"left": 50, "top": 50, "width": 1000, "height": 700},
                "crop": {"x": 50, "y": 50, "w": 1000, "h": 700},
                "visible_on_monitor": True,
                "clamped": False,
            },
            {
                "t_ms": 7500,
                "hwnd": "0x1",
                "process": "chrome.exe",
                "title": "A one",
                "is_capture_browser": True,
                "screen": {"left": 0, "top": 0, "width": 1200, "height": 800},
                "crop": {"x": 0, "y": 0, "w": 1200, "h": 800},
                "visible_on_monitor": True,
                "clamped": False,
            },
            {
                "t_ms": 8500,
                "hwnd": "0x1",
                "process": "chrome.exe",
                "title": "A two",
                "is_capture_browser": True,
                "screen": {"left": 0, "top": 0, "width": 1200, "height": 800},
                "crop": {"x": 0, "y": 0, "w": 1200, "h": 800},
                "visible_on_monitor": True,
                "clamped": False,
            },
        ]
        with (root / "windows.jsonl").open("w", encoding="utf-8") as handle:
            for row in windows:
                handle.write(json.dumps(row) + "\n")

        events = [
            {"kind": "tab.focus", "t_ms": 0, "tab_id": "tab-1"},
            {"kind": "tab.focus", "t_ms": 7000, "tab_id": "tab-1"},
            {"kind": "tab.close", "t_ms": 20_000, "tab_id": "tab-2"},
        ]
        with (root / "events.jsonl").open("w", encoding="utf-8") as handle:
            for row in events:
                handle.write(json.dumps(row) + "\n")

    def test_navigation_boundary(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._write_fixture(root)
            before = lookup_at(root, 7500)
            self.assertEqual(before["foreground"]["url"], "https://a.example/one")
            self.assertEqual(before["foreground"]["paths"]["mhtml"], str((root / "checkpoints" / "000001" / "page.mhtml").resolve()))
            after = lookup_at(root, 8500)
            self.assertEqual(after["foreground"]["url"], "https://a.example/two")

    def test_tab_background_state(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._write_fixture(root)
            result = lookup_at(root, 5500)
            self.assertEqual(result["surface"], "browser")
            self.assertEqual(result["foreground"]["tab_id"], "tab-1")
            self.assertEqual(result["tabs"]["tab-2"]["checkpoint"]["url"], "https://b.example/two")

    def test_other_window_surface(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._write_fixture(root)
            result = lookup_at(root, 6500)
            self.assertEqual(result["surface"], "other_window")
            self.assertIsNone(result["foreground"])
            self.assertEqual(result["window"]["process"], "Code.exe")

    def test_tab_close_removes_from_tab_list(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._write_fixture(root)
            result = lookup_at(root, 25_000)
            tab_ids = {row["tab_id"] for row in result["tab_list"]}
            self.assertEqual(tab_ids, {"tab-1"})

    def test_video_ms_with_lead_in(self) -> None:
        clock = SessionClock(
            t0_epoch_ms=1000,
            recording_started_epoch_ms=900,
            recording_lead_in_ms=100,
            fps=30,
        )
        self.assertEqual(video_ms(500, clock), 600)


if __name__ == "__main__":
    unittest.main()

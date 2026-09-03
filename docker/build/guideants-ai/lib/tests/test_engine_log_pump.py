import io
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

_LIB_ROOT = Path(__file__).resolve().parents[1]
if str(_LIB_ROOT) not in sys.path:
    sys.path.insert(0, str(_LIB_ROOT))

from guideants_hf.engine_process import EngineLogPump, spawn_engine_with_log_pump


class EngineLogPumpTests(unittest.TestCase):
    def test_forwards_lines_and_keeps_tail(self) -> None:
        lines: list[str] = []
        pump = EngineLogPump(emit_line=lines.append, max_lines=5)
        stream = io.StringIO("alpha\nbeta\ngamma\n")
        process = MagicMock()
        process.pid = 42
        process.stdout = stream

        pump.start(process)
        deadline = time.time() + 2.0
        while len(lines) < 3 and time.time() < deadline:
            time.sleep(0.01)
        pump.stop()

        self.assertEqual(lines, ["alpha", "beta", "gamma"])
        self.assertIn("gamma", pump.tail())

    def test_spawn_engine_with_log_pump_captures_stdout(self) -> None:
        lines: list[str] = []
        with tempfile.TemporaryDirectory() as temp_dir:
            script = Path(temp_dir) / "echo_engine.py"
            script.write_text(
                "import sys\nprint('engine-ready', flush=True)\nsys.stdout.flush()\n",
                encoding="utf-8",
            )
            process, pump = spawn_engine_with_log_pump(
                [sys.executable, str(script)],
                emit_line=lines.append,
            )
            try:
                deadline = time.time() + 5.0
                while "engine-ready" not in "\n".join(lines) and time.time() < deadline:
                    time.sleep(0.01)
                process.wait(timeout=5)
            finally:
                pump.stop()
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=5)

        self.assertTrue(any("engine-ready" in line for line in lines), lines)
        self.assertIn("engine-ready", pump.tail())


class SyncHeartbeatTests(unittest.TestCase):
    def test_tts_sync_heartbeat_emits_while_work_runs(self) -> None:
        service_root = Path(__file__).resolve().parents[2] / "tts-service"
        if str(service_root) not in sys.path:
            sys.path.insert(0, str(service_root))
        for optional in ("uvicorn", "fastapi", "fastapi.responses", "pydantic"):
            if optional not in sys.modules:
                sys.modules[optional] = MagicMock()

        import tts_service

        events: list[str] = []

        def capture(event: str, **fields: object) -> None:
            events.append(event)

        def slow() -> str:
            time.sleep(0.25)
            return "done"

        with patch.object(tts_service, "log_event", side_effect=capture):
            with patch.object(tts_service, "heartbeat_interval_seconds", return_value=0.05):
                result = tts_service.run_with_sync_heartbeat(
                    "tts_model_warmup_heartbeat",
                    {"modelRef": "chatterbox"},
                    slow,
                )

        self.assertEqual(result, "done")
        self.assertTrue(
            any(event == "tts_model_warmup_heartbeat" for event in events),
            events,
        )


if __name__ == "__main__":
    unittest.main()

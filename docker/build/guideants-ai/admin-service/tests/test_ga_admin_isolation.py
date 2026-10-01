import sys
import unittest
from pathlib import Path

_SERVICE_ROOT = Path(__file__).resolve().parents[1]
if str(_SERVICE_ROOT) not in sys.path:
    sys.path.insert(0, str(_SERVICE_ROOT))

_GA_ADMIN_SOURCE = (_SERVICE_ROOT / "ga_admin_service.py").read_text(encoding="utf-8")


class GaAdminIsolationTests(unittest.TestCase):
    def test_sd_is_not_mounted_in_ga_admin_process(self) -> None:
        self.assertNotIn('APP.mount("/sd"', _GA_ADMIN_SOURCE)
        self.assertNotIn("import sd_service", _GA_ADMIN_SOURCE)

    def test_ga_admin_does_not_proxy_aux_service_admin(self) -> None:
        self.assertNotIn("proxy_asr_admin", _GA_ADMIN_SOURCE)
        self.assertNotIn("proxy_tts_admin", _GA_ADMIN_SOURCE)
        self.assertNotIn("proxy_emb_admin", _GA_ADMIN_SOURCE)
        self.assertNotIn("engine_proxy", _GA_ADMIN_SOURCE)

if __name__ == "__main__":
    unittest.main()

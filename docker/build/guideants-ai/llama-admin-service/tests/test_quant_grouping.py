import sys
import unittest
from pathlib import Path

SERVICE_ROOT = Path(__file__).resolve().parents[1]
LIB_ROOT = SERVICE_ROOT.parent / "lib"
for path in (str(LIB_ROOT), str(SERVICE_ROOT)):
    if path not in sys.path:
        sys.path.insert(0, path)

from guideants_hf.quant_grouping import QuantGroupingError, group_repository_quants, quant_label_to_id


def _file(path: str, size: int, **extra: object) -> dict:
    record = {"type": "file", "path": path, "size": size}
    record.update(extra)
    return record


class QuantGroupingTests(unittest.TestCase):
    def test_single_gguf_group(self) -> None:
        files = [_file("nested/Qwen3.6-35B-A3B-Q4_K_M.gguf", 20_000)]
        groups = group_repository_quants(files)
        self.assertEqual(1, len(groups))
        self.assertEqual("q4_k_m", groups[0]["id"])
        self.assertEqual("Q4_K_M", groups[0]["label"])
        self.assertEqual(20_000, groups[0]["totalBytes"])

    def test_complete_shards_group(self) -> None:
        files = [
            _file("Qwen3.6-35B-A3B-Q6_K_XL-00001-of-00002.gguf", 14_000),
            _file("subdir/Qwen3.6-35B-A3B-Q6_K_XL-00002-of-00002.gguf", 14_500),
        ]
        groups = group_repository_quants(files)
        self.assertEqual(1, len(groups))
        self.assertEqual("q6_k_xl", groups[0]["id"])
        self.assertEqual(2, len(groups[0]["files"]))
        self.assertEqual(1, groups[0]["files"][0]["shardIndex"])
        self.assertEqual(2, groups[0]["files"][1]["shardIndex"])

    def test_incomplete_shard_set_rejected(self) -> None:
        files = [_file("Model-Q4_K_M-00001-of-00002.gguf", 10)]
        with self.assertRaises(QuantGroupingError) as ctx:
            group_repository_quants(files)
        self.assertEqual("INCOMPLETE_QUANT_GROUP", ctx.exception.code)

    def test_duplicate_shard_rejected(self) -> None:
        files = [
            _file("Model-Q4_K_M-00001-of-00002.gguf", 10),
            _file("Model-Q4_K_M-00001-of-00002.gguf", 11),
        ]
        with self.assertRaises(QuantGroupingError):
            group_repository_quants(files)

    def test_mixed_totals_rejected(self) -> None:
        files = [
            _file("Model-Q4_K_M-00001-of-00002.gguf", 10),
            _file("Model-Q4_K_M-00002-of-00003.gguf", 11),
        ]
        with self.assertRaises(QuantGroupingError):
            group_repository_quants(files)

    def test_projector_excluded(self) -> None:
        files = [
            _file("mmproj-F16.gguf", 100),
            _file("Qwen3.6-35B-A3B-Q4_K_M.gguf", 20_000),
        ]
        groups = group_repository_quants(files)
        self.assertEqual(1, len(groups))
        self.assertEqual("q4_k_m", groups[0]["id"])

    def test_mtp_artifacts_excluded(self) -> None:
        files = [
            _file("MTP/mtp-gemma-4-E4B-it-BF16.gguf", 5_000),
            _file("mtp-gemma-4-E4B-it.gguf", 1_000),
            _file("gemma-4-E4B-it-BF16.gguf", 8_000),
            _file("gemma-4-E4B-it-UD-Q4_K_XL.gguf", 4_000),
        ]
        groups = group_repository_quants(files)
        labels = [group["label"] for group in groups]
        self.assertEqual(["BF16", "UD-Q4_K_XL"], labels)

    def test_mtp_artifacts_do_not_mix_with_sharded_quant(self) -> None:
        files = [
            _file("MTP/mtp-gemma-4-26B-A4B-it-BF16.gguf", 5_000),
            _file("BF16/gemma-4-26B-A4B-it-BF16-00001-of-00002.gguf", 7_000),
            _file("BF16/gemma-4-26B-A4B-it-BF16-00002-of-00002.gguf", 7_500),
        ]
        groups = group_repository_quants(files)
        self.assertEqual(1, len(groups))
        self.assertEqual("bf16", groups[0]["id"])
        self.assertEqual(2, len(groups[0]["files"]))

    def test_stable_id_and_order(self) -> None:
        files = [
            _file("z/Model-UD-Q5_K_XL.gguf", 30),
            _file("a/Model-Q4_K_M.gguf", 20),
            _file("b/Model-Q6_K_XL-00002-of-00002.gguf", 15),
            _file("c/Model-Q6_K_XL-00001-of-00002.gguf", 15),
        ]
        groups = group_repository_quants(files)
        labels = [group["label"] for group in groups]
        self.assertEqual(["Q4_K_M", "Q6_K_XL", "UD-Q5_K_XL"], labels)
        self.assertEqual("ud_q5_k_xl", quant_label_to_id("UD-Q5_K_XL"))

    def test_integrity_metadata_preserved(self) -> None:
        files = [
            _file(
                "Model-Q4_K_M.gguf",
                20,
                lfsOid="sha256:abc",
                gitOid="deadbeef",
            )
        ]
        groups = group_repository_quants(files)
        file_entry = groups[0]["files"][0]
        self.assertEqual("sha256:abc", file_entry["lfsOid"])
        self.assertEqual("deadbeef", file_entry["gitOid"])


if __name__ == "__main__":
    unittest.main()

import json
import os
import tempfile
import unittest
from unittest import mock

from support import CONTRACTS_ROOT

from guideants_hf.exact_download import (
    ExactDownloadError,
    activate_staged_files,
    build_artifact_specs,
    build_immutable_input,
    resume_metadata_matches,
    stage_download_file,
    validate_shard_group,
    write_resume_metadata,
)
from guideants_hf.operation_journal import OperationJournalStore


class ExactDownloadTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmpdir = tempfile.TemporaryDirectory()
        self._root = os.path.join(self._tmpdir.name, "models")
        os.makedirs(self._root, exist_ok=True)

    def tearDown(self) -> None:
        self._tmpdir.cleanup()

    def test_validate_shard_group_requires_complete_set(self) -> None:
        validate_shard_group(
            [
                "Qwen3.6-35B-A3B-Q6_K_XL-00001-of-00002.gguf",
                "Qwen3.6-35B-A3B-Q6_K_XL-00002-of-00002.gguf",
            ]
        )
        with self.assertRaises(ExactDownloadError):
            validate_shard_group(
                [
                    "Qwen3.6-35B-A3B-Q6_K_XL-00001-of-00003.gguf",
                    "Qwen3.6-35B-A3B-Q6_K_XL-00002-of-00003.gguf",
                ]
            )

    def test_rejects_duplicate_destination_names(self) -> None:
        with self.assertRaises(Exception):
            build_artifact_specs(
                model_files=["dir/a.gguf", "other/a.gguf"],
                mmproj_files=[],
                store_root=self._root,
                target_subdir="target",
                artifact_metadata=None,
            )

    def test_rejects_directory_escape(self) -> None:
        with self.assertRaises(Exception):
            build_artifact_specs(
                model_files=["../escape.gguf"],
                mmproj_files=[],
                store_root=self._root,
                target_subdir="target",
                artifact_metadata=None,
            )

    def test_resume_metadata_must_match(self) -> None:
        meta_path = os.path.join(self._tmpdir.name, "file.tmp.meta.json")
        write_resume_metadata(
            meta_path,
            operation_id="op-1",
            repository="org/repo",
            resolved_revision="abc",
            repository_path="a.gguf",
            expected_size=10,
            digest=None,
        )
        self.assertTrue(
            resume_metadata_matches(
                meta_path,
                operation_id="op-1",
                repository="org/repo",
                resolved_revision="abc",
                repository_path="a.gguf",
                expected_size=10,
                digest=None,
            )
        )
        self.assertFalse(
            resume_metadata_matches(
                meta_path,
                operation_id="op-2",
                repository="org/repo",
                resolved_revision="abc",
                repository_path="a.gguf",
                expected_size=10,
                digest=None,
            )
        )

    def test_staging_activation_is_atomic(self) -> None:
        _, model_specs, _ = build_artifact_specs(
            model_files=["a.gguf"],
            mmproj_files=[],
            store_root=self._root,
            target_subdir="alias-dir",
            artifact_metadata=[{"path": "a.gguf", "size": 4}],
        )
        staging_dir = os.path.join(self._root, ".staging", "op-1")
        target_dir = os.path.join(self._root, "alias-dir")
        os.makedirs(staging_dir, exist_ok=True)
        staged = os.path.join(staging_dir, "a.gguf")
        with open(staged, "wb") as handle:
            handle.write(b"test")

        activate_staged_files(
            staging_dir=staging_dir,
            target_dir=target_dir,
            store_root=self._root,
            specs=model_specs,
        )
        final_path = os.path.join(target_dir, "a.gguf")
        self.assertTrue(os.path.isfile(final_path))
        self.assertEqual(os.path.getsize(final_path), 4)

    def test_journal_survives_restart(self) -> None:
        journal_root = os.path.join(self._root, ".llama-operations")
        store = OperationJournalStore(journal_root)
        immutable = build_immutable_input(
            repository="org/repo",
            resolved_revision="rev",
            model_files=["a.gguf"],
            mmproj_files=[],
            alias="alias",
            target_directory="alias",
            preset={"ctx-size": "4096"},
            preset_mode="replace",
        )
        store.create(operation_id="op-1", immutable_input=immutable, alias="alias")
        store.append_step("op-1", "downloadModelFile", "a.gguf")

        reloaded = OperationJournalStore(journal_root)
        record = reloaded.get("op-1")
        self.assertIsNotNone(record)
        assert record is not None
        self.assertEqual(record.status, "queued")
        self.assertEqual(len(record.journal), 1)

    def test_immutable_input_matches_fixture_fields(self) -> None:
        fixture = json.loads((CONTRACTS_ROOT / "immutable-operation-input.fixture.json").read_text(encoding="utf-8"))
        built = build_immutable_input(
            repository=fixture["repository"],
            resolved_revision=fixture["resolvedRevision"],
            model_files=fixture["modelFiles"],
            mmproj_files=fixture["mmprojFiles"],
            alias=fixture["routerModelId"],
            target_directory=fixture.get("targetDirectory", fixture["routerModelId"]),
            preset=fixture["routerPreset"],
            preset_mode="replace",
        )
        self.assertEqual(built["repository"], fixture["repository"])
        self.assertEqual(built["modelFiles"], fixture["modelFiles"])
        self.assertEqual(built["routerPreset"], fixture["routerPreset"])

    @mock.patch("guideants_hf.exact_download.download_hf_file")
    def test_stage_download_verifies_size(self, download_mock: mock.Mock) -> None:
        def write_file(*_args, **kwargs) -> None:
            destination = kwargs.get("destination_path") if "destination_path" in kwargs else _args[2]
            with open(destination, "wb") as handle:
                handle.write(b"1234")

        download_mock.side_effect = write_file
        _, model_specs, _ = build_artifact_specs(
            model_files=["a.gguf"],
            mmproj_files=[],
            store_root=self._root,
            target_subdir="alias",
            artifact_metadata=[{"path": "a.gguf", "size": 4}],
        )
        staging_dir = os.path.join(self._root, ".staging", "op-2")
        stage_download_file(
            repository="org/repo",
            resolved_revision="rev",
            spec=model_specs[0],
            staging_dir=staging_dir,
            token=None,
            operation_id="op-2",
        )
        self.assertTrue(os.path.isfile(os.path.join(staging_dir, "a.gguf")))


if __name__ == "__main__":
    unittest.main()

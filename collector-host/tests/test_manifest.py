import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from tools_touch_collector.manifest import validate_manifest


class ManifestTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        (self.root / "raw").mkdir()
        (self.root / "raw" / "one.json").write_text('{"content": [1]}', encoding="utf-8")
        record = {
            "external_id": "item-1", "school": "示例大学", "department": "计算机学院", "source_year": None,
            "kind": "PreRecommendation", "title": "通知", "registration_start_raw": None,
            "registration_end_raw": None, "event_start_raw": None, "event_end_raw": None,
            "official_url": "https://example.edu/notice", "application_url": None,
            "source_url": "https://example.edu/source", "raw_ref": {"path": "raw/one.json", "json_pointer": "/content/0"},
        }
        (self.root / "records.jsonl").write_text(json.dumps(record, ensure_ascii=False) + "\n", encoding="utf-8")
        def file_entry(path, kind):
            data = (self.root / path).read_bytes()
            return {"path": path, "sha256": hashlib.sha256(data).hexdigest(), "byte_length": len(data), "kind": kind}
        self.manifest = {
            "schema_version": 1, "source": "baoyan", "adapter_version": "test", "scope": {"school": "示例大学", "kind": "全部", "year": None},
            "fetched_at": "2026-09-05T00:00:00Z", "complete": True,
            "counts": {"scanned": 1, "expected": 1, "selected": 1},
            "files": [file_entry("records.jsonl", "records"), file_entry("raw/one.json", "raw")],
        }

    def tearDown(self):
        self.directory.cleanup()

    def test_valid_manifest_and_records(self):
        result = validate_manifest(self.root, self.manifest)
        self.assertEqual([item["external_id"] for item in result["records"]], ["item-1"])

    def test_hash_and_count_mismatch_rejected(self):
        self.manifest["counts"]["selected"] = 2
        self.manifest["counts"]["scanned"] = 2
        self.manifest["counts"]["expected"] = 2
        with self.assertRaisesRegex(ValueError, "RECORD_COUNT_MISMATCH"):
            validate_manifest(self.root, self.manifest)
        self.manifest["counts"]["selected"] = 1
        self.manifest["files"][0]["sha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "MANIFEST_FILE_HASH_MISMATCH"):
            validate_manifest(self.root, self.manifest)

    def test_path_and_duplicate_id_rejected(self):
        self.manifest["files"][1]["path"] = "../raw/one.json"
        with self.assertRaisesRegex(ValueError, "INVALID_MANIFEST_PATH"):
            validate_manifest(self.root, self.manifest)


if __name__ == "__main__":
    unittest.main()

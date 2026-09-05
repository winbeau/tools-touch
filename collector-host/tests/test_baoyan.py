import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from tools_touch_collector import baoyan
from tools_touch_collector.manifest import validate_manifest


class FakeClient:
    def __init__(self, raw_dir, **_kwargs):
        self.raw_dir = raw_dir
        self.last_expected = None
        self.closed = False

    def close(self):
        self.closed = True

    def pages(self, path, **_params):
        if path == "/search/colleges":
            (self.raw_dir / "response-000001.json").write_text(json.dumps({"content": [{"name": "示例大学"}]}), encoding="utf-8")
            self.last_expected = 1
            yield {"name": "示例大学"}, 1, 0
            return
        (self.raw_dir / "response-000002.json").write_text(json.dumps({"content": [{"id": "1"}]}), encoding="utf-8")
        self.last_expected = 1
        yield {
            "id": "1", "college": "示例大学", "academy": "计算机学院", "tags": "预推免", "year": 2025,
            "title": "通知", "sign_up_start": "2025-09-01", "sign_up_end": "2025-09-10",
            "start_time": None, "end_time": None, "office_url": "https://example.edu/official",
            "sign_up_url": "https://example.edu/apply", "site_url": "https://example.edu/source",
        }, 2, 0


class BaoyanMachineTests(unittest.TestCase):
    def test_export_writes_manifest_last_and_keeps_raw_reference(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "staging"
            request = {
                "source": "baoyan",
                "scope": {"school": "示例大学", "kind": "预推免", "year": 2025},
                "limits": {"max_records": 10, "max_bytes": 100_000, "timeout_ms": 30_000},
            }
            with patch.object(baoyan, "MachineClient", FakeClient):
                manifest_path = baoyan.export_baoyan(request, output)
            self.assertEqual(manifest_path.name, "manifest.json")
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            result = validate_manifest(output, manifest, max_bytes=100_000, max_records=10)
            self.assertEqual(result["records"][0]["raw_ref"]["path"], "raw/response-000002.json")
            self.assertEqual(manifest["counts"], {"scanned": 1, "expected": 1, "selected": 1})
            self.assertTrue(manifest_path.stat().st_mtime_ns >= (output / "records.jsonl").stat().st_mtime_ns)

    def test_manifest_rejects_wrong_source_and_unstable_expected_count(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "staging"
            output.mkdir()
            raw = output / "raw"; raw.mkdir()
            (raw / "one.json").write_text("{}", encoding="utf-8")
            (output / "records.jsonl").write_text("", encoding="utf-8")
            files = []
            for path, kind in ((output / "records.jsonl", "records"), (raw / "one.json", "raw")):
                digest, size = baoyan.sha256_file(path)
                files.append({"path": path.relative_to(output).as_posix(), "sha256": digest, "byte_length": size, "kind": kind})
            manifest = {"schema_version": 1, "source": "other", "adapter_version": "test",
                        "scope": {"school": "示例大学", "kind": "全部", "year": None}, "fetched_at": "2026-09-05T00:00:00Z",
                        "complete": True, "counts": {"scanned": 1, "expected": 0, "selected": 0}, "files": files}
            with self.assertRaisesRegex(ValueError, "INVALID_MANIFEST"):
                validate_manifest(output, manifest)
            manifest["source"] = "baoyan"
            with self.assertRaisesRegex(ValueError, "INVALID_MANIFEST_COUNTS"):
                validate_manifest(output, manifest)


if __name__ == "__main__":
    unittest.main()

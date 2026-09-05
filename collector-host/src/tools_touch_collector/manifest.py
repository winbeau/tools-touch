from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path, PurePosixPath
from typing import Any


SCHEMA_VERSION = 1
MAX_RECORD_LINE_BYTES = 1_048_576
RECORD_FIELDS = {
    "external_id", "school", "department", "source_year", "kind", "title",
    "registration_start_raw", "registration_end_raw", "event_start_raw",
    "event_end_raw", "official_url", "application_url", "source_url", "raw_ref",
}
MANIFEST_FIELDS = {
    "schema_version", "source", "adapter_version", "scope", "fetched_at", "complete", "counts", "files",
}
FILE_FIELDS = {"path", "sha256", "byte_length", "kind"}


def _fail(message: str) -> None:
    raise ValueError(message)


def safe_relative_path(value: Any) -> str:
    if not isinstance(value, str) or not value or "\\" in value:
        _fail("INVALID_MANIFEST_PATH")
    path = PurePosixPath(value)
    if path.is_absolute() or any(part in ("", ".", "..") for part in path.parts):
        _fail("INVALID_MANIFEST_PATH")
    normalized = path.as_posix()
    if normalized != value:
        _fail("INVALID_MANIFEST_PATH")
    return normalized


def _ensure_no_symlink(root: Path, relative: str) -> Path:
    current = root
    for part in PurePosixPath(relative).parts:
        current /= part
        if current.is_symlink() or (current.exists() and current.is_dir() and os.path.islink(current)):
            _fail("MANIFEST_SYMLINK_FORBIDDEN")
    return current


def sha256_file(path: Path) -> tuple[str, int]:
    digest = hashlib.sha256()
    size = 0
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
            size += len(chunk)
    return digest.hexdigest(), size


def _required_string(value: dict[str, Any], key: str) -> str:
    result = value.get(key)
    if not isinstance(result, str) or not result:
        _fail("INVALID_MANIFEST")
    return result


def _url_or_none(value: Any) -> None:
    if value is not None and (not isinstance(value, str) or not value.startswith(("http://", "https://"))):
        _fail("INVALID_RECORD_URL")


def validate_record(record: Any, source: str, raw_files: set[str]) -> None:
    if not isinstance(record, dict) or set(record) != RECORD_FIELDS:
        _fail("INVALID_RECORD_SCHEMA")
    _required_string(record, "external_id")
    if _required_string(record, "school") == "":
        _fail("INVALID_RECORD_SCHOOL")
    _required_string(record, "kind")
    _required_string(record, "title")
    for key in ("department", "registration_start_raw", "registration_end_raw", "event_start_raw", "event_end_raw",
                "official_url", "application_url", "source_url"):
        if record[key] is not None and not isinstance(record[key], str):
            _fail("INVALID_RECORD_FIELD")
    if record["source_url"] is None:
        _fail("INVALID_RECORD_SOURCE")
    _url_or_none(record["official_url"])
    _url_or_none(record["application_url"])
    _url_or_none(record["source_url"])
    if record["source_year"] is not None and (not isinstance(record["source_year"], int) or isinstance(record["source_year"], bool) or not 1900 <= record["source_year"] <= 2200):
        _fail("INVALID_RECORD_YEAR")
    if record["kind"] not in ("SummerCamp", "PreRecommendation", "PreRecommendation|SummerCamp"):
        _fail("INVALID_RECORD_KIND")
    raw_ref = record["raw_ref"]
    if not isinstance(raw_ref, dict) or set(raw_ref) not in ({"path"}, {"path", "json_pointer"}):
        _fail("INVALID_RAW_REF")
    raw_path = safe_relative_path(raw_ref["path"])
    if raw_path not in raw_files or not raw_path.startswith("raw/"):
        _fail("INVALID_RAW_REF")
    if "json_pointer" in raw_ref and (not isinstance(raw_ref["json_pointer"], str) or not raw_ref["json_pointer"].startswith("/")):
        _fail("INVALID_RAW_REF")


def validate_manifest(root: Path, manifest: dict[str, Any], *, max_bytes: int = 100 * 1024 * 1024,
                      max_records: int = 100_000) -> dict[str, Any]:
    if not isinstance(manifest, dict) or set(manifest) != MANIFEST_FIELDS:
        _fail("INVALID_MANIFEST_SCHEMA")
    if manifest["schema_version"] != SCHEMA_VERSION or manifest["source"] != "baoyan":
        _fail("INVALID_MANIFEST")
    _required_string(manifest, "adapter_version")
    scope = manifest["scope"]
    if not isinstance(scope, dict) or set(scope) != {"school", "kind", "year"} or not isinstance(scope["school"], str) or not scope["school"]:
        _fail("INVALID_MANIFEST_SCOPE")
    if scope["kind"] not in ("全部", "夏令营", "预推免"):
        _fail("INVALID_MANIFEST_SCOPE")
    if scope["year"] is not None and (not isinstance(scope["year"], int) or isinstance(scope["year"], bool) or not 1900 <= scope["year"] <= 2200):
        _fail("INVALID_MANIFEST_SCOPE")
    if not isinstance(manifest["fetched_at"], str) or not manifest["fetched_at"].endswith("Z"):
        _fail("INVALID_MANIFEST_TIME")
    if not isinstance(manifest["complete"], bool):
        _fail("INVALID_MANIFEST")
    counts = manifest["counts"]
    if not isinstance(counts, dict) or set(counts) != {"scanned", "expected", "selected"}:
        _fail("INVALID_MANIFEST_COUNTS")
    if not isinstance(counts["scanned"], int) or counts["scanned"] < 0 or not isinstance(counts["selected"], int) or counts["selected"] < 0:
        _fail("INVALID_MANIFEST_COUNTS")
    if counts["expected"] is not None and (not isinstance(counts["expected"], int) or counts["expected"] < 0):
        _fail("INVALID_MANIFEST_COUNTS")
    if counts["selected"] > max_records or counts["scanned"] < counts["selected"] or (counts["expected"] is not None and counts["expected"] < counts["scanned"]):
        _fail("INVALID_MANIFEST_COUNTS")
    files = manifest["files"]
    if not isinstance(files, list) or not files:
        _fail("INVALID_MANIFEST_FILES")
    seen: set[str] = set()
    total = 0
    raw_files: set[str] = set()
    records_path: str | None = None
    for item in files:
        if not isinstance(item, dict) or set(item) != FILE_FIELDS:
            _fail("INVALID_MANIFEST_FILE")
        path = safe_relative_path(item["path"])
        if path in seen:
            _fail("DUPLICATE_MANIFEST_PATH")
        seen.add(path)
        if item["kind"] not in ("records", "raw"):
            _fail("INVALID_MANIFEST_FILE_KIND")
        if not isinstance(item["sha256"], str) or len(item["sha256"]) != 64 or any(c not in "0123456789abcdef" for c in item["sha256"]):
            _fail("INVALID_MANIFEST_HASH")
        if not isinstance(item["byte_length"], int) or item["byte_length"] < 0:
            _fail("INVALID_MANIFEST_LENGTH")
        path_obj = _ensure_no_symlink(root, path)
        if not path_obj.is_file():
            _fail("MANIFEST_FILE_MISSING")
        actual_hash, actual_length = sha256_file(path_obj)
        if actual_hash != item["sha256"] or actual_length != item["byte_length"]:
            _fail("MANIFEST_FILE_HASH_MISMATCH")
        total += actual_length
        if total > max_bytes:
            _fail("MANIFEST_TOO_LARGE")
        if item["kind"] == "records":
            if records_path is not None or path != "records.jsonl":
                _fail("INVALID_RECORDS_FILE")
            records_path = path
        else:
            if not path.startswith("raw/"):
                _fail("INVALID_RAW_FILE")
            raw_files.add(path)
    if records_path is None:
        _fail("RECORDS_FILE_MISSING")
    records: list[dict[str, Any]] = []
    identities: set[str] = set()
    records_file = root / records_path
    with records_file.open("rb") as stream:
        for line in stream:
            if len(line.rstrip(b"\r\n")) > MAX_RECORD_LINE_BYTES:
                _fail("RECORD_LINE_TOO_LARGE")
            if not line.strip():
                _fail("EMPTY_RECORD_LINE")
            try:
                record = json.loads(line)
            except json.JSONDecodeError:
                _fail("INVALID_RECORD_JSON")
            validate_record(record, manifest["source"], raw_files)
            if record["external_id"] in identities:
                _fail("DUPLICATE_EXTERNAL_ID")
            identities.add(record["external_id"])
            records.append(record)
            if len(records) > max_records:
                _fail("TOO_MANY_RECORDS")
    if len(records) != counts["selected"]:
        _fail("RECORD_COUNT_MISMATCH")
    return {"records": records, "total_bytes": total, "raw_files": sorted(raw_files)}

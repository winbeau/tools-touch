from __future__ import annotations

import hashlib
import json
import os
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import httpx

from .manifest import SCHEMA_VERSION, sha256_file, validate_manifest


API = "http://api.baoyanwang.com.cn/api/v1"
SITE = "http://pc.baoyanwang.com.cn"
SIGN_SECRET = "e3fa66d113ae5dd8f291e10209e57bcf"
ADAPTER_VERSION = "baoyan-http-0.1.0"


def _now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


class MachineClient:
    def __init__(self, raw_dir: Path, *, anonymous: bool = True, delay: float = 0.3, max_pages: int = 10_000):
        self.raw_dir = raw_dir
        self.raw_index = 0
        self.token = "" if anonymous else os.environ.get("BAOYAN_TOKEN", "")
        self.delay = delay
        self.max_pages = max_pages
        self.last_request = 0.0
        self.last_expected: int | None = None
        self.http = httpx.Client(base_url=API, timeout=30, headers={"Referer": SITE + "/", "User-Agent": "tools-touch-collector/0.1"})

    def close(self) -> None:
        self.http.close()

    def get(self, path: str, **params: Any) -> dict[str, Any]:
        for attempt in range(3):
            time.sleep(max(0.0, self.delay - (time.monotonic() - self.last_request)))
            nonce = f"{uuid.uuid4()}-{int(time.time() * 1000)}"
            headers = {
                "X-Auth-Nonce": nonce,
                "X-Auth-Device": "web",
                "X-Auth-Sign": hashlib.md5(f"nonce={nonce}&secret={SIGN_SECRET}".encode()).hexdigest(),
            }
            if self.token:
                headers["X-Auth-Key"] = self.token
            self.last_request = time.monotonic()
            try:
                response = self.http.get(path, params=params, headers=headers)
                if response.status_code == 429 or response.status_code >= 500:
                    if attempt < 2:
                        time.sleep(2 ** (attempt + 1))
                        continue
                response.raise_for_status()
                data = response.json()
            except (httpx.TimeoutException, httpx.NetworkError):
                if attempt < 2:
                    time.sleep(2 ** (attempt + 1))
                    continue
                raise RuntimeError("SOURCE_NETWORK_FAILED") from None
            if not data.get("success"):
                if data.get("code") == 401:
                    raise RuntimeError("SOURCE_AUTH_REQUIRED")
                raise RuntimeError("SOURCE_REQUEST_REJECTED")
            if data.get("encrypt"):
                raise RuntimeError("SOURCE_RESPONSE_ENCRYPTED")
            self.raw_index += 1
            raw_path = self.raw_dir / f"response-{self.raw_index:06d}.json"
            raw_path.write_text(json.dumps(data["result"], ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
            return data["result"]
        raise RuntimeError("SOURCE_NETWORK_FAILED")

    def pages(self, path: str, *, size: int = 100, **params: Any):
        seen: set[str] = set()
        expected: int | None = None
        for page in range(1, self.max_pages + 1):
            result = self.get(path, page=page, size=size, **params)
            if not isinstance(result, dict) or not isinstance(result.get("content"), list):
                raise RuntimeError("SOURCE_LIST_SCHEMA_CHANGED")
            total = int(result.get("total_count", 0))
            if expected is None:
                expected = total
                self.last_expected = total
            elif expected != total:
                raise RuntimeError("SOURCE_TOTAL_CHANGED")
            batch = result["content"]
            for position, item in enumerate(batch):
                external_id = str(item.get("id", ""))
                if not external_id or external_id in seen:
                    raise RuntimeError("SOURCE_DUPLICATE_PAGE")
                seen.add(external_id)
                yield item, self.raw_index, position
            if len(seen) == total:
                return expected
            if not batch or len(seen) > total:
                raise RuntimeError("SOURCE_INCOMPLETE_PAGES")
        raise RuntimeError("SOURCE_PAGE_LIMIT")


def _kind(tags: str) -> set[str]:
    mapping = {"夏令营": "SummerCamp", "预推免": "PreRecommendation"}
    return {mapping[value.strip()] for value in tags.split(",") if value.strip() in mapping}


def _record(item: dict[str, Any], raw_index: int, raw_position: int, school: str) -> dict[str, Any]:
    tags = str(item.get("tags") or "")
    kinds = sorted(_kind(tags))
    return {
        "external_id": str(item["id"]),
        "school": school,
        "department": item.get("academy") or None,
        "source_year": item.get("year") or None,
        "kind": kinds[0] if len(kinds) == 1 else "|".join(kinds),
        "title": str(item.get("title") or ""),
        "registration_start_raw": item.get("sign_up_start") or None,
        "registration_end_raw": item.get("sign_up_end") or None,
        "event_start_raw": item.get("start_time") or None,
        "event_end_raw": item.get("end_time") or None,
        "official_url": item.get("office_url") or None,
        "application_url": item.get("sign_up_url") or None,
        "source_url": item.get("site_url") or f"{SITE}/articles/{item['id']}",
        "raw_ref": {"path": f"raw/response-{raw_index:06d}.json", "json_pointer": f"/content/{raw_position}"},
    }


def export_baoyan(request: dict[str, Any], output: Path) -> Path:
    if set(request) != {"source", "scope", "limits"} or request.get("source") != "baoyan":
        raise ValueError("INVALID_COLLECTOR_REQUEST")
    scope = request["scope"]
    limits = request["limits"]
    if not isinstance(scope, dict) or set(scope) - {"school", "kind", "year"} or not scope.get("school"):
        raise ValueError("INVALID_COLLECTOR_SCOPE")
    if not isinstance(limits, dict) or set(limits) - {"max_records", "max_bytes", "timeout_ms"}:
        raise ValueError("INVALID_COLLECTOR_LIMITS")
    max_records = int(limits.get("max_records", 100_000))
    max_bytes = int(limits.get("max_bytes", 100 * 1024 * 1024))
    if max_records < 1 or max_records > 100_000 or max_bytes < 1:
        raise ValueError("INVALID_COLLECTOR_LIMITS")
    if output.exists() and any(output.iterdir()):
        raise ValueError("OUTPUT_STAGING_NOT_EMPTY")
    output.mkdir(parents=True, exist_ok=True)
    raw_dir = output / "raw"
    raw_dir.mkdir()
    records_path = output / "records.jsonl"
    selected: list[dict[str, Any]] = []
    scanned = 0
    expected: int | None = None
    kind_filter = scope.get("kind", "全部")
    if kind_filter in (None, "", "全部"):
        wanted_kinds = {"SummerCamp", "PreRecommendation"}
    elif str(kind_filter) == "夏令营":
        wanted_kinds = {"SummerCamp"}
    elif str(kind_filter) == "预推免":
        wanted_kinds = {"PreRecommendation"}
    else:
        raise ValueError("INVALID_COLLECTOR_SCOPE")
    year = scope.get("year")
    client = MachineClient(raw_dir, anonymous=True, delay=float(limits.get("delay", 0.3)), max_pages=int(limits.get("max_pages", 10_000)))
    try:
        schools = list(client.pages("/search/colleges", name=scope["school"]))
        names = {str(item.get("name")) for item, _, _ in schools}
        if scope["school"] not in names:
            raise RuntimeError("SOURCE_SCHOOL_NOT_FOUND")
        page_result = client.pages("/articles", size=min(int(limits.get("page_size", 100)), 100), college=scope["school"], category="保研信息", all=1)
        # pages() yields items and returns the expected count through its generator return value only;
        # scanned records remain the authoritative count for the machine manifest.
        for item, raw_index, raw_position in page_result:
            scanned += 1
            tags = _kind(str(item.get("tags") or ""))
            if not tags.intersection(wanted_kinds):
                continue
            if year is not None and str(item.get("year") or "") != str(year):
                continue
            selected.append(_record(item, raw_index, raw_position, scope["school"]))
            if len(selected) > max_records:
                raise RuntimeError("COLLECTOR_RECORD_LIMIT")
        expected = client.last_expected
        with records_path.open("wb") as stream:
            for record in selected:
                stream.write((json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8"))
        files = []
        for path in sorted(output.rglob("*")):
            if path.is_file() and path.name != "manifest.json":
                digest, length = sha256_file(path)
                relative = path.relative_to(output).as_posix()
                files.append({"path": relative, "sha256": digest, "byte_length": length,
                              "kind": "records" if relative == "records.jsonl" else "raw"})
        manifest = {
            "schema_version": SCHEMA_VERSION,
            "source": "baoyan",
            "adapter_version": ADAPTER_VERSION,
            "scope": {"school": scope["school"], "kind": kind_filter or "全部", "year": year},
            "fetched_at": _now(),
            "complete": True,
            "counts": {"scanned": scanned, "expected": expected, "selected": len(selected)},
            "files": files,
        }
        staging_manifest = output / "manifest.json"
        temp_manifest = output / ".manifest.json.tmp"
        temp_manifest.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        os.replace(temp_manifest, staging_manifest)
        validate_manifest(output, manifest, max_bytes=max_bytes, max_records=max_records)
        return staging_manifest
    finally:
        client.close()

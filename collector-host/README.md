# Tools Touch machine collector

This package is the machine-readable adapter boundary for source collection. It keeps the existing `baoyan-cli` human-facing CSV/XLSX/JSON export unchanged.

The application invokes one short-lived command with a request file and an empty staging directory:

```sh
python -m tools_touch_collector export-baoyan \
  --request request.json \
  --output staging-directory
```

The request contains `source`, `scope` (`school`, optional `kind` and `year`) and `limits`. The source adapter currently performs anonymous baoyanwang HTTP pagination. It writes `records.jsonl`, captured responses under `raw/`, and writes `manifest.json` last. stdout contains only one JSON object with the manifest path; progress and failures belong on stderr.

The manifest records SHA-256 and byte length for every published file. Both the Python package and C# bridge reject unsafe paths, duplicate external IDs, inconsistent counts, incomplete output and hash mismatches. A source total describes the queried scope; `complete` does not claim nationwide or official coverage.

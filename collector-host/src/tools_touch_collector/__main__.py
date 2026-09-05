from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from .baoyan import export_baoyan
from .xlsx import export_xlsx, import_xlsx, validate_xlsx


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Tools Touch machine collector")
    sub = parser.add_subparsers(dest="command", required=True)
    baoyan = sub.add_parser("export-baoyan")
    baoyan.add_argument("--request", required=True)
    baoyan.add_argument("--output", required=True)
    xlsx = sub.add_parser("export-xlsx")
    xlsx.add_argument("--request", required=True)
    xlsx.add_argument("--output", required=True)
    xlsx_import = sub.add_parser("import-xlsx")
    xlsx_import.add_argument("--request", required=True)
    xlsx_import.add_argument("--output", required=True)
    args = parser.parse_args(argv)
    try:
        request = json.loads(Path(args.request).read_text(encoding="utf-8"))
        if args.command == "export-baoyan":
            manifest = export_baoyan(request, Path(args.output).resolve())
            print(json.dumps({"manifest_path": str(manifest)}, ensure_ascii=False), flush=True)
        elif args.command == "export-xlsx":
            workbook = export_xlsx(request, Path(args.output).resolve())
            manifest = validate_xlsx(workbook.parent)
            print(json.dumps({"workbook_path": str(workbook), "manifest": manifest}, ensure_ascii=False), flush=True)
        else:
            manifest = import_xlsx(request, Path(args.output).resolve())
            print(json.dumps({"manifest_path": str(manifest)}, ensure_ascii=False), flush=True)
        return 0
    except KeyboardInterrupt:
        print("COLLECTOR_CANCELLED", file=sys.stderr)
        return 130
    except (OSError, ValueError, KeyError, TypeError, RuntimeError) as error:
        print(str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

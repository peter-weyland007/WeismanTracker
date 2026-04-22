#!/usr/bin/env python3
"""Post normalized printer telemetry into WeismanTracker.

Usage:
  python3 scripts/post_printer_telemetry.py \
    --base-url http://100.75.86.96:5224 \
    --api-key weismantracker-dev-printer-collector \
    --collector-id printerops-lv426 \
    --name "Ricoh Front Office" \
    --hostname ricoh-front \
    --ip 10.20.51.204 \
    --manufacturer Ricoh \
    --model MP3353 \
    --serial RCH123456 \
    --state online \
    --total-pages 123456 \
    --mono-pages 120000 \
    --color-pages 3456 \
    --consumable "Black Toner:42:ok" \
    --consumable "Waste Toner:12:monitor"
"""

from __future__ import annotations

import argparse
import json
import sys
import urllib.error
import urllib.request
from datetime import datetime, timezone


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Post printer telemetry to WeismanTracker")
    parser.add_argument("--base-url", required=True, help="WeismanTracker API base URL, e.g. http://100.75.86.96:5224")
    parser.add_argument("--api-key", required=True, help="Collector API key for X-Printer-Collector-Key")
    parser.add_argument("--collector-id", required=True)
    parser.add_argument("--captured-at", help="ISO 8601 UTC timestamp. Defaults to now.")
    parser.add_argument("--name", required=True)
    parser.add_argument("--hostname")
    parser.add_argument("--ip")
    parser.add_argument("--manufacturer")
    parser.add_argument("--model")
    parser.add_argument("--serial")
    parser.add_argument("--state", default="online")
    parser.add_argument("--alert")
    parser.add_argument("--total-pages", type=int)
    parser.add_argument("--mono-pages", type=int)
    parser.add_argument("--color-pages", type=int)
    parser.add_argument(
        "--consumable",
        action="append",
        default=[],
        help="Consumable in the format Name:Percent:Status. Percent/status may be empty.",
    )
    return parser.parse_args()


def parse_consumable(raw: str) -> dict:
    parts = raw.split(":", 2)
    if len(parts) != 3:
        raise ValueError(f"Invalid consumable '{raw}'. Expected Name:Percent:Status")

    name, percent_raw, status_raw = parts
    if not name.strip():
        raise ValueError(f"Consumable name is required: '{raw}'")

    percent = None
    if percent_raw.strip():
        percent = float(percent_raw)

    status = status_raw.strip() or None
    return {
        "name": name.strip(),
        "percentRemaining": percent,
        "status": status,
    }


def resolve_captured_at(raw: str | None) -> str:
    if raw:
        return raw
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def build_payload(args: argparse.Namespace) -> dict:
    return {
        "collectorId": args.collector_id,
        "capturedAtUtc": resolve_captured_at(args.captured_at),
        "printer": {
            "name": args.name,
            "hostname": args.hostname,
            "ipAddress": args.ip,
            "manufacturer": args.manufacturer,
            "model": args.model,
            "serialNumber": args.serial,
        },
        "status": {
            "state": args.state,
            "alert": args.alert,
        },
        "usage": {
            "totalPages": args.total_pages,
            "monoPages": args.mono_pages,
            "colorPages": args.color_pages,
        },
        "consumables": [parse_consumable(item) for item in args.consumable],
    }


def post_payload(base_url: str, api_key: str, payload: dict) -> dict:
    url = base_url.rstrip("/") + "/api/printers/telemetry"
    data = json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(
        url,
        data=data,
        method="POST",
        headers={
            "Content-Type": "application/json",
            "X-Printer-Collector-Key": api_key,
        },
    )

    with urllib.request.urlopen(request, timeout=15) as response:
        return json.loads(response.read().decode("utf-8"))


def main() -> int:
    args = parse_args()

    try:
        payload = build_payload(args)
        result = post_payload(args.base_url, args.api_key, payload)
    except ValueError as exc:
        print(f"payload error: {exc}", file=sys.stderr)
        return 2
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", errors="replace")
        print(f"http error {exc.code}: {body}", file=sys.stderr)
        return 3
    except urllib.error.URLError as exc:
        print(f"connection error: {exc}", file=sys.stderr)
        return 4

    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

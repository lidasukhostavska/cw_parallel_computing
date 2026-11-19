#!/usr/bin/env python3

import argparse
import json
import time
import urllib.request
from pathlib import Path


THREAD_COUNTS = [1, 2, 4, 8]
OUTPUT_PATH = Path(__file__).with_name("index_performance.json")


def post_index(base_url, threads):
    url = base_url.rstrip("/") + "/api/index"
    body = json.dumps({"threads": threads}).encode("utf-8")
    request = urllib.request.Request(
        url,
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )

    with urllib.request.urlopen(request, timeout=600) as response:
        if response.status < 200 or response.status >= 300:
            raise RuntimeError(
                f"HTTP {response.status} returned by {url}"
            )
        return json.loads(response.read().decode("utf-8"))


def print_table(rows):
    headers = [
        "Threads",
        "Documents",
        "Time (ms)",
        "Speedup (T1/Tn)",
        "Efficiency (%)",
    ]
    values = [
        [
            str(row["threads"]),
            str(row["documents_indexed"]),
            f'{row["time_ms"]:.2f}',
            f'{row["speedup"]:.2f}',
            f'{row["efficiency_percent"]:.2f}',
        ]
        for row in rows
    ]
    widths = [
        max(len(headers[index]), *(len(row[index]) for row in values))
        for index in range(len(headers))
    ]

    def line():
        print("+-" + "-+-".join("-" * width for width in widths) + "-+")

    line()
    print(
        "| "
        + " | ".join(
            headers[index].ljust(widths[index])
            for index in range(len(headers))
        )
        + " |"
    )
    line()
    for row in values:
        print(
            "| "
            + " | ".join(
                row[index].ljust(widths[index])
                for index in range(len(headers))
            )
            + " |"
        )
    line()


def parse_args():
    parser = argparse.ArgumentParser(
        description="Measure indexing scalability."
    )
    parser.add_argument(
        "--url",
        default="http://localhost:8080",
        help="Backend base URL (default: http://localhost:8080)",
    )
    return parser.parse_args()


def main():
    args = parse_args()
    rows = []
    baseline_time = None

    for threads in THREAD_COUNTS:
        payload = post_index(args.url, threads)
        time_ms = float(payload["time_ms"])
        documents = int(payload["documents_indexed"])

        if baseline_time is None:
            baseline_time = time_ms

        speedup = baseline_time / time_ms if time_ms > 0 else 0.0
        efficiency = speedup / threads * 100.0

        rows.append(
            {
                "threads": threads,
                "documents_indexed": documents,
                "time_ms": time_ms,
                "speedup": speedup,
                "efficiency_percent": efficiency,
            }
        )

    print_table(rows)

    results = {
        "base_url": args.url.rstrip("/"),
        "results": rows,
    }
    OUTPUT_PATH.write_text(
        json.dumps(results, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )
    print(f"\nSaved results to {OUTPUT_PATH}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

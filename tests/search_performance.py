#!/usr/bin/env python3

import argparse
import concurrent.futures
import json
import statistics
import time
import urllib.request
from pathlib import Path


CONCURRENCY_LEVELS = [1, 5, 10, 20]
QUERIES = [
    "space",
    "moon",
    "adventure",
    "king",
    "love",
    "war",
    "science",
    "ocean",
    "history",
    "night",
]
OUTPUT_PATH = Path(__file__).with_name("search_performance.json")


def post_search(base_url, query):
    url = base_url.rstrip("/") + "/api/search"
    body = json.dumps({"query": query, "mode": "keyword"}).encode("utf-8")
    request = urllib.request.Request(
        url,
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )

    started = time.perf_counter()
    with urllib.request.urlopen(request, timeout=60) as response:
        if response.status < 200 or response.status >= 300:
            raise RuntimeError(
                f"HTTP {response.status} returned by {url}"
            )
        response.read()
    return (time.perf_counter() - started) * 1000.0


def p95(values):
    if not values:
        return 0.0

    ordered = sorted(values)
    rank = (len(ordered) - 1) * 0.95
    lower = int(rank)
    upper = min(lower + 1, len(ordered) - 1)
    fraction = rank - lower
    return ordered[lower] + (ordered[upper] - ordered[lower]) * fraction


def print_table(rows):
    headers = [
        "Clients",
        "Total Requests",
        "Total Time (s)",
        "Throughput (req/s)",
        "Avg Latency (ms)",
        "P95 Latency (ms)",
    ]
    values = [
        [
            str(row["concurrency"]),
            str(row["total_requests"]),
            f'{row["total_time_s"]:.3f}',
            f'{row["throughput_req_s"]:.2f}',
            f'{row["average_latency_ms"]:.2f}',
            f'{row["p95_latency_ms"]:.2f}',
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


def positive_int(value):
    number = int(value)
    if number <= 0:
        raise argparse.ArgumentTypeError("must be greater than zero")
    return number


def parse_args():
    parser = argparse.ArgumentParser(
        description="Measure concurrent keyword search throughput."
    )
    parser.add_argument(
        "--url",
        default="http://localhost:8080",
        help="Backend base URL (default: http://localhost:8080)",
    )
    parser.add_argument(
        "--requests",
        type=positive_int,
        default=100,
        help="Total requests per concurrency level (default: 100)",
    )
    return parser.parse_args()


def main():
    args = parse_args()
    rows = []

    for concurrency in CONCURRENCY_LEVELS:
        latencies = []
        started = time.perf_counter()

        with concurrent.futures.ThreadPoolExecutor(
            max_workers=concurrency
        ) as executor:
            futures = []
            for request_number in range(args.requests):
                query = QUERIES[request_number % len(QUERIES)]
                futures.append(
                    executor.submit(post_search, args.url, query)
                )

            for future in concurrent.futures.as_completed(futures):
                latencies.append(future.result())

        total_time = time.perf_counter() - started
        throughput = args.requests / total_time if total_time > 0 else 0.0
        average_latency = statistics.mean(latencies) if latencies else 0.0

        rows.append(
            {
                "concurrency": concurrency,
                "total_requests": args.requests,
                "total_time_s": total_time,
                "throughput_req_s": throughput,
                "average_latency_ms": average_latency,
                "p95_latency_ms": p95(latencies),
            }
        )

    print_table(rows)

    results = {
        "base_url": args.url.rstrip("/"),
        "requests_per_level": args.requests,
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

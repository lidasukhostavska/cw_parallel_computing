#!/usr/bin/env python3

import argparse
import os
import sys
from typing import Optional, Sequence


def positive_int(value: str) -> int:
    try:
        number = int(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("must be an integer") from exc

    if number <= 0:
        raise argparse.ArgumentTypeError("must be greater than zero")
    return number


def build_parser() -> argparse.ArgumentParser:
    default_url = os.environ.get("SEARCH_API_URL", "http://localhost:8080")
    parser = argparse.ArgumentParser(
        prog="search",
        description="Search documents through the parallel search API.",
    )
    parser.add_argument("query", nargs="?", help="search text")
    parser.add_argument(
        "--mode",
        choices=("ws", "keyword"),
        default="ws",
        help="search mode (default: ws)",
    )
    parser.add_argument(
        "--url",
        default=default_url,
        help="API base URL (default: %(default)s)",
    )
    parser.add_argument(
        "--top",
        type=positive_int,
        default=10,
        metavar="N",
        help="maximum number of results to display (default: 10)",
    )
    return parser


def run_keyword_search(
    query: str,
    url: str,
    top: int,
) -> int:
    """Stub for keyword REST search."""
    print("Keyword search is not implemented yet.", file=sys.stderr)
    return 1


def run_ws_search(
    query: str,
    url: str,
    top: int,
) -> int:
    """Stub for WebSocket streaming search."""
    print("WebSocket search is not implemented yet.", file=sys.stderr)
    return 1


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)

    if args.query is None:
        parser.error("missing positional argument: query")

    query = args.query.strip()
    if not query:
        parser.error("query must not be empty")

    if args.mode == "keyword":
        return run_keyword_search(query, args.url.rstrip("/"), args.top)
    return run_ws_search(query, args.url.rstrip("/"), args.top)


if __name__ == "__main__":
    raise SystemExit(main())

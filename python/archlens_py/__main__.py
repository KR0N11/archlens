"""python -m archlens_py <folder> [--include-tests]: prints the boxes and arrows as JSON.

The C# side runs this for Python repos and reads stdout, then traces and scores like any other graph.
"""
import argparse
import json
import sys

from .graph import analyze


def main() -> int:
    parser = argparse.ArgumentParser(prog="archlens_py")
    parser.add_argument("root")
    parser.add_argument("--include-tests", action="store_true")
    args = parser.parse_args()
    graph = analyze(args.root, args.include_tests)
    for warning in graph.pop("warnings"):
        print(warning, file=sys.stderr)
    json.dump(graph, sys.stdout)
    return 0


if __name__ == "__main__":
    sys.exit(main())

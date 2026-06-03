#!/usr/bin/env python3
import csv
import sys


def main() -> int:
    rows = list(csv.reader(sys.stdin))
    if not rows or len(rows[0]) < 24:
        print("0,,,,,")
        return 0

    r = rows[0]
    # 1-based columns from CliCsvFormatter.Columns:
    # 10=mean_mbps 20=jitter_ms 21=loss_pct 17=ping_avg 19=ping_p95 24=rtt_load_p95
    print(",".join([r[9], r[19], r[20], r[16], r[18], r[23]]))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

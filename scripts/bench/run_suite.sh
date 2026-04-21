#!/usr/bin/env bash
# gard vs iperf3 benchmark suite.
# Ver docs/benchmarks/README.md para contexto.

set -euo pipefail

# ---------- defaults ----------
HOST=""
TAG="adhoc"
MATRIX=""
REPS_OVERRIDE=""
QUICK=0
DRY_RUN=0
SELF_HOST=0
GARD_PORT=7737
IPERF_PORT=5201
GAP_S=3
GARD_CMD=""
REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"

usage() {
    cat <<EOF
uso: run_suite.sh --host <ip> [opciones]

opciones:
  --host <ip>          IP del servidor (obligatorio salvo --self-host)
  --tag <nombre>       etiqueta de la corrida (default: adhoc)
  --matrix <file>      CSV de matriz (default: docs/benchmarks/matrix.csv)
  --reps <N>           sobrescribe reps de todas las celdas
  --quick              1 rep, duracion 5s, solo primeras 3 celdas tcp
  --dry-run            imprime el plan sin ejecutar
  --self-host          levanta gard host + iperf3 -s localmente (para smoke)
  --gard-port <N>      puerto gard (default: 7737)
  --iperf-port <N>     puerto iperf3 (default: 5201)
  --gap <s>            pausa entre corridas (default: 3)
  --gard-cmd <cmd>     comando gard custom (default: dotnet run --project src/Gard.Cli --)
  -h | --help
EOF
}

# ---------- parse args ----------
while [[ $# -gt 0 ]]; do
    case "$1" in
        --host)       HOST="$2"; shift 2 ;;
        --tag)        TAG="$2"; shift 2 ;;
        --matrix)     MATRIX="$2"; shift 2 ;;
        --reps)       REPS_OVERRIDE="$2"; shift 2 ;;
        --quick)      QUICK=1; shift ;;
        --dry-run)    DRY_RUN=1; shift ;;
        --self-host)  SELF_HOST=1; shift ;;
        --gard-port)  GARD_PORT="$2"; shift 2 ;;
        --iperf-port) IPERF_PORT="$2"; shift 2 ;;
        --gap)        GAP_S="$2"; shift 2 ;;
        --gard-cmd)   GARD_CMD="$2"; shift 2 ;;
        -h|--help)    usage; exit 0 ;;
        *)            echo "arg desconocido: $1" >&2; usage; exit 2 ;;
    esac
done

[[ -z "$MATRIX" ]] && MATRIX="$REPO_ROOT/docs/benchmarks/matrix.csv"
if [[ -z "$GARD_CMD" ]]; then
    GARD_CMD="dotnet run --project $REPO_ROOT/src/Gard.Cli -c Release --"
fi

if [[ $SELF_HOST -eq 1 ]]; then
    [[ -z "$HOST" ]] && HOST="127.0.0.1"
fi
if [[ -z "$HOST" ]]; then
    echo "error: falta --host <ip>" >&2; usage; exit 2
fi

# ---------- deps ----------
for bin in iperf3 jq awk python3; do
    command -v "$bin" >/dev/null || { echo "falta dependencia: $bin" >&2; exit 3; }
done
[[ -f "$MATRIX" ]] || { echo "no encuentro matrix: $MATRIX" >&2; exit 3; }

# ---------- self-host (smoke) ----------
declare -a BG_PIDS=()
cleanup() {
    local code=$?
    for pid in "${BG_PIDS[@]:-}"; do
        [[ -n "$pid" ]] && kill "$pid" 2>/dev/null || true
    done
    wait 2>/dev/null || true
    exit $code
}
trap cleanup EXIT INT TERM

if [[ $SELF_HOST -eq 1 ]]; then
    echo "→ self-host: levantando iperf3 -s :$IPERF_PORT y gard host :$GARD_PORT..."
    iperf3 -s -p "$IPERF_PORT" >/dev/null 2>&1 &
    BG_PIDS+=($!)
    # shellcheck disable=SC2086
    $GARD_CMD host --port "$GARD_PORT" >/dev/null 2>&1 &
    BG_PIDS+=($!)
    sleep 3  # dar tiempo a que gard (dotnet build + start) arranque
fi

# ---------- preflight ----------
echo "→ preflight ping a $HOST..."
if ! ping -c 3 -W 1000 "$HOST" >/dev/null 2>&1; then
    echo "  AVISO: ping falla, sigo igual"
fi

# ---------- output layout ----------
RUN_DIR="$REPO_ROOT/docs/benchmarks/runs/$(date +%Y-%m-%d)_$TAG"
mkdir -p "$RUN_DIR"
RESULTS="$RUN_DIR/results.csv"
SUMMARY="$RUN_DIR/summary.md"

echo "run_id,timestamp,rep,tool,transport,direction,streams,duration_s,payload,target_bitrate_bps,throughput_mbps,jitter_ms,loss_pct,ping_avg_ms,ping_p95_ms,rtt_load_p95_ms,notes" > "$RESULTS"

RUN_ID="$(date +%Y%m%d-%H%M%S)-$TAG"

# ---------- load matrix ----------
declare -a CELLS=()
while IFS= read -r line; do
    [[ -z "$line" || "$line" =~ ^# ]] && continue
    [[ "$line" == direction,* ]] && continue
    CELLS+=("$line")
done < "$MATRIX"

if [[ $QUICK -eq 1 ]]; then
    # primeras 3 celdas, reps=1, duracion forzada a 5s
    CELLS=("${CELLS[@]:0:3}")
    REPS_OVERRIDE=1
    # reemplazar columna 3 (duration_s) con 5 en cada celda
    for i in "${!CELLS[@]}"; do
        IFS=',' read -r d s _ p t b r <<< "${CELLS[$i]}"
        CELLS[$i]="$d,$s,5,$p,$t,$b,$r"
    done
fi

echo "→ matriz: ${#CELLS[@]} celdas, salida en $RUN_DIR"

# ---------- iperf3 runner ----------
run_iperf3() {
    local direction="$1" streams="$2" duration="$3" payload="$4" transport="$5" bitrate="$6"
    local flags=(-c "$HOST" -p "$IPERF_PORT" -t "$duration" -P "$streams" --json --omit 2)
    case "$direction" in
        down)  flags+=(-R) ;;
        up)    : ;;
        bidir) flags+=(--bidir) ;;
    esac
    if [[ "$transport" == udp ]]; then
        flags+=(-u -b "${bitrate}" -l "$payload")
    else
        flags+=(-l "$payload")
    fi
    if [[ $DRY_RUN -eq 1 ]]; then echo "DRY: iperf3 ${flags[*]}" >&2; echo '{}'; return; fi
    iperf3 "${flags[@]}" 2>/dev/null || echo '{}'
}

parse_iperf3() {
    # stdin: json iperf3
    # echo: throughput_mbps,jitter_ms,loss_pct
    jq -r '
        if .end then
            [ (if .end.sum_bidir_recv then
                 ((.end.sum_bidir_recv.bits_per_second // 0) + (.end.sum_bidir_send.bits_per_second // 0)) / 1e6
               elif .end.sum_received then
                 (.end.sum_received.bits_per_second // 0) / 1e6
               elif .end.sum then
                 (.end.sum.bits_per_second // 0) / 1e6
               else 0 end),
              (.end.sum.jitter_ms // ""),
              (.end.sum.lost_percent // "") ]
        else [0,"",""]
        end
        | @csv' 2>/dev/null | tr -d '"' || echo "0,,"
}

# ---------- gard runner ----------
run_gard() {
    local direction="$1" streams="$2" duration="$3" payload="$4" transport="$5" bitrate="$6"
    local flags=(test --host "$HOST" --port "$GARD_PORT" --direction "$direction"
                 --streams "$streams" --duration "$duration" --payload "$payload"
                 --warmup 2 --format csv --label "$RUN_ID")
    if [[ "$transport" == udp ]]; then
        flags+=(--transport udp --bitrate "$bitrate")
    fi
    if [[ $DRY_RUN -eq 1 ]]; then echo "DRY: gard ${flags[*]}" >&2; echo ""; return; fi
    # shellcheck disable=SC2086
    $GARD_CMD "${flags[@]}" 2>/dev/null || echo ""
}

parse_gard() {
    # stdin: CSV row (sin header). Columnas documentadas en Program.cs:CsvColumns().
    # Indices (0-based): 7=mean_mbps, 17=jitter_ms, 18=loss_pct, 14=ping_avg_ms, 16=ping_p95_ms, 22=rtt_load_p95_ms
    awk -F',' '{
        if (NF < 10) { print "0,,,,,"; exit }
        print $8","$18","$19","$15","$17","$23
    }'
}

# ---------- iteration ----------
shuffle_indices() {
    local n="$1"
    python3 -c "import random,sys; r=list(range(int(sys.argv[1]))); random.shuffle(r); print(' '.join(map(str,r)))" "$n"
}

run_cell() {
    local rep="$1" cell="$2" order="$3"
    IFS=',' read -r direction streams duration payload transport bitrate reps <<< "$cell"

    local tool1 tool2
    if [[ "$order" == ig ]]; then tool1=iperf3; tool2=gard; else tool1=gard; tool2=iperf3; fi

    for tool in "$tool1" "$tool2"; do
        local ts; ts="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        local tp="" jit="" loss="" ping_avg="" ping_p95="" rtt_p95=""
        echo "  [$tool] $transport $direction s=$streams d=${duration}s p=$payload${bitrate:+ br=$bitrate}"
        if [[ "$tool" == iperf3 ]]; then
            local out; out="$(run_iperf3 "$direction" "$streams" "$duration" "$payload" "$transport" "$bitrate")"
            IFS=',' read -r tp jit loss <<< "$(echo "$out" | parse_iperf3)"
        else
            if [[ "$transport" == udp ]]; then
                echo "    SKIP gard udp — no implementado aun (LSP/1.2)"
                continue
            fi
            local out; out="$(run_gard "$direction" "$streams" "$duration" "$payload" "$transport" "$bitrate")"
            IFS=',' read -r tp jit loss ping_avg ping_p95 rtt_p95 <<< "$(echo "$out" | parse_gard)"
        fi
        echo "$RUN_ID,$ts,$rep,$tool,$transport,$direction,$streams,$duration,$payload,$bitrate,$tp,$jit,$loss,$ping_avg,$ping_p95,$rtt_p95," >> "$RESULTS"
        sleep "$GAP_S"
    done
}

# ---------- main loop ----------
MAX_REPS=1
for cell in "${CELLS[@]}"; do
    IFS=',' read -r _ _ _ _ _ _ reps <<< "$cell"
    [[ -n "$REPS_OVERRIDE" ]] && reps="$REPS_OVERRIDE"
    (( reps > MAX_REPS )) && MAX_REPS="$reps"
done

for rep in $(seq 1 "$MAX_REPS"); do
    echo "→ repeticion $rep / $MAX_REPS"
    # orden randomizado de celdas
    indices=$(shuffle_indices "${#CELLS[@]}")
    for idx in $indices; do
        cell="${CELLS[$idx]}"
        IFS=',' read -r _ _ _ _ _ _ cell_reps <<< "$cell"
        [[ -n "$REPS_OVERRIDE" ]] && cell_reps="$REPS_OVERRIDE"
        (( rep > cell_reps )) && continue
        # alternar orden herramienta celda por celda
        order=$([[ $((rep + idx)) -eq 0 || $(( (rep + idx) % 2 )) -eq 0 ]] && echo ig || echo gi)
        run_cell "$rep" "$cell" "$order"
    done
done

# ---------- summary ----------
if [[ $DRY_RUN -eq 1 ]]; then
    echo "→ dry-run, no se genera summary"
    exit 0
fi

echo "→ generando summary.md..."
python3 - "$RESULTS" "$SUMMARY" "$RUN_ID" <<'PY'
import csv, statistics, sys
from collections import defaultdict

results_path, summary_path, run_id = sys.argv[1], sys.argv[2], sys.argv[3]

rows = list(csv.DictReader(open(results_path)))
rows = [r for r in rows if r["throughput_mbps"] not in ("", "0")]

cells = defaultdict(lambda: defaultdict(list))
for r in rows:
    key = (r["transport"], r["direction"], r["streams"], r["duration_s"], r["payload"], r["target_bitrate_bps"])
    cells[key][r["tool"]].append(float(r["throughput_mbps"]))

def med(xs): return round(statistics.median(xs), 2) if xs else None

with open(summary_path, "w") as f:
    f.write(f"# Summary — {run_id}\n\n")
    f.write("| transport | dir | streams | dur | payload | bitrate | iperf3 med (Mb/s) | gard med (Mb/s) | delta % | n iperf3 | n gard |\n")
    f.write("|---|---|---|---|---|---|---|---|---|---|---|\n")
    for key, by_tool in sorted(cells.items()):
        i = by_tool.get("iperf3", [])
        g = by_tool.get("gard", [])
        mi, mg = med(i), med(g)
        if mi and mg:
            delta = round((mg - mi) / mi * 100, 1)
        else:
            delta = ""
        f.write(f"| {key[0]} | {key[1]} | {key[2]} | {key[3]}s | {key[4]} | {key[5]} | {mi or ''} | {mg or ''} | {delta} | {len(i)} | {len(g)} |\n")
    f.write("\n_Mediana de las repeticiones por celda. `delta %` = (gard - iperf3) / iperf3._\n")
PY

echo "✓ listo. resultados: $RESULTS"
echo "✓ summary:          $SUMMARY"

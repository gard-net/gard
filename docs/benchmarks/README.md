# Benchmarks de gard

Este directorio documenta los tests de esfuerzo y validación del protocolo **LSP/1** y su implementación de referencia **gard**. Cada cambio al protocolo o al data-plane debe ir acompañado de una corrida nueva bajo `runs/` para trazar regresiones.

> **Regla**: desde 2026-04-21 las implementaciones del protocolo se hacen primero en gard (.NET) y luego se portan a las apps de referencia (Swift). Los benchmarks viven aquí.

---

## Estructura

```
docs/benchmarks/
  README.md            ← este archivo
  matrix.csv           ← definición de celdas a ejecutar (editable)
  runs/
    YYYY-MM-DD_<tag>/  ← una carpeta por corrida
      results.csv      ← resultados crudos (salida del script)
      summary.md       ← tabla de medianas gard vs iperf3
      environment.md   ← equipo, router, cables, versiones
      notes.md         ← observaciones cualitativas
```

---

## Metodología

### Principio
Cada celda de la matriz se ejecuta **N veces** por herramienta en modo *back-to-back*: primero iperf3, luego gard (o viceversa, alternado). Esto neutraliza deriva térmica, ruido Wi-Fi puntual, y carga del host. Se reporta la **mediana** y min/max de las N repeticiones.

### Control de variables
- Wi-Fi apagado durante pruebas por cable (el script intenta detectarlo y advierte).
- Spotlight / Time Machine / cualquier sync pausados en el MBP antes de correr.
- Mismo puerto del router / switch en todas las corridas de una suite.
- Warmup de 2s descartado en ambas herramientas (`gard --warmup 2`, `iperf3 --omit 2`).
- Orden de celdas randomizado dentro de cada repetición.

### Métricas
| Métrica | iperf3 | gard |
|---|---|---|
| throughput mean / peak | ✅ | ✅ |
| jitter (UDP) | ✅ | ✅ (TCP+UDP) |
| loss % | sólo UDP | ✅ siempre |
| ping bajo carga (p95, spikes) | ❌ | ✅ |
| one-way delay | ❌ | ✅ (con UDP + ClockSync, LSP/1.2) |

La **comparación directa** es sólo throughput. Las métricas exclusivas de gard se reportan como información adicional de LSP.

---

## Cómo correr una suite

### Pre-requisitos

**Máquina Windows** (servidor):
```powershell
# terminal 1
.\iperf3.exe -s -p 5201

# terminal 2
.\gard.exe host --port 7737
```

Abrir ambos puertos en el firewall. Confirmar IP con `ipconfig`.

**Máquina macOS** (cliente, donde corre el script):
```bash
brew install iperf3 jq      # si no están
dotnet build                # gard CLI disponible vía `dotnet run --project src/Gard.Cli`
```

### Ejecutar

```bash
# smoke-test rápido (1 rep, duración corta) sobre loopback
scripts/bench/run_suite.sh --host 127.0.0.1 --quick --tag smoke

# suite completa contra Windows
scripts/bench/run_suite.sh --host 192.168.1.50 --tag cable_baseline

# con matrix custom
scripts/bench/run_suite.sh --host 192.168.1.50 --matrix mi_matrix.csv --tag extremo_wifi
```

El script crea `runs/YYYY-MM-DD_<tag>/` con `results.csv` y un `summary.md` preliminar. El operador completa `environment.md` y `notes.md` a mano.

### Dry-run

```bash
scripts/bench/run_suite.sh --host 192.168.1.50 --dry-run
```

Imprime el plan sin ejecutar.

---

## Formato de `results.csv`

```
run_id,timestamp,rep,tool,transport,direction,streams,duration_s,payload,target_bitrate_bps,
throughput_mbps,jitter_ms,loss_pct,ping_avg_ms,ping_p95_ms,rtt_load_p95_ms,notes
```

Una fila por (celda × rep × herramienta). `jitter_ms`, `loss_pct`, `ping_*`, `rtt_load_*` quedan vacíos cuando la herramienta no los reporta (ej. iperf3 TCP no da loss%).

---

## Formato de `matrix.csv`

Ver `matrix.csv`. Cada fila es una celda: `direction, streams, duration_s, payload, transport, target_bitrate_bps, reps`. Se puede editar sin tocar el script.

---

## Convenciones para archivar una corrida

1. Correr el script con `--tag <descriptivo>`.
2. Editar `runs/.../environment.md` siguiendo el template.
3. Escribir `notes.md` con cualquier cosa inusual (pico de CPU, reconexión, usuario del router haciendo ruido, etc.).
4. **No commitear** `results.csv` si la corrida fue contaminada — marcarla `notes.md` y re-correr.
5. Commit con mensaje `bench: <tag> (<N> celdas, gard vs iperf3)`.

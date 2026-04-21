# Landspeed Protocol v1 (LSP/1)

**Versión:** 1.2
**Estado:** estable. Compatible hacia atrás con peers LSP/1.0 y LSP/1.1 vía negociación de `caps` (§5) y tolerancia a campos JSON desconocidos (§3). LSP/1.2 añade plano de datos UDP — §12.
**Licencia de la especificación:** libre para implementación en cualquier plataforma. Esta especificación es la **fuente de verdad** para ports a Windows, Android, Linux u otros.

LSP/1 es un protocolo simple sobre TCP para medir calidad de red local (throughput, latencia, jitter, pérdida de paquetes) entre dos peers en la misma LAN. No es un protocolo de Internet: está diseñado para el enlace entre dispositivos del usuario.

Objetivos de diseño:

- **Simple:** especificable en una página, implementable en un fin de semana.
- **Portable:** nada específico de Apple; cualquier stack con TCP + mDNS puede implementarlo.
- **Auditable:** framing binario trivial, control en JSON legible.
- **Honesto en la medición:** control y datos viajan por frames distintos para que el parser de control no limite el throughput.

---

## 1. Descubrimiento (Bonjour / mDNS)

- **Tipo de servicio:** `_landspeed._tcp`
- **Puerto por defecto:** `7737`. El host anuncia su puerto real; 7737 es sólo el predeterminado cuando está libre.
- **TXT records obligatorios** publicados por el host:

| Clave | Valor | Descripción |
|---|---|---|
| `v` | `1` | Versión mayor del protocolo. |
| `name` | UTF-8 ≤ 63 bytes | Nombre visible del dispositivo. |
| `platform` | `ios` / `ipados` / `macos` / `tvos` / `windows` / `android` / `linux` | Plataforma del peer. |
| `caps` | hex uint32 | Capacidades soportadas (ver §5). |

---

## 2. Formato de frame

Todo byte transmitido en **cualquier** conexión LSP/1 sigue este frame:

```
+--------+--------+---------------------+
| len    | type   | payload             |
| 4 B BE | 1 B    | (len - 1) bytes     |
+--------+--------+---------------------+
```

- `len` (uint32 big-endian): tamaño del resto del frame en bytes, esto es `1 + payload.length`.
- `type` (uint8): ver §2.1.
- `payload`: `len - 1` bytes; su formato depende de `type`.

**Límite:** `len ≤ 16 MiB` (16 777 216). Un frame mayor es rechazado con `ERROR {code: 1001}` y la conexión se cierra.

**Longitud cero** (`len == 0`) es inválida; se rechaza con `ERROR {code: 1002}` y cierre.

### 2.1 Tipos de frame

| `type` | Nombre | Payload | Uso |
|---|---|---|---|
| `0x01` | `CONTROL_JSON` | JSON UTF-8 (§3) | Mensajes de control. |
| `0x02` | `DATA_BINARY` | bytes arbitrarios | Bloques de throughput (relleno pseudoaleatorio). |
| `0x03` | `HEARTBEAT` | vacío o `uint64 BE ns` | Keep-alive; opcionalmente lleva timestamp monotónico. |
| `0x04` | `DATA_BINARY_ECHO` | ≥ 8 B (ver abajo) | El receptor DEBE reenviar el payload tal cual al remitente. Usado para RTT bajo carga (LSP/1.1). |
| `0x05 … 0xEF` | — | — | Reservados para v1.x. |
| `0xFF` | `ERROR` | JSON `{code,message}` | Notifica error; suele ir seguido de cierre. |

Tipos no conocidos se rechazan con `ERROR {code: 1003}`.

**Formato del payload `DATA_BINARY_ECHO` (LSP/1.1):**

- Bytes `0..8`: timestamp monotónico del remitente en nanosegundos, `uint64` **little-endian**.
- Bytes `8..N`: padding pseudoaleatorio opcional (tamaño libre; la implementación de referencia usa 56 B ⇒ payload total 64 B).

El receptor DEBE reenviar el payload **tal cual** (mismo tamaño, mismos bytes) sobre la misma conexión. El remitente mide `RTT = now - timestamp` al recibir la réplica. Implementaciones que no anuncien `data_echo` (cap `0x0020`) pueden ignorar estos frames.

### 2.2 Diagrama de decodificación

```
  ┌──── header (5 B) ───┐
  │ 00 00 00 03 01      │ len=3, type=0x01 (CONTROL_JSON)
  │ 7B 22 74 22 3A ...  │ payload = {"t":...}
```

**Patrón de parser streaming recomendado:**

```
buffer ← []
mientras llegan bytes:
    añadir al buffer
    mientras len(buffer) ≥ 4 y len(buffer) ≥ 4 + buffer[0..4].be_uint32:
        declared_len ← buffer[0..4].be_uint32
        emitir frame(type=buffer[4], payload=buffer[5..4+declared_len])
        buffer ← buffer[4+declared_len:]
```

---

## 3. Mensajes `CONTROL_JSON`

Todo mensaje de control es un objeto JSON UTF-8 con al menos:

```json
{ "t": "<tipo>", "id": <uint64> }
```

- `t`: discriminador textual (snake_case).
- `id`: correlation id elegido por el emisor. Las respuestas usan el mismo `id`.

Campos desconocidos en futuras menor-versiones **deben ignorarse** (forward compatibility).

### 3.1 Tabla de mensajes v1

| `t` | Sentido | Campos adicionales |
|---|---|---|
| `hello` | cliente → host | `protocol_version`, `app_version`, `platform`, `device_name`, `caps`, `nonce` |
| `hello_ack` | host → cliente | `protocol_version`, `session_id` (UUID), `server_time_ns`, `caps`, `requires_pairing` |
| `pair_request` | cliente → host | `pairing_code` (string, 6 dígitos) |
| `pair_ack` | host → cliente | `ok` (bool), `reason` (string, opcional) |
| `clock_sync` | cliente → host | `t0_ns` (uint64, monotónico cliente) |
| `clock_sync_ack` | host → cliente | `t0_ns`, `t1_ns` (entrada en host), `t2_ns` (salida de host) |
| `ping` | cualquier dir. | `seq` (uint32), `sent_ns` (uint64) |
| `pong` | respuesta | `seq`, `sent_ns`, `received_ns` |
| `test_start` | cliente → host | `direction` (`up`/`down`/`bidir`), `duration_s`, `streams` (1,2,4,8), `payload_size`, `warmup_s`, `bidir_mode` (`simultaneous`/`sequential`, opcional, LSP/1.1), `gap_s` (double, opcional, LSP/1.1), `transport` (`tcp`/`udp`, opcional, default `tcp`, LSP/1.2), `target_bitrate_bps` (uint64, opcional, sólo udp, LSP/1.2) |
| `test_start_ack` | host → cliente | `accepted`, `data_ports` (lista de puertos TCP), `reason` opcional |
| `test_tick` | host → cliente | `elapsed_s`, `bytes_up`, `bytes_down`, `ping_avg_ms`, `jitter_ms`, `loss_pct` |
| `test_end` | cualquiera | — |
| `result` | host → cliente | Ver §4 |
| `goodbye` | cualquiera | `reason` (string, opcional) |
| `error` | cualquiera | `code` (int), `message` (string) |

### 3.2 Unidades y tiempos

- **Throughput en el wire:** siempre **bits por segundo** (`bps`). La UI convierte a Mbps / MB/s según preferencia.
- **Timestamps:** `uint64` nanosegundos desde un epoch **monotónico** local de cada peer. Para comparar entre peers, se usa el offset obtenido en `clock_sync` (estilo NTP simplificado):
  ```
  offset ≈ ((t1 - t0) + (t2 - t3)) / 2    (con t3 = recepción del ack en cliente)
  RTT    ≈ (t3 - t0) - (t2 - t1)
  ```
- Todos los campos duración (`duration_s`, `warmup_s`, `elapsed_s`) son `double` segundos.

---

## 4. Estructura `result`

```json
{
  "t": "result",
  "id": 42,
  "session_id": "UUID",
  "started_at": "ISO-8601",
  "ended_at": "ISO-8601",
  "direction": "down",
  "streams": 4,
  "duration_s": 10.0,
  "throughput": {
    "mean_bps": 942873421,
    "peak_bps": 981223910,
    "per_stream_bps": [236000000, 235000000, 235900000, 235973421]
  },
  "latency_ms": { "min": 0.42, "avg": 0.71, "max": 3.11, "p95": 1.12 },
  "jitter_ms": 0.23,
  "loss_pct": 0.0,
  "samples": 234,
  "rtt_under_load": {
    "samples": 47,
    "min_ms": 0.9,
    "median_ms": 1.3,
    "p95_ms": 3.8,
    "max_ms": 12.1,
    "stdev_ms": 1.4,
    "baseline_median_ms": 1.1,
    "spikes_count": 2
  },
  "intervals": {
    "window_ms": 200,
    "samples": [
      { "start_s": 0.0, "end_s": 0.2, "bps": 935_000_000 },
      { "start_s": 0.2, "end_s": 0.4, "bps": 942_800_000 }
    ],
    "stats": {
      "median_bps": 941_500_000,
      "p95_bps": 948_000_000,
      "stdev_bps": 5_200_000,
      "min_bps": 915_000_000,
      "max_bps": 949_000_000
    }
  },
  "throughput_up":   { "mean_bps": 612_000_000, "peak_bps": 640_000_000, "per_stream_bps": [612_000_000] },
  "throughput_down": { "mean_bps": 480_000_000, "peak_bps": 495_000_000, "per_stream_bps": [480_000_000] },
  "protocol_version": "1.1"
}
```

- `mean_bps`: media descartando el warmup inicial.
- `peak_bps`: máximo de ventanas de 200 ms dentro del periodo útil.
- `jitter_ms`: desviación estándar de los RTTs de la fase ping.
- `loss_pct`: `pings_sin_pong / pings_enviados × 100` con timeout 500 ms.
- `rtt_under_load` (**LSP/1.1**, opcional): estadísticas de RTT medidas mediante frames `DATA_BINARY_ECHO` durante la fase de throughput. Sólo presente si ambos peers anunciaron `data_echo`. `baseline_median_ms` es la mediana del primer 25 % de muestras (post-warmup); `spikes_count` cuenta muestras cuyo RTT excede `3 × baseline` y actúa como proxy de eventos de retransmisión TCP.
- `intervals` (**LSP/1.1**, opcional): reporte granular por ventana. `window_ms` es el ancho nominal (200 ms en la referencia), `samples` el array cronológico de `{start_s,end_s,bps}`, y `stats` los agregados (`median_bps`, `p95_bps`, `stdev_bps`, `min_bps`, `max_bps`). Sólo presente si ambos peers anunciaron `interval_reporting`.
- `throughput_up`, `throughput_down` (**LSP/1.1**, opcionales): desglose por dirección en tests `bidir` con `bidir_mode=sequential`. Cada uno tiene la misma forma que `throughput` (`mean_bps`, `peak_bps`, `per_stream_bps`). El `throughput` top-level en secuencial agrega ambas direcciones: `mean_bps = up.mean + down.mean`, `peak_bps = max(up.peak, down.peak)`. En bidir simultáneo (o cuando `bidir_mode` está ausente) estos campos no se emiten.

---

## 5. Capabilities bitmask (`caps`)

`caps` se transmite como `uint32` en hexadecimal (minúsculas, sin `0x`, longitud variable):

| Bit | Nombre | Significado |
|---|---|---|
| `0x0001` | `parallel_streams` | Soporta N streams TCP paralelos de datos. |
| `0x0002` | `bidirectional` | Soporta direcciones simultáneas. |
| `0x0004` | `tls` | Soporta TLS sobre la conexión de control. |
| `0x0008` | `pairing` | Soporta pairing con código de 6 dígitos. |
| `0x0010` | `clock_sync` | Soporta sincronización de reloj NTP-style. |
| `0x0020` | `data_echo` | Soporta frames `DATA_BINARY_ECHO` (0x04) (RTT bajo carga). |
| `0x0040` | `bidir_sequential` | Soporta `test_start` con `bidir_mode = sequential`. **LSP/1.1**. |
| `0x0080` | `interval_reporting` | Soporta campo `intervals` en `result` (mediana/p95/stdev/min/max). **LSP/1.1**. |
| `0x0100` | `udp_data_plane` | Soporta plano de datos UDP (ver §12). **LSP/1.2**. |
| `0x0200 … 0x8000` | reservados | — |
| `0x0001_0000 …` | reservados para v2 | — |

Un peer DEBE operar con el subconjunto **mínimo común** (`a.caps ∩ b.caps`).

---

## 6. Flujo canónico

```
cliente                                         host
  │── TCP connect (control) ─────────────────────▶│
  │── CONTROL hello ──────────────────────────────▶│
  │◀──────────────────────── CONTROL hello_ack (requires_pairing=true)
  │── CONTROL pair_request (pairing_code) ────────▶│
  │◀───────────────────────────── CONTROL pair_ack (ok=true)
  │── CONTROL clock_sync (t0_ns) ─────────────────▶│
  │◀───────────────────────────── CONTROL clock_sync_ack
  │── CONTROL ping × N durante 1 s ───────────────▶│      ← fase PING/JITTER (20 pings @ 50 ms)
  │◀───────────────────────────── CONTROL pong
  │── CONTROL test_start (down, 4 streams) ───────▶│
  │◀────────── CONTROL test_start_ack (data_ports=[p1..p4])
  │── TCP connect × 4 a data_ports ───────────────▶│      ← fase THROUGHPUT
  │◀────────── DATA_BINARY (ráfaga continua)
  │         … durante duration_s …
  │◀────────── CONTROL test_tick cada 200 ms
  │── CONTROL test_end ───────────────────────────▶│
  │◀───────────────────────────── CONTROL result
  │── CONTROL goodbye ────────────────────────────▶│
  │── TCP close ──────────────────────────────────▶│
```

Ventana de pérdida: mientras corre throughput, el cliente intercala `ping` cada 100 ms por la conexión de control.

### 6.1 Flujo bidireccional secuencial (LSP/1.1)

Cuando el cliente anuncia `caps.bidir_sequential` y `caps.bidir_sequential` también está en el subconjunto común, puede enviar `test_start` con `direction=bidir` y `bidir_mode=sequential`:

```
cliente                                         host
  │── CONTROL test_start (bidir, sequential, gap_s=0.5) ─▶│
  │◀────────── CONTROL test_start_ack
  │── TCP connect × N a data_ports ─────────────────▶│
  │── DATA_BINARY durante duration_s ───────────────▶│      ← fase UP (cliente→host)
  │                     … pausa gap_s …            │      ← gap
  │◀──────────────────── DATA_BINARY durante duration_s   ← fase DOWN (host→cliente)
  │── CONTROL test_end ─────────────────────────────▶│
  │◀───── CONTROL result (throughput_up + throughput_down)
```

Ambos lados usan los **mismos** sockets de datos para las dos fases; no se abren nuevos puertos. La pausa `gap_s` sirve para drenar colas del kernel y aislar las mediciones. Cada peer decide independientemente cuándo transitar entre fases usando su reloj monotónico — no hay sincronización explícita más allá de que ambos ya conocen `duration_s` y `gap_s` desde el `test_start`.

---

## 7. Seguridad

LSP/1 asume LAN de confianza moderada. Para redes públicas o compartidas:

- **Pairing obligatorio** en la primera conexión entre dos peers. El host genera un código numérico de 6 dígitos y lo muestra al usuario; el cliente lo introduce. Sin pairing válido, el host rechaza `test_start`.
  - Persistencia recomendada: almacén de secretos del sistema (Keychain en Apple, DPAPI / Credential Manager en Windows, Keystore en Android).
  - Rate limit sugerido: 5 intentos/minuto por IP.
- **TLS opcional** (`caps.tls`): certificado autofirmado intercambiado en el pairing. Fuera del alcance de v1.0 de la app de referencia.
- El protocolo **no** autentica a los peers más allá del pairing. No uses LSP/1 para medir enlaces sobre redes donde otros usuarios tengan acceso al mismo segmento sin haber emparejado.

---

## 8. Códigos de error (campo `code` en frame `ERROR` y mensaje `error`)

| Código | Significado |
|---|---|
| `1001` | Frame excede el límite máximo (16 MiB). |
| `1002` | Frame con longitud 0. |
| `1003` | Tipo de frame desconocido. |
| `1004` | JSON de control malformado. |
| `1005` | Tipo de mensaje de control desconocido. |
| `2000` | Versión mayor del protocolo incompatible. |
| `3001` | Pairing code inválido. |
| `3002` | Pairing requerido y no suministrado. |
| `3003` | Transport UDP solicitado pero `udp_data_plane` no negociado (LSP/1.2). |
| `3004` | `payload_size` fuera del rango permitido para el transport (LSP/1.2). |
| `3005` | `target_bitrate_bps` fuera del rango permitido o no alcanzable (LSP/1.2, informativo). |
| `9000+` | Reservados para implementaciones. |

---

## 9. Versionado

SemVer:

- **Mayor** distinta → rechazo con `code=2000`.
- **Menor** distinta → operar con el subconjunto común declarado en `caps`; campos JSON desconocidos se ignoran.

Versión de referencia actual: **1.2**. Cambios 1.1 → 1.2 (ninguno rompe compatibilidad):

- §5: nuevo bit `0x0100 udp_data_plane`.
- §3.1: `test_start` acepta campos opcionales `transport` (`tcp`|`udp`, default `tcp`) y `target_bitrate_bps` (uint64, `0` = sin límite, sólo aplicable cuando `transport=udp`).
- §3.1: `test_start_ack` reinterpreta `data_ports` como puertos **UDP** cuando el test negociado usa UDP. El host DEBE rechazar `transport=udp` si alguno de los peers no anunció `udp_data_plane` en `caps`, devolviendo `error` con `code=3003`.
- §4: `result` admite campos opcionales `udp` (ver §12.4) cuando `transport=udp`. `loss_pct` y `jitter_ms` top-level se rellenan con los valores del data-plane UDP (reemplazan la medición por ping de control).
- §8: nuevos códigos `3003` (UDP no soportado), `3004` (payload fuera de rango), `3005` (bitrate no respetado).
- §12: nueva sección completa con el data-plane UDP.

Cambios 1.0 → 1.1 (ninguno rompe compatibilidad):

- §2.1: `DATA_BINARY_ECHO` (0x04) pasa de reservado a **implementado** (RTT bajo carga). Payload convención: primeros 8 B timestamp monotónico LE, resto padding.
- §3.1: `test_start` acepta campos opcionales `bidir_mode` (`simultaneous`|`sequential`) y `gap_s` (segundos de pausa entre direcciones cuando el modo es secuencial).
- §4: `result` admite campos opcionales `throughput_up`, `throughput_down`, `rtt_under_load` (objeto con `samples`, `min_ms`, `median_ms`, `p95_ms`, `max_ms`, `stdev_ms`, `baseline_median_ms`, `spikes_count`), `intervals` (objeto con `window_ms`, array `samples` de `{start_s,end_s,bps}` y `stats` con `median_bps`/`p95_bps`/`stdev_bps`/`min_bps`/`max_bps`), `payload_bytes`.
- §5: nuevos bits de capacidad `0x0040 bidir_sequential` y `0x0080 interval_reporting`.

Todos los campos nuevos son opcionales; un peer 1.0 los ignora sin romper la conexión.

---

## 10. Nota de portabilidad

- El framing está diseñado para ser trivial de implementar en C, Rust, Go, Kotlin, Swift, C# y cualquier lenguaje moderno.
- El control JSON se puede parsear con cualquier librería estándar. No se usan extensiones no estándar.
- Para Windows, la librería recomendada para mDNS es `Bonjour SDK for Windows` o `mdns-responder` portado; no es obligatorio: cualquier implementación compatible RFC 6762/6763 vale.
- Para Android, usar `NsdManager` + `android.net.nsd`.

---

## 11. Conformidad

Una implementación es **conforme a LSP/1.x** (baseline 1.0, vigente 1.1) si:

1. Respeta el framing §2 sin desviaciones.
2. Implementa como mínimo los mensajes `hello`, `hello_ack`, `ping`, `pong`, `test_start`, `test_start_ack`, `test_tick`, `test_end`, `result`, `goodbye`, `error`.
3. Declara `caps` correctamente y respeta la intersección.
4. Rechaza frames inválidos con los códigos de §8.
5. Publica el servicio `_landspeed._tcp` con los TXT records de §1 (si actúa como host).

`pairing`, `clock_sync`, `tls`, `data_echo`, `udp_data_plane` son **opcionales** pero fuertemente recomendados para uso real fuera del laboratorio.

---

## 12. Data-plane UDP (LSP/1.2)

El data-plane UDP es una alternativa al data-plane TCP para la fase de throughput. El **control sigue siempre sobre TCP** con el framing de §2. UDP sólo reemplaza a los `DATA_BINARY` del plano de datos.

Motivación:

- Medir el techo bruto del enlace sin que el control de congestión TCP limite.
- Reportar **pérdida real** y **jitter inter-paquete** en lugar de inferirlos desde métricas de ping.
- Caracterizar enlaces Wi-Fi degradados, donde TCP oculta la pérdida mediante retransmisiones.

### 12.1 Negociación

1. Ambos peers DEBEN anunciar `udp_data_plane` (`0x0100`) en `caps`.
2. El cliente envía `test_start` con `transport: "udp"` y, opcionalmente, `target_bitrate_bps`.
3. Si el host no negoció `udp_data_plane`, responde `error` con `code=3003` y no abre puertos UDP.
4. Si acepta, responde `test_start_ack` con `data_ports` = puertos **UDP** efímeros (uno por stream).
5. El cliente abre un socket UDP local por puerto destino y envía un paquete `HELLO_UDP` (§12.2) a cada uno para que el host aprenda la 5-tupla de retorno (pinhole, simetría direccional).

### 12.2 Formato del paquete UDP

Cada datagrama es un paquete autónomo (sin framing LSP — no tiene sentido sobre UDP):

```
+--------+--------+--------+----------------+
| seq    | ts_ns  | stream | padding        |
| 8 B LE | 8 B LE | 2 B LE | N bytes random |
+--------+--------+--------+----------------+
```

- `seq` (uint64, little-endian): número de secuencia dentro del stream. Empieza en `1` para paquetes de datos. `seq = 0` identifica un paquete `HELLO_UDP` (payload puede ser todo cero).
- `ts_ns` (uint64, little-endian): timestamp monotónico del remitente al momento del envío. Permite calcular one-way delay si ambos peers han hecho `clock_sync`.
- `stream` (uint16, little-endian): id del stream (`0..streams-1`). Permite a un receptor multiplexar sobre un único socket si así lo prefiere (no requerido por la spec).
- `padding`: bytes pseudoaleatorios hasta completar `payload_size` bytes totales. El contenido no importa para la medición.

**Tamaño:** `payload_size` mínimo 64 B, máximo 65_507 B (límite teórico UDP/IPv4). Default recomendado: **1200 B** para caber en la MTU típica de 1500 sin fragmentación IP. Valores fuera de rango se rechazan con `code=3004`.

### 12.3 Control de ritmo (`target_bitrate_bps`)

- `0` o campo ausente: el emisor envía lo más rápido posible (útil para "encontrar el techo"), equivalente a `iperf3 -u -b 0`.
- Valor positivo: el emisor aplica un token bucket simple con ritmo constante. La tolerancia permitida es ±5 % sobre la ventana de medición. Si el emisor no logra mantener el ritmo, DEBE incluir `bitrate_miss_pct` en `result.udp` (§12.4).

### 12.4 Métricas en `result`

Cuando el test fue UDP, `result` incluye un objeto `udp`:

```json
"udp": {
  "packets_sent": 824123,
  "packets_received": 824089,
  "packets_lost": 34,
  "loss_pct": 0.004,
  "reorder_count": 12,
  "reorder_pct": 0.0015,
  "duplicate_count": 0,
  "jitter_ms": 0.21,
  "owd_ms": { "min": 0.31, "median": 0.42, "p95": 0.71, "max": 1.42 },
  "target_bitrate_bps": 100000000,
  "bitrate_miss_pct": 0.0
}
```

- `packets_*`: contadores crudos. `packets_sent` lo reporta el emisor, `packets_received` el receptor; el peer que construye el `result` agrega ambos (se intercambian por `test_tick` / `test_end`).
- `loss_pct`: `(packets_sent - packets_received) / packets_sent × 100`. Se asume que paquetes con `seq` no vistos están perdidos (no retransmitidos — no hay retransmisión).
- `reorder_count`: cantidad de paquetes recibidos con `seq` menor al máximo `seq` observado.
- `duplicate_count`: paquetes con `seq` ya visto. No cuentan como recibidos.
- `jitter_ms`: estimado con el algoritmo RFC 3550 (decay EMA de `|(D_i) - (D_{i-1})|`, con `D_i = (recv_ts_i - send_ts_i)`).
- `owd_ms` (one-way delay): sólo presente si ambos peers tienen `clock_sync` válido. Se calcula `recv_mono - send_mono - offset` (offset viene del handshake de reloj). Sin clock sync, se omite.
- `bitrate_miss_pct`: porcentaje del intervalo útil en el que el emisor no alcanzó el `target_bitrate_bps` (con tolerancia ±5 %). `0` si siempre alcanzó el ritmo.

Las métricas top-level `jitter_ms` y `loss_pct` del `result` toman su valor de `udp.jitter_ms` y `udp.loss_pct` cuando el test fue UDP (reemplazando la medición vía pings). `rtt_under_load` se **omite** en tests UDP (no hay echo frames; la medición RTT bajo carga requiere TCP).

### 12.5 Flujo canónico UDP

```
cliente                                          host
  │── CONTROL test_start (udp, streams=4, ...) ───▶│
  │◀───── CONTROL test_start_ack (data_ports=UDP[4])
  │── UDP HELLO_UDP (seq=0) × 4 ──────────────────▶│   ← pinhole / 5-tupla
  │◀──────────── UDP DATA (seq=1..N) × streams         ← fase THROUGHPUT (down)
  │        … durante duration_s …
  │◀──────────── CONTROL test_tick cada 200 ms         ← sobre el canal TCP de control
  │── CONTROL test_end ──────────────────────────▶│
  │── CONTROL udp_stats (packets_sent por stream) ▶│  ← emisor reporta contadores
  │◀───────────────────────── CONTROL result (udp:{...})
```

Para `direction=up`, el cliente es el emisor; el host es quien reporta los contadores de recepción a través de `test_tick` / `result`. El cliente envía un mensaje de control `udp_stats` (nuevo en LSP/1.2, formato `{t:"udp_stats", id, per_stream:[{stream, packets_sent, bytes_sent}]}`) tras `test_end` para que el host pueda computar pérdida. En `down` el intercambio es simétrico.

### 12.6 Bidireccional UDP

Tests `bidir` sobre UDP son permitidos y siguen la misma semántica que en TCP (`simultaneous` / `sequential`). En modo simultáneo cada stream transporta un sentido (el stream `i` es down si `i` es par, up si impar — convención simple). En modo secuencial se alternan las fases como en §6.1. El `result` emite `throughput_up` / `throughput_down` y los contadores UDP por dirección bajo `udp.up` y `udp.down` en lugar de top-level.

### 12.7 Límites y buenas prácticas

- Saturar un enlace con UDP sin control de ritmo PUEDE dejar al router en estado inestable. La implementación de referencia avisa en el CLI cuando `target_bitrate_bps=0` y se ejecuta contra un peer que no está en el mismo segmento L2 directo.
- El receptor DEBE dimensionar el buffer de socket (`SO_RCVBUF`) a al menos 4 MiB para evitar pérdida por saturación del socket kernel (causa habitual de loss "falso" reportado por iperf3).
- El emisor debería usar `SO_SNDBUF` ≥ 2 MiB por la misma razón.
- `payload_size` cerca del MTU (1200 B en redes típicas) evita fragmentación IP, que introduce loss binaria artificial.


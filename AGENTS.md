# Gard Agent Card

Ruta canonica:

```text
/Users/marcorojasbelmar/Developer/patagua/50-network-tools/landspeed/gard
```

- Familia/prioridad: LandSpeed P1.
- Estado agentes: `paused`.
- Deploy: `unknown`.
- Control plane: `/Users/marcorojasbelmar/Developer/patagua/00-control-plane/patagua.dev`.
- Permitido: lectura e inspeccion dirigida.
- Hard stops: no builds, tests, installs, release/network scripts, commits ni
  pushes sin instruccion explicita.
- Invariantes locales: renombrado localmente desde `garc`; el remote sigue
  `gard-net/gard`.
- Lanes: Codex ejecuta, Claude audita/planifica. Son blandos: si el presupuesto de
  tokens del momento lo hace mas eficiente, cualquiera toma el carril del otro,
  respetando los hard stops y las aprobaciones de Marco.
- Focus: compatibilidad de protocolo/API y riesgo de release.

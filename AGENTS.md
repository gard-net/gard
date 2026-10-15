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
- Release (método vigente desde v0.3.0): bump `Directory.Build.props` +
  CHANGELOG → tag `vX.Y.Z` (release.yml deja un draft) → publicar el draft →
  `packaging/homebrew/publish-tap.sh vX.Y.Z` para actualizar el tap
  `gard-net/homebrew-gard` con las credenciales locales (no hay secret
  `HOMEBREW_TAP_TOKEN`). Detalle en `packaging/README.md`.
- Tooling local (hp-ia): el `gh` del PATH es un shim de mise que se cuelga;
  usar `GH=~/.local/share/mise/installs/gh/<ver>/gh_<ver>_linux_amd64/bin/gh`.

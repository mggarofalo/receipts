# Color-Vision-Deficiency Audit

Periodic check that no critical UI signal relies on color alone. WCAG
1.4.1 (Use of Color) and Wong palette are the bars to clear. Runs on
demand via `npm run audit:cvd` from `src/client/` — the script captures
PNGs of each surface under three CVD simulations and writes them to
`docs/a11y/cvd-screenshots/`.

## Simulated CVDs

| Type | Prevalence (males) | What's affected |
|------|---|---|
| **Deuteranopia** | ~6% | Red-green: red appears tan/khaki, green appears beige |
| **Protanopia** | ~1% | Red-green: similar to deuteranopia but red is darker |
| **Tritanopia** | <0.01% | Blue-yellow: blue appears green, yellow appears pink |

Simulation runs through Chromium's `Emulation.setEmulatedVisionDeficiency`
CDP command (Playwright spec at
`src/client/tests/visual/cvd-audit.spec.ts`).

## Audit scope — current

Public (unauthenticated) surfaces only on this pass. Authenticated
surfaces with charts/badges (Dashboard widgets, Reports, YnabChip,
receipt totals) need deterministic API fixtures before they can be
audited reproducibly — fixture scaffolding is in
`src/client/tests/visual/fixtures/api-mocks.ts`.

| Surface | Audited | Findings |
|---|---|---|
| `/login` | ✅ | None — interface is high-contrast achromatic + accent blue. The accent is distinguishable under all three CVDs (no red/green confusion since it isn't red or green). |
| `/this-route-does-not-exist` (404) | ✅ | None — same achromatic palette, no color-coded signal. |
| Dashboard widgets | ⏸ pending fixtures | Recharts series defaults to red/green for positive/negative deltas; high deuteranopia/protanopia risk. Slate for re-audit after fixtures land. |
| Reports charts | ⏸ pending fixtures | Same as Dashboard. |
| YnabChip status dots | ⏸ pending fixtures | Three statuses (synced/failed/pending) use `--pos`/`--neg`/`--warn` tokens; the chip carries a text label as well as the dot, so color is reinforced — but worth a screenshot pass to confirm contrast holds. |
| Receipts list bulk-actions toolbar | ⏸ pending fixtures | Action buttons differentiate by icon + label, not color alone. Low risk. |

## How to re-run

```bash
cd src/client
npm run audit:cvd
```

Output PNGs land in `docs/a11y/cvd-screenshots/<surface>-<cvd>.png`.
Inspect by eye; failures look like indistinguishable color blocks
where the rendered page uses red/green/yellow to convey distinct meaning.

## Acceptance gates

- Every charted surface must be screenshot-able under all three CVDs.
- For each surface, the team eyeballs the screenshot and either:
  - ✅ Passes: information is still distinguishable.
  - ❌ Fails: opens a follow-up issue. Common fixes:
    - Add a non-color affordance (icon, label, pattern).
    - Swap to the Wong CVD-safe palette
      (https://www.nature.com/articles/nmeth.1618).

## Known limitations

- Chromium CDP simulation is a post-process pixel filter; it doesn't
  perfectly model real CVD perception (which involves photoreceptor
  signal mixing in the eye). Use it as a strong signal, not as
  guaranteed conformance.
- Anomalous trichromacy (mild forms; far more common than dichromacy)
  is not simulated. Real-world users with mild deuteranomaly may still
  distinguish reds and greens that the simulation flattens.
- The audit doesn't cover hover/focus colors or transient UI states
  (toasts, modals). Add those as fixtures mature.

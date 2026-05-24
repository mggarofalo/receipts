# Visual Regression

Playwright-based pixel-diff harness for the React client. Catches subtle
padding / contrast / layout regressions that unit tests miss. Configured
in `src/client/playwright.config.ts`.

## Status

**Non-blocking on initial rollout.** Baselines are Windows-generated and
CI runs on Ubuntu, so `continue-on-error: true` is set on the
`visual-regression` job. Flip to blocking once Linux-generated baselines
are committed (see "Bootstrapping Linux baselines" below).

## Running locally

From `src/client/`:

```bash
# Run the suite against current baselines
npm run test:visual

# Regenerate baselines after intentional UI changes
npm run test:visual:update
```

The dev server (`npm run dev`) is started automatically by Playwright's
`webServer` block. If port 5173 is already in use, kill the existing
process first.

## Where baselines live

```
src/client/tests/visual/baselines/<spec-file>/<project>-<test-name>.png
```

Example: `src/client/tests/visual/baselines/public-pages.spec.ts/desktop-chromium-login.png`

PNGs are committed to git (plain blobs, no LFS). Migrate to LFS if the
repo grows uncomfortable; tracked in RECEIPTS-746-class follow-up issues.

## Cross-platform pixel diffs

Font rendering differs between macOS, Windows, and Linux. The harness
sets `maxDiffPixelRatio: 0.02` (2% of pixels can differ) to absorb
subpixel-level antialiasing noise without missing genuine layout
regressions. Tighten this once everyone runs against
Linux-generated baselines.

## Bootstrapping Linux baselines

1. Open a PR that includes only the harness changes (no UI changes).
2. CI runs `npm run test:visual`. If the Windows-generated baselines
   don't match within 2%, the job will fail but be non-blocking.
3. Download the `playwright-report` artifact from the failed run.
4. Replace the Windows baselines under
   `src/client/tests/visual/baselines/` with the Linux versions from
   the artifact's `test-results/<test>/<viewport>-actual.png` files.
5. Commit and push. The CI job should now pass.
6. Flip `continue-on-error` to `false` in `.github/workflows/github-ci.yml`
   on a follow-up PR.

## Adding a new surface

1. Add a new spec under `src/client/tests/visual/`.
2. If the surface needs auth or API data, import from
   `tests/visual/fixtures/api-mocks.ts` — that helper stubs every
   endpoint the app touches at boot and pins the clock to
   `2026-05-24T12:00:00Z` so relative-time strings stay stable.
3. Run `npm run test:visual:update` to generate baselines.
4. Commit the new baselines alongside the spec.

Example for a public surface (no auth, no API needed):

```ts
import { test, expect } from "@playwright/test";

test("login", async ({ page }) => {
  await page.goto("/login");
  await expect(page.getByRole("heading")).toBeVisible();
  await expect(page).toHaveScreenshot("login.png", { fullPage: true });
});
```

Example for an authed surface with mocked API:

```ts
import { test, expect } from "@playwright/test";
import { installApiMocks, signInAsFixtureUser } from "./fixtures/api-mocks";

test("dashboard", async ({ page }) => {
  await installApiMocks(page);
  await signInAsFixtureUser(page);
  await page.goto("/");
  await expect(page.getByText(/dashboard/i)).toBeVisible();
  await expect(page).toHaveScreenshot("dashboard.png", { fullPage: true });
});
```

## Bypassing the gate intentionally

Add the `visual-regression-allowed` label to the PR. Per the
established convention (see AGENTS.md), the job's `if:` reads labels
from the triggering event, so after labeling you must re-trigger CI
(close+reopen the PR, or push) for the gate to skip.

## Tooling decision

Chose Playwright over Chromatic:

- **Free, self-hosted.** Chromatic charges per snapshot beyond a free
  tier; Playwright stores baselines in git at no per-snapshot cost.
- **No third-party dependency.** Baselines are repo content, not
  managed in an external service.
- **Already-in-stack browser primitive.** Future E2E/keyboard tests
  (RECEIPTS-745) reuse the same Playwright install.

Trade-off: slightly slower local runs and we manage baseline files
manually. Path-to-upgrade to LFS is documented above.

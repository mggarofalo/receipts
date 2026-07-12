import { test, type Page } from "@playwright/test";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

// ESM workaround: __dirname/__filename aren't defined.
const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

// Color-vision-deficiency screenshot harness. Runs on demand
// (`npm run audit:cvd`) and dumps PNGs under docs/a11y/cvd-screenshots/
// so a reviewer can eyeball each surface under three CVDs:
//
//   - deuteranopia (red-green; ~6% of males)
//   - protanopia  (red-green, distinct from deuteranopia; ~1%)
//   - tritanopia  (blue-yellow; rare, <0.01%)
//
// We capture public surfaces only for now — authenticated surfaces
// require deterministic API fixtures (RECEIPTS-744 fixture work).
// Expand the surface list as fixtures mature.
//
// Methodology + findings are tracked in docs/a11y/color-blind-audit.md.

type Cvd = "deuteranopia" | "protanopia" | "tritanopia";

const CVDS: Cvd[] = ["deuteranopia", "protanopia", "tritanopia"];

interface Surface {
  url: string;
  name: string;
}

const SURFACES: Surface[] = [
  { url: "/login", name: "login" },
  { url: "/this-route-does-not-exist", name: "not-found" },
];

const OUTPUT_DIR = path.resolve(
  __dirname,
  "..",
  "..",
  "..",
  "..",
  "docs",
  "a11y",
  "cvd-screenshots",
);

async function applyCvd(page: Page, cvd: Cvd): Promise<void> {
  const client = await page.context().newCDPSession(page);
  await client.send("Emulation.setEmulatedVisionDeficiency", { type: cvd });
}

test.describe("Color-vision-deficiency audit", () => {
  test.beforeAll(() => {
    fs.mkdirSync(OUTPUT_DIR, { recursive: true });
  });

  // Only run on the desktop project — CVD simulation results don't
  // change at smaller viewports, and duplicating doubles the disk noise.
  test.skip(
    ({ browserName }) => browserName !== "chromium",
    "CDP setEmulatedVisionDeficiency is Chromium-only",
  );

  for (const surface of SURFACES) {
    for (const cvd of CVDS) {
      test(`${surface.name} under ${cvd}`, async ({ page }) => {
        await page.goto(surface.url);
        // Wait for paint settle; CVDs are post-process on the rendered
        // pixels, so we need stable content first.
        await page.waitForLoadState("networkidle");
        await applyCvd(page, cvd);
        const outPath = path.join(OUTPUT_DIR, `${surface.name}-${cvd}.png`);
        await page.screenshot({ path: outPath, fullPage: true });
      });
    }
  }
});

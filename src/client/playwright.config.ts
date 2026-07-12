import { defineConfig, devices } from "@playwright/test";

// Visual-regression harness (RECEIPTS-744). The baseline strategy is
// network-mocked: every test stubs /api/** with deterministic fixtures so
// snapshots don't depend on a live backend, a particular DB seed, or wall
// clock time. Baselines live in tests/visual/baselines/, committed to git
// in plain PNG form (migrate to LFS if the repo gets noisy).
//
// Two viewports — desktop (1280x720) + mobile (375x812) — cover the layout
// breakpoints (sidebar collapse at 900px, mobile tabbar swap-in).
export default defineConfig({
  testDir: "./tests/visual",
  // Each test should write its own baseline file relative to itself.
  snapshotPathTemplate: "{testDir}/baselines/{testFilePath}/{projectName}-{arg}{ext}",
  // Snapshots tolerate cross-platform font-rendering jitter (Windows vs
  // Linux/CI antialiasing differs slightly). The 2% threshold catches
  // genuine layout regressions while letting subpixel-level font
  // hinting differences pass — adjust down once CI-generated baselines
  // are committed and the dev/CI rendering surface matches.
  expect: {
    toHaveScreenshot: {
      maxDiffPixelRatio: 0.02,
      animations: "disabled",
      caret: "hide",
    },
  },
  // Local dev: Vite at default port. CI gets the same via the webServer
  // block — Playwright boots the dev server on demand.
  use: {
    baseURL: "http://localhost:5173",
    viewport: { width: 1280, height: 720 },
    // Faster timeouts: tests should be deterministic so they shouldn't
    // wait long for anything.
    actionTimeout: 5_000,
    navigationTimeout: 10_000,
  },
  projects: [
    {
      name: "desktop-chromium",
      use: { ...devices["Desktop Chrome"], viewport: { width: 1280, height: 720 } },
    },
    {
      name: "mobile-chromium",
      use: { ...devices["Desktop Chrome"], viewport: { width: 375, height: 812 } },
    },
  ],
  webServer: {
    command: "npm run dev",
    url: "http://localhost:5173",
    reuseExistingServer: !process.env.CI,
    timeout: 60_000,
  },
  // Single worker keeps the snapshot output stable; parallel runs across
  // multiple browser contexts have been observed to introduce timing
  // differences in animations even with animations: "disabled".
  workers: 1,
  reporter: process.env.CI ? [["github"], ["html", { open: "never" }]] : [["list"]],
  // Tests should not retry — a flaky VR test is a real bug.
  retries: 0,
});

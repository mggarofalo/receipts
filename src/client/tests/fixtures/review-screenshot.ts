import type { Page, PageScreenshotOptions, TestInfo } from "@playwright/test";

/**
 * Opt-in evidence screenshots for manual UI review.
 *
 * Behavioural specs assert on behaviour, not pixels, so a routine run takes no
 * screenshots: they cost seconds per full-page capture and nothing reads them.
 * Set E2E_REVIEW_SCREENSHOTS=1 to capture them into the test's own output
 * directory under test-results/ (gitignored), e.g.
 *
 *   E2E_REVIEW_SCREENSHOTS=1 npm run test:e2e -- category-navigation
 *
 * `settleMs` waits before the capture, for JS-driven animations (Recharts
 * entrance) that `animations: "disabled"` cannot finish. It is only paid when
 * capturing — never use it to wait for application state.
 */
export async function reviewScreenshot(
  page: Page,
  testInfo: TestInfo,
  name: string,
  { settleMs, ...options }: Omit<PageScreenshotOptions, "path"> & { settleMs?: number } = {},
): Promise<void> {
  if (!process.env.E2E_REVIEW_SCREENSHOTS) return;
  if (settleMs) await page.waitForTimeout(settleMs);
  await page.screenshot({ fullPage: true, ...options, path: testInfo.outputPath(name) });
}

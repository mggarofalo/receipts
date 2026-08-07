import { test, expect, type Page } from "@playwright/test";
import { ADMIN_FIXTURE_USER, installApiMocks, signInAs } from "../fixtures/api-mocks";

// Structural guard: a page must never scroll horizontally, and a table must never be so wide
// that it forces the page to (RECEIPTS-880).
//
// The complaint this comes from is about the Normalized Descriptions page, but the failure mode
// is general. Wide content — a table with five columns, one of which holds four buttons — is
// fine on a desktop monitor and catastrophic at 375px, and the two ways it goes wrong pull in
// opposite directions:
//
//   1. The page scrolls sideways. Every fixed element (sidebar, headers) detaches from the
//      content as you scroll, and on touch it competes with vertical scrolling.
//   2. The table is squeezed until its cells overlap or its buttons stack into columns of one
//      character. That is the RECEIPTS-880 complaint at the other end of the sweep.
//
// So the invariants are: the document never exceeds the viewport width, and any element that
// legitimately needs more room scrolls *inside its own box* rather than pushing the page.
//
// A baseline screenshot would not catch either: it is taken at one width, and the widths where
// this breaks are the ones nobody screenshots. Hence a sweep with no baselines to regenerate.
//
// Fix the layout, not this test: give the wide container `overflow-x: auto` so it scrolls
// itself, or let its content wrap.

const WIDTHS = [375, 414, 768, 1024, 1280, 1920, 2560];

const SURFACES: { path: string; ready: string; tab?: string }[] = [
  // The page the issue is about. Both tabs, because the Registry table and the Review Queue
  // table have different column counts and the Registry was the worse of the two.
  { path: "/admin/normalized-descriptions", ready: "Normalized Descriptions" },
  { path: "/admin/normalized-descriptions", ready: "Normalized Descriptions", tab: "Registry" },
];

interface Probe {
  /** Wider than the viewport by this many px; 0 or less is fine. */
  documentOverflow: number;
  /** Elements whose own box spills past the viewport's right edge. */
  spillingElements: string[];
}

async function probe(page: Page, viewportWidth: number): Promise<Probe> {
  return page.evaluate((width) => {
    const doc = document.documentElement;
    // scrollWidth rounds up, and sub-pixel layout routinely lands a pixel over. A 1px tolerance
    // keeps the test about real overflow rather than rounding.
    const documentOverflow = Math.max(0, doc.scrollWidth - width - 1);

    // An element inside a horizontally-scrolling box is allowed to be wider than the viewport —
    // that is what the box is for. Only its own box matters, so walk up and skip anything with a
    // scrolling ancestor; otherwise every cell of a scrollable table reports as an offender and
    // buries the element that is actually widening the page.
    const insideScroller = (el: Element): boolean => {
      for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
        const overflowX = getComputedStyle(p).overflowX;
        if (overflowX === "auto" || overflowX === "scroll") return true;
      }
      return false;
    };

    const spilling: string[] = [];
    for (const el of Array.from(document.querySelectorAll("body *"))) {
      const style = getComputedStyle(el);
      if (style.display === "none" || style.visibility === "hidden") continue;
      // An element that scrolls itself is doing the right thing — its content may exceed its
      // box, but the page is unaffected.
      if (style.overflowX === "auto" || style.overflowX === "scroll") continue;
      if (insideScroller(el)) continue;
      // Portalled overlays (tooltips, dialogs) position themselves and are not page layout.
      if (style.position === "fixed" || style.position === "absolute") continue;

      const rect = el.getBoundingClientRect();
      if (rect.width === 0 || rect.height === 0) continue;
      if (rect.right > width + 1) {
        const tag = el.tagName.toLowerCase();
        const cls = (el.getAttribute("class") ?? "").slice(0, 60);
        spilling.push(`${tag}.${cls} right=${Math.round(rect.right)}`);
      }
    }

    // One entry per offending element is unreadable on a big page; the outermost few are what
    // actually need fixing.
    return { documentOverflow, spillingElements: spilling.slice(0, 5) };
  }, viewportWidth);
}

test.describe("page never scrolls horizontally", () => {
  for (const surface of SURFACES) {
    const name = surface.tab
      ? `${surface.path} (${surface.tab} tab)`
      : surface.path;

    test(name, async ({ page }) => {
      // Admin, because the registry is gated behind RequireAdmin and would otherwise render
      // an empty shell that passes vacuously.
      await installApiMocks(page, { user: ADMIN_FIXTURE_USER });
      await signInAs(page, ADMIN_FIXTURE_USER);

      await page.setViewportSize({ width: WIDTHS[WIDTHS.length - 1], height: 900 });
      await page.goto(surface.path);
      await expect(page.getByRole("heading", { name: surface.ready })).toBeVisible();

      if (surface.tab) {
        await page.getByRole("tab", { name: surface.tab }).click();
      }

      // Proof the surface actually rendered content, so a page that failed to load cannot pass.
      await expect(page.locator("table tbody tr").first()).toBeVisible();

      const failures: string[] = [];
      for (const width of WIDTHS) {
        await page.setViewportSize({ width, height: 900 });
        const result = await probe(page, width);

        if (result.documentOverflow > 0) {
          failures.push(
            `${width}px: document is ${result.documentOverflow}px wider than the viewport` +
              (result.spillingElements.length
                ? ` — first offenders: ${result.spillingElements.join("; ")}`
                : ""),
          );
        }
      }

      expect(failures, failures.join("\n")).toHaveLength(0);
    });
  }
});

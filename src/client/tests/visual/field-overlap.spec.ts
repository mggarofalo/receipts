import { test, expect, type Page } from "@playwright/test";
import { installApiMocks, signInAsFixtureUser } from "../fixtures/api-mocks";

// Structural guard: form fields must never overlap, at any viewport width.
//
// The class of bug this exists to prevent: a field row sizes its items with
// the layout algorithm (flex/grid), but the control *inside* an item has a
// larger min-content width than the item — a Combobox with a long
// placeholder, say. Because a `display: grid` FormItem gives its children
// `min-width: auto`, the control refuses to shrink, renders at its intrinsic
// width, and spills out of its own box over the row gap onto the neighbouring
// field. Nothing about that is visible in a unit test, and a fixed-viewport
// screenshot only catches it at the width it was taken.
//
// So we assert two invariants across a continuous width sweep:
//
//   1. CONTAINMENT — a control never renders wider than its own FormItem.
//      This catches the root cause directly, even at widths where the
//      overflow happens not to land on a neighbour.
//   2. NO OVERLAP — no two form controls intersect.
//
// Deliberate layering is exempt from (2): DateInput composes a text input
// with an absolutely-positioned calendar button inside one `relative`
// wrapper, and the input reserves matching right padding. That is a button
// over an input, not two fields colliding, so anything positioned
// absolute/fixed is skipped.
//
// Fix the layout, not this test: rows should be `flex flex-wrap gap-*` with
// a per-field `min-w-[…]`, so fields keep their gap and their minimum usable
// width and drop to a new row instead of being crushed together.

const WIDTHS = [320, 360, 375, 414, 480, 560, 640, 768, 900, 1024, 1152, 1280, 1440, 1600];

// minControls: the smallest number of form controls the surface must expose
// at every width, so a page that fails to render can't pass vacuously.
// openDialog: click this button first, to reach forms that live in a modal.
const SURFACES: {
  path: string;
  ready: string;
  minControls: number;
  openDialog?: string;
}[] = [
  // The densest form in the app: header row, transactions row, and both
  // line-item rows — where every reported overlap actually occurred.
  { path: "/receipts/new", ready: "New Receipt", minControls: 8 },
  // ItemTemplateForm's paired comboboxes only exist inside the create dialog.
  { path: "/item-templates", ready: "Templates", minControls: 3, openDialog: "New template" },
  { path: "/receipts", ready: "Receipts", minControls: 1 },
];

interface Violation {
  kind: "containment" | "overlap";
  detail: string;
}

interface Probe {
  controlCount: number;
  violations: Violation[];
}

async function collectViolations(page: Page): Promise<Probe> {
  return page.evaluate(() => {
    const out: { kind: "containment" | "overlap"; detail: string }[] = [];

    const visible = (el: Element): boolean => {
      const cs = getComputedStyle(el);
      if (cs.display === "none" || cs.visibility === "hidden") return false;
      if (parseFloat(cs.opacity) === 0) return false;
      const r = el.getBoundingClientRect();
      return r.width > 1 && r.height > 1;
    };

    // A floating layer (popover, dialog, tooltip) is its own stacking context
    // and legitimately sits over page content. Rather than skip its fields
    // outright — dialogs hold real forms we want checked — we record which
    // layer each control belongs to and only compare controls within the same
    // one. `null` means the ordinary page flow.
    const floatingRoot = (el: Element): Element | null =>
      el.closest(
        '[data-slot="popover-content"],[data-radix-popper-content-wrapper],[role="dialog"],[data-slot="select-content"]',
      );

    const hidden = (el: Element): boolean => !!el.closest('[aria-hidden="true"]');

    // Deliberate layering: the element, or an ancestor up to its FormItem, is
    // taken out of normal flow on purpose.
    const layered = (el: Element): boolean => {
      let cur: Element | null = el;
      while (cur && cur !== document.body) {
        const pos = getComputedStyle(cur).position;
        if (pos === "absolute" || pos === "fixed") return true;
        if (cur.matches('[data-slot="form-item"]')) break;
        cur = cur.parentElement;
      }
      return false;
    };

    const describe = (el: Element): string => {
      const name =
        el.getAttribute("name") ||
        el.getAttribute("aria-label") ||
        (el.textContent || "").trim().slice(0, 24);
      return `${el.tagName.toLowerCase()}${name ? `(${name})` : ""}`;
    };

    // Every interactive control that behaves like a form field. Switches and
    // select triggers count: they sit in the same rows and collide the same way.
    const SELECTOR = [
      'input:not([type="hidden"])',
      "select",
      "textarea",
      '[role="combobox"]',
      '[role="switch"]',
      '[data-slot="input"]',
      '[data-slot="select-trigger"]',
    ].join(",");
    const controls = [...document.querySelectorAll(SELECTOR)].filter(
      (el) => visible(el) && !hidden(el),
    );

    // (1) Containment — control must fit inside its own FormItem.
    for (const c of controls) {
      const item = c.closest('[data-slot="form-item"]');
      if (!item || layered(c)) continue;
      const rc = c.getBoundingClientRect();
      const ri = item.getBoundingClientRect();
      const overshootRight = Math.round(rc.right - ri.right);
      const overshootLeft = Math.round(ri.left - rc.left);
      if (overshootRight > 1 || overshootLeft > 1) {
        out.push({
          kind: "containment",
          detail: `${describe(c)} overflows its FormItem by ${Math.max(overshootRight, overshootLeft)}px (control ${Math.round(rc.width)}px vs item ${Math.round(ri.width)}px)`,
        });
      }
    }

    // (2) No two controls may intersect.
    const flow = controls.filter((c) => !layered(c));
    for (let i = 0; i < flow.length; i++) {
      for (let j = i + 1; j < flow.length; j++) {
        const a = flow[i];
        const b = flow[j];
        if (a.contains(b) || b.contains(a)) continue;
        // Different stacking layers are allowed to sit over one another.
        if (floatingRoot(a) !== floatingRoot(b)) continue;
        const ra = a.getBoundingClientRect();
        const rb = b.getBoundingClientRect();
        const ox = Math.min(ra.right, rb.right) - Math.max(ra.left, rb.left);
        const oy = Math.min(ra.bottom, rb.bottom) - Math.max(ra.top, rb.top);
        if (ox > 1 && oy > 1) {
          out.push({
            kind: "overlap",
            detail: `${describe(a)} and ${describe(b)} overlap by ${Math.round(ox)}x${Math.round(oy)}px`,
          });
        }
      }
    }

    return { controlCount: controls.length, violations: out };
  });
}

// The sweep sets its own viewport for every assertion, so it runs identically
// under both the desktop and mobile projects. That duplication is cheap and
// keeps the spec free of project-name coupling.
test.describe("Form fields never overlap", () => {
  for (const surface of SURFACES) {
    test(`${surface.path} across ${WIDTHS.length} widths`, async ({ page }) => {
      // Catch-all first so it loses to the specific mocks registered after it
      // (Playwright gives precedence to the most recently added route).
      await page.route("**/api/**", (route) =>
        route.fulfill({
          status: 200,
          contentType: "application/json",
          body: JSON.stringify({ data: [], totalCount: 0 }),
        }),
      );
      await signInAsFixtureUser(page);
      await installApiMocks(page);

      await page.goto(surface.path);
      await expect(
        page.getByRole("heading", { name: new RegExp(surface.ready, "i") }).first(),
      ).toBeVisible();

      if (surface.openDialog) {
        await page.getByRole("button", { name: surface.openDialog }).first().click();
        await expect(page.getByRole("dialog")).toBeVisible();
      }

      const failures: string[] = [];
      let minControls = Number.POSITIVE_INFINITY;
      for (const width of WIDTHS) {
        await page.setViewportSize({ width, height: 900 });
        // Let flex/grid reflow and any ResizeObserver-driven layout settle.
        await page.waitForTimeout(120);
        const { controlCount, violations } = await collectViolations(page);
        minControls = Math.min(minControls, controlCount);
        for (const v of violations) failures.push(`@${width}px [${v.kind}] ${v.detail}`);
      }

      // Guard against a vacuous pass: if the surface renders no form controls
      // (auth bounce, mock gap, loading skeleton) the sweep above proves
      // nothing. Assert we actually inspected the fields we think we did.
      expect(
        minControls,
        `${surface.path} exposed too few form controls to be a meaningful check — the page probably didn't render.`,
      ).toBeGreaterThanOrEqual(surface.minControls);

      expect(failures, `Field layout violations on ${surface.path}:\n${failures.join("\n")}`).toEqual([]);
    });
  }
});

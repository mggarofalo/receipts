import { test, expect } from "@playwright/test";

// Keyboard-only navigation coverage for the public (unauthenticated)
// surfaces. Authenticated keyboard flows (Cmd+K palette, list shortcuts,
// bulk select) require deterministic API fixtures and will live in
// keyboard-authed.spec.ts once the fixture layer stabilizes —
// scaffolding is in tests/visual/fixtures/api-mocks.ts.
//
// What we assert here:
// - Every focusable element on /login is reachable via Tab
// - Enter submits the form from the password field
// - Esc and Tab don't crash or strand focus
// - Focus order matches DOM order (email -> password -> submit)
//
// Why this matters: WCAG 2.1.1 (Keyboard) requires that all
// functionality is operable through a keyboard. The login form is the
// gate to every other surface; if it traps focus or skips an input,
// nobody on assistive tech can sign in.

test.describe("Keyboard navigation — login", () => {
  test("Tab visits email -> password -> submit in DOM order", async ({ page }) => {
    await page.goto("/login");

    // Start with body focus so the first Tab lands on the first focusable.
    await page.locator("body").click({ position: { x: 0, y: 0 } });
    await page.keyboard.press("Tab");

    // Walk focus through the form. We assert via document.activeElement
    // attributes rather than aria roles because focused-element matching
    // through Playwright's role queries is fragile on shadcn primitives.
    const tagAndType = async () =>
      page.evaluate(() => {
        const el = document.activeElement as HTMLElement | null;
        return el
          ? { tag: el.tagName, type: (el as HTMLInputElement).type ?? "", name: el.getAttribute("name") ?? "" }
          : null;
      });

    // Tab order may include skip-link first; advance until we hit the email field.
    let safety = 0;
    while (safety++ < 10) {
      const focused = await tagAndType();
      if (focused?.tag === "INPUT" && (focused.type === "email" || focused.name === "email")) {
        break;
      }
      await page.keyboard.press("Tab");
    }
    expect(safety).toBeLessThan(10);

    // From email, Tab should reach a password input.
    await page.keyboard.press("Tab");
    const afterEmail = await tagAndType();
    expect(afterEmail?.type).toBe("password");

    // From password, Tab should land on a button (sign-in).
    await page.keyboard.press("Tab");
    const afterPassword = await tagAndType();
    expect(afterPassword?.tag === "BUTTON" || afterPassword?.type === "submit").toBeTruthy();
  });

  test("Enter submits the form from the password field", async ({ page }) => {
    await page.goto("/login");
    // PasswordInput from shadcn doesn't propagate the FormField id to a
    // native label, so getByLabel doesn't find it. Locate by name
    // attribute (set via react-hook-form's field spread).
    await page.locator('input[name="email"]').fill("vr@example.com");
    await page.locator('input[name="password"]').fill("not-a-real-password");
    // No API mock here; submit will fail, but we only care that Enter
    // triggers the submit handler.
    const requestStarted = page
      .waitForRequest(/\/api\/auth\/login/, { timeout: 3000 })
      .catch(() => null);
    await page.keyboard.press("Enter");
    const req = await requestStarted;
    expect(req).not.toBeNull();
  });

  test("Esc on the form doesn't strand focus", async ({ page }) => {
    await page.goto("/login");
    const email = page.getByLabel(/email/i);
    await email.focus();
    await page.keyboard.press("Escape");
    // Focus should still be a focusable element (not body/null) — Esc
    // shouldn't blow away the focus context.
    const stillFocusable = await page.evaluate(() => {
      const el = document.activeElement;
      return !!el && el !== document.body;
    });
    expect(stillFocusable).toBeTruthy();
  });
});

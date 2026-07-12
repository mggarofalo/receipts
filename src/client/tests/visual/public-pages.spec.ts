import { test, expect } from "@playwright/test";

// Baseline visual-regression coverage for surfaces that don't require auth.
// Network mocking is unnecessary here — the login form and 404 page render
// purely from client code with no API calls at boot.
//
// New surfaces that need auth + API stubs should land in a separate spec
// (e.g. authed-pages.spec.ts) that imports the fixtures helper from
// ./fixtures/api-mocks.ts.

test.describe("Public pages", () => {
  test("login page", async ({ page }) => {
    await page.goto("/login");
    // Wait for the form to be in the DOM so we never screenshot mid-mount.
    await expect(page.getByRole("heading", { name: /sign in|log in|receipts/i })).toBeVisible();
    await expect(page).toHaveScreenshot("login.png", { fullPage: true });
  });

  test("404 not-found page", async ({ page }) => {
    await page.goto("/this-route-definitely-does-not-exist");
    // NotFound's title is a styled <div>, not a semantic heading — wait
    // on the visible text instead.
    await expect(page.getByText(/this page left the counter/i)).toBeVisible();
    await expect(page).toHaveScreenshot("not-found.png", { fullPage: true });
  });
});

import { expect, test } from "@playwright/test";
import {
  ADMIN_FIXTURE_USER,
  FIXTURE_USER,
  installApiMocks,
  signInAs,
} from "../fixtures/api-mocks";

const artifact = "C:/Users/mggar/Source/receipts-017-artifacts/1059/ui-review";
const id = "10000000-0000-0000-0000-000000000059";
for (const width of [1280, 375]) {
  test(`owner sign-in lifecycle and explicit disconnect at ${width}px`, async ({
    page,
  }) => {
    await page.setViewportSize({ width, height: 900 });
    let connected = false;
    let pollState = "waiting";
    let starts = 0;
    let disconnects = 0;
    let cancels = 0;
    const writes: string[] = [];
    const waiting = () => ({
      id,
      state: "waiting",
      userCode: "SYNTHETIC-1059",
      verificationUrl: "https://auth.openai.com/codex/device",
      localExpiresAt: new Date(Date.now() + 60000).toISOString(),
    });
    await installApiMocks(page, {
      user: ADMIN_FIXTURE_USER,
      freezeClock: false,
    });
    // Disallow any provider traffic: this suite never follows the verification link.
    await page.route("https://auth.openai.com/**", (route) => route.abort());
    await page.route("**/api/normalization-ai/**", async (route) => {
      const path = new URL(route.request().url()).pathname;
      const respond = (json: unknown) => route.fulfill({ json });
      if (route.request().method() === "POST") {
        writes.push(path);
        if (path.endsWith("/disconnect")) {
          disconnects++;
          connected = false;
          return route.fulfill({ status: 204 });
        }
        if (path.endsWith("/cancel")) {
          cancels++;
          return respond({ ...waiting(), state: "cancelled" });
        }
        expect(route.request().postDataJSON()).toEqual({ ownerConsent: true });
        starts++;
        return respond(starts === 2 ? { id, state: "busy" } : waiting());
      }
      if (path.endsWith("/connection"))
        return respond({
          configured: true,
          isOwner: true,
          credentialsPresent: connected,
          busy: false,
          generation: id,
          models: ["synthetic-model"],
        });
      if (path.endsWith("/runs")) return respond([]);
      if (path.endsWith("/candidates")) return respond({ items: [], total: 0 });
      if (path.includes("/sign-in/")) {
        if (pollState === "success") connected = true;
        return respond({ ...waiting(), state: pollState });
      }
      throw new Error(`Unexpected route ${path}`);
    });
    await signInAs(page, ADMIN_FIXTURE_USER);
    await page.route("**/api/normalization-inbox/preferences", (route) =>
      route.fulfill({
        json: {
          isOwner: true,
          enabled: false,
          model: null,
          dailyLimit: 10,
          revision: id,
        },
      }),
    );
    await page.route("**/api/normalization-catalog/**", (route) =>
      route.fulfill({
        json: new URL(route.request().url()).pathname.endsWith("/coverage")
          ? {
              totalItems: 0,
              verifiedItems: 0,
              pendingItems: 0,
              pendingSources: 0,
            }
          : new URL(route.request().url()).pathname.endsWith("/operations")
            ? []
            : { items: [], total: 0 },
      }),
    );
    await page.goto("/admin/normalized-descriptions?tab=chatgpt");
    const consent = page.getByRole("checkbox", {
      name: /I own this ChatGPT account/,
    });
    const start = page.getByRole("button", {
      name: "Sign in with ChatGPT",
      exact: true,
    });
    await expect(start).toBeDisabled();
    expect(starts).toBe(0);
    await page.screenshot({
      path: `${artifact}/${width}-initial.png`,
      fullPage: true,
    });
    await consent.check();
    await start.click();
    await expect(
      page.getByText("SYNTHETIC-1059", { exact: true }),
    ).toBeVisible();
    const link = page.getByRole("link", { name: "Open OpenAI sign-in" });
    await expect(link).toHaveAttribute(
      "href",
      "https://auth.openai.com/codex/device",
    );
    await expect(link).toHaveAttribute("rel", "noopener noreferrer");
    await expect(link).toHaveAttribute("referrerpolicy", "no-referrer");
    expect(
      await page.evaluate(() =>
        JSON.stringify({ ...localStorage, ...sessionStorage }),
      ),
    ).not.toContain("SYNTHETIC-1059");
    await page.screenshot({
      path: `${artifact}/${width}-waiting.png`,
      fullPage: true,
    });
    await page.getByRole("button", { name: "Cancel sign-in" }).click();
    await expect(page.getByText(/Sign-in cancelled/)).toBeVisible();
    await expect(page.getByText("SYNTHETIC-1059", { exact: true })).toHaveCount(
      0,
    );
    await expect(consent).not.toBeChecked();
    await expect(start).toBeDisabled();
    expect(cancels).toBe(1);
    expect(disconnects).toBe(0);
    await consent.check();
    await start.click();
    await expect(page.getByText(/Another ChatGPT operation/)).toBeVisible();
    pollState = "success";
    await expect(
      page.getByText(/ChatGPT connected\. Select a model/),
    ).toBeVisible();
    await expect(
      page.getByRole("combobox", { name: "ChatGPT model" }),
    ).toBeVisible();
    await expect(page.getByText("SYNTHETIC-1059", { exact: true })).toHaveCount(
      0,
    );
    await expect(
      page.getByRole("button", { name: "Reconnect ChatGPT", exact: true }),
    ).toBeDisabled();
    await page.screenshot({
      path: `${artifact}/${width}-success.png`,
      fullPage: true,
    });
    const setup = page.getByRole("link", {
      name: "Set up post-save ChatGPT suggestions",
    });
    await expect(setup).toHaveAttribute(
      "href",
      "/admin/normalized-descriptions?tab=catalog",
    );
    await setup.click();
    await expect(page).toHaveURL(/tab=catalog/);
    await page
      .getByText("Post-save ChatGPT suggestions: off", { exact: true })
      .click();
    await expect(
      page.getByRole("checkbox", { name: "Enable post-save suggestions" }),
    ).not.toBeChecked();
    await expect(
      page.getByRole("combobox", { name: "Post-save model" }),
    ).toBeVisible();
    await page.screenshot({
      path: `${artifact}/${width}-assistance-destination.png`,
      fullPage: true,
    });
    await page.goto("/admin/normalized-descriptions?tab=chatgpt");
    await consent.check();
    await page
      .getByRole("button", { name: "Reconnect ChatGPT", exact: true })
      .click();
    await expect(
      page.getByText("SYNTHETIC-1059", { exact: true }),
    ).toBeVisible();
    expect(writes.at(-1)).toBe("/api/normalization-ai/connection/reconnect");
    await page.getByRole("button", { name: "Cancel sign-in" }).click();
    await expect(page.getByText(/Sign-in cancelled/)).toBeVisible();
    await expect(consent).not.toBeChecked();
    await page
      .getByRole("button", { name: "Disconnect ChatGPT", exact: true })
      .click();
    expect(disconnects).toBe(0);
    await page.getByRole("button", { name: "Keep connection" }).click();
    expect(disconnects).toBe(0);
    await page
      .getByRole("button", { name: "Disconnect ChatGPT", exact: true })
      .click();
    await page.getByRole("button", { name: "Confirm disconnect" }).click();
    await expect(
      page.getByText("ChatGPT disconnected.", { exact: true }),
    ).toBeVisible();
    expect(disconnects).toBe(1);
    expect(writes).toHaveLength(6);
    expect(
      await page.evaluate(
        () =>
          document.documentElement.scrollWidth >
          document.documentElement.clientWidth + 1,
      ),
    ).toBe(false);
    await page.screenshot({
      path: `${artifact}/${width}-disconnected.png`,
      fullPage: true,
    });
  });
}

test("non-owner admin and non-admin cannot initiate ceremony", async ({
  page,
}) => {
  for (const user of [ADMIN_FIXTURE_USER, FIXTURE_USER]) {
    let writes = 0;
    await installApiMocks(page, { user, freezeClock: false });
    await page.route("**/api/normalization-ai/**", (route) => {
      if (route.request().method() !== "GET") writes++;
      return route.fulfill({
        json: {
          configured: true,
          isOwner: false,
          credentialsPresent: false,
          busy: false,
          generation: id,
          models: [],
        },
      });
    });
    await signInAs(page, user);
    await page.goto("/admin/normalized-descriptions?tab=chatgpt");
    if (user === ADMIN_FIXTURE_USER)
      await expect(
        page.getByText(
          /This connection belongs to the designated administrator/,
        ),
      ).toBeVisible();
    else
      await expect(
        page.getByRole("heading", { name: "ChatGPT normalization" }),
      ).toHaveCount(0);
    await expect(
      page.getByRole("button", { name: "Sign in with ChatGPT" }),
    ).toHaveCount(0);
    expect(writes).toBe(0);
  }
});

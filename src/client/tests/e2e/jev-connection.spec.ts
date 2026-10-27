import { expect, test } from "@playwright/test";
import {
  ADMIN_FIXTURE_USER,
  FIXTURE_USER,
  installApiMocks,
  signInAs,
} from "../fixtures/api-mocks";

for (const width of [375, 1280]) {
  test(`category assistance environment setup (${width}px)`, async ({
    page,
  }) => {
    await page.setViewportSize({ width, height: 900 });
    await installApiMocks(page, {
      user: ADMIN_FIXTURE_USER,
      freezeClock: false,
    });
    await page.route("**/api/category-assistance/status", (route) =>
      route.fulfill({
        json: {
          capability: "unavailable",
          consentEnabled: false,
          suggestionsAvailable: false,
          provider: null,
          models: [],
        },
      }),
    );
    await page.route("**/api/category-assistance/settings", (route) =>
      route.fulfill({
        json: {
          enabled: false,
          provider: null,
          model: null,
          dailyLimit: 10,
          pendingLimit: 3,
          revision: "11111111-1111-4111-8111-111111111111",
        },
      }),
    );
    await signInAs(page, ADMIN_FIXTURE_USER);
    await page.goto("/admin/category-assistance");
    const guidance = page
      .getByText("TYPESAFE_API_KEY", { exact: true })
      .locator("..");
    await expect(guidance).toBeVisible();
    await expect(guidance).toContainText("Create a Jev API key");
    await expect(guidance).toContainText("API server environment");
    await expect(guidance).toContainText(
      "restart the API and check the connection",
    );
    await expect(guidance).not.toContainText(
      /User Secrets|local development|dotnet/i,
    );
    await expect(
      page.getByRole("checkbox", { name: /enable automatic/i }),
    ).toBeDisabled();
    await expect(page.getByLabel("Model", { exact: true })).toHaveCount(0);
  });
  test(`admin Jev setup and explicit check (${width}px)`, async ({
    page,
  }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await installApiMocks(page, {
      user: ADMIN_FIXTURE_USER,
      freezeClock: false,
    });
    let checks = 0;
    let state = "missing";
    await page.route("**/api/jev/status", (route) =>
      route.fulfill({
        json: {
          state,
          reason: state === "missing" ? "missingKey" : "notChecked",
          models: [],
        },
      }),
    );
    await page.route("**/api/jev/check", (route) => {
      checks++;
      return route.fulfill({
        json: {
          state: "connected",
          reason: "authenticationVerified",
          models: ["jev-latest"],
        },
      });
    });
    await signInAs(page, ADMIN_FIXTURE_USER);
    await page.goto("/settings");
    await expect(
      page.getByText("Not configured", { exact: true }),
    ).toBeVisible();
    await expect(
      page.getByText("Classification assistance is not enabled.", {
        exact: true,
      }),
    ).toBeVisible();
    await expect(page.getByText(/Create a Jev API key/)).not.toBeVisible();
    await page.getByText("Setup", { exact: true }).click();
    const guidance = page.getByText(/Create a Jev API key/);
    await expect(guidance).toBeVisible();
    await expect(guidance).toContainText(
      "TYPESAFE_API_KEY in the API server environment",
    );
    await expect(guidance).toContainText(
      "Restart the API, then check the connection",
    );
    await expect(guidance).not.toContainText(
      /User Secrets|local development|dotnet/i,
    );
    await expect(
      page.getByText(/missingKey|authenticationVerified|configuredUnverified/),
    ).toHaveCount(0);
    await page.getByText("Setup", { exact: true }).click();
    await expect(
      page.getByRole("button", { name: "Check connection" }),
    ).toHaveCount(0);
    await expect(
      page.getByRole("region", { name: "Available Jev models" }),
    ).toHaveCount(0);
    expect(checks).toBe(0);
    await page.screenshot({
      path: testInfo.outputPath("jev-missing.png"),
      fullPage: true,
    });
    state = "configuredUnverified";
    await page.reload();
    await expect(
      page.getByRole("button", { name: "Check connection" }),
    ).toBeVisible();
    expect(checks).toBe(0);
    await expect(
      page.getByRole("region", { name: "Available Jev models" }),
    ).toHaveCount(0);
    await page.getByRole("button", { name: "Check connection" }).click();
    await expect(
      page.getByRole("region", { name: "Available Jev models" }),
    ).toContainText("jev-latest");
    expect(checks).toBe(1);
    await expect(page.getByText("Connected", { exact: true })).toBeVisible();
    await expect(page.getByText(/authenticationVerified/)).toHaveCount(0);
    await page.screenshot({
      path: testInfo.outputPath("jev-connected.png"),
      fullPage: true,
    });
    await page.route("**/api/jev/check", (route) =>
      route.fulfill({
        status: 500,
        json: { status: 500, title: "Unavailable" },
      }),
    );
    await page.getByRole("button", { name: "Check connection" }).click();
    await expect(
      page.getByText("Connection check unavailable. Try again."),
    ).toBeVisible();
    await expect(
      page.getByRole("region", { name: "Available Jev models" }),
    ).toHaveCount(0);
    await expect(page.getByText("Connected", { exact: true })).toHaveCount(0);
    await expect(page.getByText("Unavailable", { exact: true })).toBeVisible();
    await page.screenshot({
      path: testInfo.outputPath("jev-check-error.png"),
      fullPage: true,
    });
  });
}

test("Jev loading and failed status hide checks and models", async ({
  page,
}, testInfo) => {
  await installApiMocks(page, { user: ADMIN_FIXTURE_USER, freezeClock: false });
  let release!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  let checks = 0;
  await page.route("**/api/jev/status", async (route) => {
    await gate;
    await route.fulfill({
      status: 500,
      json: { title: "Unavailable", status: 500 },
    });
  });
  await page.route("**/api/jev/check", (route) => {
    checks++;
    return route.fulfill({ status: 500 });
  });
  await signInAs(page, ADMIN_FIXTURE_USER);
  await page.goto("/settings");
  await expect(page.getByText("Loading connection status…")).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Check connection" }),
  ).toHaveCount(0);
  await expect(
    page.getByRole("region", { name: "Available Jev models" }),
  ).toHaveCount(0);
  await page.screenshot({
    path: testInfo.outputPath("jev-loading.png"),
    fullPage: true,
  });
  release();
  await expect(
    page.getByText("Connection status unavailable.", { exact: true }),
  ).toBeVisible({ timeout: 15000 });
  await expect(
    page.getByRole("button", { name: "Check connection" }),
  ).toHaveCount(0);
  await expect(
    page.getByRole("region", { name: "Available Jev models" }),
  ).toHaveCount(0);
  expect(checks).toBe(0);
  await page.screenshot({
    path: testInfo.outputPath("jev-error.png"),
    fullPage: true,
  });
});

test("non-admin Settings never mounts Jev or calls endpoints", async ({
  page,
}) => {
  await installApiMocks(page, { user: FIXTURE_USER, freezeClock: false });
  let calls = 0;
  await page.route("**/api/jev/**", (route) => {
    calls++;
    return route.fulfill({ status: 403 });
  });
  await signInAs(page, FIXTURE_USER);
  await page.goto("/settings");
  await expect(
    page.getByRole("heading", { name: "Settings", exact: true }),
  ).toBeVisible();
  await expect(page.getByText("Jev connection", { exact: true })).toHaveCount(
    0,
  );
  expect(calls).toBe(0);
});

import { expect, test } from "@playwright/test";
import type { components } from "../../src/generated/api";
import {
  ADMIN_FIXTURE_USER,
  installApiMocks,
  signInAs,
} from "../fixtures/api-mocks";

// Synthetic HTTP contracts exercise the real browser/router/forms/query lifecycle.
// This does not establish provider entitlement or actual database persistence.
for (const width of [1280, 375]) {
  test(`ChatGPT selection and review survive reload at ${width}px`, async ({
    page,
  }) => {
    await page.setViewportSize({ width, height: 900 });
    const runId = "10000000-0000-0000-0000-000000000001";
    const revision = "20000000-0000-0000-0000-000000000001";
    const itemId = "30000000-0000-0000-0000-000000000001";
    const proposalId = "40000000-0000-0000-0000-000000000001";
    let run: components["schemas"]["NormalizationAiRunResponse"] | undefined;
    const writes: { path: string; body: unknown }[] = [];
    let rejectApply = true;
    await installApiMocks(page, {
      freezeClock: false,
      user: ADMIN_FIXTURE_USER,
      overrides: {
        "**/api/normalization-ai/**": async (route) => {
          const path = new URL(route.request().url()).pathname;
          const respond = (data: unknown, status = 200) =>
            route.fulfill({
              status,
              contentType:
                status >= 400 ? "application/problem+json" : "application/json",
              body: JSON.stringify(data),
            });
          if (route.request().method() === "POST") {
            const body = route.request().postDataJSON();
            writes.push({ path, body });
            if (path.endsWith("/apply")) {
              if (rejectApply)
                return respond(
                  {
                    status: 409,
                    detail: "A source changed. Nothing was applied.",
                    code: "source_changed",
                  },
                  409,
                );
              run = {
                ...run!,
                state: "applied",
                proposals: run!.proposals.map((p) => ({
                  ...p,
                  state: "applied",
                })),
              };
              return respond(run);
            }
            run = {
              id: runId,
              revision,
              clientRequestId: body.clientRequestId,
              state: "ready",
              model: "test-model",
              createdAt: "2026-01-01T00:00:00Z",
              completedAt: "2026-01-01T00:00:01Z",
              errorCode: null,
              errorDetail: null,
              usage: { input: 30, output: 12, cacheRead: 0, cacheWrite: 0 },
              proposals: [
                {
                  id: proposalId,
                  receiptItemId: itemId,
                  description: "SYNTHETIC MLK",
                  action: "create",
                  targetId: null,
                  canonicalName: "Whole milk",
                  displayLabel: null,
                  reason: "Explicit product identity",
                  state: "pending",
                },
              ],
            };
            return respond(run);
          }
          if (path.endsWith("/connection"))
            return respond({
              configured: true,
              isOwner: true,
              credentialsPresent: true,
              busy: false,
              generation: revision,
              models: ["test-model"],
            });
          if (path.endsWith("/candidates"))
            return respond({
              total: 1,
              items: [
                {
                  id: itemId,
                  receiptId: runId,
                  description: "SYNTHETIC MLK",
                  location: "Synthetic store",
                  date: "2026-01-01",
                },
              ],
            });
          if (path.endsWith("/runs")) return respond(run ? [run] : []);
          if (path.endsWith(`/runs/${runId}`)) return respond(run);
          throw new Error(`Unexpected normalization route ${path}`);
        },
      },
    });
    await signInAs(page, ADMIN_FIXTURE_USER);
    await page.goto("/admin/normalized-descriptions");
    await page.getByRole("tab", { name: "ChatGPT", exact: true }).click();
    await expect(
      page.getByRole("heading", { name: "ChatGPT normalization" }),
    ).toBeVisible();
    await page.getByRole("checkbox", { name: "Select SYNTHETIC MLK" }).check();
    await expect(
      page.getByRole("combobox", { name: "ChatGPT model" }),
    ).toHaveValue("");
    await page
      .getByRole("combobox", { name: "ChatGPT model" })
      .selectOption("test-model");
    await page
      .getByRole("button", { name: "Request suggestions for 1 items" })
      .click();
    await expect(page).toHaveURL(new RegExp(`aiRun=${runId}`));
    expect(writes).toHaveLength(1);
    expect(writes[0].body).toMatchObject({
      model: "test-model",
      itemIds: [itemId],
    });
    await page.reload();
    const review = page.getByRole("region", {
      name: "Review ChatGPT suggestions",
    });
    await expect(
      review.getByRole("cell", {
        name: "Whole milk Create reviewed name ... pending",
        exact: true,
      }),
    ).toBeVisible();
    const apply = review.getByRole("button", {
      name: "Apply reviewed suggestions",
    });
    await expect(apply).toBeDisabled();
    await review
      .getByRole("checkbox", { name: "Accept suggestion for SYNTHETIC MLK" })
      .check();
    await expect(apply).toBeDisabled();
    await review
      .getByRole("checkbox", { name: "I reviewed these 1 suggestions" })
      .check();
    await apply.click();
    await expect(review.getByRole("alert")).toHaveText(
      "A source changed. Nothing was applied.",
    );
    await expect(
      review.getByRole("checkbox", {
        name: "Accept suggestion for SYNTHETIC MLK",
      }),
    ).toBeChecked();
    expect(writes[1].body).toEqual({
      proposalIds: [proposalId],
      expectedRevision: revision,
    });
    rejectApply = false;
    await apply.click();
    await expect(review.getByRole("status")).toContainText("Applied");
    expect(writes).toHaveLength(3);
    const overflow = await page.evaluate(
      () =>
        document.documentElement.scrollWidth >
        document.documentElement.clientWidth + 1,
    );
    expect(overflow).toBe(false);
  });
}

for (const width of [1280, 375]) {
  test(`Unknown busy run stays concise and closes to new selection at ${width}px`, async ({
    page,
  }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    const id = "10000000-0000-0000-0000-000000000001";
    const run: components["schemas"]["NormalizationAiRunResponse"] = {
      id,
      revision: id,
      clientRequestId: id,
      state: "unknown",
      model: "test-model",
      createdAt: "2026-01-01T00:00:00Z",
      completedAt: null,
      errorCode: "unknown_outcome",
      errorDetail: "Inference may have started; usage may have occurred.",
      usage: null,
      proposals: Array.from({ length: 25 }, (_, i) => ({
        id: `${id.slice(0, -2)}${String(i).padStart(2, "0")}`,
        receiptItemId: id,
        description: `Synthetic item ${i}`,
        action: "pending",
        targetId: null,
        canonicalName: null,
        displayLabel: null,
        reason: null,
        state: "pending",
      })),
    };
    let writes = 0;
    await installApiMocks(page, {
      freezeClock: false,
      user: ADMIN_FIXTURE_USER,
      overrides: {
        "**/api/normalization-ai/**": async (route) => {
          if (route.request().method() !== "GET") writes++;
          const path = new URL(route.request().url()).pathname;
          const data = path.endsWith("/connection")
            ? {
                configured: true,
                isOwner: true,
                credentialsPresent: true,
                busy: true,
                generation: id,
                models: ["test-model"],
              }
            : path.endsWith("/candidates")
              ? {
                  items: [
                    {
                      id,
                      receiptId: id,
                      description: "SYNTHETIC ITEM",
                      location: "Test store",
                      date: "2026-01-01",
                    },
                  ],
                  total: 1,
                }
              : path.endsWith("/runs")
                ? [run]
                : run;
          await route.fulfill({
            contentType: "application/json",
            body: JSON.stringify(data),
          });
        },
      },
    });
    await signInAs(page, ADMIN_FIXTURE_USER);
    await page.goto(`/admin/normalized-descriptions?tab=chatgpt&aiRun=${id}`);
    const review = page.getByRole("region", {
      name: "Review ChatGPT suggestions",
    });
    await expect(review.getByRole("status")).toContainText("Outcome uncertain");
    await expect(review.getByRole("table")).toHaveCount(0);
    await expect(review.getByRole("checkbox")).toHaveCount(0);
    await expect(review.getByText(/allowance again/)).toHaveCount(1);
    await expect(
      page.getByRole("region", { name: "Select items for ChatGPT" }),
    ).toHaveCount(0);
    await expect(
      review.getByRole("button", { name: "Retry as a new request" }),
    ).toBeEnabled();
    await expect(
      page.locator("#chatgpt-connection-settings"),
    ).not.toHaveAttribute("open");
    await page.screenshot({
      path: testInfo.outputPath(`run-ui-unknown-busy-${width}.png`),
      fullPage: true,
    });
    await page.getByRole("button", { name: "Close run" }).click();
    const selection = page.getByRole("region", {
      name: "Select items for ChatGPT",
    });
    await expect(selection).toBeVisible();
    const model = selection.getByRole("combobox", { name: "ChatGPT model" });
    const request = selection.getByRole("button", {
      name: "Request suggestions for 0 items",
    });
    await expect(request).toBeDisabled();
    const modelBox = await model.boundingBox();
    const itemBox = await selection
      .getByRole("checkbox", { name: "Select SYNTHETIC ITEM" })
      .boundingBox();
    expect(modelBox!.y).toBeLessThan(itemBox!.y);
    expect(writes).toBe(0);
    expect(
      await page.evaluate(
        () =>
          document.documentElement.scrollWidth >
          document.documentElement.clientWidth + 1,
      ),
    ).toBe(false);
  });
}

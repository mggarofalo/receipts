import type { Mock } from "vitest";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithQueryClient } from "@/test/test-utils";
import client from "@/lib/api-client";
import { RememberCategory, RememberCategoryControl } from "./RememberCategory";
const permissions = vi.hoisted(() => ({ admin: true }));
vi.mock("@/hooks/usePermission", () => ({
  usePermission: () => ({ isAdmin: () => permissions.admin }),
}));
vi.mock("@/lib/api-client", () => ({
  default: { POST: vi.fn(), GET: vi.fn() },
}));
const post = client.POST as unknown as Mock<
  (path: string, options?: { body?: unknown }) => Promise<unknown>
>;
const source = { location: "Market", description: "MILK", itemCode: "123" };
const node = {
  id: "dairy",
  name: "Dairy",
  parentId: null,
  archived: false,
  definition: "Dairy food",
};
const revision = { id: "taxonomy", nodes: [node] };
const inspection = {
  source,
  sourceFingerprint: "fingerprint",
  taxonomyRevisionId: "taxonomy",
  disposition: "approved",
  category: { id: "dairy" },
  mapping: {
    id: "mapping",
    category: { id: "dairy" },
    taxonomyRevisionId: "taxonomy",
    actor: "Alice",
    reason: "Reviewed",
    recordedAt: "2026-10-26",
  },
};
const classification = {
  assignmentId: "assignment",
  categoryId: "dairy",
  taxonomyRevisionId: "taxonomy",
  recordedPath: [node],
  source: "manual",
  reason: "Selected",
};
const writes = () =>
  post.mock.calls.filter(([path]) => path === "/api/category-mappings/changes");
beforeEach(() => {
  vi.clearAllMocks();
  permissions.admin = true;
  vi.mocked(client.GET).mockResolvedValue({ data: revision } as never);
  post.mockImplementation(
    async (path) =>
      ({
        data:
          path === "/api/category-mappings/inspect"
            ? inspection
            : { id: "new" },
      }) as never,
  );
});
function renderPanel() {
  return renderWithQueryClient(
    <RememberCategory source={source} classification={classification} />,
  );
}
async function ready() {
  await screen.findByText("Remembered category: Dairy");
}
async function reason() {
  await userEvent.type(
    screen.getByRole("textbox", { name: /reason/i }),
    "Exact source reviewed",
  );
}
describe("RememberCategory", () => {
  it("requires fresh confirmation before replacing another remembered category from a completed decision", async () => {
    const other = {
      ...inspection,
      mapping: { ...inspection.mapping, category: { id: "other" } },
    };
    post.mockImplementation(
      async (path) =>
        ({ data: path.endsWith("/inspect") ? other : { id: "new" } }) as never,
    );
    renderWithQueryClient(
      <RememberCategory
        source={source}
        preferredCategoryId="dairy"
        confirmDifferentMapping
      />,
    );
    await screen.findByText("Remembered category: other");
    const change = screen.getByRole("button", {
      name: "Change remembered category",
    });
    expect(change).toBeDisabled();
    await userEvent.click(
      screen.getByRole("checkbox", {
        name: /Replace the current remembered category/i,
      }),
    );
    expect(change).toBeEnabled();
    await userEvent.click(change);
    await waitFor(() => expect(writes()).toHaveLength(1));
    expect(writes()[0][1]).toEqual(
      expect.objectContaining({
        body: expect.objectContaining({
          expectedMappingRevisionId: "mapping",
          category: { id: "dairy" },
          sourceFingerprint: "fingerprint",
        }),
      }),
    );
  });
  it("recognizes an already approved saved category without another change", async () => {
    renderWithQueryClient(
      <RememberCategory
        source={source}
        preferredCategoryId="dairy"
        confirmDifferentMapping
      />,
    );
    await ready();
    expect(
      screen.getByText(/already remembered with the saved category/i),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Change remembered category" }),
    ).toBeDisabled();
    expect(writes()).toHaveLength(0);
  });
  it("prefills an approved item category without inventing a receipt assignment", async () => {
    renderWithQueryClient(
      <RememberCategory source={source} preferredCategoryId="dairy" />,
    );
    await ready();
    await userEvent.click(
      screen.getByRole("button", { name: "Change remembered category" }),
    );
    await waitFor(() => expect(writes()).toHaveLength(1));
    expect(writes()[0][1]).toEqual(
      expect.objectContaining({
        body: expect.objectContaining({
          category: { id: "dairy" },
          expectedMappingRevisionId: "mapping",
          sourceFingerprint: "fingerprint",
        }),
      }),
    );
  });
  it("loads lazily from the receipt affordance, shows exact scope and never approves on inspection", async () => {
    renderWithQueryClient(
      <RememberCategoryControl
        source={source}
        classification={classification}
      />,
    );
    expect(client.POST).not.toHaveBeenCalled();
    await userEvent.click(
      screen.getByRole("button", { name: "Remember category…" }),
    );
    await ready();
    expect(screen.getByText("Market")).toBeVisible();
    expect(screen.getByText("MILK")).toBeVisible();
    await userEvent.click(screen.getByText("Matching details and history"));
    expect(screen.getByText(/Only surrounding spaces/)).toBeVisible();
    expect(screen.getByText(/Approved by Alice/)).toBeVisible();
    expect(writes()).toHaveLength(0);
  });
  it("explicitly approves the recorded category independently of receipt persistence", async () => {
    renderPanel();
    await ready();
    await reason();
    await userEvent.click(
      screen.getByRole("button", { name: "Change remembered category" }),
    );
    await screen.findByText(/Remembered category updated/);
    expect(writes()).toHaveLength(1);
    expect(writes()[0][1]).toEqual(
      expect.objectContaining({
        body: expect.objectContaining({
          source,
          sourceFingerprint: "fingerprint",
          expectedMappingRevisionId: "mapping",
          expectedTaxonomyRevisionId: "taxonomy",
          category: { id: "dairy" },
          reason: "Exact source reviewed",
          operationId: expect.any(String),
        }),
      }),
    );
    expect(
      post.mock.calls.every(([path]) =>
        path.startsWith("/api/category-mappings/"),
      ),
    ).toBe(true);
  });
  it("revokes with an explicit null category", async () => {
    renderPanel();
    await ready();
    await reason();
    await userEvent.click(
      screen.getByRole("button", { name: "Revoke remembered category" }),
    );
    await screen.findByText(/Remembered category updated/);
    expect(writes()[0][1]?.body).toEqual(
      expect.objectContaining({ category: null }),
    );
  });
  it("retries an uncertain revocation with exactly the same operation and payload", async () => {
    let failed = false;
    post.mockImplementation(async (path) => {
      if (path.endsWith("inspect")) return { data: inspection } as never;
      if (!failed) {
        failed = true;
        throw new TypeError("Lost response");
      }
      return { data: { id: "new" } } as never;
    });
    renderPanel();
    await ready();
    await reason();
    await userEvent.click(
      screen.getByRole("button", { name: "Revoke remembered category" }),
    );
    await userEvent.click(
      await screen.findByRole("button", { name: "Retry same change" }),
    );
    await screen.findByText(/Remembered category updated/);
    expect(writes()[1][1]?.body).toEqual(writes()[0][1]?.body);
    expect(writes()[1][1]?.body).toEqual(
      expect.objectContaining({ category: null }),
    );
  });
  it("requires refresh and explicit reapproval after a CAS conflict", async () => {
    post.mockImplementation(async (path) =>
      path.endsWith("inspect")
        ? ({ data: inspection } as never)
        : ({ error: { status: 409, detail: "Mapping changed" } } as never),
    );
    renderPanel();
    await ready();
    await reason();
    await userEvent.click(
      screen.getByRole("button", { name: "Change remembered category" }),
    );
    await screen.findByText(/mapping or categories changed/);
    expect(
      screen.queryByRole("button", { name: "Retry same change" }),
    ).not.toBeInTheDocument();
    await userEvent.click(
      screen.getByRole("button", { name: "Refresh remembered category" }),
    );
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Change remembered category" }),
      ).toBeEnabled(),
    );
    await userEvent.click(
      screen.getByRole("button", { name: "Change remembered category" }),
    );
    await waitFor(() => expect(writes()).toHaveLength(2));
    expect(writes()[1][1]?.body).not.toEqual(writes()[0][1]?.body);
  });
  it("provides authenticated inspection without approval authority to non Admin users", async () => {
    permissions.admin = false;
    renderPanel();
    await ready();
    expect(screen.getByText(/An active Admin/)).toBeVisible();
    expect(screen.queryByRole("combobox")).not.toBeInTheDocument();
    expect(writes()).toHaveLength(0);
  });
  it("does not silently convert a legacy category label or unclassified receipt to approval", async () => {
    renderWithQueryClient(<RememberCategory source={source} />);
    await ready();
    await reason();
    expect(
      screen.getByRole("button", { name: "Change remembered category" }),
    ).toBeDisabled();
    expect(writes()).toHaveLength(0);
  });
});

it("creates a first approval only on a separate explicit action", async () => {
  post.mockImplementation(
    async (path) =>
      ({
        data: path.endsWith("inspect")
          ? {
              ...inspection,
              mapping: null,
              category: null,
              disposition: "missing",
            }
          : { id: "first" },
      }) as never,
  );
  renderPanel();
  await screen.findByText("Remembered category: None");
  expect(writes()).toHaveLength(0);
  await reason();
  await userEvent.click(
    screen.getByRole("button", { name: "Remember for future matches" }),
  );
  await screen.findByText(/Remembered category updated/);
  expect(writes()[0][1]?.body).toEqual(
    expect.objectContaining({
      expectedMappingRevisionId: null,
      category: { id: "dairy" },
    }),
  );
});

it("retains inspection and allows retry when mapping lookup fails", async () => {
  post
    .mockResolvedValueOnce({
      error: { status: 503, detail: "Inspection unavailable" },
    } as never)
    .mockResolvedValue({ data: inspection } as never);
  renderPanel();
  await screen.findByText("Inspection unavailable");
  await userEvent.click(
    screen.getByRole("button", { name: "Refresh remembered category" }),
  );
  await ready();
  expect(writes()).toHaveLength(0);
});

it.each([400, 403, 404, 422])(
  "permits fresh review after a definitive %s rejection",
  async (status) => {
    post.mockImplementation(async (path) =>
      path.endsWith("inspect")
        ? { data: inspection }
        : { error: { status, detail: "Change rejected" } },
    );
    renderPanel();
    await ready();
    await reason();
    await userEvent.click(
      screen.getByRole("button", { name: "Change remembered category" }),
    );
    await screen.findByText("Change rejected");
    expect(
      screen.queryByRole("button", { name: "Retry same change" }),
    ).not.toBeInTheDocument();
    await userEvent.click(
      screen.getByRole("button", { name: "Refresh remembered category" }),
    );
    await waitFor(() =>
      expect(screen.getByRole("textbox", { name: /reason/i })).toBeEnabled(),
    );
    await userEvent.click(
      screen.getByRole("button", { name: "Change remembered category" }),
    );
    await waitFor(() => expect(writes()).toHaveLength(2));
    expect(writes()[1][1]?.body).not.toEqual(writes()[0][1]?.body);
  },
);

it("preserves an uncertain approval when collapsed and reopened", async () => {
  post.mockImplementation(async (path) => {
    if (path.endsWith("inspect")) return { data: inspection };
    throw new TypeError("Lost response");
  });
  renderWithQueryClient(
    <RememberCategoryControl source={source} classification={classification} />,
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Remember category…" }),
  );
  await ready();
  await reason();
  await userEvent.click(
    screen.getByRole("button", { name: "Change remembered category" }),
  );
  await screen.findByRole("button", { name: "Retry same change" });
  await userEvent.click(screen.getByRole("button", { name: /^Close$/ }));
  await userEvent.click(
    screen.getByRole("button", { name: "Remember category…" }),
  );
  expect(screen.getByRole("textbox", { name: /reason/i })).toBeDisabled();
  await userEvent.click(
    screen.getByRole("button", { name: "Retry same change" }),
  );
  await waitFor(() => expect(writes()).toHaveLength(2));
  expect(writes()[1][1]?.body).toEqual(writes()[0][1]?.body);
});

it.each(["Change remembered category", "Revoke remembered category"])(
  "allows %s without requiring a custom note",
  async (name) => {
    renderPanel();
    await ready();
    await userEvent.click(screen.getByRole("button", { name }));
    await screen.findByText(/Remembered category updated/);
    expect(writes()).toHaveLength(1);
    expect(writes()[0][1]?.body).toEqual(
      expect.objectContaining({ reason: expect.stringMatching(/\S/) }),
    );
  },
);

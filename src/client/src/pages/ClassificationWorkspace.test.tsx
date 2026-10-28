import { screen, within, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithQueryClient } from "@/test/test-utils";
import mockClient, { resetMockClient } from "@/test/mock-api-client";
import ClassificationWorkspace from "./ClassificationWorkspace";

vi.mock("@/lib/api-client", () => ({ default: mockClient }));
vi.mock("@/hooks/usePermission", () => ({
  usePermission: () => ({ isAdmin: () => true }),
}));

const attemptId = "11111111-1111-4111-8111-111111111111";
const itemA = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const itemB = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
const zeroRevision = "00000000-0000-0000-0000-000000000000";
const eligibleItems = [
  {
    id: itemA,
    receiptId: "receipt-a",
    date: "2026-10-24",
    description: "Whole Milk",
    store: "Store",
    quantity: 1,
    totalAmount: 4.5,
    currency: "USD",
  },
  {
    id: itemB,
    receiptId: "receipt-b",
    date: "2026-10-25",
    description: "Low Fat Milk",
    store: "Store",
    quantity: 1,
    totalAmount: 3.5,
    currency: "USD",
  },
];
const status = {
  capability: "ready",
  consentEnabled: true,
  suggestionsAvailable: true,
  provider: "jev",
  models: ["test"],
};
const revision = {
  id: "22222222-2222-4222-8222-222222222222",
  nodes: [
    {
      id: "33333333-3333-4333-8333-333333333333",
      name: "Food",
      archived: false,
    },
    {
      id: "44444444-4444-4444-8444-444444444444",
      name: "Dairy",
      parentId: "33333333-3333-4333-8333-333333333333",
      archived: false,
    },
    {
      id: "55555555-5555-4555-8555-555555555555",
      name: "Bakery",
      parentId: "33333333-3333-4333-8333-333333333333",
      archived: false,
    },
  ],
};
const suggestion = {
  attemptId,
  source: { location: "Store", description: "Milk" },
  originalDescription: "Whole Milk",
  affectedCount: 2,
  proposedPath: [revision.nodes[0].id, revision.nodes[1].id],
  abstentionReason: null,
  confidence: 0.9,
  probabilityScope: "global_choice",
  state: "suggested",
  isCurrent: true,
  createdAt: "2026-10-25T12:00:00Z",
  completedAt: "2026-10-25T12:00:01Z",
};
const ok = (data: unknown) => ({
  data,
  error: undefined,
  response: { status: 200 },
});
const fail = (statusCode: number, detail: string) => ({
  data: undefined,
  error: { status: statusCode, detail },
  response: { status: statusCode },
});

beforeEach(() => {
  resetMockClient();
  window.sessionStorage.clear();
  mockClient.GET.mockImplementation(async (path: string) => {
    if (path.endsWith("/status")) return ok(status);
    if (path.endsWith("/review-preference"))
      return ok({
        attemptId,
        state: "active",
        revision: zeroRevision,
        excludedItemIds: [],
      });
    if (path.endsWith("/suggestions"))
      return ok({ total: 1, offset: 0, limit: 25, items: [suggestion] });
    if (path.endsWith("/items"))
      return ok({
        total: 2,
        offset: 0,
        limit: 100,
        itemIds: [itemA, itemB],
        items: eligibleItems,
      });
    if (path === "/api/taxonomy") return ok(revision);
    throw new Error(path);
  });
  mockClient.POST.mockResolvedValue(
    ok({
      attemptId,
      decision: "accepted",
      affectedItemIds: [itemA, itemB],
      categoryId: revision.nodes[1].id,
    }),
  );
  mockClient.PUT.mockImplementation(
    async (
      _path: string,
      options: { body: { state: string; excludedItemIds: string[] } },
    ) =>
      ok({
        attemptId,
        state: options.body.state,
        revision: revision.id,
        excludedItemIds: options.body.excludedItemIds,
      }),
  );
});

const admin = (userId: string) => ({
  user: {
    userId,
    email: `${userId}@example.test`,
    roles: ["Admin"],
    mustResetPassword: false,
  },
});

it("keeps optional future mapping after accepted attempts disappear, through mapping failure and reload", async () => {
  let accepted = false;
  let inspectionFails = true;
  const inspect = {
    source: suggestion.source,
    sourceFingerprint: "source-fingerprint",
    taxonomyRevisionId: revision.id,
    disposition: "unmapped",
    mapping: null,
  };
  mockClient.GET.mockImplementation(async (path: string) => {
    if (path.endsWith("/status")) return ok(status);
    if (path.endsWith("/review-preference"))
      return ok({
        attemptId,
        state: "active",
        revision: zeroRevision,
        excludedItemIds: [],
      });
    if (path.endsWith("/suggestions"))
      return ok({
        total: accepted ? 0 : 1,
        offset: 0,
        limit: 25,
        items: accepted ? [] : [suggestion],
      });
    if (path.endsWith("/items"))
      return ok({
        total: 2,
        offset: 0,
        limit: 100,
        itemIds: [itemA, itemB],
        items: eligibleItems,
      });
    if (path === "/api/taxonomy") return ok(revision);
    if (path === "/api/category-assistance/suggestions/{attemptId}")
      return accepted ? fail(404, "Gone") : ok(suggestion);
    throw new Error(path);
  });
  mockClient.POST.mockImplementation(async (path: string) => {
    if (path.endsWith("/accept")) {
      accepted = true;
      return ok({
        attemptId,
        decision: "accepted",
        affectedItemIds: [itemA],
        categoryId: revision.nodes[1].id,
      });
    }
    if (path.endsWith("/inspect"))
      return inspectionFails
        ? fail(503, "Mapping inspection down")
        : ok(inspect);
    if (path.endsWith("/changes")) return ok({ id: "mapping-id" });
    throw new Error(path);
  });
  const first = renderWithQueryClient(<ClassificationWorkspace />, {
    auth: admin("alice"),
  });
  await userEvent.click(
    await screen.findByRole("button", { name: "Review details" }),
  );
  await screen.findByText("Low Fat Milk");
  await userEvent.click(
    screen.getByRole("checkbox", { name: /Select Whole Milk/i }),
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Apply to selected" }),
  );
  expect(
    await screen.findByRole("region", {
      name: "Completed classifications awaiting future mapping",
    }),
  ).toBeInTheDocument();
  await waitFor(() =>
    expect(
      screen.queryByRole("button", { name: "Review details" }),
    ).not.toBeInTheDocument(),
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Remember category…" }),
  );
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Mapping inspection down",
  );
  first.unmount();

  const second = renderWithQueryClient(<ClassificationWorkspace />, {
    auth: admin("alice"),
    route: `/classification?attempt=${attemptId}`,
  });
  expect(
    await screen.findByRole("region", {
      name: "Completed classifications awaiting future mapping",
    }),
  ).toBeInTheDocument();
  expect(await screen.findByRole("alert")).toHaveTextContent("Gone");
  inspectionFails = false;
  await userEvent.click(
    screen.getByRole("button", { name: "Remember category…" }),
  );
  await screen.findByText("Remembered category: None");
  await userEvent.click(
    screen.getByRole("button", { name: "Remember for future matches" }),
  );
  await waitFor(() =>
    expect(
      screen.queryByRole("region", {
        name: "Completed classifications awaiting future mapping",
      }),
    ).not.toBeInTheDocument(),
  );
  expect(
    mockClient.POST.mock.calls.filter(([path]) => path.endsWith("/accept")),
  ).toHaveLength(1);
  expect(
    mockClient.POST.mock.calls.filter(([path]) => path.endsWith("/changes")),
  ).toHaveLength(1);
  second.unmount();
});

it("isolates pending future mapping by signed-in actor and clears it only when skipped", async () => {
  window.sessionStorage.setItem(
    "receipts:classification:pending-remember:alice",
    JSON.stringify([
      {
        attemptId,
        source: suggestion.source,
        categoryId: revision.nodes[1].id,
        affectedItemCount: 1,
        savedAt: "2026-10-26T12:00:00Z",
      },
    ]),
  );
  mockClient.GET.mockImplementation(async (path: string) =>
    path.endsWith("/status")
      ? ok({
          ...status,
          capability: "unconfigured",
          suggestionsAvailable: false,
        })
      : fail(500, "Provider reads should stay disabled"),
  );
  const bob = renderWithQueryClient(<ClassificationWorkspace />, {
    auth: admin("bob"),
  });
  expect(
    screen.queryByRole("region", {
      name: "Completed classifications awaiting future mapping",
    }),
  ).not.toBeInTheDocument();
  bob.unmount();
  renderWithQueryClient(<ClassificationWorkspace />, { auth: admin("alice") });
  expect(
    await screen.findByRole("region", {
      name: "Completed classifications awaiting future mapping",
    }),
  ).toBeInTheDocument();
  await userEvent.click(
    screen.getByRole("button", { name: "Skip remembering" }),
  );
  expect(
    screen.queryByRole("region", {
      name: "Completed classifications awaiting future mapping",
    }),
  ).not.toBeInTheDocument();
  expect(mockClient.POST).not.toHaveBeenCalled();
});

it("keeps manual browse available when assistance capability is absent", async () => {
  mockClient.GET.mockImplementation(async (path: string) =>
    path.endsWith("/status")
      ? ok({
          ...status,
          capability: "unconfigured",
          suggestionsAvailable: false,
        })
      : fail(500, "Unexpected request"),
  );
  renderWithQueryClient(<ClassificationWorkspace />);
  expect(
    await screen.findByText(/suggestions are unavailable/i),
  ).toBeInTheDocument();
  expect(
    screen.getByRole("link", { name: "Browse categories" }),
  ).toHaveAttribute("href", "/categories");
  expect(mockClient.GET).toHaveBeenCalledTimes(1);
});

it.each([
  ["local_choice", "Final choice confidence: 90%"],
  ["global_choice", "Confidence: 90%"],
])("labels %s suggestion scores accurately", async (scope, expected) => {
  const get = mockClient.GET.getMockImplementation()!;
  mockClient.GET.mockImplementation(async (path: string, options: unknown) => {
    if (path.endsWith("/suggestions"))
      return ok({
        total: 1,
        offset: 0,
        limit: 25,
        items: [{ ...suggestion, probabilityScope: scope }],
      });
    return get(path, options);
  });
  renderWithQueryClient(<ClassificationWorkspace />);
  await screen.findByText("Whole Milk");
  await userEvent.click(screen.getByRole("button", { name: "Review details" }));
  expect(await screen.findByText(new RegExp(expected))).toBeInTheDocument();
  expect(
    screen.getByRole("link", { name: "Browse categories" }),
  ).toHaveAttribute("href", "/categories");
});

it("hides provider actions while capability loads or is unknown", async () => {
  let finish!: (value: ReturnType<typeof ok>) => void;
  mockClient.GET.mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        finish = resolve;
      }),
  );
  renderWithQueryClient(<ClassificationWorkspace />);
  expect(screen.getByText("Checking category assistance…")).toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "Review details" }),
  ).not.toBeInTheDocument();
  finish(ok({ ...status, capability: "unknown" }));
  expect(
    await screen.findByText(/suggestions are unavailable/i),
  ).toBeInTheDocument();
  expect(mockClient.GET).toHaveBeenCalledTimes(1);
});

it("shows a status read error and allows retry without showing provider actions", async () => {
  mockClient.GET.mockResolvedValueOnce(fail(503, "Status down"));
  renderWithQueryClient(<ClassificationWorkspace />);
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "status could not be loaded",
  );
  expect(
    screen.queryByText("No suggestions to review right now."),
  ).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Retry" }));
  expect(await screen.findByText("Whole Milk")).toBeInTheDocument();
});

it("reviews and applies only checked eligible items with a pinned category revision", async () => {
  renderWithQueryClient(<ClassificationWorkspace />);
  expect(await screen.findByText("Whole Milk")).toBeInTheDocument();
  expect(screen.getByText("Food / Dairy")).toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Review details" }));
  await screen.findByText("Low Fat Milk");
  expect(
    screen.getByRole("button", { name: "Apply to selected" }),
  ).toBeDisabled();
  await userEvent.click(
    screen.getByRole("checkbox", { name: /Select Whole Milk/i }),
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Apply to selected" }),
  );
  await waitFor(() =>
    expect(mockClient.POST).toHaveBeenCalledWith(
      "/api/category-assistance/suggestions/{attemptId}/accept",
      expect.objectContaining({
        body: expect.objectContaining({
          expectedTaxonomyRevision: revision.id,
          itemIds: [itemA],
          categoryId: revision.nodes[1].id,
        }),
      }),
    ),
  );
});

it("keeps a stale suggestion visible but disables applying it", async () => {
  mockClient.GET.mockImplementation(async (path: string) => {
    if (path.endsWith("/status")) return ok(status);
    if (path.endsWith("/review-preference"))
      return ok({
        attemptId,
        state: "active",
        revision: zeroRevision,
        excludedItemIds: [],
      });
    if (path.endsWith("/suggestions"))
      return ok({
        total: 1,
        offset: 0,
        limit: 25,
        items: [{ ...suggestion, isCurrent: false }],
      });
    if (path === "/api/taxonomy") return ok(revision);
    throw new Error(path);
  });
  renderWithQueryClient(<ClassificationWorkspace />);
  await userEvent.click(
    await screen.findByRole("button", { name: "Review details" }),
  );
  expect(screen.getByRole("alert")).toHaveTextContent("stale");
  expect(
    screen.queryByRole("button", { name: "Apply to selected" }),
  ).not.toBeInTheDocument();
  expect(mockClient.POST).not.toHaveBeenCalled();
});

it("supports manual correction before grouped approval", async () => {
  renderWithQueryClient(<ClassificationWorkspace />);
  await userEvent.click(
    await screen.findByRole("button", { name: "Review details" }),
  );
  await screen.findByText("Low Fat Milk");
  await userEvent.click(
    screen.getByRole("checkbox", { name: /Select Whole Milk/i }),
  );
  await userEvent.click(
    screen.getByRole("checkbox", { name: /Select Low Fat Milk/i }),
  );
  await userEvent.click(screen.getByRole("combobox"));
  await userEvent.click(
    within(screen.getByRole("dialog")).getByRole("option", {
      name: "Food / Bakery",
    }),
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Apply to selected" }),
  );
  await waitFor(() =>
    expect(mockClient.POST).toHaveBeenCalledWith(
      "/api/category-assistance/suggestions/{attemptId}/accept",
      expect.objectContaining({
        body: expect.objectContaining({
          itemIds: [itemA, itemB],
          categoryId: revision.nodes[2].id,
        }),
      }),
    ),
  );
});

it("retries a failed accept with the same operation ID", async () => {
  mockClient.POST.mockResolvedValueOnce(fail(503, "Temporary failure"));
  renderWithQueryClient(<ClassificationWorkspace />);
  await userEvent.click(
    await screen.findByRole("button", { name: "Review details" }),
  );
  await screen.findByText("Low Fat Milk");
  await userEvent.click(
    screen.getByRole("checkbox", { name: /Select Whole Milk/i }),
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Apply to selected" }),
  );
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Temporary failure",
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Apply to selected" }),
  );
  await waitFor(() => expect(mockClient.POST).toHaveBeenCalledTimes(2));
  expect(mockClient.POST.mock.calls[0][1].body.operationId).toBe(
    mockClient.POST.mock.calls[1][1].body.operationId,
  );
});

it("loads a linked attempt directly and opens its receipt context", async () => {
  mockClient.GET.mockImplementation(async (path: string) => {
    if (path.endsWith("/status")) return ok(status);
    if (path.endsWith("/review-preference"))
      return ok({
        attemptId,
        state: "active",
        revision: zeroRevision,
        excludedItemIds: [],
      });
    if (path === "/api/category-assistance/suggestions/{attemptId}")
      return ok(suggestion);
    if (path.endsWith("/items"))
      return ok({
        total: 2,
        offset: 0,
        limit: 100,
        itemIds: [itemA, itemB],
        items: eligibleItems,
      });
    if (path === "/api/taxonomy") return ok(revision);
    throw new Error(path);
  });
  renderWithQueryClient(<ClassificationWorkspace />, {
    route: `/classification?attempt=${attemptId}`,
  });
  expect(await screen.findByText("Low Fat Milk")).toBeInTheDocument();
  expect(
    screen.getAllByRole("link", { name: "View receipt" })[0],
  ).toHaveAttribute("href", "/receipts/receipt-a");
  expect(mockClient.GET).not.toHaveBeenCalledWith(
    "/api/category-assistance/suggestions",
    expect.anything(),
  );
});

it("saves durable review choices and uses server filtered lists", async () => {
  let state = "active";
  let currentRevision = zeroRevision;
  const originalGet = mockClient.GET.getMockImplementation()!;
  mockClient.GET.mockImplementation(
    async (
      path: string,
      options: { params?: { query?: { review?: string } } },
    ) => {
      if (path.endsWith("/review-preference"))
        return ok({
          attemptId,
          state,
          revision: currentRevision,
          excludedItemIds: [],
        });
      if (path.endsWith("/suggestions"))
        return ok({
          total: options.params?.query?.review === state ? 1 : 0,
          offset: 0,
          limit: 25,
          items:
            options.params?.query?.review === state
              ? [{ ...suggestion, reviewState: state }]
              : [],
        });
      return originalGet(path, options);
    },
  );
  mockClient.PUT.mockImplementation(
    async (
      _path: string,
      options: {
        body: {
          state: string;
          expectedRevision: string;
          excludedItemIds: string[];
        };
      },
    ) => {
      expect(options.body.expectedRevision).toBe(currentRevision);
      state = options.body.state;
      currentRevision = revision.id;
      return ok({
        attemptId,
        state,
        revision: currentRevision,
        excludedItemIds: [],
      });
    },
  );
  renderWithQueryClient(<ClassificationWorkspace />);
  await userEvent.click(
    await screen.findByRole("button", { name: "Review details" }),
  );
  await screen.findByText(/Current choice: active/);
  await userEvent.click(
    within(screen.getByRole("region", { name: "Review preference" })).getByRole(
      "button",
      { name: "Later" },
    ),
  );
  await waitFor(() =>
    expect(mockClient.PUT).toHaveBeenCalledWith(
      expect.stringContaining("review-preference"),
      expect.objectContaining({
        body: expect.objectContaining({
          state: "later",
          expectedRevision: zeroRevision,
          excludedItemIds: [],
        }),
      }),
    ),
  );
  await userEvent.click(
    within(screen.getByRole("navigation", { name: "Review state" })).getByRole(
      "button",
      { name: "Later" },
    ),
  );
  await screen.findByRole("button", { name: "Review details" });
  expect(
    mockClient.GET.mock.calls.some(
      ([path, options]) =>
        path.endsWith("/suggestions") &&
        options.params.query.review === "later",
    ),
  ).toBe(true);
  await userEvent.click(screen.getByRole("button", { name: "Review details" }));
  await screen.findByText(/Current choice: later/);
  await userEvent.click(screen.getByRole("button", { name: "Reopen" }));
  await waitFor(() =>
    expect(mockClient.PUT).toHaveBeenCalledWith(
      expect.stringContaining("review-preference"),
      expect.objectContaining({
        body: expect.objectContaining({
          state: "active",
          expectedRevision: revision.id,
        }),
      }),
    ),
  );
});

it("saves selected eligible exclusions with the complete set and can include them again", async () => {
  let excludedItemIds: string[] = [];
  let currentRevision = zeroRevision;
  const originalGet = mockClient.GET.getMockImplementation()!;
  mockClient.GET.mockImplementation(async (path: string, options: unknown) => {
    if (path.endsWith("/review-preference"))
      return ok({
        attemptId,
        state: "active",
        revision: currentRevision,
        excludedItemIds,
      });
    return originalGet(path, options);
  });
  mockClient.PUT.mockImplementation(
    async (
      _path: string,
      options: {
        body: { expectedRevision: string; excludedItemIds: string[] };
      },
    ) => {
      expect(options.body.expectedRevision).toBe(currentRevision);
      excludedItemIds = options.body.excludedItemIds;
      currentRevision = revision.id;
      return ok({
        attemptId,
        state: "active",
        revision: currentRevision,
        excludedItemIds,
      });
    },
  );
  renderWithQueryClient(<ClassificationWorkspace />);
  await userEvent.click(
    await screen.findByRole("button", { name: "Review details" }),
  );
  await screen.findByText("Low Fat Milk");
  await userEvent.click(
    screen.getByRole("checkbox", { name: /Select Whole Milk/i }),
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Exclude selected from assistance" }),
  );
  await waitFor(() => expect(excludedItemIds).toEqual([itemA]));
  await userEvent.click(
    await screen.findByRole("checkbox", { name: `Excluded item ${itemA}` }),
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Include selected excluded items" }),
  );
  await waitFor(() => expect(excludedItemIds).toEqual([]));
  expect(mockClient.PUT).toHaveBeenCalledTimes(2);
});

it("preserves older exclusions when adding an item and changing review state", async () => {
  const olderItemId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
  let excludedItemIds = [olderItemId];
  let currentRevision = zeroRevision;
  const originalGet = mockClient.GET.getMockImplementation()!;
  mockClient.GET.mockImplementation(async (path: string, options: unknown) => {
    if (path.endsWith("/review-preference"))
      return ok({
        attemptId,
        state: "active",
        revision: currentRevision,
        excludedItemIds,
      });
    return originalGet(path, options);
  });
  mockClient.PUT.mockImplementation(
    async (
      _path: string,
      options: {
        body: {
          state: string;
          expectedRevision: string;
          excludedItemIds: string[];
        };
      },
    ) => {
      expect(options.body.expectedRevision).toBe(currentRevision);
      excludedItemIds = options.body.excludedItemIds;
      currentRevision = crypto.randomUUID();
      return ok({
        attemptId,
        state: options.body.state,
        revision: currentRevision,
        excludedItemIds,
      });
    },
  );
  renderWithQueryClient(<ClassificationWorkspace />);
  await userEvent.click(
    await screen.findByRole("button", { name: "Review details" }),
  );
  await screen.findByRole("checkbox", { name: `Excluded item ${olderItemId}` });
  await userEvent.click(
    screen.getByRole("checkbox", { name: /Select Whole Milk/i }),
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Exclude selected from assistance" }),
  );
  await waitFor(() => expect(excludedItemIds).toEqual([olderItemId, itemA]));
  await userEvent.click(screen.getByRole("button", { name: "Leave as is" }));
  await waitFor(() => expect(mockClient.PUT).toHaveBeenCalledTimes(2));
  expect(mockClient.PUT.mock.calls[1][1].body).toMatchObject({
    state: "left_as_is",
    excludedItemIds: [olderItemId, itemA],
  });
});

it("retries uncertain preference writes with the same operation and blocks a revision conflict", async () => {
  mockClient.PUT.mockResolvedValueOnce(
    fail(503, "Temporary failure"),
  ).mockResolvedValueOnce(fail(409, "Revision changed"));
  renderWithQueryClient(<ClassificationWorkspace />);
  await userEvent.click(
    await screen.findByRole("button", { name: "Review details" }),
  );
  await screen.findByText(/Current choice: active/);
  await userEvent.click(screen.getByRole("button", { name: "Leave as is" }));
  expect(
    await screen.findByRole("button", { name: "Retry preference save" }),
  ).toBeInTheDocument();
  await userEvent.click(
    screen.getByRole("button", { name: "Retry preference save" }),
  );
  await waitFor(() => expect(mockClient.PUT).toHaveBeenCalledTimes(2));
  expect(mockClient.PUT.mock.calls[0][1].body).toEqual(
    mockClient.PUT.mock.calls[1][1].body,
  );
  expect(
    await screen.findByText(/Review preference changed/),
  ).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Leave as is" })).toBeDisabled();
});

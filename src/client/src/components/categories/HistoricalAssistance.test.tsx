import {
  act,
  fireEvent,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import {
  createQueryClient,
  renderWithQueryClient as renderWithQueryClientBase,
} from "@/test/test-utils";
import { QueryClientProvider } from "@tanstack/react-query";
import mockClient, { resetMockClient } from "@/test/mock-api-client";
import { HistoricalAssistance } from "./HistoricalAssistance";
import { AuthContext } from "@/contexts/auth-context";

// Historical assistance is authenticated; retain the established fixture actor
// so persisted-intent assertions exercise exactly the same storage key.
const renderWithQueryClient: typeof renderWithQueryClientBase = (ui, options) =>
  renderWithQueryClientBase(ui, {
    ...options,
    auth: {
      user: {
        userId: "anonymous",
        email: "test@example.com",
        roles: [],
        mustResetPassword: false,
      },
      ...options?.auth,
    },
  });

const savedKey = (actor = "anonymous") =>
  `receipts:historical-assistance-intent:${actor}`;
const savedBody = () => ({
  operationId: "old-exact-id",
  taxonomyRevisionId: "old-rev",
  groups: [row("Original").group],
});

vi.mock("@/lib/api-client", () => ({ default: mockClient }));

const row = (name: string, purchaseCount = 3) => ({
  group: { store: "Shop", rawDescription: name, itemCode: null },
  purchaseCount,
  impactAmount: 0,
  impactCurrency: "USD" as const,
  conflict: false,
  migratedBroad: false,
  unresolved: false,
  unassigned: false,
  currentCategories: [],
  disposition: null,
  dispositionVersion: 0,
});
const progress = (operationId = "op-1") => ({
  operationId,
  createdAt: "2026-10-26T00:00:00Z",
  groupCount: 1,
  itemCount: 3,
  state: "ready",
  groups: [
    {
      group: row("Milk").group,
      itemCount: 3,
      attemptIds: ["attempt-1"],
      states: ["suggested"],
      failureReasons: [null as string | null],
    },
  ],
});
const success = (data: unknown) => ({ data, error: undefined });
const ready = {
  capability: "ready",
  consentEnabled: true,
  suggestionsAvailable: true,
  dailyLimit: 20,
  pendingLimit: 5,
};

beforeEach(() => {
  sessionStorage.clear();
  resetMockClient();
  mockClient.GET.mockImplementation((path: string) => {
    if (path === "/api/category-assistance/status")
      return Promise.resolve(success(ready));
    if (path === "/api/category-assistance/historical-requests")
      return Promise.resolve(success({ total: 0, items: [] }));
    if (path === "/api/category-assistance/historical-requests/{operationId}")
      return Promise.resolve({ error: { status: 404, detail: "Not found" } });
    throw new Error(path);
  });
  mockClient.POST.mockResolvedValue(success(progress()));
});

it.each([
  {
    capability: "unavailable",
    consentEnabled: true,
    suggestionsAvailable: true,
  },
  { capability: "ready", consentEnabled: false, suggestionsAvailable: false },
  { capability: "unknown", consentEnabled: true, suggestionsAvailable: false },
])(
  "hides assistance when capability or consent disallows it",
  async (status) => {
    mockClient.GET.mockResolvedValue(success(status));
    renderWithQueryClient(
      <HistoricalAssistance selected={[row("Milk")]} revisionId="rev" />,
    );
    await waitFor(() => expect(mockClient.GET).toHaveBeenCalled());
    expect(
      screen.queryByRole("region", { name: "Historical suggestions" }),
    ).not.toBeInTheDocument();
    expect(mockClient.POST).not.toHaveBeenCalled();
  },
);

it("shows limits and blocks a group over 100 purchases", async () => {
  renderWithQueryClient(
    <HistoricalAssistance selected={[row("Huge", 101)]} revisionId="rev" />,
  );
  expect(
    await screen.findByText(
      /20 suggestions per day and 5 awaiting review or processing/,
    ),
  ).toBeInTheDocument();
  expect(screen.getByRole("alert")).toHaveTextContent(
    "more than 100 purchases",
  );
  expect(
    screen.getByRole("button", { name: "Request suggestions" }),
  ).toBeDisabled();
});

it("requires published limits and blocks more than 25 selected groups", async () => {
  mockClient.GET.mockImplementation((path: string) => {
    if (path === "/api/category-assistance/status")
      return Promise.resolve(success({ ...ready, dailyLimit: undefined }));
    if (path === "/api/category-assistance/historical-requests")
      return Promise.resolve(success({ total: 0, items: [] }));
    throw new Error(path);
  });
  const { rerender } = renderWithQueryClient(
    <HistoricalAssistance selected={[row("Milk")]} revisionId="rev" />,
  );
  expect(
    await screen.findByText(/Configured limits unavailable/),
  ).toBeInTheDocument();
  expect(
    screen.getByRole("button", { name: "Request suggestions" }),
  ).toBeDisabled();
  mockClient.GET.mockImplementation((path: string) => {
    if (path === "/api/category-assistance/status")
      return Promise.resolve(success(ready));
    if (path === "/api/category-assistance/historical-requests")
      return Promise.resolve(success({ total: 0, items: [] }));
    throw new Error(path);
  });
  await userEvent.click(
    screen.getByRole("button", { name: "Refresh capability status" }),
  );
  await screen.findByText(/20 suggestions per day/);
  rerender(
    <HistoricalAssistance
      selected={Array.from({ length: 26 }, (_, i) => row(`Group ${i}`))}
      revisionId="rev"
    />,
  );
  expect(
    screen.getByRole("button", { name: "Request suggestions" }),
  ).toBeDisabled();
  expect(mockClient.POST).not.toHaveBeenCalled();
});

it("retries the identical bounded payload after an uncertain failure", async () => {
  mockClient.POST.mockResolvedValueOnce({
    error: { detail: "Connection lost" },
  }).mockResolvedValueOnce(success(progress()));
  const { rerender } = renderWithQueryClient(
    <HistoricalAssistance selected={[row("Milk")]} revisionId="rev" />,
  );
  await screen.findByText(/20 suggestions per day/);
  await userEvent.click(
    screen.getByRole("button", { name: "Request suggestions" }),
  );
  expect(
    await screen.findByText(/Receipt of this request is not confirmed/),
  ).toBeInTheDocument();
  rerender(
    <HistoricalAssistance
      selected={[row("Milk"), row("Bread")]}
      revisionId="new-rev"
    />,
  );
  await userEvent.click(screen.getByRole("button", { name: "Retry safely" }));
  await waitFor(() => expect(mockClient.POST).toHaveBeenCalledTimes(2));
  const first = mockClient.POST.mock.calls[0][1].body;
  expect(first.groups).toEqual([row("Milk").group]);
  expect(first.taxonomyRevisionId).toBe("rev");
  expect(mockClient.POST.mock.calls[1][1].body).toEqual(first);
});

it("releases a definitively rejected request so a changed selection can be submitted in the same tab", async () => {
  mockClient.POST.mockResolvedValueOnce({
    error: {
      status: 409,
      detail: "Historical request exceeds available capacity of 20 purchases.",
    },
    response: new Response(null, { status: 409 }),
  }).mockResolvedValueOnce(success(progress("replacement-operation")));
  const { rerender } = renderWithQueryClient(
    <HistoricalAssistance
      selected={Array.from({ length: 22 }, (_, i) => row(`Group ${i}`))}
      revisionId="rev"
    />,
  );
  await screen.findByText(/20 suggestions per day/);
  await userEvent.click(
    screen.getByRole("button", { name: "Request suggestions" }),
  );
  expect(await screen.findByRole("alert")).toHaveTextContent(/20 purchases/);
  await waitFor(() => expect(sessionStorage.length).toBe(0));
  expect(
    screen.queryByRole("button", { name: "Retry safely" }),
  ).not.toBeInTheDocument();

  rerender(
    <HistoricalAssistance selected={[row("Bread")]} revisionId="new-rev" />,
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Request suggestions" }),
  );
  await waitFor(() => expect(mockClient.POST).toHaveBeenCalledTimes(2));
  const first = mockClient.POST.mock.calls[0][1].body;
  const replacement = mockClient.POST.mock.calls[1][1].body;
  expect(replacement.operationId).not.toBe(first.operationId);
  expect(replacement.groups).toEqual([row("Bread").group]);
  expect(replacement.taxonomyRevisionId).toBe("new-rev");
});

it("keeps the original operation after a lost connection and prevents duplicate submissions during retry", async () => {
  let acknowledge!: (value: ReturnType<typeof success>) => void;
  mockClient.POST.mockRejectedValueOnce(
    new TypeError("Failed to fetch"),
  ).mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        acknowledge = resolve;
      }),
  );
  const { rerender } = renderWithQueryClient(
    <HistoricalAssistance selected={[row("Milk")]} revisionId="rev" />,
  );
  await screen.findByText(/20 suggestions per day/);
  await userEvent.click(
    screen.getByRole("button", { name: "Request suggestions" }),
  );
  await screen.findByText(/Receipt of this request is not confirmed/);
  const original = mockClient.POST.mock.calls[0][1].body;
  expect(sessionStorage.length).toBe(1);
  rerender(
    <HistoricalAssistance selected={[row("Bread")]} revisionId="changed" />,
  );
  const retry = screen.getByRole("button", { name: "Retry safely" });
  await userEvent.dblClick(retry);
  expect(mockClient.POST).toHaveBeenCalledTimes(2);
  expect(mockClient.POST.mock.calls[1][1].body).toEqual(original);
  expect(retry).toBeDisabled();
  await act(async () => acknowledge(success(progress(original.operationId))));
  await waitFor(() => expect(sessionStorage.length).toBe(0));
  expect(
    screen.getByRole("button", { name: "Request suggestions" }),
  ).toBeEnabled();
});

it("explains a 20-purchase capacity rejection and allows recovery without retrying the rejected operation", async () => {
  mockClient.POST.mockResolvedValueOnce({
    error: {
      status: 409,
      detail: "Historical request exceeds available capacity of 20 purchases.",
    },
    response: new Response(null, { status: 409 }),
  });
  renderWithQueryClient(
    <HistoricalAssistance selected={[row("Milk", 21)]} revisionId="rev" />,
  );
  await screen.findByText(/20 suggestions per day/);
  await userEvent.click(
    screen.getByRole("button", { name: "Request suggestions" }),
  );
  const alert = await screen.findByRole("alert");
  expect(alert).toHaveTextContent(/20 purchases/);
  expect(alert).toHaveTextContent(
    /smaller|reduce|fewer|batch|try again later/i,
  );
  expect(alert).not.toHaveTextContent(/retrying the same request/i);
  expect(
    screen.getByRole("button", { name: "Request suggestions" }),
  ).toBeEnabled();
});

it("restores an uncertain request after reload and cannot expand its selected groups", async () => {
  mockClient.POST.mockResolvedValueOnce({
    error: { detail: "Connection lost" },
  }).mockResolvedValueOnce(success(progress()));
  const firstMount = renderWithQueryClient(
    <HistoricalAssistance selected={[row("Milk")]} revisionId="rev" />,
  );
  await screen.findByText(/20 suggestions per day/);
  await userEvent.click(
    screen.getByRole("button", { name: "Request suggestions" }),
  );
  await screen.findByText(/Receipt of this request is not confirmed/);
  const original = mockClient.POST.mock.calls[0][1].body;
  firstMount.unmount();

  renderWithQueryClient(
    <HistoricalAssistance
      selected={[row("Milk"), row("Bread")]}
      revisionId="new-rev"
    />,
  );
  await screen.findByText(/20 suggestions per day/);
  expect(
    screen.queryByRole("button", { name: "Request suggestions" }),
  ).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Retry safely" }));
  await waitFor(() => expect(mockClient.POST).toHaveBeenCalledTimes(2));
  expect(mockClient.POST.mock.calls[1][1].body).toEqual(original);
  await waitFor(() => expect(sessionStorage.length).toBe(0));
});

it("does not submit when retry intent cannot be saved", async () => {
  const storage = vi
    .spyOn(Storage.prototype, "setItem")
    .mockImplementation(() => {
      throw new DOMException("Quota exceeded", "QuotaExceededError");
    });
  try {
    renderWithQueryClient(
      <HistoricalAssistance selected={[row("Milk")]} revisionId="rev" />,
    );
    await screen.findByText(/20 suggestions per day/);
    await userEvent.click(
      screen.getByRole("button", { name: "Request suggestions" }),
    );
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "could not be saved",
    );
    expect(mockClient.POST).not.toHaveBeenCalled();
  } finally {
    storage.mockRestore();
  }
});

it("recovers recent requests after reload and links available attempts", async () => {
  mockClient.GET.mockImplementation((path: string) => {
    if (path === "/api/category-assistance/status")
      return Promise.resolve(success(ready));
    if (path === "/api/category-assistance/historical-requests")
      return Promise.resolve(success({ total: 1, items: [progress()] }));
    if (path === "/api/category-assistance/historical-requests/{operationId}")
      return Promise.resolve(success(progress()));
    throw new Error(path);
  });
  renderWithQueryClient(
    <HistoricalAssistance selected={[]} revisionId="rev" />,
  );
  const recent = within(
    await screen.findByRole("region", { name: "Historical suggestions" }),
  );
  await userEvent.click(
    await recent.findByRole("button", {
      name: /Ready to review · 1 groups · 3 purchases/,
    }),
  );
  const detail = within(
    await screen.findByLabelText("Suggestion request progress"),
  );
  expect(
    await detail.findByText(/Milk.*3 purchases.*1 ready to review/),
  ).toBeInTheDocument();
  expect(
    detail.getByRole("link", { name: "Review suggestion" }),
  ).toHaveAttribute("href", "/classification?attempt=attempt-1");
  expect(recent.queryByText(/op-1/)).not.toBeInTheDocument();
});

it("shows sanitized failure reasons beside captured purchases and keeps manual actions available", async () => {
  const failed = progress();
  failed.state = "failed";
  failed.groups[0].states = ["failed", "suggested", "failed"];
  failed.groups[0].failureReasons = [
    "PayloadTooLarge",
    null,
    "unexpected provider detail",
  ];
  mockClient.GET.mockImplementation((path: string) => {
    if (path === "/api/category-assistance/status")
      return Promise.resolve(success(ready));
    if (path === "/api/category-assistance/historical-requests")
      return Promise.resolve(success({ total: 1, items: [failed] }));
    if (path === "/api/category-assistance/historical-requests/{operationId}")
      return Promise.resolve(success(failed));
    throw new Error(path);
  });
  renderWithQueryClient(
    <HistoricalAssistance selected={[row("Milk")]} revisionId="rev" />,
  );
  await userEvent.click(
    await screen.findByRole("button", {
      name: /Unable to complete · 1 groups/,
    }),
  );
  const detail = within(screen.getByLabelText("Suggestion request progress"));
  expect((await detail.findAllByRole("alert"))[0]).toBeInTheDocument();
  expect(
    detail.getByText("The category hierarchy is too large for this request.", {
      selector: "p",
    }),
  ).toBeInTheDocument();
  expect(
    detail.getByText("Category assistance could not complete this request.", {
      selector: "p",
    }),
  ).toBeInTheDocument();
  expect(
    detail.queryByText(/unexpected provider detail/),
  ).not.toBeInTheDocument();
  expect(
    screen.getByRole("button", { name: "Request suggestions" }),
  ).toBeEnabled();
});

it("finds an acknowledged saved operation by exact ID outside the recent 25", async () => {
  const body = savedBody();
  sessionStorage.setItem(savedKey(), JSON.stringify(body));
  mockClient.GET.mockImplementation(
    (
      path: string,
      options: { params?: { path?: { operationId: string } } },
    ) => {
      if (path.endsWith("/status")) return Promise.resolve(success(ready));
      if (path.endsWith("/{operationId}"))
        return Promise.resolve(
          success(progress(options.params!.path!.operationId)),
        );
      return Promise.resolve(
        success({
          total: 100,
          items: Array.from({ length: 25 }, (_, i) => progress(`other-${i}`)),
        }),
      );
    },
  );
  renderWithQueryClient(
    <HistoricalAssistance selected={[row("Changed")]} revisionId="changed" />,
  );
  expect(
    await screen.findByRole("button", { name: "Request suggestions" }),
  ).toBeEnabled();
  await screen.findByText("Request progress");
  await waitFor(() => expect(sessionStorage.getItem(savedKey())).toBeNull());
  expect(mockClient.GET).toHaveBeenCalledWith(
    "/api/category-assistance/historical-requests/{operationId}",
    expect.objectContaining({
      params: { path: { operationId: body.operationId } },
    }),
  );
  expect(mockClient.POST).not.toHaveBeenCalled();
});

it.each([401, 403, 404, 408, 429, 502])(
  "keeps an unknown original after replay receives HTTP %i",
  async (status) => {
    const body = savedBody();
    sessionStorage.setItem(savedKey(), JSON.stringify(body));
    mockClient.POST.mockResolvedValue({
      error: { status, detail: "Replay failed" },
      response: new Response(null, { status }),
    });
    renderWithQueryClient(
      <HistoricalAssistance selected={[row("Changed")]} revisionId="changed" />,
    );
    await screen.findByText(/earlier request has not appeared yet/);
    await userEvent.click(screen.getByRole("button", { name: "Retry safely" }));
    await screen.findByText(/Receipt of this request is not confirmed/);
    expect(mockClient.POST.mock.calls[0][1].body).toEqual(body);
    expect(JSON.parse(sessionStorage.getItem(savedKey())!)).toEqual(body);
    expect(
      screen.queryByRole("button", { name: "Request suggestions" }),
    ).not.toBeInTheDocument();
  },
);

it("shows a storage clearing failure after acknowledgement while preserving a safe saved identity", async () => {
  const remove = vi
    .spyOn(Storage.prototype, "removeItem")
    .mockImplementation(() => {
      throw new Error("blocked");
    });
  try {
    renderWithQueryClient(
      <HistoricalAssistance selected={[row("Milk")]} revisionId="rev" />,
    );
    await screen.findByText(/20 suggestions per day/);
    await userEvent.click(
      screen.getByRole("button", { name: "Request suggestions" }),
    );
    expect(
      await screen.findByText(
        /request was received, but browser storage could not be cleared/,
      ),
    ).toHaveAttribute("role", "alert");
    expect(JSON.parse(sessionStorage.getItem(savedKey())!)).toEqual(
      mockClient.POST.mock.calls[0][1].body,
    );
  } finally {
    remove.mockRestore();
  }
});

it("aggregates 56 purchase states and deduplicates reasons and review links", async () => {
  const report = progress();
  report.state = "partial";
  report.itemCount = 56;
  report.groups[0] = {
    ...report.groups[0],
    itemCount: 56,
    states: Array(56).fill("failed"),
    failureReasons: Array(56).fill("PayloadTooLarge"),
    attemptIds: ["attempt/1", "attempt/1"],
  };
  mockClient.POST.mockResolvedValue(success(report));
  mockClient.GET.mockImplementation((path: string) =>
    Promise.resolve(
      success(
        path.endsWith("/status")
          ? ready
          : path.endsWith("/{operationId}")
            ? report
            : { total: 0, items: [] },
      ),
    ),
  );
  renderWithQueryClient(
    <HistoricalAssistance selected={[row("Milk", 56)]} revisionId="rev" />,
  );
  await screen.findByText(/20 suggestions per day/);
  await userEvent.click(
    screen.getByRole("button", { name: "Request suggestions" }),
  );
  const detail = within(
    await screen.findByLabelText("Suggestion request progress"),
  );
  expect(await detail.findByRole("status")).toHaveTextContent("56 failed");
  expect(detail.getAllByRole("alert")).toHaveLength(1);
  await userEvent.click(detail.getByText("View groups and review suggestions"));
  expect(
    detail.getAllByText(
      "The category hierarchy is too large for this request.",
      { selector: "li" },
    ),
  ).toHaveLength(1);
  expect(detail.getAllByRole("link")).toHaveLength(1);
  expect(
    detail.getByRole("link", { name: "Review suggestion" }),
  ).toHaveAttribute("href", "/classification?attempt=attempt%2F1");
  expect(detail.getByRole("status").textContent!.length).toBeLessThan(40);
  expect(detail.queryByText(/op-1/)).not.toBeInTheDocument();
});

it("keeps saved retry intent isolated when the authenticated actor changes", async () => {
  sessionStorage.setItem(savedKey("alice"), JSON.stringify(savedBody()));
  const auth = (userId: string) => ({
    user: {
      userId,
      email: `${userId}@example.com`,
      roles: [],
      mustResetPassword: false,
    },
    isLoading: false,
    mustResetPassword: false,
    login: async () => {},
    logout: async () => {},
    changePassword: async () => {},
  });
  const { rerender } = renderWithQueryClient(
    <AuthContext.Provider value={auth("alice")}>
      <HistoricalAssistance selected={[row("Milk")]} revisionId="rev" />
    </AuthContext.Provider>,
  );
  await screen.findByRole("button", { name: "Retry safely" });
  rerender(
    <AuthContext.Provider value={auth("bob")}>
      <HistoricalAssistance selected={[row("Bread")]} revisionId="new" />
    </AuthContext.Provider>,
  );
  await screen.findByRole("button", { name: "Request suggestions" });
  await userEvent.click(
    screen.getByRole("button", { name: "Request suggestions" }),
  );
  expect(mockClient.POST.mock.calls[0][1].body.groups).toEqual([
    row("Bread").group,
  ]);
  expect(mockClient.POST.mock.calls[0][1].body.operationId).not.toBe(
    "old-exact-id",
  );
  expect(JSON.parse(sessionStorage.getItem(savedKey("alice"))!)).toEqual(
    savedBody(),
  );
});

it("blocks concurrent initial submissions before acknowledgement", async () => {
  let resolve!: (value: ReturnType<typeof success>) => void;
  mockClient.POST.mockImplementation(
    () =>
      new Promise((ack) => {
        resolve = ack;
      }),
  );
  renderWithQueryClient(
    <HistoricalAssistance selected={[row("Milk")]} revisionId="rev" />,
  );
  const button = await screen.findByRole("button", {
    name: "Request suggestions",
  });
  act(() => {
    fireEvent.click(button);
    fireEvent.click(button);
  });
  expect(mockClient.POST).toHaveBeenCalledTimes(1);
  expect(button).toBeDisabled();
  await act(async () => resolve(success(progress())));
});

it("polls pending progress and recent requests without repeating submission", async () => {
  const report = progress();
  report.state = "pending";
  report.groups[0].states = ["queued"];
  mockClient.GET.mockImplementation((path: string) =>
    Promise.resolve(
      success(
        path.endsWith("/status")
          ? ready
          : path.endsWith("/{operationId}")
            ? report
            : { total: 1, items: [report] },
      ),
    ),
  );
  const view = renderWithQueryClient(
    <HistoricalAssistance selected={[]} revisionId="rev" />,
  );
  await userEvent.click(
    await screen.findByRole("button", { name: /Processing · 1 groups/ }),
  );
  await screen.findByText("1 in progress");
  const count = (suffix: string) =>
    mockClient.GET.mock.calls.filter(([path]) => path.endsWith(suffix)).length;
  const recentBefore = count("/historical-requests");
  const progressBefore = count("/{operationId}");
  vi.useFakeTimers();
  try {
    // Re-render under the fake clock so query intervals also use that clock.
    view.unmount();
    const mounted = renderWithQueryClient(
      <HistoricalAssistance selected={[]} revisionId="rev" />,
    );
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1);
    });
    fireEvent.click(
      screen.getByRole("button", { name: /Processing · 1 groups/ }),
    );
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1);
    });
    const newRecent = count("/historical-requests");
    const newProgress = count("/{operationId}");
    await act(async () => {
      await vi.advanceTimersByTimeAsync(5_000);
    });
    expect(count("/{operationId}")).toBeGreaterThan(newProgress);
    expect(count("/historical-requests")).toBe(newRecent);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(10_000);
    });
    expect(count("/historical-requests")).toBeGreaterThan(newRecent);
    expect(newRecent).toBeGreaterThan(recentBefore);
    expect(newProgress).toBeGreaterThan(progressBefore);
    expect(mockClient.POST).not.toHaveBeenCalled();
    mounted.unmount();
  } finally {
    vi.useRealTimers();
  }
});

it("shows unreadable saved retry storage and prevents a potentially duplicate submission", async () => {
  const read = vi
    .spyOn(Storage.prototype, "getItem")
    .mockImplementation((key) => {
      if (key === savedKey())
        throw new DOMException("Blocked", "SecurityError");
      return null;
    });
  try {
    renderWithQueryClient(
      <HistoricalAssistance selected={[row("Milk")]} revisionId="rev" />,
    );
    await screen.findByText(/20 suggestions per day/);
    expect(screen.getByRole("alert")).toHaveTextContent(/storage|saved|retry/i);
    expect(
      screen.getByRole("button", { name: "Request suggestions" }),
    ).toBeDisabled();
    expect(mockClient.POST).not.toHaveBeenCalled();
  } finally {
    read.mockRestore();
  }
});

it.each(["{broken-json", JSON.stringify({ operationId: "lost-body" })])(
  "shows corrupt saved intent without silently replacing it: %s",
  async (raw) => {
    sessionStorage.setItem(savedKey(), raw);
    renderWithQueryClient(
      <HistoricalAssistance selected={[row("Milk")]} revisionId="rev" />,
    );
    await screen.findByText(/20 suggestions per day/);
    expect(screen.getByRole("alert")).toHaveTextContent(/storage|saved|retry/i);
    expect(
      screen.getByRole("button", { name: "Request suggestions" }),
    ).toBeDisabled();
    expect(sessionStorage.getItem(savedKey())).toBe(raw);
    expect(mockClient.POST).not.toHaveBeenCalled();
  },
);

it("reports cleanup failure when direct recovery confirms the saved operation", async () => {
  sessionStorage.setItem(savedKey(), JSON.stringify(savedBody()));
  const remove = vi
    .spyOn(Storage.prototype, "removeItem")
    .mockImplementation(() => {
      throw new Error("Blocked");
    });
  mockClient.GET.mockImplementation((path: string) =>
    Promise.resolve(
      success(
        path.endsWith("/status")
          ? ready
          : path.endsWith("/{operationId}")
            ? progress("old-exact-id")
            : { total: 0, items: [] },
      ),
    ),
  );
  try {
    renderWithQueryClient(
      <HistoricalAssistance selected={[row("Milk")]} revisionId="rev" />,
    );
    await screen.findByText("Request progress");
    expect(await screen.findByRole("alert")).toHaveTextContent(
      /storage.*clear|clear.*storage/i,
    );
    expect(JSON.parse(sessionStorage.getItem(savedKey())!)).toEqual(
      savedBody(),
    );
    expect(mockClient.POST).not.toHaveBeenCalled();
  } finally {
    remove.mockRestore();
  }
});

const unavailableStatuses = [
  {
    name: "status 503",
    result: { error: { status: 503, detail: "Status unavailable" } },
  },
  {
    name: "provider absent",
    result: success({
      ...ready,
      capability: "absent",
      suggestionsAvailable: false,
    }),
  },
  {
    name: "consent disabled",
    result: success({
      ...ready,
      consentEnabled: false,
      suggestionsAvailable: false,
    }),
  },
];

it.each(unavailableStatuses)(
  "recovers a saved receipt by exact ID during $name and keeps new sends disabled",
  async ({ result }) => {
    const body = savedBody();
    sessionStorage.setItem(savedKey(), JSON.stringify(body));
    mockClient.GET.mockImplementation((path: string) =>
      Promise.resolve(
        path.endsWith("/status")
          ? result
          : path.endsWith("/{operationId}")
            ? success(progress(body.operationId))
            : success({ total: 0, items: [] }),
      ),
    );
    renderWithQueryClient(
      <HistoricalAssistance
        selected={[row("New selection")]}
        revisionId="new"
      />,
    );
    await screen.findByText("Request progress");
    expect(await screen.findByText("1 ready to review")).toHaveAttribute(
      "role",
      "status",
    );
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/category-assistance/historical-requests/{operationId}",
      expect.objectContaining({
        params: { path: { operationId: body.operationId } },
      }),
    );
    expect(
      screen.queryByRole("button", { name: "Request suggestions" }),
    ).not.toBeInTheDocument();
    expect(mockClient.POST).not.toHaveBeenCalled();
  },
);

it.each(unavailableStatuses)(
  "keeps an unresolved saved receipt visible during $name without starting another send",
  async ({ result }) => {
    const body = savedBody();
    sessionStorage.setItem(savedKey(), JSON.stringify(body));
    mockClient.GET.mockImplementation((path: string) =>
      Promise.resolve(
        path.endsWith("/status")
          ? result
          : path.endsWith("/{operationId}")
            ? { error: { status: 404, detail: "Not found" } }
            : success({ total: 0, items: [] }),
      ),
    );
    renderWithQueryClient(
      <HistoricalAssistance
        selected={[row("New selection")]}
        revisionId="new"
      />,
    );
    await screen.findByText(/earlier request has not appeared yet/);
    expect(
      screen.getByRole("region", { name: "Historical suggestions" }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "Request suggestions" }),
    ).not.toBeInTheDocument();
    expect(JSON.parse(sessionStorage.getItem(savedKey())!)).toEqual(body);
    expect(mockClient.POST).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: "Retry safely" }));
    expect(mockClient.POST).toHaveBeenCalledTimes(1);
    expect(mockClient.POST.mock.calls[0][1].body).toEqual(body);
  },
);

it.each(unavailableStatuses)(
  "keeps active progress readable after $name and disables new sends",
  async ({ result }) => {
    const queryClient = createQueryClient();
    let unavailable = false;
    mockClient.GET.mockImplementation((path: string) =>
      Promise.resolve(
        path.endsWith("/status")
          ? unavailable
            ? result
            : success(ready)
          : path.endsWith("/{operationId}")
            ? success(progress())
            : success({ total: 0, items: [] }),
      ),
    );
    renderWithQueryClient(
      <QueryClientProvider client={queryClient}>
        <HistoricalAssistance selected={[row("Milk")]} revisionId="rev" />
      </QueryClientProvider>,
    );
    await screen.findByText(/20 suggestions per day/);
    await userEvent.click(
      screen.getByRole("button", { name: "Request suggestions" }),
    );
    await screen.findByText("1 ready to review");
    unavailable = true;
    await act(async () => {
      await queryClient.invalidateQueries({
        queryKey: ["category-assistance", "status", "anonymous"],
      });
    });
    expect(
      screen.getByLabelText("Suggestion request progress"),
    ).toHaveTextContent("1 ready to review");
    await waitFor(() =>
      expect(
        screen.queryByRole("button", { name: "Request suggestions" }),
      ).not.toBeInTheDocument(),
    );
    const before = mockClient.GET.mock.calls.filter(([path]) =>
      path.endsWith("/{operationId}"),
    ).length;
    await act(async () => {
      await queryClient.invalidateQueries({
        queryKey: [
          "category-assistance",
          "historical-request",
          "anonymous",
          "op-1",
        ],
      });
    });
    expect(
      mockClient.GET.mock.calls.filter(([path]) =>
        path.endsWith("/{operationId}"),
      ).length,
    ).toBeGreaterThan(before);
    expect(mockClient.POST).toHaveBeenCalledTimes(1);
  },
);

it.each(unavailableStatuses)(
  "keeps assistance hidden with no saved or active operation during $name",
  async ({ result }) => {
    mockClient.GET.mockResolvedValue(result);
    renderWithQueryClient(
      <HistoricalAssistance selected={[row("Milk")]} revisionId="rev" />,
    );
    await waitFor(() => expect(mockClient.GET).toHaveBeenCalled());
    expect(
      screen.queryByRole("region", { name: "Historical suggestions" }),
    ).not.toBeInTheDocument();
    expect(
      mockClient.GET.mock.calls.every(([path]) => path.endsWith("/status")),
    ).toBe(true);
    expect(mockClient.POST).not.toHaveBeenCalled();
  },
);

it("keeps corrupt retry storage visible when provider status is absent", async () => {
  sessionStorage.setItem(savedKey(), "{broken");
  mockClient.GET.mockResolvedValue(
    success({ ...ready, capability: "absent", suggestionsAvailable: false }),
  );
  renderWithQueryClient(
    <HistoricalAssistance selected={[row("Milk")]} revisionId="rev" />,
  );
  expect(await screen.findByRole("alert")).toHaveTextContent(
    /storage|saved|retry/i,
  );
  expect(
    screen.queryByRole("button", { name: "Request suggestions" }),
  ).not.toBeInTheDocument();
  expect(mockClient.POST).not.toHaveBeenCalled();
});

it("does not expose authenticated assistance or read saved requests after sign out", async () => {
  sessionStorage.setItem(savedKey(), JSON.stringify(savedBody()));
  renderWithQueryClientBase(
    <HistoricalAssistance selected={[row("Milk")]} revisionId="rev" />,
  );
  expect(
    screen.queryByRole("region", { name: "Historical suggestions" }),
  ).not.toBeInTheDocument();
  expect(mockClient.GET).not.toHaveBeenCalled();
  expect(mockClient.POST).not.toHaveBeenCalled();
});

it("hides saved Jev request controls while configuration loads and when unavailable", async () => {
  const body = savedBody();
  sessionStorage.setItem(savedKey(), JSON.stringify(body));
  let finish!: (value: ReturnType<typeof success>) => void;
  mockClient.GET.mockImplementation(
    () =>
      new Promise((resolve) => {
        finish = resolve;
      }),
  );
  renderWithQueryClient(
    <HistoricalAssistance selected={[row("Milk")]} revisionId="rev" />,
  );
  expect(
    screen.queryByRole("region", { name: "Historical suggestions" }),
  ).not.toBeInTheDocument();
  expect(mockClient.GET).toHaveBeenCalledTimes(1);
  await act(async () =>
    finish(
      success({
        ...ready,
        capability: "unavailable",
        suggestionsAvailable: false,
      }),
    ),
  );
  expect(
    screen.queryByRole("region", { name: "Historical suggestions" }),
  ).not.toBeInTheDocument();
  expect(mockClient.GET).toHaveBeenCalledTimes(1);
  expect(mockClient.POST).not.toHaveBeenCalled();
  expect(sessionStorage.getItem(savedKey())).toBe(JSON.stringify(body));
});

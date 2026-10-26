import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithQueryClient } from "@/test/test-utils";
import mockClient, { resetMockClient } from "@/test/mock-api-client";
import { HistoricalAssistance } from "./HistoricalAssistance";

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
      states: ["ready"],
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
      return Promise.resolve(success(progress()));
    throw new Error(path);
  });
  mockClient.POST.mockResolvedValue(success(progress()));
});

it.each([
  { capability: "absent", consentEnabled: true, suggestionsAvailable: true },
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
    await screen.findByText(/20 requests per day and 5 pending requests/),
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
  await screen.findByText(/20 requests per day/);
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
  await screen.findByText(/20 requests per day/);
  await userEvent.click(
    screen.getByRole("button", { name: "Request suggestions" }),
  );
  expect(
    await screen.findByText(/Check recent requests before retrying/),
  ).toBeInTheDocument();
  rerender(
    <HistoricalAssistance
      selected={[row("Milk"), row("Bread")]}
      revisionId="new-rev"
    />,
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Retry request suggestions" }),
  );
  await waitFor(() => expect(mockClient.POST).toHaveBeenCalledTimes(2));
  const first = mockClient.POST.mock.calls[0][1].body;
  expect(first.groups).toEqual([row("Milk").group]);
  expect(first.taxonomyRevisionId).toBe("rev");
  expect(mockClient.POST.mock.calls[1][1].body).toEqual(first);
});

it("restores an uncertain request after reload and cannot expand its selected groups", async () => {
  mockClient.POST.mockResolvedValueOnce({
    error: { detail: "Connection lost" },
  }).mockResolvedValueOnce(success(progress()));
  const firstMount = renderWithQueryClient(
    <HistoricalAssistance selected={[row("Milk")]} revisionId="rev" />,
  );
  await screen.findByText(/20 requests per day/);
  await userEvent.click(
    screen.getByRole("button", { name: "Request suggestions" }),
  );
  await screen.findByText(/Check recent requests before retrying/);
  const original = mockClient.POST.mock.calls[0][1].body;
  firstMount.unmount();

  renderWithQueryClient(
    <HistoricalAssistance
      selected={[row("Milk"), row("Bread")]}
      revisionId="new-rev"
    />,
  );
  await screen.findByText(/20 requests per day/);
  expect(
    screen.queryByRole("button", { name: "Request suggestions" }),
  ).not.toBeInTheDocument();
  await userEvent.click(
    screen.getByRole("button", { name: "Retry request suggestions" }),
  );
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
    await screen.findByText(/20 requests per day/);
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
      name: /ready · 1 groups · 3 purchases · op-1/,
    }),
  );
  const detail = within(
    await screen.findByLabelText("Suggestion request progress"),
  );
  expect(
    await detail.findByText(/Milk.*3 purchases.*ready/),
  ).toBeInTheDocument();
  expect(
    detail.getByRole("link", { name: "Review suggestion attempt-1" }),
  ).toHaveAttribute("href", "/classification?attempt=attempt-1");
  await userEvent.click(
    detail.getByRole("button", { name: "Refresh suggestion progress" }),
  );
  await waitFor(() =>
    expect(
      mockClient.GET.mock.calls.filter(
        ([path]) =>
          path === "/api/category-assistance/historical-requests/{operationId}",
      ),
    ).toHaveLength(2),
  );
});

it("shows sanitized failure reasons beside captured purchases and keeps manual actions available", async () => {
  const failed = progress();
  failed.state = "failed";
  failed.groups[0].states = ["failed", "ready", "failed"];
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
    await screen.findByRole("button", { name: /failed · 1 groups/ }),
  );
  const detail = within(screen.getByLabelText("Suggestion request progress"));
  expect(
    await detail.findByText(/Purchase 1: The category hierarchy is too large/),
  ).toBeInTheDocument();
  expect(
    detail.getByText(/Purchase 3: Category assistance could not complete/),
  ).toBeInTheDocument();
  expect(
    detail.queryByText(/unexpected provider detail/),
  ).not.toBeInTheDocument();
  expect(
    screen.getByRole("button", { name: "Request suggestions" }),
  ).toBeEnabled();
});

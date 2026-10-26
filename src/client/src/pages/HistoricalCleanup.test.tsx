import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithQueryClient } from "@/test/test-utils";
import mockClient, { resetMockClient } from "@/test/mock-api-client";
import HistoricalCleanup from "./HistoricalCleanup";

vi.mock("@/lib/api-client", () => ({ default: mockClient }));
vi.mock("@/components/categories/CategoryPathPicker", () => ({
  CategoryPathPicker: ({
    onValueChange,
    ...props
  }: {
    onValueChange: (value: unknown) => void;
    "aria-label"?: string;
  }) => (
    <button
      aria-label={props["aria-label"] ?? "Bulk destination"}
      onClick={() =>
        onValueChange({
          categoryId: "milk",
          taxonomyRevisionId: "rev",
          path: [{ id: "milk", name: "Milk" }],
        })
      }
    >
      Choose Milk
    </button>
  ),
}));

const row = (name: string) => ({
  group: { store: "Shop", rawDescription: name, itemCode: null },
  purchaseCount: 3,
  conflict: false,
  migratedBroad: true,
  unresolved: false,
  unassigned: false,
  currentCategories: [
    { categoryId: "dairy", path: "Grocery / Dairy", purchaseCount: 3 },
  ],
  disposition: null,
  dispositionVersion: 0,
});
const progress = (state = "preview", pending = 3) => ({
  operationId: "op-1",
  state,
  total: 3,
  applied: 3 - pending,
  conflicted: 0,
  skipped: 0,
  pending,
  undone: 0,
  undoConflicted: 0,
  undoSkipped: 0,
});
const summary = (state = "preview", pending = 3) => ({
  operationId: "op-1",
  state,
  capturedAt: "2026-10-26T00:00:00Z",
  taxonomyRevisionId: "rev",
  reason: "Saved reason",
  groups: [{ group: row("Dairy").group, targetCategoryId: "milk" }],
  progress: progress(state, pending),
});
const success = (data: unknown) => ({
  data,
  error: undefined,
  response: new Response(null, { status: 200 }),
});
const failure = (detail: string) => ({
  data: undefined,
  error: { status: 503, detail },
  response: new Response(null, { status: 503 }),
});

beforeEach(() => {
  resetMockClient();
  mockClient.GET.mockImplementation(
    async (
      path: string,
      options?: { params?: { query?: { offset?: number } } },
    ) => {
      if (path === "/api/taxonomy") return success({ id: "rev", nodes: [] });
      if (path === "/api/historical-reclassifications/groups") {
        const offset = options?.params?.query?.offset ?? 0;
        return success(
          offset === 0
            ? {
                items: [row("Dairy"), row("Bread")],
                totalGroups: 3,
                nextOffset: 25,
              }
            : { items: [row("Eggs")], totalGroups: 3, nextOffset: null },
        );
      }
      if (path === "/api/historical-reclassifications")
        return success({ items: [], nextOffset: null });
      if (path === "/api/historical-reclassifications/{id}")
        return success(progress());
      if (path === "/api/historical-reclassifications/{id}/summary")
        return success(summary());
      if (path === "/api/historical-reclassifications/{id}/items")
        return success({ items: [], undoItems: [], nextOffset: null });
      throw Error(path);
    },
  );
  mockClient.POST.mockResolvedValue(success(progress()));
});

it("shows loading, a local error, and retry for groups", async () => {
  const original = mockClient.GET.getMockImplementation()!;
  let first = true;
  mockClient.GET.mockImplementation((path: string, options: unknown) => {
    if (path === "/api/historical-reclassifications/groups" && first) {
      first = false;
      return Promise.resolve(failure("Groups unavailable"));
    }
    return original(path, options);
  });
  renderWithQueryClient(<HistoricalCleanup />);
  expect(screen.getByText("Loading purchase groups…")).toBeInTheDocument();
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Groups unavailable",
  );
  await userEvent.click(screen.getByRole("button", { name: "Retry groups" }));
  expect(await screen.findByText("Dairy")).toBeInTheDocument();
});

it("selects only the visible page and sends explicit choices to preview", async () => {
  renderWithQueryClient(<HistoricalCleanup />);
  await screen.findByText("Dairy");
  await userEvent.click(
    screen.getByRole("checkbox", { name: "Select visible page" }),
  );
  expect(screen.getByText("Selected groups (2/25)")).toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Next" }));
  await screen.findByText("Eggs");
  expect(screen.queryByText("Selected groups (2/25)")).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Previous" }));
  await screen.findByText("Dairy");
  expect(
    screen.getByRole("checkbox", { name: "Select Dairy at Shop" }),
  ).not.toBeChecked();
  await userEvent.click(screen.getByRole("button", { name: "Next" }));
  await screen.findByText("Eggs");
  await userEvent.click(
    screen.getByRole("checkbox", { name: "Select Eggs at Shop" }),
  );
  expect(screen.getByText("Selected groups (1/25)")).toBeInTheDocument();
  await userEvent.click(
    screen.getByRole("button", { name: "Bulk destination" }),
  );
  await userEvent.type(
    screen.getByRole("textbox", { name: "Reason for reclassification" }),
    "Refine old groceries",
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Capture server preview" }),
  );
  await waitFor(() =>
    expect(mockClient.POST).toHaveBeenCalledWith(
      "/api/historical-reclassifications",
      expect.objectContaining({
        body: expect.objectContaining({
          reason: "Refine old groceries",
          groups: [
            expect.objectContaining({
              group: expect.objectContaining({ rawDescription: "Eggs" }),
              targetCategoryId: "milk",
            }),
          ],
        }),
      }),
    ),
  );
  expect(await screen.findByText("Captured operation")).toBeInTheDocument();
});

it("resumes a saved operation and shows partial progress before another bounded chunk", async () => {
  mockClient.GET.mockImplementation(async (path: string) => {
    if (path === "/api/taxonomy") return success({ id: "rev", nodes: [] });
    if (path === "/api/historical-reclassifications/groups")
      return success({ items: [], totalGroups: 0, nextOffset: null });
    if (path === "/api/historical-reclassifications")
      return success({ items: [], nextOffset: null });
    if (path === "/api/historical-reclassifications/{id}")
      return success(progress("applying", 2));
    if (path === "/api/historical-reclassifications/{id}/summary")
      return success(summary("applying", 2));
    if (path === "/api/historical-reclassifications/{id}/items")
      return success({ items: [], undoItems: [], nextOffset: null });
    throw Error(path);
  });
  mockClient.POST.mockResolvedValue(success(progress("completed", 0)));
  renderWithQueryClient(<HistoricalCleanup />, {
    route: "/categories/cleanup?operation=op-1",
  });
  const current = within(
    await screen.findByRole("region", { name: "Current operation" }),
  );
  expect(
    await current.findByText(/1 applied.*2 pending of 3/),
  ).toBeInTheDocument();
  expect(
    current.getByText(/Dairy.*milk.*current label unavailable/),
  ).toBeInTheDocument();
  await userEvent.click(
    current.getByRole("button", { name: "Apply next 100" }),
  );
  await waitFor(() =>
    expect(mockClient.POST).toHaveBeenCalledWith(
      "/api/historical-reclassifications/{id}/apply",
      expect.objectContaining({
        body: { limit: 100 },
        params: { path: { id: "op-1" } },
      }),
    ),
  );
});

it("blocks apply on an older deep link until captured choices load", async () => {
  const original = mockClient.GET.getMockImplementation()!;
  let finishSummary!: (value: ReturnType<typeof success>) => void;
  const pendingSummary = new Promise<ReturnType<typeof success>>((resolve) => {
    finishSummary = resolve;
  });
  mockClient.GET.mockImplementation((path: string, options: unknown) =>
    path === "/api/historical-reclassifications/{id}/summary"
      ? pendingSummary
      : original(path, options),
  );
  renderWithQueryClient(<HistoricalCleanup />, {
    route: "/categories/cleanup?operation=op-1",
  });
  const current = within(
    await screen.findByRole("region", { name: "Current operation" }),
  );
  expect(
    await current.findByRole("button", { name: "Apply next 100" }),
  ).toBeDisabled();
  expect(current.getByText("Loading captured choices…")).toBeInTheDocument();
  finishSummary(success(summary()));
  expect(
    await current.findByText(/Dairy.*milk.*current label unavailable/),
  ).toBeInTheDocument();
  expect(current.getByRole("button", { name: "Apply next 100" })).toBeEnabled();
  expect(mockClient.GET).toHaveBeenCalledWith(
    "/api/historical-reclassifications/{id}/summary",
    expect.objectContaining({ params: { path: { id: "op-1" } } }),
  );
});

it("retries a disposition with its original CAS version and operation ID", async () => {
  mockClient.PUT.mockResolvedValueOnce(
    failure("Connection lost"),
  ).mockResolvedValueOnce(
    success({
      group: row("Dairy").group,
      disposition: "later",
      version: 1,
      operationId: "saved",
      updatedAt: "2026-10-26T00:00:00Z",
    }),
  );
  renderWithQueryClient(<HistoricalCleanup />);
  const dairy = within((await screen.findByText("Dairy")).closest("li")!);
  await userEvent.click(dairy.getByRole("button", { name: "Later" }));
  expect(
    await dairy.findByRole("button", { name: "Retry Later" }),
  ).toBeInTheDocument();
  await userEvent.click(dairy.getByRole("button", { name: "Retry Later" }));
  await waitFor(() => expect(mockClient.PUT).toHaveBeenCalledTimes(2));
  const first = mockClient.PUT.mock.calls[0][1].body;
  const second = mockClient.PUT.mock.calls[1][1].body;
  expect(second).toEqual(first);
  expect(first.expectedVersion).toBe(0);
  expect(first.operationId).toBeTruthy();
});

it("allows a new selection after returning from a captured preview", async () => {
  renderWithQueryClient(<HistoricalCleanup />);
  await screen.findByText("Dairy");
  await userEvent.click(
    screen.getByRole("checkbox", { name: "Select Dairy at Shop" }),
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Bulk destination" }),
  );
  await userEvent.type(
    screen.getByRole("textbox", { name: "Reason for reclassification" }),
    "First batch",
  );
  await userEvent.click(
    screen.getByRole("button", { name: "Capture server preview" }),
  );
  await screen.findByText("Captured operation");
  await userEvent.click(screen.getByRole("button", { name: "Back to groups" }));
  expect(
    screen.getByRole("checkbox", { name: "Select Bread at Shop" }),
  ).toBeEnabled();
});

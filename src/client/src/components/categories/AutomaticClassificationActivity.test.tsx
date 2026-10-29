import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithQueryClient } from "@/test/test-utils";
import mockClient, { resetMockClient } from "@/test/mock-api-client";
import { AutomaticClassificationActivity } from "./AutomaticClassificationActivity";

vi.mock("@/lib/api-client", () => ({ default: mockClient }));
const operationId = "11111111-1111-4111-8111-111111111111";
const revisionId = "22222222-2222-4222-8222-222222222222";
const categoryId = "33333333-3333-4333-8333-333333333333";
const ok = (data: unknown) => ({
  data,
  error: undefined,
  response: { status: 200 },
});
const entry = {
  operationId,
  state: "completed",
  capturedAt: "2026-10-29T12:00:00Z",
  groups: [
    {
      group: { store: "Market", rawDescription: "Whole Milk" },
      targetCategoryId: categoryId,
    },
  ],
  progress: {
    total: 101,
    applied: 101,
    pending: 0,
    undone: 0,
    undoConflicted: 0,
    undoSkipped: 0,
  },
};
beforeEach(() => {
  resetMockClient();
  mockClient.GET.mockImplementation(async (path: string) =>
    ok(
      path === "/api/taxonomy"
        ? {
            id: revisionId,
            nodes: [{ id: categoryId, name: "Dairy", archived: false }],
          }
        : { items: [entry], nextOffset: null },
    ),
  );
});

it("loads on demand and undoes a batch without exposing identifiers or requesting a reason", async () => {
  const user = userEvent.setup();
  mockClient.POST.mockResolvedValueOnce(
    ok({ state: "undoing" }),
  ).mockResolvedValueOnce(ok({ state: "undone" }));
  renderWithQueryClient(<AutomaticClassificationActivity />);
  expect(mockClient.GET).not.toHaveBeenCalled();
  await user.click(screen.getByText("Automatic classification activity"));
  expect(
    await screen.findByText("Whole Milk at Market · Dairy"),
  ).toBeInTheDocument();
  expect(screen.queryByText(operationId)).not.toBeInTheDocument();
  await user.click(screen.getByRole("button", { name: "Undo" }));
  await waitFor(() => expect(mockClient.POST).toHaveBeenCalledTimes(2));
  expect(mockClient.POST).toHaveBeenLastCalledWith(
    "/api/historical-reclassifications/{id}/undo",
    expect.objectContaining({
      params: { path: { id: operationId } },
      body: {
        taxonomyRevisionId: revisionId,
        reason: "User undid automatic classification",
        limit: 100,
      },
    }),
  );
});

it("shows loading and an empty activity list", async () => {
  const user = userEvent.setup();
  let resolve!: (value: unknown) => void;
  mockClient.GET.mockImplementation((path: string) =>
    path === "/api/taxonomy"
      ? Promise.resolve(ok({ id: revisionId, nodes: [] }))
      : new Promise((done) => {
          resolve = done;
        }),
  );
  renderWithQueryClient(<AutomaticClassificationActivity />);
  await user.click(screen.getByText("Automatic classification activity"));
  expect(screen.getByRole("status")).toHaveTextContent("Loading activity");
  resolve(ok({ items: [], nextOffset: null }));
  expect(
    await screen.findByText("No automatic classifications yet."),
  ).toBeInTheDocument();
});

it("makes a failed activity request retryable", async () => {
  mockClient.GET.mockResolvedValue({
    error: { detail: "Unavailable" },
    data: undefined,
    response: { status: 503 },
  });
  renderWithQueryClient(<AutomaticClassificationActivity />);
  await userEvent.click(screen.getByText("Automatic classification activity"));
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Could not load activity",
  );
  mockClient.GET.mockResolvedValue(ok({ items: [], nextOffset: null }));
  await userEvent.click(screen.getByRole("button", { name: "Retry activity" }));
  expect(
    await screen.findByText("No automatic classifications yet."),
  ).toBeInTheDocument();
});

it("resumes an undo with its saved revision and keeps changed purchases visible", async () => {
  mockClient.GET.mockImplementation(async (path: string) =>
    ok(
      path === "/api/taxonomy"
        ? { id: "new-revision", nodes: [] }
        : {
            items: [
              {
                ...entry,
                state: "undoing",
                undoTaxonomyRevisionId: revisionId,
                progress: { ...entry.progress, undone: 99, undoConflicted: 1 },
              },
            ],
            nextOffset: null,
          },
    ),
  );
  mockClient.POST.mockResolvedValueOnce({
    error: { status: 409, detail: "Try later" },
    data: undefined,
    response: { status: 409 },
  }).mockResolvedValueOnce(ok({ state: "undone" }));
  renderWithQueryClient(<AutomaticClassificationActivity />);
  await userEvent.click(screen.getByText("Automatic classification activity"));
  await userEvent.click(
    await screen.findByRole("button", { name: "Continue undo" }),
  );
  expect(await screen.findByRole("alert")).toHaveTextContent("Try later");
  expect(screen.getByText(/1 kept because they changed/)).toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Continue undo" }));
  await waitFor(() => expect(mockClient.POST).toHaveBeenCalledTimes(2));
  expect(mockClient.POST).toHaveBeenLastCalledWith(
    expect.anything(),
    expect.objectContaining({
      body: expect.objectContaining({ taxonomyRevisionId: revisionId }),
    }),
  );
});

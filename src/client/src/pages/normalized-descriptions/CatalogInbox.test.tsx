import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClientProvider } from "@tanstack/react-query";
import { createQueryClient, renderWithProviders } from "@/test/test-utils";
import client from "@/lib/api-client";
import { CatalogInbox } from "./CatalogInbox";

vi.mock("@/lib/api-client", () => ({
  default: { GET: vi.fn(), POST: vi.fn(), PUT: vi.fn() },
}));
vi.mock("@/hooks/useNormalizationAi", () => ({
  useAiConnection: () => ({ data: { models: ["test-model"] } }),
}));
const source = { location: "Market", description: "MILK", itemCode: "123" };
const group = {
  sourceHash: "source-a",
  source,
  purchaseCount: 2,
  receiptCount: 1,
  firstSeen: "2026-10-23T00:00:00Z",
  assistanceState: "auth_required",
  attemptRevision: "attempt-a",
  runId: null,
  receipts: [{ receiptId: "receipt-a", itemId: "item-a", date: "2026-10-22" }],
};
const preferences = {
  isOwner: true,
  enabled: true,
  model: "test-model",
  dailyLimit: 10,
  revision: "preference-a",
};
beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(client.GET).mockImplementation(
    async (path) =>
      ({
        data:
          path === "/api/normalization-inbox/preferences"
            ? preferences
            : { items: [group], total: 1, offset: 0, limit: 25 },
        response: new Response(),
      }) as never,
  );
});
function renderInbox() {
  const onReview = vi.fn();
  const onRun = vi.fn();
  renderWithProviders(
    <QueryClientProvider client={createQueryClient()}>
      <CatalogInbox onReview={onReview} onRun={onRun} />
    </QueryClientProvider>,
  );
  return { onReview, onRun };
}

it("keeps exact-source manual review and receipt links usable without a working provider", async () => {
  const { onReview } = renderInbox();
  await screen.findByText("MILK");
  expect(screen.getByText(/2 purchases across 1 receipts/)).toBeVisible();
  expect(
    screen.getByRole("link", { name: "Receipt 2026-10-22" }),
  ).toHaveAttribute("href", "/receipts/receipt-a");
  await userEvent.click(screen.getByRole("button", { name: "Review source" }));
  expect(onReview).toHaveBeenCalledWith(source);
  expect(client.POST).not.toHaveBeenCalled();
});

it("renders the RFC9457 retry reason while retaining the pending purchase", async () => {
  vi.mocked(client.POST).mockResolvedValue({
    error: {
      status: 409,
      detail: "The attempt changed; refresh before retrying.",
    },
    response: new Response(null, { status: 409 }),
  } as never);
  renderInbox();
  await userEvent.click(
    await screen.findByRole("button", { name: "Retry assistance" }),
  );
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "The attempt changed; refresh before retrying.",
  );
  expect(screen.getByText("MILK")).toBeVisible();
});

it("shows the settings rejection and preserves edited consent controls", async () => {
  vi.mocked(client.PUT).mockResolvedValue({
    error: { status: 409, detail: "Another session changed your settings." },
    response: new Response(null, { status: 409 }),
  } as never);
  renderInbox();
  const user = userEvent.setup();
  await user.click(
    await screen.findByText("Post-save ChatGPT suggestions: enabled"),
  );
  await user.click(
    screen.getByRole("checkbox", { name: "Enable post-save suggestions" }),
  );
  await user.click(
    screen.getByRole("button", { name: "Save assistance settings" }),
  );
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Another session changed your settings.",
  );
  expect(
    screen.getByRole("checkbox", { name: "Enable post-save suggestions" }),
  ).not.toBeChecked();
  expect(client.PUT).toHaveBeenCalledWith(
    "/api/normalization-inbox/preferences",
    {
      middleware: expect.any(Array),
      body: {
        enabled: false,
        model: "test-model",
        dailyLimit: 10,
        expectedRevision: "preference-a",
      },
    },
  );
  vi.mocked(client.GET).mockImplementation(
    async (path) =>
      ({
        data:
          path === "/api/normalization-inbox/preferences"
            ? { ...preferences, revision: "preference-b", dailyLimit: 3 }
            : { items: [group], total: 1, offset: 0, limit: 25 },
        response: new Response(),
      }) as never,
  );
  await user.click(
    screen.getByRole("button", {
      name: "Reload saved settings and discard these edits",
    }),
  );
  await user.click(
    await screen.findByText("Post-save ChatGPT suggestions: enabled"),
  );
  expect(
    screen.getByRole("checkbox", { name: "Enable post-save suggestions" }),
  ).toBeChecked();
  expect(screen.getByRole("spinbutton")).toHaveValue(3);
  expect(
    screen.queryByText("Another session changed your settings."),
  ).not.toBeInTheDocument();
});

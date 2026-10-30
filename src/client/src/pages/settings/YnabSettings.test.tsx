import type { Mock } from "vitest";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@/test/setup-combobox-polyfills";

beforeAll(() => {
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.setPointerCapture ??= () => {};
  Element.prototype.releasePointerCapture ??= () => {};
});
import { renderWithProviders } from "@/test/test-utils";
import { mockQueryResult, mockMutationResult } from "@/test/mock-hooks";
import YnabSettings from "./YnabSettings";

const envelopes = [
  { id: "env-groceries", name: "Groceries", isActive: true },
  { id: "env-gifts", name: "Gifts", isActive: true },
];

vi.mock("@/hooks/useEnvelopes", () => ({
  useEnvelopes: vi.fn(),
}));

// Match a settled useStableQuery projection and its successful wire payload;
// loading/error fixtures remain genuinely unverified instead of claiming success.
function settingsQuery(fields: Record<string, unknown>) {
  const loading = fields.isLoading === true;
  const error = fields.isError === true;
  const collection = [
    "budgets",
    "accounts",
    "mappings",
    "categories",
    "envelopes",
  ].find((key) => key in fields);
  const payload =
    fields.data ??
    (collection
      ? { data: fields[collection] }
      : "selectedBudgetId" in fields
        ? { selectedBudgetId: fields.selectedBudgetId }
        : "rateLimitStatus" in fields
            ? fields.rateLimitStatus
            : "isConfigured" in fields
              ? {
                  isConfigured: fields.isConfigured,
                  isConnected: fields.isConnected,
                  lastSuccessfulSyncUtc: fields.lastSuccessfulSyncUtc,
                }
              : {
                  staleAccountMappingCount: fields.staleAccountMappingCount,
                  staleCategoryMappingCount: fields.staleCategoryMappingCount,
                });
  return mockQueryResult({
    data: loading || error ? undefined : payload,
    isLoading: loading,
    isPending: loading,
    isFetching: loading,
    isSuccess: !loading && !error,
    isError: error,
    isFetched: !loading,
    status: loading ? "pending" : error ? "error" : "success",
    fetchStatus: loading ? "fetching" : "idle",
    error: error ? new Error("Lookup unavailable") : null,
    ...fields,
  });
}

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

vi.mock("@/hooks/useAccounts", () => ({
  useAllAccounts: vi.fn(() => settingsQuery({ data: [], isLoading: false })),
}));

vi.mock("@/hooks/useYnab", () => ({
  useYnabConnectionStatus: vi.fn(() =>
    settingsQuery({
      isConfigured: false,
      isConnected: false,
      lastSuccessfulSyncUtc: null,
      isLoading: false,
    }),
  ),
  useYnabBudgets: vi.fn(() =>
    settingsQuery({
      budgets: [{ id: "budget-1", name: "Budget" }],
      isLoading: false,
      isError: false,
    }),
  ),
  useSelectedYnabBudget: vi.fn(() =>
    settingsQuery({ selectedBudgetId: null, isLoading: false }),
  ),
  useSelectYnabBudget: vi.fn(() => mockMutationResult()),
  useYnabAccounts: vi.fn(() =>
    settingsQuery({ accounts: [], isLoading: false }),
  ),
  useYnabAccountMappings: vi.fn(() =>
    settingsQuery({ mappings: [], isLoading: false }),
  ),
  useCreateYnabAccountMapping: vi.fn(() => mockMutationResult()),
  useUpdateYnabAccountMapping: vi.fn(() => mockMutationResult()),
  useDeleteYnabAccountMapping: vi.fn(() => mockMutationResult()),
  useYnabCategories: vi.fn(() =>
    settingsQuery({ categories: [], isLoading: false }),
  ),
  useYnabEnvelopeMappings: vi.fn(() =>
    settingsQuery({ mappings: [], isLoading: false }),
  ),
  useSetYnabEnvelopeMapping: vi.fn(() => mockMutationResult()),
  useDeleteYnabEnvelopeMapping: vi.fn(() => mockMutationResult()),
  useYnabRateLimitStatus: vi.fn(() => settingsQuery({ rateLimitStatus: null })),
  useStaleMappings: vi.fn(() =>
    settingsQuery({
      staleAccountMappingCount: 0,
      staleCategoryMappingCount: 0,
      hasStaleMappings: false,
    }),
  ),
  useClearStaleMappings: vi.fn(() => mockMutationResult()),
}));

vi.mock("@/components/YnabBulkSyncCard", () => ({
  YnabBulkSyncCard: () => (
    <div data-testid="ynab-bulk-sync-card">Bulk YNAB Sync</div>
  ),
}));

beforeEach(async () => {
  const { useEnvelopes } = await import("@/hooks/useEnvelopes");
  vi.mocked(useEnvelopes).mockReturnValue(
    settingsQuery({ envelopes }) as ReturnType<typeof useEnvelopes>,
  );
  const hooks = await import("@/hooks/useYnab");
  const defaults = {
    useYnabConnectionStatus: {
      isConfigured: true,
      isConnected: true,
      lastSuccessfulSyncUtc: null,
    },
    useYnabBudgets: { budgets: [{ id: "budget-1", name: "Budget" }] },
    useSelectedYnabBudget: { selectedBudgetId: "budget-1" },
    useYnabAccounts: { accounts: [] },
    useYnabAccountMappings: { mappings: [] },
    useYnabCategories: { categories: [] },
    useYnabEnvelopeMappings: { mappings: [] },
    useYnabRateLimitStatus: { rateLimitStatus: null },
    useStaleMappings: {
      staleAccountMappingCount: 0,
      staleCategoryMappingCount: 0,
      hasStaleMappings: false,
    },
  };
  for (const [name, fields] of Object.entries(defaults)) {
    (hooks[name as keyof typeof hooks] as Mock).mockReturnValue(
      settingsQuery(fields),
    );
  }
});

describe("YnabSettings – Connection Status", () => {
  it("shows 'Connected' badge when configured and connected", async () => {
    const { useYnabConnectionStatus } = await import("@/hooks/useYnab");
    vi.mocked(useYnabConnectionStatus).mockReturnValue(
      settingsQuery({
        isConfigured: true,
        isConnected: true,
        lastSuccessfulSyncUtc: null,
        isLoading: false,
      }),
    );

    renderWithProviders(<YnabSettings />);
    expect(
      screen.queryByText(
        "Configure your YNAB integration for transaction sync",
      ),
    ).not.toBeInTheDocument();

    expect(screen.getByText("Connected")).toBeInTheDocument();
    expect(screen.getByText("No syncs yet")).toBeInTheDocument();
  });

  it("shows 'Connected' badge with last sync time when available", async () => {
    const { useYnabConnectionStatus } = await import("@/hooks/useYnab");
    const recentDate = new Date(Date.now() - 5 * 60000).toISOString();
    vi.mocked(useYnabConnectionStatus).mockReturnValue(
      settingsQuery({
        isConfigured: true,
        isConnected: true,
        lastSuccessfulSyncUtc: recentDate,
        isLoading: false,
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText("Connected")).toBeInTheDocument();
    expect(screen.getByText(/Last sync:/)).toBeInTheDocument();
  });

  it("shows 'Not Configured' badge when PAT is missing", async () => {
    const { useYnabConnectionStatus } = await import("@/hooks/useYnab");
    vi.mocked(useYnabConnectionStatus).mockReturnValue(
      settingsQuery({
        isConfigured: false,
        isConnected: false,
        lastSuccessfulSyncUtc: null,
        isLoading: false,
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(
      screen.queryByText(
        "Configure your YNAB integration for transaction sync",
      ),
    ).not.toBeInTheDocument();
    expect(screen.getByText("Not Configured")).toBeInTheDocument();
  });

  it("shows 'Disconnected' badge when configured but connection fails", async () => {
    const { useYnabConnectionStatus } = await import("@/hooks/useYnab");
    vi.mocked(useYnabConnectionStatus).mockReturnValue(
      settingsQuery({
        isConfigured: true,
        isConnected: false,
        lastSuccessfulSyncUtc: null,
        isLoading: false,
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText("Disconnected")).toBeInTheDocument();
  });

  it("shows loading spinner while checking connection", async () => {
    const { useYnabConnectionStatus } = await import("@/hooks/useYnab");
    vi.mocked(useYnabConnectionStatus).mockReturnValue(
      settingsQuery({
        isConfigured: false,
        isConnected: false,
        lastSuccessfulSyncUtc: null,
        isLoading: true,
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText("Checking connection...")).toBeInTheDocument();
  });
});

describe("YnabSettings – Envelope Mapping", () => {
  it("hides the mapping cards when YNAB is not configured", async () => {
    const { useYnabConnectionStatus } = await import("@/hooks/useYnab");
    vi.mocked(useYnabConnectionStatus).mockReturnValue(
      settingsQuery({
        isConfigured: false,
        isConnected: false,
        lastSuccessfulSyncUtc: null,
      }),
    );
    const { useYnabBudgets } = await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({ budgets: [], isLoading: false, isError: true }),
    );

    renderWithProviders(<YnabSettings />);

    // Only the Connection Status card renders; the mapping cards (and their
    // empty "Configure YNAB to..." placeholders) are hidden until a PAT is set.
    expect(screen.getByText("Connection Status")).toBeInTheDocument();
    expect(screen.queryByText("Budget Selection")).not.toBeInTheDocument();
    expect(screen.queryByText("Account Mapping")).not.toBeInTheDocument();
    expect(screen.queryByText("Envelope Mapping")).not.toBeInTheDocument();
  });

  it("shows 'Select a budget above to link envelopes.' when selectedBudgetId is null and not in error state", async () => {
    const { useYnabBudgets, useSelectedYnabBudget } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "budget-1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      settingsQuery({ selectedBudgetId: null, isLoading: false }),
    );

    renderWithProviders(<YnabSettings />);

    expect(
      screen.getByText("Select a budget above to link envelopes."),
    ).toBeInTheDocument();
  });

  it("explains the mapping and export consequences of switching destination budgets", () => {
    renderWithProviders(<YnabSettings />);

    expect(
      screen.getByText(
        /mappings from previously selected budgets are preserved.*ignored/i,
      ),
    ).toBeInTheDocument();
    expect(
      screen.getByText(
        /switching.*budget.*map.*accounts.*categories.*re-export/i,
      ),
    ).toBeInTheDocument();
  });

  it("shows loading spinner while YNAB categories load", async () => {
    const { useYnabBudgets, useSelectedYnabBudget, useYnabCategories } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "budget-1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      settingsQuery({
        selectedBudgetId: "budget-1",
        isLoading: false,
      }),
    );
    vi.mocked(useYnabCategories).mockReturnValue(
      settingsQuery({ categories: [], isLoading: true }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText("Loading envelopes...")).toBeInTheDocument();
  });

  it("shows empty envelope guidance when no active envelopes exist", async () => {
    const { useEnvelopes } = await import("@/hooks/useEnvelopes");
    vi.mocked(useEnvelopes).mockReturnValue(
      settingsQuery({
        envelopes: [{ id: "env-old", name: "Old", isActive: false }],
      }) as ReturnType<typeof useEnvelopes>,
    );

    renderWithProviders(<YnabSettings />);

    expect(
      screen.getByText("No active envelopes found. Create an envelope first."),
    ).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Envelopes" })).toHaveAttribute(
      "href",
      "/settings/envelopes",
    );
  });

  it("shows bulk sync card when YNAB is configured and budget is selected", async () => {
    const { useYnabBudgets, useSelectedYnabBudget } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "b1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      settingsQuery({ selectedBudgetId: "b1", isLoading: false }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByTestId("ynab-bulk-sync-card")).toBeInTheDocument();
  });

  it("hides bulk sync card when YNAB is not configured", async () => {
    const { useYnabConnectionStatus } = await import("@/hooks/useYnab");
    vi.mocked(useYnabConnectionStatus).mockReturnValue(
      settingsQuery({
        isConfigured: false,
        isConnected: false,
        lastSuccessfulSyncUtc: null,
      }),
    );
    const { useYnabBudgets } = await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({ budgets: [], isLoading: false, isError: true }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.queryByTestId("ynab-bulk-sync-card")).not.toBeInTheDocument();
  });

  it("hides bulk sync card when no budget is selected", async () => {
    const { useYnabBudgets, useSelectedYnabBudget } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "b1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      settingsQuery({ selectedBudgetId: null, isLoading: false }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.queryByTestId("ynab-bulk-sync-card")).not.toBeInTheDocument();
  });

  const ynabCategories = [
    { id: "ynab-food", name: "Food", categoryGroupName: "Living" },
    { id: "ynab-fun", name: "Fun", categoryGroupName: "Wants" },
  ];
  const groceriesLink = {
    id: "link-1",
    envelopeId: "env-groceries",
    envelopeName: "Groceries",
    ynabCategoryId: "ynab-food",
    ynabCategoryName: "Food",
    ynabCategoryGroupName: "Living",
    ynabBudgetId: "budget-1",
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z",
  };

  it("lists one YNAB category select per active envelope", async () => {
    const hooks = await import("@/hooks/useYnab");
    vi.mocked(hooks.useYnabCategories).mockReturnValue(
      settingsQuery({ categories: ynabCategories }),
    );
    vi.mocked(hooks.useYnabEnvelopeMappings).mockReturnValue(
      settingsQuery({ mappings: [groceriesLink] }),
    );

    renderWithProviders(<YnabSettings />);

    expect(
      screen.getByRole("combobox", { name: "YNAB category for Groceries" }),
    ).toHaveTextContent("Food");
    expect(
      screen.getByRole("combobox", { name: "YNAB category for Gifts" }),
    ).toHaveTextContent("Not linked");
    expect(
      screen.getByRole("button", { name: "Unlink Groceries" }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "Unlink Gifts" }),
    ).not.toBeInTheDocument();
  });

  it("links an envelope by PUTting the chosen YNAB category for the selected budget", async () => {
    const user = userEvent.setup();
    const hooks = await import("@/hooks/useYnab");
    const link = vi.fn();
    vi.mocked(hooks.useYnabCategories).mockReturnValue(
      settingsQuery({ categories: ynabCategories }),
    );
    vi.mocked(hooks.useSetYnabEnvelopeMapping).mockReturnValue(
      mockMutationResult({ mutate: link }) as ReturnType<
        typeof hooks.useSetYnabEnvelopeMapping
      >,
    );
    renderWithProviders(<YnabSettings />);

    await user.click(
      screen.getByRole("combobox", { name: "YNAB category for Gifts" }),
    );
    await user.click(screen.getByRole("option", { name: "Fun" }));

    expect(link).toHaveBeenCalledWith({
      envelopeId: "env-gifts",
      ynabCategoryId: "ynab-fun",
      ynabCategoryName: "Fun",
      ynabCategoryGroupName: "Wants",
      ynabBudgetId: "budget-1",
    });
  });

  it("unlinks an envelope by its envelope id", async () => {
    const user = userEvent.setup();
    const hooks = await import("@/hooks/useYnab");
    const unlink = vi.fn();
    vi.mocked(hooks.useYnabCategories).mockReturnValue(
      settingsQuery({ categories: ynabCategories }),
    );
    vi.mocked(hooks.useYnabEnvelopeMappings).mockReturnValue(
      settingsQuery({ mappings: [groceriesLink] }),
    );
    vi.mocked(hooks.useDeleteYnabEnvelopeMapping).mockReturnValue(
      mockMutationResult({ mutate: unlink }) as ReturnType<
        typeof hooks.useDeleteYnabEnvelopeMapping
      >,
    );
    renderWithProviders(<YnabSettings />);

    await user.click(screen.getByRole("button", { name: "Unlink Groceries" }));

    expect(unlink).toHaveBeenCalledWith("env-groceries");
  });

  it("keeps an archived envelope listed while it is still linked, and hides archived unlinked ones", async () => {
    const hooks = await import("@/hooks/useYnab");
    const { useEnvelopes } = await import("@/hooks/useEnvelopes");
    vi.mocked(useEnvelopes).mockReturnValue(
      settingsQuery({
        envelopes: [
          { id: "env-groceries", name: "Groceries", isActive: false },
          { id: "env-travel", name: "Travel", isActive: false },
          { id: "env-gifts", name: "Gifts", isActive: true },
        ],
      }) as ReturnType<typeof useEnvelopes>,
    );
    vi.mocked(hooks.useYnabCategories).mockReturnValue(
      settingsQuery({ categories: ynabCategories }),
    );
    vi.mocked(hooks.useYnabEnvelopeMappings).mockReturnValue(
      settingsQuery({ mappings: [groceriesLink] }),
    );

    renderWithProviders(<YnabSettings />);

    const archived = screen.getByRole("combobox", {
      name: "YNAB category for Groceries",
    }).parentElement!;
    expect(archived).toHaveTextContent("Archived");
    expect(
      screen.queryByRole("combobox", { name: "YNAB category for Travel" }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole("combobox", { name: "YNAB category for Gifts" }),
    ).toBeInTheDocument();
  });
});

describe("YnabSettings – Rate Limit Card", () => {
  it("renders rate limit card when YNAB is configured and status is available", async () => {
    const { useYnabBudgets, useYnabRateLimitStatus } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "budget-1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useYnabRateLimitStatus).mockReturnValue(
      settingsQuery({
        rateLimitStatus: {
          remainingRequests: 150,
          maxRequests: 200,
          requestsUsed: 50,
          windowResetAt: "2026-04-05T23:00:00Z",
          oldestRequestAt: "2026-04-05T22:00:00Z",
        },
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText("API Rate Limit")).toBeInTheDocument();
    expect(screen.getByText("50 / 200 requests used")).toBeInTheDocument();
    expect(screen.getByText("150 remaining")).toBeInTheDocument();
  });

  it("does not render rate limit card when YNAB is not configured", async () => {
    const { useYnabConnectionStatus } = await import("@/hooks/useYnab");
    vi.mocked(useYnabConnectionStatus).mockReturnValue(
      settingsQuery({
        isConfigured: false,
        isConnected: false,
        lastSuccessfulSyncUtc: null,
      }),
    );
    const { useYnabBudgets, useYnabRateLimitStatus } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({ budgets: [], isLoading: false, isError: true }),
    );
    vi.mocked(useYnabRateLimitStatus).mockReturnValue(
      settingsQuery({ rateLimitStatus: null }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.queryByText("API Rate Limit")).not.toBeInTheDocument();
  });

  it("shows warning when quota is low", async () => {
    const { useYnabBudgets, useYnabRateLimitStatus } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "budget-1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useYnabRateLimitStatus).mockReturnValue(
      settingsQuery({
        rateLimitStatus: {
          remainingRequests: 10,
          maxRequests: 200,
          requestsUsed: 190,
          windowResetAt: "2026-04-05T23:00:00Z",
          oldestRequestAt: "2026-04-05T22:00:00Z",
        },
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText(/API quota is running low/)).toBeInTheDocument();
  });

  it("rate limit bar has role=progressbar with correct aria attributes", async () => {
    const { useYnabBudgets, useYnabRateLimitStatus } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "budget-1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useYnabRateLimitStatus).mockReturnValue(
      settingsQuery({
        rateLimitStatus: {
          remainingRequests: 150,
          maxRequests: 200,
          requestsUsed: 50,
          windowResetAt: "2026-04-05T23:00:00Z",
          oldestRequestAt: "2026-04-05T22:00:00Z",
        },
      }),
    );

    renderWithProviders(<YnabSettings />);

    const progressbar = screen.getByRole("progressbar");
    expect(progressbar).toBeInTheDocument();
    expect(progressbar).toHaveAttribute("aria-valuenow", "50");
    expect(progressbar).toHaveAttribute("aria-valuemin", "0");
    expect(progressbar).toHaveAttribute("aria-valuemax", "200");
    expect(progressbar).toHaveAttribute("aria-label", "API rate limit usage");
  });

  it("rate limit bar aria-valuenow reflects current usage", async () => {
    const { useYnabBudgets, useYnabRateLimitStatus } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "budget-1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useYnabRateLimitStatus).mockReturnValue(
      settingsQuery({
        rateLimitStatus: {
          remainingRequests: 10,
          maxRequests: 200,
          requestsUsed: 190,
          windowResetAt: "2026-04-05T23:00:00Z",
          oldestRequestAt: "2026-04-05T22:00:00Z",
        },
      }),
    );

    renderWithProviders(<YnabSettings />);

    const progressbar = screen.getByRole("progressbar");
    expect(progressbar).toHaveAttribute("aria-valuenow", "190");
    expect(progressbar).toHaveAttribute("aria-valuemax", "200");
  });
});

describe("YnabSettings – Stale Mappings", () => {
  it("shows stale mapping banner when stale account mappings exist", async () => {
    const { useYnabBudgets, useSelectedYnabBudget, useStaleMappings } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "budget-1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      settingsQuery({ selectedBudgetId: "budget-1", isLoading: false }),
    );
    vi.mocked(useStaleMappings).mockReturnValue(
      settingsQuery({
        staleAccountMappingCount: 2,
        staleCategoryMappingCount: 0,
        hasStaleMappings: true,
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText(/2 account mapping\(s\)/)).toBeInTheDocument();
    expect(
      screen.getByText("Delete previous-budget mappings"),
    ).toBeInTheDocument();
  });

  it("shows stale mapping banner when stale envelope links exist", async () => {
    const { useYnabBudgets, useSelectedYnabBudget, useStaleMappings } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "budget-1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      settingsQuery({ selectedBudgetId: "budget-1", isLoading: false }),
    );
    vi.mocked(useStaleMappings).mockReturnValue(
      settingsQuery({
        staleAccountMappingCount: 0,
        staleCategoryMappingCount: 3,
        hasStaleMappings: true,
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText(/3 envelope link\(s\)/)).toBeInTheDocument();
    expect(
      screen.getByText("Delete previous-budget mappings"),
    ).toBeInTheDocument();
  });

  it("shows both account and envelope link counts when both are stale", async () => {
    const { useYnabBudgets, useSelectedYnabBudget, useStaleMappings } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "budget-1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      settingsQuery({ selectedBudgetId: "budget-1", isLoading: false }),
    );
    vi.mocked(useStaleMappings).mockReturnValue(
      settingsQuery({
        staleAccountMappingCount: 2,
        staleCategoryMappingCount: 3,
        hasStaleMappings: true,
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(screen.getByText(/2 account mapping\(s\)/)).toBeInTheDocument();
    expect(screen.getByText(/3 envelope link\(s\)/)).toBeInTheDocument();
  });

  it("does not show stale mapping banner when no stale mappings exist", async () => {
    const { useYnabBudgets, useSelectedYnabBudget, useStaleMappings } =
      await import("@/hooks/useYnab");
    vi.mocked(useYnabBudgets).mockReturnValue(
      settingsQuery({
        budgets: [{ id: "budget-1", name: "Budget" }],
        isLoading: false,
        isError: false,
      }),
    );
    vi.mocked(useSelectedYnabBudget).mockReturnValue(
      settingsQuery({ selectedBudgetId: "budget-1", isLoading: false }),
    );
    vi.mocked(useStaleMappings).mockReturnValue(
      settingsQuery({
        staleAccountMappingCount: 0,
        staleCategoryMappingCount: 0,
        hasStaleMappings: false,
      }),
    );

    renderWithProviders(<YnabSettings />);

    expect(
      screen.queryByText("Delete previous-budget mappings"),
    ).not.toBeInTheDocument();
  });
});

it("renders unavailable budget feedback without claiming the configured integration is absent", async () => {
  const { useYnabBudgets } = await import("@/hooks/useYnab");
  vi.mocked(useYnabBudgets).mockReturnValue(
    settingsQuery({ budgets: [], isError: true }),
  );
  renderWithProviders(<YnabSettings />);
  expect(screen.getByText("Connected")).toBeInTheDocument();
  expect(
    screen.getByText(
      "YNAB budgets are unavailable. Any choices shown are last known.",
    ),
  ).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument();
  expect(screen.queryByText("Not Configured")).not.toBeInTheDocument();
  expect(screen.getByText("Budget Selection")).toBeInTheDocument();
  // Bulk sync retains its existing selected-budget contract; this test owns
  // budget lookup feedback, not that separate action workflow.
  expect(screen.getByTestId("ynab-bulk-sync-card")).toBeInTheDocument();
});

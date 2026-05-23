import { screen } from "@testing-library/react";
import { renderWithProviders } from "@/test/test-utils";
import { mockQueryResult } from "@/test/mock-hooks";
import "@/test/setup-combobox-polyfills";
import AuditLog from "./AuditLog";

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

vi.mock("@/hooks/useEnumMetadata", () => ({
  useEnumMetadata: vi.fn(() => ({
    adjustmentTypes: [],
    authEventTypes: [],
    auditActions: [],
    entityTypes: [],
    adjustmentTypeLabels: {},
    authEventLabels: {},
    auditActionLabels: {},
    entityTypeLabels: {},
    isLoading: false,
  })),
}));

vi.mock("@/hooks/useAudit", () => ({
  useRecentAuditLogs: vi.fn(() => ({
    data: [],
    total: 0,
    isLoading: false,
  })),
}));

vi.mock("@/hooks/useUsers", () => ({
  useUsers: vi.fn(() => ({ data: [], total: 0, isLoading: false })),
}));

vi.mock("@/hooks/useServerPagination", () => ({
  useServerPagination: vi.fn(() => ({
    offset: 0,
    limit: 50,
    currentPage: 1,
    pageSize: 50,
    totalPages: () => 1,
    setPage: vi.fn(),
    setPageSize: vi.fn(),
    resetPage: vi.fn(),
  })),
}));

vi.mock("@/hooks/useServerSort", () => ({
  useServerSort: vi.fn(() => ({
    sortBy: "changedAt",
    sortDirection: "desc",
    toggleSort: vi.fn(),
  })),
}));

vi.mock("@/components/AuditLogTable", () => ({
  AuditLogTable: function MockAuditLogTable({
    isLoading,
    logs,
  }: {
    isLoading: boolean;
    logs: unknown[];
  }) {
    if (isLoading) return "Loading...";
    return <div data-testid="audit-table">AuditLogTable ({logs?.length ?? 0} rows)</div>;
  },
}));

vi.mock("@/components/Pagination", () => ({
  Pagination: vi.fn(function MockPagination() {
    return <div data-testid="pagination">Pagination</div>;
  }),
}));

describe("AuditLog", () => {
  it("renders the page heading", () => {
    renderWithProviders(<AuditLog />);
    expect(
      screen.getByRole("heading", { name: /audit log/i }),
    ).toBeInTheDocument();
  });

  it("renders the Export CSV button", () => {
    renderWithProviders(<AuditLog />);
    expect(
      screen.getByRole("button", { name: /export csv/i }),
    ).toBeInTheDocument();
  });

  it("renders the AuditLogTable component", () => {
    renderWithProviders(<AuditLog />);
    expect(screen.getByTestId("audit-table")).toBeInTheDocument();
  });

  it("renders the Pagination component", () => {
    renderWithProviders(<AuditLog />);
    expect(screen.getByTestId("pagination")).toBeInTheDocument();
  });

  it("shows loading state in AuditLogTable when data is loading", async () => {
    const { useRecentAuditLogs } = await import("@/hooks/useAudit");
    vi.mocked(useRecentAuditLogs).mockReturnValue(mockQueryResult({
      data: undefined,
      isLoading: true,
    }));

    renderWithProviders(<AuditLog />);
    expect(screen.getByText("Loading...")).toBeInTheDocument();
  });

  it("passes server-returned logs to AuditLogTable", async () => {
    const { useRecentAuditLogs } = await import("@/hooks/useAudit");
    vi.mocked(useRecentAuditLogs).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "1",
          entityType: "Account",
          entityId: "abc-123",
          action: "Create",
          changedAt: "2024-01-15T10:00:00Z",
          changedByUserId: "user-1",
          changedByApiKeyId: null,
          ipAddress: "127.0.0.1",
          changesJson: "{}",
        },
      ],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<AuditLog />);
    expect(screen.getByTestId("audit-table")).toHaveTextContent("1 rows");
  });

  it("enables Export CSV button when logs exist", async () => {
    const { useRecentAuditLogs } = await import("@/hooks/useAudit");
    vi.mocked(useRecentAuditLogs).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "1",
          entityType: "Account",
          entityId: "abc-123",
          action: "Create",
          changedAt: "2024-01-15T10:00:00Z",
          changedByUserId: "user-1",
          changedByApiKeyId: null,
          ipAddress: "127.0.0.1",
          changesJson: "{}",
        },
      ],
      total: 1,
      isLoading: false,
    }));

    renderWithProviders(<AuditLog />);
    expect(
      screen.getByRole("button", { name: /export csv/i }),
    ).not.toBeDisabled();
  });

  it("triggers CSV export when Export CSV button is clicked", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useRecentAuditLogs } = await import("@/hooks/useAudit");
    vi.mocked(useRecentAuditLogs).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "1",
          entityType: "Account",
          entityId: "abc-123",
          action: "Create",
          changedAt: "2024-01-15T10:00:00Z",
          changedByUserId: "user-1",
          changedByApiKeyId: null,
          ipAddress: "127.0.0.1",
          changesJson: "{}",
        },
      ],
      total: 1,
      isLoading: false,
    }));

    const mockCreateObjectURL = vi.fn(() => "blob:mock-url");
    const mockRevokeObjectURL = vi.fn();
    const mockClick = vi.fn();
    const originalCreateObjectURL = URL.createObjectURL;
    const originalRevokeObjectURL = URL.revokeObjectURL;
    URL.createObjectURL = mockCreateObjectURL;
    URL.revokeObjectURL = mockRevokeObjectURL;

    const originalCreateElement = document.createElement.bind(document);
    vi.spyOn(document, "createElement").mockImplementation((tag: string, options?: ElementCreationOptions) => {
      if (tag === "a") {
        return { href: "", download: "", click: mockClick } as unknown as HTMLAnchorElement;
      }
      return originalCreateElement(tag, options);
    });

    renderWithProviders(<AuditLog />);
    await user.click(screen.getByRole("button", { name: /export csv/i }));

    expect(mockCreateObjectURL).toHaveBeenCalled();
    expect(mockClick).toHaveBeenCalled();
    expect(mockRevokeObjectURL).toHaveBeenCalledWith("blob:mock-url");

    URL.createObjectURL = originalCreateObjectURL;
    URL.revokeObjectURL = originalRevokeObjectURL;
    vi.restoreAllMocks();
  });

  it("renders entity type filter trigger", () => {
    renderWithProviders(<AuditLog />);
    // Radix Select renders trigger buttons; count the combobox elements
    const triggers = screen.getAllByRole("combobox");
    expect(triggers.length).toBeGreaterThanOrEqual(2);
  });

  it("entity type combobox has an explicit accessible name", () => {
    renderWithProviders(<AuditLog />);
    expect(
      screen.getByRole("combobox", { name: /filter by entity type/i }),
    ).toBeInTheDocument();
  });

  it("action combobox has an explicit accessible name", () => {
    renderWithProviders(<AuditLog />);
    expect(
      screen.getByRole("combobox", { name: /filter by action/i }),
    ).toBeInTheDocument();
  });

  it("renders DateRangePicker From and To buttons", () => {
    renderWithProviders(<AuditLog />);
    const buttons = screen.getAllByRole("button");
    const fromButton = buttons.find((b) => b.textContent === "From");
    const toButton = buttons.find((b) => b.textContent === "To");
    expect(fromButton).toBeDefined();
    expect(toButton).toBeDefined();
  });

  it("passes filter params to useRecentAuditLogs", async () => {
    const { useRecentAuditLogs } = await import("@/hooks/useAudit");
    const mockFn = vi.mocked(useRecentAuditLogs);
    mockFn.mockReturnValue(mockQueryResult({
      data: [],
      total: 0,
      isLoading: false,
    }));

    renderWithProviders(<AuditLog />);

    // Verify it was called with filter object (server-side filtering)
    expect(mockFn).toHaveBeenCalledWith(
      expect.objectContaining({
        offset: expect.any(Number),
        limit: expect.any(Number),
      }),
    );
  });

  it("renders entity type and action options when enum data exists", async () => {
    const { useEnumMetadata } = await import("@/hooks/useEnumMetadata");
    vi.mocked(useEnumMetadata).mockReturnValue({
      adjustmentTypes: [],
      authEventTypes: [],
      auditActions: [{ value: "Create", label: "Create" }, { value: "Update", label: "Update" }],
      entityTypes: [{ value: "Account", label: "Account" }, { value: "Receipt", label: "Receipt" }],
      adjustmentTypeLabels: {},
      authEventLabels: {},
      auditActionLabels: {},
      entityTypeLabels: { Account: "Account", Receipt: "Receipt" },
      isLoading: false,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    renderWithProviders(<AuditLog />);
    // Combobox triggers should be present for entity type and action filters
    const triggers = screen.getAllByRole("combobox");
    expect(triggers.length).toBeGreaterThanOrEqual(2);
  });

  it("resets pagination when search input changes", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockResetPage = vi.fn();
    const { useServerPagination } = await import("@/hooks/useServerPagination");
    vi.mocked(useServerPagination).mockReturnValue({
      offset: 0,
      limit: 50,
      currentPage: 1,
      pageSize: 50,
      totalPages: () => 1,
      setPage: vi.fn(),
      setPageSize: vi.fn(),
      resetPage: mockResetPage,
    });

    renderWithProviders(<AuditLog />);
    const searchInput = screen.getByLabelText(/search audit log/i);
    await user.type(searchInput, "test");

    expect(mockResetPage).toHaveBeenCalled();
  });

  it("disables Export CSV button when no logs exist", () => {
    renderWithProviders(<AuditLog />);
    expect(screen.getByRole("button", { name: /export csv/i })).toBeDisabled();
  });

  it("handles CSV export with special characters in data", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useRecentAuditLogs } = await import("@/hooks/useAudit");
    vi.mocked(useRecentAuditLogs).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "1",
          entityType: "Account",
          entityId: "has,comma",
          action: 'has"quote',
          changedAt: "2024-01-15T10:00:00Z",
          changedByUserId: "user-1",
          changedByApiKeyId: null,
          ipAddress: "127.0.0.1",
          changesJson: '{"key":"value"}',
        },
      ],
      total: 1,
      isLoading: false,
    }));

    const mockClick = vi.fn();
    const originalCreateElement = document.createElement.bind(document);
    vi.spyOn(document, "createElement").mockImplementation((tag: string, options?: ElementCreationOptions) => {
      if (tag === "a") {
        return { href: "", download: "", click: mockClick } as unknown as HTMLAnchorElement;
      }
      return originalCreateElement(tag, options);
    });
    const mockCreateObjectURL = vi.fn(() => "blob:csv-url");
    const mockRevokeObjectURL = vi.fn();
    const origCreate = URL.createObjectURL;
    const origRevoke = URL.revokeObjectURL;
    URL.createObjectURL = mockCreateObjectURL;
    URL.revokeObjectURL = mockRevokeObjectURL;

    renderWithProviders(<AuditLog />);
    await user.click(screen.getByRole("button", { name: /export csv/i }));

    expect(mockClick).toHaveBeenCalled();
    expect(mockCreateObjectURL).toHaveBeenCalled();

    URL.createObjectURL = origCreate;
    URL.revokeObjectURL = origRevoke;
    vi.restoreAllMocks();
  });

  it("exports CSV with null fields correctly", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const { useRecentAuditLogs } = await import("@/hooks/useAudit");
    vi.mocked(useRecentAuditLogs).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "1",
          entityType: "Account",
          entityId: "abc-123",
          action: "Create",
          changedAt: "2024-01-15T10:00:00Z",
          changedByUserId: null,
          changedByApiKeyId: "apikey-1",
          ipAddress: null,
          changesJson: "{}",
        },
      ],
      total: 1,
      isLoading: false,
    }));

    const mockClick = vi.fn();
    const originalCreateElement = document.createElement.bind(document);
    vi.spyOn(document, "createElement").mockImplementation((tag: string, options?: ElementCreationOptions) => {
      if (tag === "a") {
        return { href: "", download: "", click: mockClick } as unknown as HTMLAnchorElement;
      }
      return originalCreateElement(tag, options);
    });
    const mockCreateObjectURL = vi.fn(() => "blob:null-url");
    const mockRevokeObjectURL = vi.fn();
    const origCreate = URL.createObjectURL;
    const origRevoke = URL.revokeObjectURL;
    URL.createObjectURL = mockCreateObjectURL;
    URL.revokeObjectURL = mockRevokeObjectURL;

    renderWithProviders(<AuditLog />);
    await user.click(screen.getByRole("button", { name: /export csv/i }));

    expect(mockClick).toHaveBeenCalled();
    expect(mockCreateObjectURL).toHaveBeenCalled();

    URL.createObjectURL = origCreate;
    URL.revokeObjectURL = origRevoke;
    vi.restoreAllMocks();
  });

  it("filters by entity type when combobox option is selected", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockResetPage = vi.fn();
    const { useServerPagination } = await import("@/hooks/useServerPagination");
    vi.mocked(useServerPagination).mockReturnValue({
      offset: 0,
      limit: 50,
      currentPage: 1,
      pageSize: 50,
      totalPages: () => 1,
      setPage: vi.fn(),
      setPageSize: vi.fn(),
      resetPage: mockResetPage,
    });

    const { useEnumMetadata } = await import("@/hooks/useEnumMetadata");
    vi.mocked(useEnumMetadata).mockReturnValue({
      adjustmentTypes: [],
      authEventTypes: [],
      auditActions: [{ value: "Create", label: "Create" }],
      entityTypes: [{ value: "Account", label: "Account" }],
      adjustmentTypeLabels: {},
      authEventLabels: {},
      auditActionLabels: {},
      entityTypeLabels: { Account: "Account" },
      isLoading: false,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    renderWithProviders(<AuditLog />);

    // Click the entity type combobox trigger (first combobox)
    const triggers = screen.getAllByRole("combobox");
    await user.click(triggers[0]);

    // Select "Account" option
    const accountOption = await screen.findByRole("option", { name: "Account" });
    await user.click(accountOption);

    // handleEntityTypeChange should call resetPage
    expect(mockResetPage).toHaveBeenCalled();
  });

  it("filters by action when combobox option is selected", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockResetPage = vi.fn();
    const { useServerPagination } = await import("@/hooks/useServerPagination");
    vi.mocked(useServerPagination).mockReturnValue({
      offset: 0,
      limit: 50,
      currentPage: 1,
      pageSize: 50,
      totalPages: () => 1,
      setPage: vi.fn(),
      setPageSize: vi.fn(),
      resetPage: mockResetPage,
    });

    const { useEnumMetadata } = await import("@/hooks/useEnumMetadata");
    vi.mocked(useEnumMetadata).mockReturnValue({
      adjustmentTypes: [],
      authEventTypes: [],
      auditActions: [{ value: "Create", label: "Create" }, { value: "Update", label: "Update" }],
      entityTypes: [{ value: "Account", label: "Account" }],
      adjustmentTypeLabels: {},
      authEventLabels: {},
      auditActionLabels: {},
      entityTypeLabels: { Account: "Account" },
      isLoading: false,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    renderWithProviders(<AuditLog />);

    // Click the action combobox trigger (second combobox)
    const triggers = screen.getAllByRole("combobox");
    await user.click(triggers[1]);

    // Select "Create" option
    const createOption = await screen.findByRole("option", { name: "Create" });
    await user.click(createOption);

    // handleActionChange should call resetPage
    expect(mockResetPage).toHaveBeenCalled();
  });

  it("calls setPage when pagination page changes", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockSetPage = vi.fn();
    const { useServerPagination } = await import("@/hooks/useServerPagination");
    vi.mocked(useServerPagination).mockReturnValue({
      offset: 0,
      limit: 50,
      currentPage: 1,
      pageSize: 50,
      totalPages: () => 2,
      setPage: mockSetPage,
      setPageSize: vi.fn(),
      resetPage: vi.fn(),
    });

    const { useRecentAuditLogs } = await import("@/hooks/useAudit");
    vi.mocked(useRecentAuditLogs).mockReturnValue(mockQueryResult({
      data: [
        {
          id: "1",
          entityType: "Account",
          entityId: "abc-123",
          action: "Create",
          changedAt: "2024-01-15T10:00:00Z",
          changedByUserId: "user-1",
          changedByApiKeyId: null,
          ipAddress: "127.0.0.1",
          changesJson: "{}",
        },
      ],
      total: 100,
      isLoading: false,
    }));

    const { Pagination } = await import("@/components/Pagination");
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    vi.mocked(Pagination).mockImplementation(({ onPageChange }: any) => (
      <div data-testid="pagination">
        <button data-testid="next-page" onClick={() => onPageChange(2)}>Next</button>
      </div>
    ));

    renderWithProviders(<AuditLog />);
    await user.click(screen.getByTestId("next-page"));

    expect(mockSetPage).toHaveBeenCalledWith(2, 100);
  });
});

import { screen } from "@testing-library/react";
import { renderWithProviders } from "@/test/test-utils";
import { mockQueryResult } from "@/test/mock-hooks";
import SecurityLog from "./SecurityLog";

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

vi.mock("@/hooks/usePermission", () => ({
  usePermission: vi.fn(() => ({
    isAdmin: () => true,
  })),
}));

vi.mock("@/hooks/useAuthAudit", () => ({
  useMyAuthAuditLog: vi.fn(() => ({
    data: [],
    total: 0,
    isLoading: false,
  })),
  useRecentAuthAuditLogs: vi.fn(() => ({
    data: [],
    total: 0,
    isLoading: false,
  })),
  useFailedAuthAttempts: vi.fn(() => ({
    data: [],
    total: 0,
    isLoading: false,
  })),
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

vi.mock("@/hooks/useServerSort", () => ({
  useServerSort: vi.fn(() => ({
    sortBy: "timestamp",
    sortDirection: "desc",
    toggleSort: vi.fn(),
  })),
}));

vi.mock("@/components/Pagination", () => ({
  Pagination: function MockPagination() {
    return <div data-testid="pagination">Pagination</div>;
  },
}));

vi.mock("@/components/AuthAuditTable", () => ({
  AuthAuditTable: function MockAuthAuditTable({
    isLoading,
  }: {
    isLoading: boolean;
  }) {
    return isLoading ? "Loading..." : "AuthAuditTable";
  },
}));

describe("SecurityLog", () => {
  it("renders the page heading", () => {
    renderWithProviders(<SecurityLog />);
    expect(
      screen.getByRole("heading", { name: /security log/i }),
    ).toBeInTheDocument();
  });

  it("renders the My Activity tab", () => {
    renderWithProviders(<SecurityLog />);
    expect(
      screen.getByRole("tab", { name: /my activity/i }),
    ).toBeInTheDocument();
  });

  it("renders admin-only tabs when user is admin", () => {
    renderWithProviders(<SecurityLog />);
    expect(
      screen.getByRole("tab", { name: /all events/i }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("tab", { name: /failed logins/i }),
    ).toBeInTheDocument();
  });

  it("hides admin tabs when user is not admin", async () => {
    const { usePermission } = await import("@/hooks/usePermission");
    vi.mocked(usePermission).mockReturnValue(mockQueryResult({
      isAdmin: () => false,
    }));

    renderWithProviders(<SecurityLog />);
    expect(
      screen.queryByRole("tab", { name: /all events/i }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole("tab", { name: /failed logins/i }),
    ).not.toBeInTheDocument();
  });

  it("passes enabled=false to the admin-only audit hooks when not admin (no 403 storm)", async () => {
    const { usePermission } = await import("@/hooks/usePermission");
    vi.mocked(usePermission).mockReturnValue(mockQueryResult({
      isAdmin: () => false,
    }));
    const { useRecentAuthAuditLogs, useFailedAuthAttempts } = await import(
      "@/hooks/useAuthAudit"
    );

    renderWithProviders(<SecurityLog />);

    expect(vi.mocked(useRecentAuthAuditLogs)).toHaveBeenCalledWith(
      0,
      50,
      "timestamp",
      "desc",
      { enabled: false },
    );
    expect(vi.mocked(useFailedAuthAttempts)).toHaveBeenCalledWith(
      0,
      50,
      "timestamp",
      "desc",
      { enabled: false },
    );
  });

  it("passes enabled=true to the admin-only audit hooks when admin", async () => {
    const { useRecentAuthAuditLogs, useFailedAuthAttempts } = await import(
      "@/hooks/useAuthAudit"
    );

    renderWithProviders(<SecurityLog />);

    expect(vi.mocked(useRecentAuthAuditLogs)).toHaveBeenCalledWith(
      0,
      50,
      "timestamp",
      "desc",
      { enabled: true },
    );
    expect(vi.mocked(useFailedAuthAttempts)).toHaveBeenCalledWith(
      0,
      50,
      "timestamp",
      "desc",
      { enabled: true },
    );
  });
});

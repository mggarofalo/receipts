import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { AuditLogTable } from "./AuditLogTable";
import { TooltipProvider } from "@/components/ui/tooltip";
import type { AuditLog } from "@/lib/audit-utils";

const mockLogs: AuditLog[] = [
  {
    id: "log-1",
    entityType: "Receipt",
    entityId: "entity-001-full-uuid",
    action: "Created",
    changesJson: "[]",
    changedByUserId: "user-001-full-uuid",
    changedByApiKeyId: null,
    changedAt: "2025-01-15T10:30:00Z",
    ipAddress: "192.168.1.1",
  },
  {
    id: "log-2",
    entityType: "Account",
    entityId: "entity-002-full-uuid",
    action: "Updated",
    changesJson: JSON.stringify([
      { field: "name", oldValue: "Old Name", newValue: "New Name" },
    ]),
    changedByUserId: null,
    changedByApiKeyId: "apikey-001-full-uuid",
    changedAt: "2025-01-16T14:00:00Z",
    ipAddress: null,
  },
];

function renderWithTooltip(ui: React.ReactElement) {
  return render(<TooltipProvider>{ui}</TooltipProvider>);
}

describe("AuditLogTable", () => {
  it("renders loading skeletons when isLoading is true", () => {
    const { container } = renderWithTooltip(
      <AuditLogTable logs={[]} isLoading={true} />,
    );
    const skeletons = container.querySelectorAll('[data-slot="skeleton"]');
    expect(skeletons.length).toBeGreaterThan(0);
  });

  it("renders empty state when logs array is empty", () => {
    renderWithTooltip(<AuditLogTable logs={[]} isLoading={false} />);
    expect(screen.getByText("No audit log entries found.")).toBeInTheDocument();
  });

  it("renders table headers without sort when onToggleSort is not provided", () => {
    renderWithTooltip(
      <AuditLogTable logs={mockLogs} isLoading={false} />,
    );
    expect(screen.getByText("Timestamp")).toBeInTheDocument();
    expect(screen.getByText("Entity Type")).toBeInTheDocument();
    expect(screen.getByText("Entity ID")).toBeInTheDocument();
    expect(screen.getByText("Action")).toBeInTheDocument();
    expect(screen.getByText("Changed By")).toBeInTheDocument();
    expect(screen.getByText("Changes")).toBeInTheDocument();
  });

  it("renders sortable table headers when onToggleSort is provided", () => {
    const toggleSort = vi.fn();
    renderWithTooltip(
      <AuditLogTable
        logs={mockLogs}
        isLoading={false}
        sortBy="changedAt"
        sortDirection="desc"
        onToggleSort={toggleSort}
      />,
    );
    expect(screen.getByText("Timestamp")).toBeInTheDocument();
    expect(screen.getByText("Entity Type")).toBeInTheDocument();
    expect(screen.getByText("Entity ID")).toBeInTheDocument();
    expect(screen.getByText("Action")).toBeInTheDocument();
  });

  it("renders rows for each audit log entry", () => {
    renderWithTooltip(
      <AuditLogTable logs={mockLogs} isLoading={false} />,
    );
    // Action badges
    expect(screen.getByText("Created")).toBeInTheDocument();
    expect(screen.getByText("Updated")).toBeInTheDocument();
    // Entity type labels
    expect(screen.getByText("Receipt")).toBeInTheDocument();
    expect(screen.getByText("Account")).toBeInTheDocument();
  });

  it("displays truncated entity IDs", () => {
    renderWithTooltip(
      <AuditLogTable logs={mockLogs} isLoading={false} />,
    );
    // truncateId truncates to 8 chars + "..." -- both logs share the prefix
    const truncatedIds = screen.getAllByText("entity-0...");
    expect(truncatedIds.length).toBe(2);
  });

  it("shows change count for entries with changes", () => {
    renderWithTooltip(
      <AuditLogTable logs={mockLogs} isLoading={false} />,
    );
    // The second log has 1 change
    expect(screen.getByText("1")).toBeInTheDocument();
  });

  it("labels writes with no user and no API key as System", () => {
    const systemLog: AuditLog[] = [
      {
        id: "log-sys",
        entityType: "ItemEmbedding",
        entityId: "embedding-001",
        action: "Created",
        changesJson: "[]",
        changedByUserId: null,
        changedByApiKeyId: null,
        changedAt: "2025-01-17T09:00:00Z",
        ipAddress: null,
      },
    ];
    renderWithTooltip(<AuditLogTable logs={systemLog} isLoading={false} />);
    expect(screen.getByText("System")).toBeInTheDocument();
  });

  it("resolves a changed-by user id to a friendly label", () => {
    renderWithTooltip(
      <AuditLogTable
        logs={mockLogs}
        isLoading={false}
        userLabels={{ "user-001-full-uuid": "ada@receipts.local" }}
      />,
    );
    expect(screen.getByText("ada@receipts.local")).toBeInTheDocument();
  });
});

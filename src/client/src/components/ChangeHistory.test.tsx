import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { ChangeHistory } from "./ChangeHistory";
import { TooltipProvider } from "@/components/ui/tooltip";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { mockQueryResult } from "@/test/mock-hooks";

vi.mock("@/hooks/useAudit", () => ({
  useEntityAuditHistory: vi.fn(),
}));

import { useEntityAuditHistory } from "@/hooks/useAudit";

const mockUseEntityAuditHistory = vi.mocked(useEntityAuditHistory);

function renderWithProviders(ui: React.ReactElement) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <TooltipProvider>{ui}</TooltipProvider>
    </QueryClientProvider>,
  );
}

describe("ChangeHistory", () => {
  it("renders loading skeletons when data is loading", () => {
    mockUseEntityAuditHistory.mockReturnValue(mockQueryResult({
      data: undefined,
      isLoading: true,
    }));

    const { container } = renderWithProviders(
      <ChangeHistory entityType="Receipt" entityId="abc-123" />,
    );
    const skeletons = container.querySelectorAll('[data-slot="skeleton"]');
    expect(skeletons.length).toBeGreaterThan(0);
  });

  it("renders empty state when no history is available", () => {
    mockUseEntityAuditHistory.mockReturnValue(mockQueryResult({
      data: [],
      isLoading: false,
    }));

    renderWithProviders(
      <ChangeHistory entityType="Receipt" entityId="abc-123" />,
    );
    expect(
      screen.getByText("No history available for this entity."),
    ).toBeInTheDocument();
  });

  it("renders empty state when data is undefined (null-ish)", () => {
    mockUseEntityAuditHistory.mockReturnValue(mockQueryResult({
      data: undefined,
      isLoading: false,
    }));

    renderWithProviders(
      <ChangeHistory entityType="Receipt" entityId="abc-123" />,
    );
    expect(
      screen.getByText("No history available for this entity."),
    ).toBeInTheDocument();
  });

  it("renders timeline entries when data is available", () => {
    mockUseEntityAuditHistory.mockReturnValue(mockQueryResult({
      data: [
        {
          id: "log-1",
          entityType: "Receipt",
          entityId: "abc-123",
          action: "Created",
          changesJson: "[]",
          changedByUserId: "user-001-abcde",
          changedByApiKeyId: null,
          changedAt: new Date().toISOString(),
          ipAddress: "192.168.1.1",
        },
        {
          id: "log-2",
          entityType: "Receipt",
          entityId: "abc-123",
          action: "Updated",
          changesJson: JSON.stringify([
            { field: "description", oldValue: "Old", newValue: "New" },
          ]),
          changedByUserId: null,
          changedByApiKeyId: null,
          changedAt: new Date().toISOString(),
          ipAddress: null,
        },
      ],
      isLoading: false,
    }));

    renderWithProviders(
      <ChangeHistory entityType="Receipt" entityId="abc-123" />,
    );
    expect(screen.getByText("Created")).toBeInTheDocument();
    expect(screen.getByText("Updated")).toBeInTheDocument();
  });

  it("keeps populated history in a definite-height scroll container", () => {
    mockUseEntityAuditHistory.mockReturnValue(mockQueryResult({
      data: [
        {
          id: "log-overflow",
          entityType: "Receipt",
          entityId: "abc-123",
          action: "Updated",
          changesJson: "[]",
          changedByUserId: null,
          changedByApiKeyId: null,
          changedAt: new Date().toISOString(),
          ipAddress: null,
        },
      ],
      isLoading: false,
    }));

    const { container } = renderWithProviders(
      <ChangeHistory entityType="Receipt" entityId="abc-123" />,
    );

    const root = container.querySelector<HTMLElement>('[data-slot="scroll-area"]');
    const viewport = container.querySelector<HTMLElement>(
      '[data-slot="scroll-area-viewport"]',
    );

    expect(root).not.toBeNull();
    expect(root).toHaveClass("h-[400px]");
    expect(root).not.toHaveClass("max-h-[400px]");
    expect(viewport).not.toBeNull();
    expect(viewport?.parentElement).toBe(root);
    expect(viewport).toHaveClass("size-full");
    expect(viewport?.style.overflowY).toBe("scroll");
  });

  it("passes correct entityType and entityId to the hook", () => {
    mockUseEntityAuditHistory.mockReturnValue(mockQueryResult({
      data: [],
      isLoading: false,
    }));

    renderWithProviders(
      <ChangeHistory entityType="Account" entityId="xyz-789" />,
    );
    expect(mockUseEntityAuditHistory).toHaveBeenCalledWith("Account", "xyz-789");
  });

  it("timestamp tooltip trigger is keyboard-focusable (native button element)", () => {
    const changedAt = new Date("2025-01-15T10:30:00Z").toISOString();
    mockUseEntityAuditHistory.mockReturnValue(mockQueryResult({
      data: [
        {
          id: "log-ts",
          entityType: "Receipt",
          entityId: "abc-123",
          action: "Created",
          changesJson: "[]",
          changedByUserId: null,
          changedByApiKeyId: null,
          changedAt,
          ipAddress: null,
        },
      ],
      isLoading: false,
    }));

    renderWithProviders(
      <ChangeHistory entityType="Receipt" entityId="abc-123" />,
    );

    // Timestamp trigger is a native <button>, which is keyboard-focusable by default
    const buttons = screen.getAllByRole("button");
    expect(buttons.length).toBeGreaterThan(0);
  });

  it("user ID tooltip trigger is keyboard-focusable and exposes full ID in aria-label", () => {
    const fullUserId = "550e8400-e29b-41d4-a716-446655440000";
    mockUseEntityAuditHistory.mockReturnValue(mockQueryResult({
      data: [
        {
          id: "log-user",
          entityType: "Receipt",
          entityId: "abc-123",
          action: "Updated",
          changesJson: "[]",
          changedByUserId: fullUserId,
          changedByApiKeyId: null,
          changedAt: new Date().toISOString(),
          ipAddress: null,
        },
      ],
      isLoading: false,
    }));

    renderWithProviders(
      <ChangeHistory entityType="Receipt" entityId="abc-123" />,
    );

    // The user ID trigger is a native <button> and has an aria-label with the full ID
    const userIdButton = screen.getByRole("button", {
      name: new RegExp(fullUserId),
    });
    expect(userIdButton).toBeInTheDocument();
  });

  it("API key tooltip trigger is keyboard-focusable and exposes full ID in aria-label", () => {
    const fullApiKeyId = "660e8400-e29b-41d4-a716-446655440001";
    mockUseEntityAuditHistory.mockReturnValue(mockQueryResult({
      data: [
        {
          id: "log-apikey",
          entityType: "Receipt",
          entityId: "abc-123",
          action: "Created",
          changesJson: "[]",
          changedByUserId: null,
          changedByApiKeyId: fullApiKeyId,
          changedAt: new Date().toISOString(),
          ipAddress: null,
        },
      ],
      isLoading: false,
    }));

    renderWithProviders(
      <ChangeHistory entityType="Receipt" entityId="abc-123" />,
    );

    // The API key trigger is a native <button> and has an aria-label with the full ID
    const apiKeyButton = screen.getByRole("button", {
      name: new RegExp(fullApiKeyId),
    });
    expect(apiKeyButton).toBeInTheDocument();
  });

  it("tooltip triggers are native button elements (keyboard-accessible without tabIndex)", () => {
    mockUseEntityAuditHistory.mockReturnValue(mockQueryResult({
      data: [
        {
          id: "log-role",
          entityType: "Receipt",
          entityId: "abc-123",
          action: "Created",
          changesJson: "[]",
          changedByUserId: "user-001",
          changedByApiKeyId: null,
          changedAt: new Date().toISOString(),
          ipAddress: null,
        },
      ],
      isLoading: false,
    }));

    const { container } = renderWithProviders(
      <ChangeHistory entityType="Receipt" entityId="abc-123" />,
    );

    // Tooltip triggers are native <button> elements — keyboard-accessible by default,
    // no explicit tabIndex needed, and no ARIA role mismatch.
    const buttons = container.querySelectorAll("button[aria-label]");
    expect(buttons.length).toBeGreaterThanOrEqual(2);
  });

  it("full timestamp is exposed in aria-label for screen readers", () => {
    const changedAt = new Date("2025-06-01T14:00:00Z").toISOString();
    mockUseEntityAuditHistory.mockReturnValue(mockQueryResult({
      data: [
        {
          id: "log-aria",
          entityType: "Receipt",
          entityId: "abc-123",
          action: "Deleted",
          changesJson: "[]",
          changedByUserId: null,
          changedByApiKeyId: null,
          changedAt,
          ipAddress: null,
        },
      ],
      isLoading: false,
    }));

    const { container } = renderWithProviders(
      <ChangeHistory entityType="Receipt" entityId="abc-123" />,
    );

    // The aria-label on the timestamp trigger must expose the full formatted timestamp
    const timestampTrigger = container.querySelector(
      '[aria-label^="Timestamp:"]',
    );
    expect(timestampTrigger).not.toBeNull();
    expect(timestampTrigger?.getAttribute("aria-label")).toContain("2025");
  });
});

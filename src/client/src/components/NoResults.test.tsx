import { describe, it, expect, vi } from "vitest";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import { NoResults } from "./NoResults";

vi.mock("@/hooks/useSearchHistory", () => ({
  useSearchHistory: vi.fn(() => ({
    history: [],
    addEntry: vi.fn(),
    clearAll: vi.fn(),
  })),
}));

import { useSearchHistory } from "@/hooks/useSearchHistory";

describe("NoResults", () => {
  const defaultProps = {
    searchTerm: "xyz",
    onClearSearch: vi.fn(),
  };

  beforeEach(() => {
    vi.mocked(useSearchHistory).mockReturnValue({
      history: [],
      addEntry: vi.fn(),
      clearAll: vi.fn(),
    });
  });

  it("displays the search term in the message", () => {
    renderWithProviders(<NoResults {...defaultProps} />);
    expect(screen.getByText("xyz")).toBeInTheDocument();
    expect(screen.queryByText(/Try fewer keywords/)).not.toBeInTheDocument();
  });

  it("uses default entity name 'results'", () => {
    renderWithProviders(<NoResults {...defaultProps} />);
    expect(screen.getByText(/No results match/)).toBeInTheDocument();
  });

  it("uses custom entity name", () => {
    renderWithProviders(
      <NoResults {...defaultProps} entityName="accounts" />,
    );
    expect(screen.getByText(/No accounts match/)).toBeInTheDocument();
  });

  it("calls onClearSearch when clear button is clicked", async () => {
    const user = userEvent.setup();
    const onClearSearch = vi.fn();
    renderWithProviders(
      <NoResults {...defaultProps} onClearSearch={onClearSearch} />,
    );

    await user.click(screen.getByText("Clear search"));
    expect(onClearSearch).toHaveBeenCalledOnce();
  });

  it("shows recent search suggestions when history exists", () => {
    vi.mocked(useSearchHistory).mockReturnValue({
      history: ["alpha", "beta", "gamma"],
      addEntry: vi.fn(),
      clearAll: vi.fn(),
    });

    renderWithProviders(
      <NoResults {...defaultProps} onSelectSuggestion={vi.fn()} />,
    );

    expect(screen.getByText("Recent searches")).toBeInTheDocument();
    expect(screen.getByText("alpha")).toBeInTheDocument();
    expect(screen.getByText("beta")).toBeInTheDocument();
    expect(screen.getByText("gamma")).toBeInTheDocument();
  });

  it("calls onSelectSuggestion when a suggestion is clicked", async () => {
    const user = userEvent.setup();
    vi.mocked(useSearchHistory).mockReturnValue({
      history: ["alpha", "beta"],
      addEntry: vi.fn(),
      clearAll: vi.fn(),
    });

    const onSelectSuggestion = vi.fn();
    renderWithProviders(
      <NoResults
        {...defaultProps}
        onSelectSuggestion={onSelectSuggestion}
      />,
    );

    await user.click(screen.getByText("alpha"));
    expect(onSelectSuggestion).toHaveBeenCalledWith("alpha");
  });

  it("has role='status' so screen readers announce the empty state", () => {
    renderWithProviders(<NoResults {...defaultProps} />);
    // role="status" is an implicit aria-live="polite" region;
    // when NoResults mounts, its content is announced to screen readers.
    const statusRegion = screen.getByRole("status");
    expect(statusRegion).toBeInTheDocument();
  });

  it("role='status' region contains the no-results message", () => {
    renderWithProviders(
      <NoResults {...defaultProps} searchTerm="hello" entityName="receipts" />,
    );
    const statusRegion = screen.getByRole("status");
    expect(statusRegion).toHaveTextContent(/No receipts match/);
    expect(statusRegion).toHaveTextContent("hello");
  });
});

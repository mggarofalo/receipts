import { describe, it, expect, vi } from "vitest";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import { MobileTabbar } from "./MobileTabbar";

describe("MobileTabbar", () => {
  it("renders all four primary tabs plus a More button", () => {
    renderWithProviders(<MobileTabbar onOpenMore={() => {}} />);

    // <a> elements: Home, Receipts, New, Reports (Link renders as anchor)
    expect(screen.getByRole("link", { name: /home/i })).toBeInTheDocument();
    expect(
      screen.getByRole("link", { name: /^receipts$/i }),
    ).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /^new$/i })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /reports/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^more$/i })).toBeInTheDocument();
  });

  it("marks the Home tab active when the path is '/'", () => {
    renderWithProviders(<MobileTabbar onOpenMore={() => {}} />, { route: "/" });
    expect(screen.getByRole("link", { name: /home/i })).toHaveAttribute(
      "aria-current",
      "page",
    );
    expect(screen.getByRole("link", { name: /^receipts$/i })).not.toHaveAttribute(
      "aria-current",
    );
  });

  it("marks the Receipts tab active on /receipts but not on /receipts/new", () => {
    renderWithProviders(<MobileTabbar onOpenMore={() => {}} />, {
      route: "/receipts",
    });
    expect(screen.getByRole("link", { name: /^receipts$/i })).toHaveAttribute(
      "aria-current",
      "page",
    );
    expect(screen.getByRole("link", { name: /^new$/i })).not.toHaveAttribute(
      "aria-current",
    );
  });

  it("marks the New tab active on /receipts/new and not the Receipts tab", () => {
    renderWithProviders(<MobileTabbar onOpenMore={() => {}} />, {
      route: "/receipts/new",
    });
    expect(screen.getByRole("link", { name: /^new$/i })).toHaveAttribute(
      "aria-current",
      "page",
    );
    expect(screen.getByRole("link", { name: /^receipts$/i })).not.toHaveAttribute(
      "aria-current",
    );
  });

  it("marks the Receipts tab active on a receipt detail route", () => {
    renderWithProviders(<MobileTabbar onOpenMore={() => {}} />, {
      route: "/receipts/abc-123",
    });
    // Detail pages are still part of the receipts list workflow, so the
    // Receipts tab stays lit — keeps mental model consistent with the desktop sidebar.
    expect(screen.getByRole("link", { name: /^receipts$/i })).toHaveAttribute(
      "aria-current",
      "page",
    );
  });

  it("invokes onOpenMore when the More button is clicked", async () => {
    const onOpenMore = vi.fn();
    const user = userEvent.setup();
    renderWithProviders(<MobileTabbar onOpenMore={onOpenMore} />);

    await user.click(screen.getByRole("button", { name: /^more$/i }));
    expect(onOpenMore).toHaveBeenCalledOnce();
  });
});

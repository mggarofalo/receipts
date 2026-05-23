import { describe, it, expect, vi } from "vitest";
import { screen } from "@testing-library/react";
import { renderWithProviders } from "@/test/test-utils";
import Unauthorized from "./Unauthorized";

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

describe("Unauthorized", () => {
  it("renders the 401 code", () => {
    renderWithProviders(<Unauthorized />);
    expect(screen.getByText("401")).toBeInTheDocument();
  });

  it("uses the editorial heading from the design bundle", () => {
    renderWithProviders(<Unauthorized />);
    expect(screen.getByText(/you need to sign in/i)).toBeInTheDocument();
  });

  it("provides a Sign in link that points at /login", () => {
    renderWithProviders(<Unauthorized />);
    const link = screen.getByRole("link", { name: /sign in/i });
    expect(link).toHaveAttribute("href", "/login");
  });
});

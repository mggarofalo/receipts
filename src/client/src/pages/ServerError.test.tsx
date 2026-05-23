import { describe, it, expect, vi } from "vitest";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import ServerError from "./ServerError";

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

const navigateMock = vi.fn();

vi.mock("react-router", async (importOriginal) => {
  const actual = await importOriginal<typeof import("react-router")>();
  return {
    ...actual,
    useNavigate: vi.fn(() => navigateMock),
  };
});

describe("ServerError", () => {
  it("renders the 500 code", () => {
    renderWithProviders(<ServerError />);
    expect(screen.getByText("500")).toBeInTheDocument();
  });

  it("uses the editorial heading from the design bundle", () => {
    renderWithProviders(<ServerError />);
    expect(screen.getByText(/the kitchen is closed/i)).toBeInTheDocument();
  });

  it("provides a Try again button that navigates back", async () => {
    const user = userEvent.setup();
    renderWithProviders(<ServerError />);
    await user.click(screen.getByRole("button", { name: /try again/i }));
    expect(navigateMock).toHaveBeenCalledWith(-1);
  });

  it("provides a Dashboard link", () => {
    renderWithProviders(<ServerError />);
    const link = screen.getByRole("link", { name: /dashboard/i });
    expect(link).toHaveAttribute("href", "/");
  });
});

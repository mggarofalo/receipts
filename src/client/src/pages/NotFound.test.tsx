import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import NotFound from "./NotFound";

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

describe("NotFound", () => {
  it("renders the 404 text", () => {
    renderWithProviders(<NotFound />);
    expect(screen.getByText("404")).toBeInTheDocument();
  });

  it("renders a not-found message", () => {
    renderWithProviders(<NotFound />);
    expect(
      screen.getByText(/this page left the counter/i),
    ).toBeInTheDocument();
  });

  it("renders the descriptive sub-copy", () => {
    renderWithProviders(<NotFound />);
    expect(
      screen.getByText(/that route doesn’t exist/i),
    ).toBeInTheDocument();
  });

  it("renders the Dashboard link", () => {
    renderWithProviders(<NotFound />);
    const link = screen.getByRole("link", { name: /dashboard/i });
    expect(link).toHaveAttribute("href", "/");
  });

  it("calls navigate(-1) when the Back button is clicked", async () => {
    navigateMock.mockClear();
    const user = userEvent.setup();
    renderWithProviders(<NotFound />);
    await user.click(screen.getByRole("button", { name: /back/i }));
    expect(navigateMock).toHaveBeenCalledWith(-1);
  });
});

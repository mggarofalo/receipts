import { describe, it, expect, vi, beforeEach } from "vitest";
import { act, screen } from "@testing-library/react";
import { renderWithProviders } from "@/test/test-utils";
import { RootLayout } from "./RootLayout";
import { notifyServerError } from "@/lib/server-error-bus";

vi.mock("@/components/ui/sonner", () => ({
  Toaster: () => <div data-testid="toaster">Toaster</div>,
}));

vi.mock("@/components/ErrorBoundary", () => ({
  ErrorBoundary: ({ children }: { children: React.ReactNode }) => (
    <div data-testid="error-boundary">{children}</div>
  ),
}));

const navigateMock = vi.fn();
vi.mock("react-router", async (importOriginal) => {
  const actual = await importOriginal<typeof import("react-router")>();
  return {
    ...actual,
    useNavigate: vi.fn(() => navigateMock),
  };
});

const toastErrorMock = vi.fn();
vi.mock("sonner", () => ({
  toast: { error: (m: string) => toastErrorMock(m) },
}));

describe("RootLayout", () => {
  beforeEach(() => {
    navigateMock.mockClear();
    toastErrorMock.mockClear();
    window.sessionStorage.clear();
  });

  it("renders the ErrorBoundary wrapper", () => {
    renderWithProviders(<RootLayout />);
    expect(screen.getByTestId("error-boundary")).toBeInTheDocument();
  });

  it("renders the Toaster component", () => {
    renderWithProviders(<RootLayout />);
    expect(screen.getByTestId("toaster")).toBeInTheDocument();
  });

  it("renders ErrorBoundary as the outermost element", () => {
    renderWithProviders(<RootLayout />);
    const errorBoundary = screen.getByTestId("error-boundary");
    const toaster = screen.getByTestId("toaster");
    expect(errorBoundary).toContainElement(toaster);
  });

  describe("ServerErrorBridge (RECEIPTS-740)", () => {
    it("navigates to /error/500 on the first 5xx of the session", () => {
      renderWithProviders(<RootLayout />);
      act(() => {
        notifyServerError(500);
      });
      expect(navigateMock).toHaveBeenCalledWith("/error/500");
      expect(toastErrorMock).not.toHaveBeenCalled();
    });

    it("toasts subsequent 5xx after the first has shown the page", () => {
      window.sessionStorage.setItem("receipts:server-error-shown", "1");
      renderWithProviders(<RootLayout />);
      act(() => {
        notifyServerError(503);
      });
      expect(navigateMock).not.toHaveBeenCalled();
      expect(toastErrorMock).toHaveBeenCalledWith(
        "A server error occurred. Please try again.",
      );
    });

    it("does not bounce back to /error/500 if already there", () => {
      renderWithProviders(<RootLayout />, { route: "/error/500" });
      act(() => {
        notifyServerError(500);
      });
      expect(navigateMock).not.toHaveBeenCalled();
    });
  });
});

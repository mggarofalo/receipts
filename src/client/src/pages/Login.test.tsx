import { screen } from "@testing-library/react";
import { renderWithProviders } from "@/test/test-utils";
import Login from "./Login";

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

vi.mock("@/hooks/useAuth", () => ({
  useAuth: vi.fn(() => ({
    user: null,
    mustResetPassword: false,
    login: vi.fn(),
  })),
}));

describe("Login", () => {
  it("renders the sign in heading", () => {
    renderWithProviders(<Login />);
    expect(
      screen.getByRole("heading", { name: /sign in/i }),
    ).toBeInTheDocument();
  });

  it("renders email and password fields", () => {
    renderWithProviders(<Login />);
    expect(screen.getByLabelText(/email/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/^password/i)).toBeInTheDocument();
  });

  it("renders the sign in button", () => {
    renderWithProviders(<Login />);
    expect(
      screen.getByRole("button", { name: /sign in/i }),
    ).toBeInTheDocument();
  });

  it("renders the description text", () => {
    renderWithProviders(<Login />);
    expect(
      screen.getByText(/welcome back/i),
    ).toBeInTheDocument();
  });

  it("redirects to / when user is already authenticated", async () => {
    const { useAuth } = await import("@/hooks/useAuth");
    vi.mocked(useAuth).mockReturnValue({
      user: { email: "test@example.com" } as ReturnType<typeof useAuth>["user"],
      mustResetPassword: false,
      login: vi.fn(),
      logout: vi.fn(),
      changePassword: vi.fn(),
      isLoading: false,
    });

    renderWithProviders(<Login />);
    expect(screen.queryByRole("heading", { name: /sign in/i })).not.toBeInTheDocument();
  });

  it("redirects to /change-password when mustResetPassword is true", async () => {
    const { useAuth } = await import("@/hooks/useAuth");
    vi.mocked(useAuth).mockReturnValue({
      user: { email: "test@example.com" } as ReturnType<typeof useAuth>["user"],
      mustResetPassword: true,
      login: vi.fn(),
      logout: vi.fn(),
      changePassword: vi.fn(),
      isLoading: false,
    });

    renderWithProviders(<Login />);
    expect(screen.queryByRole("heading", { name: /sign in/i })).not.toBeInTheDocument();
  });

  it("calls login on successful form submission", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockLogin = vi.fn().mockResolvedValue(undefined);
    const { useAuth } = await import("@/hooks/useAuth");
    vi.mocked(useAuth).mockReturnValue({
      user: null,
      mustResetPassword: false,
      login: mockLogin,
      logout: vi.fn(),
      changePassword: vi.fn(),
      isLoading: false,
    });

    renderWithProviders(<Login />);
    await user.type(screen.getByLabelText(/email/i), "test@example.com");
    await user.type(screen.getByLabelText(/^password/i), "Password123");
    await user.click(screen.getByRole("button", { name: /sign in/i }));

    await vi.waitFor(() => {
      expect(mockLogin).toHaveBeenCalledWith("test@example.com", "Password123");
    });
  });

  it("shows error alert on failed form submission", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockLogin = vi.fn().mockRejectedValue(new Error("fail"));
    const { useAuth } = await import("@/hooks/useAuth");
    vi.mocked(useAuth).mockReturnValue({
      user: null,
      mustResetPassword: false,
      login: mockLogin,
      logout: vi.fn(),
      changePassword: vi.fn(),
      isLoading: false,
    });

    renderWithProviders(<Login />);
    await user.type(screen.getByLabelText(/email/i), "test@example.com");
    await user.type(screen.getByLabelText(/^password/i), "Password123");
    await user.click(screen.getByRole("button", { name: /sign in/i }));

    expect(await screen.findByText(/invalid email or password/i)).toBeInTheDocument();
  });

  it("shows timeout message when login times out", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const timeoutError = new DOMException("Signal timed out", "TimeoutError");
    const mockLogin = vi.fn().mockRejectedValue(timeoutError);
    const { useAuth } = await import("@/hooks/useAuth");
    vi.mocked(useAuth).mockReturnValue({
      user: null,
      mustResetPassword: false,
      login: mockLogin,
      logout: vi.fn(),
      changePassword: vi.fn(),
      isLoading: false,
    });

    renderWithProviders(<Login />);
    await user.type(screen.getByLabelText(/email/i), "test@example.com");
    await user.type(screen.getByLabelText(/^password/i), "Password123");
    await user.click(screen.getByRole("button", { name: /sign in/i }));

    expect(await screen.findByText(/request timed out/i)).toBeInTheDocument();
  });

  it("shows network error message when server is unreachable", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const networkError = new TypeError("Failed to fetch");
    const mockLogin = vi.fn().mockRejectedValue(networkError);
    const { useAuth } = await import("@/hooks/useAuth");
    vi.mocked(useAuth).mockReturnValue({
      user: null,
      mustResetPassword: false,
      login: mockLogin,
      logout: vi.fn(),
      changePassword: vi.fn(),
      isLoading: false,
    });

    renderWithProviders(<Login />);
    await user.type(screen.getByLabelText(/email/i), "test@example.com");
    await user.type(screen.getByLabelText(/^password/i), "Password123");
    await user.click(screen.getByRole("button", { name: /sign in/i }));

    expect(await screen.findByText(/unable to reach the server/i)).toBeInTheDocument();
  });
});

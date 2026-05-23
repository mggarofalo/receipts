import { screen } from "@testing-library/react";
import { renderWithProviders } from "@/test/test-utils";
import ChangePassword from "./ChangePassword";

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

const mockChangePassword = vi.fn();

vi.mock("@/hooks/useAuth", () => ({
  useAuth: vi.fn(() => ({
    user: { email: "test@example.com" },
    mustResetPassword: true,
    changePassword: mockChangePassword,
  })),
}));

describe("ChangePassword", () => {
  it("renders the change password heading when user must reset", () => {
    renderWithProviders(<ChangePassword />);
    expect(
      screen.getByRole("heading", { name: /change password/i }),
    ).toBeInTheDocument();
  });

  it("renders current password, new password, and confirm password fields", () => {
    renderWithProviders(<ChangePassword />);
    expect(screen.getByLabelText(/^current password/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/^new password/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/^confirm new password/i)).toBeInTheDocument();
  });

  it("renders the change password submit button", () => {
    renderWithProviders(<ChangePassword />);
    expect(
      screen.getByRole("button", { name: /change password/i }),
    ).toBeInTheDocument();
  });

  it("shows description text about password requirement", () => {
    renderWithProviders(<ChangePassword />);
    expect(
      screen.getByText(/required before continuing/i),
    ).toBeInTheDocument();
  });

  it("redirects to /login when user is null", async () => {
    const { useAuth } = await import("@/hooks/useAuth");
    vi.mocked(useAuth).mockReturnValue({
      user: null,
      mustResetPassword: false,
      changePassword: vi.fn(),
      login: vi.fn(),
      logout: vi.fn(),
      isLoading: false,
    });

    renderWithProviders(<ChangePassword />, { route: "/change-password" });
    // Navigate replaces with /login — the component renders nothing visible
    expect(screen.queryByRole("heading", { name: /change password/i })).not.toBeInTheDocument();
  });

  it("redirects to / when mustResetPassword is false", async () => {
    const { useAuth } = await import("@/hooks/useAuth");
    vi.mocked(useAuth).mockReturnValue({
      user: { email: "test@example.com" } as ReturnType<typeof useAuth>["user"],
      mustResetPassword: false,
      changePassword: vi.fn(),
      login: vi.fn(),
      logout: vi.fn(),
      isLoading: false,
    });

    renderWithProviders(<ChangePassword />);
    expect(screen.queryByRole("heading", { name: /change password/i })).not.toBeInTheDocument();
  });

  it("calls changePassword on successful form submission", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockChangePassword = vi.fn().mockResolvedValue(undefined);
    const { useAuth } = await import("@/hooks/useAuth");
    vi.mocked(useAuth).mockReturnValue({
      user: { email: "test@example.com" } as ReturnType<typeof useAuth>["user"],
      mustResetPassword: true,
      changePassword: mockChangePassword,
      login: vi.fn(),
      logout: vi.fn(),
      isLoading: false,
    });

    renderWithProviders(<ChangePassword />);
    await user.type(screen.getByLabelText(/^current password/i), "OldPass123");
    await user.type(screen.getByLabelText(/^new password/i), "NewPass456");
    await user.type(screen.getByLabelText(/^confirm new password/i), "NewPass456");
    await user.click(screen.getByRole("button", { name: /change password/i }));

    await vi.waitFor(() => {
      expect(mockChangePassword).toHaveBeenCalledWith("OldPass123", "NewPass456");
    });
  });

  it("shows error alert on failed form submission", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const mockChangePassword = vi.fn().mockRejectedValue(new Error("fail"));
    const { useAuth } = await import("@/hooks/useAuth");
    vi.mocked(useAuth).mockReturnValue({
      user: { email: "test@example.com" } as ReturnType<typeof useAuth>["user"],
      mustResetPassword: true,
      changePassword: mockChangePassword,
      login: vi.fn(),
      logout: vi.fn(),
      isLoading: false,
    });

    renderWithProviders(<ChangePassword />);
    await user.type(screen.getByLabelText(/^current password/i), "OldPass123");
    await user.type(screen.getByLabelText(/^new password/i), "NewPass456");
    await user.type(screen.getByLabelText(/^confirm new password/i), "NewPass456");
    await user.click(screen.getByRole("button", { name: /change password/i }));

    expect(await screen.findByText(/failed to change password/i)).toBeInTheDocument();
  });

  it("shows backend error_description when available", async () => {
    const user = (await import("@testing-library/user-event")).default.setup();
    const backendError = {
      error: "invalid_request",
      error_description: "Passwords must have at least one uppercase ('A'-'Z').",
    };
    const mockChangePassword = vi.fn().mockRejectedValue(backendError);
    const { useAuth } = await import("@/hooks/useAuth");
    vi.mocked(useAuth).mockReturnValue({
      user: { email: "test@example.com" } as ReturnType<typeof useAuth>["user"],
      mustResetPassword: true,
      changePassword: mockChangePassword,
      login: vi.fn(),
      logout: vi.fn(),
      isLoading: false,
    });

    renderWithProviders(<ChangePassword />);
    await user.type(screen.getByLabelText(/^current password/i), "OldPass123");
    await user.type(screen.getByLabelText(/^new password/i), "weakpass");
    await user.type(screen.getByLabelText(/^confirm new password/i), "weakpass");
    await user.click(screen.getByRole("button", { name: /change password/i }));

    expect(
      await screen.findByText("Passwords must have at least one uppercase ('A'-'Z')."),
    ).toBeInTheDocument();
  });
});

import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, act } from "@testing-library/react";
import { useContext, useState } from "react";
import { AuthProvider } from "./AuthContext";
import { AuthContext } from "./auth-context";

vi.mock("@/lib/api-client", () => {
  const mockClient = {
    GET: vi.fn(),
    POST: vi.fn(),
    PUT: vi.fn(),
    DELETE: vi.fn(),
    use: vi.fn(),
  };
  return { default: mockClient };
});

vi.mock("@/lib/auth", () => ({
  isAuthenticated: vi.fn(() => false),
  getAccessToken: vi.fn(() => null),
  parseJwtPayload: vi.fn(() => null),
  setTokens: vi.fn(),
  clearTokens: vi.fn(),
  addTokenRefreshListener: vi.fn(() => vi.fn()),
  addPasswordChangeRequiredListener: vi.fn(() => vi.fn()),
}));

import client from "@/lib/api-client";
import * as auth from "@/lib/auth";
import type { Mock } from "vitest";

const mockedClient = client as unknown as { POST: Mock; GET: Mock };
const mockedAuth = vi.mocked(auth);

// Helper JWT: header.payload.signature with payload = { email, role }
function makeJwt(email: string, role: string): string {
  const payload = btoa(JSON.stringify({ email, role }));
  return `header.${payload}.signature`;
}

function TestConsumer() {
  const ctx = useContext(AuthContext);
  if (!ctx) return <div>no context</div>;
  return (
    <div>
      <span data-testid="user">{ctx.user?.email ?? "null"}</span>
      <span data-testid="loading">{String(ctx.isLoading)}</span>
      <span data-testid="must-reset">{String(ctx.mustResetPassword)}</span>
      <button data-testid="login" onClick={() => ctx.login("a@b.com", "pw")} />
      <button data-testid="logout" onClick={() => ctx.logout()} />
      <button
        data-testid="change-pw"
        onClick={() => ctx.changePassword("old", "new")}
      />
    </div>
  );
}

describe("AuthProvider", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockedAuth.isAuthenticated.mockReturnValue(false);
    mockedAuth.getAccessToken.mockReturnValue(null);
    mockedAuth.parseJwtPayload.mockReturnValue(null);
    mockedAuth.addTokenRefreshListener.mockReturnValue(vi.fn());
  });

  it("provides initial null user when no token is stored", () => {
    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    );

    expect(screen.getByTestId("user")).toHaveTextContent("null");
    expect(screen.getByTestId("loading")).toHaveTextContent("false");
    expect(screen.getByTestId("must-reset")).toHaveTextContent("false");
  });

  it("parses stored token on mount and provides user", () => {
    const token = makeJwt("user@test.com", "User");
    mockedAuth.isAuthenticated.mockReturnValue(true);
    mockedAuth.getAccessToken.mockReturnValue(token);
    mockedAuth.parseJwtPayload.mockReturnValue({
      userId: "user-id",
      email: "user@test.com",
      roles: ["User"],
      mustResetPassword: false,
    });

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    );

    expect(screen.getByTestId("user")).toHaveTextContent("user@test.com");
  });

  it("initializes mustResetPassword from JWT claim", () => {
    const token = makeJwt("admin@test.com", "Admin");
    mockedAuth.isAuthenticated.mockReturnValue(true);
    mockedAuth.getAccessToken.mockReturnValue(token);
    mockedAuth.parseJwtPayload.mockReturnValue({
      userId: "admin-id",
      email: "admin@test.com",
      roles: ["Admin"],
      mustResetPassword: true,
    });

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    );

    expect(screen.getByTestId("must-reset")).toHaveTextContent("true");
  });

  it("sets mustResetPassword when password-change-required listener fires", () => {
    let capturedListener: (() => void) | undefined;
    mockedAuth.addPasswordChangeRequiredListener.mockImplementation((cb: () => void) => {
      capturedListener = cb;
      return vi.fn();
    });

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    );

    expect(screen.getByTestId("must-reset")).toHaveTextContent("false");

    act(() => {
      capturedListener!();
    });

    expect(screen.getByTestId("must-reset")).toHaveTextContent("true");
  });

  it("login calls POST /api/auth/login, stores tokens, and sets user", async () => {
    const token = makeJwt("a@b.com", "User");
    mockedClient.POST.mockResolvedValueOnce({
      data: {
        accessToken: token,
        refreshToken: "rt-123",
        mustResetPassword: false,
      },
      error: undefined,
    });
    mockedAuth.parseJwtPayload.mockReturnValue({
      userId: "user-id",
      email: "a@b.com",
      roles: ["User"],
      mustResetPassword: false,
    });

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    );

    await act(async () => {
      screen.getByTestId("login").click();
    });

    expect(mockedClient.POST).toHaveBeenCalledWith("/api/auth/login", {
      body: { email: "a@b.com", password: "pw" },
    });
    expect(mockedAuth.setTokens).toHaveBeenCalledWith(token, "rt-123");
    expect(screen.getByTestId("user")).toHaveTextContent("a@b.com");
  });

  it("logout clears tokens and sets user to null", async () => {
    const token = makeJwt("user@test.com", "User");
    mockedAuth.isAuthenticated.mockReturnValue(true);
    mockedAuth.getAccessToken.mockReturnValue(token);
    mockedAuth.parseJwtPayload.mockReturnValue({
      userId: "user-id",
      email: "user@test.com",
      roles: ["User"],
      mustResetPassword: false,
    });
    mockedClient.POST.mockResolvedValueOnce({
      data: undefined,
      error: undefined,
    });

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    );

    expect(screen.getByTestId("user")).toHaveTextContent("user@test.com");

    await act(async () => {
      screen.getByTestId("logout").click();
    });

    expect(mockedAuth.clearTokens).toHaveBeenCalled();
    expect(screen.getByTestId("user")).toHaveTextContent("null");
  });

  it("ends the local session immediately while the remote logout is pending", async () => {
    const token = makeJwt("user@test.com", "User");
    mockedAuth.isAuthenticated.mockReturnValue(true);
    mockedAuth.getAccessToken.mockReturnValue(token);
    mockedAuth.parseJwtPayload.mockReturnValue({
      userId: "user-id", email: "user@test.com", roles: ["User"], mustResetPassword: true,
    });
    let finishLogout!: () => void;
    const remoteLogout = new Promise<void>((resolve) => { finishLogout = resolve; });
    mockedClient.POST.mockImplementationOnce(() => remoteLogout);
    render(<AuthProvider><TestConsumer /></AuthProvider>);

    try {
      await act(async () => { screen.getByTestId("logout").click(); });

      expect(mockedAuth.clearTokens).toHaveBeenCalledOnce();
      expect(screen.getByTestId("user")).toHaveTextContent("null");
      expect(screen.getByTestId("must-reset")).toHaveTextContent("false");
      expect(mockedClient.POST).toHaveBeenCalledWith("/api/auth/logout", {
        headers: { Authorization: `Bearer ${token}` },
      });
    } finally {
      await act(async () => { finishLogout(); await remoteLogout; });
    }
  });

  it("waits for remote logout before submitting a new login", async () => {
    const oldToken = makeJwt("alice@test.com", "User");
    mockedAuth.isAuthenticated.mockReturnValue(true);
    mockedAuth.getAccessToken.mockReturnValue(oldToken);
    mockedAuth.parseJwtPayload.mockReturnValue({
      userId: "alice-id", email: "alice@test.com", roles: ["User"], mustResetPassword: false,
    });
    let finishLogout!: () => void;
    const remoteLogout = new Promise<void>((resolve) => { finishLogout = resolve; });
    mockedClient.POST.mockImplementationOnce(() => remoteLogout);
    const newToken = makeJwt("bob@test.com", "User");
    mockedClient.POST.mockResolvedValueOnce({ data: { accessToken: newToken, refreshToken: "bob-refresh", mustResetPassword: false } });
    render(<AuthProvider><TestConsumer /></AuthProvider>);

    try {
      await act(async () => { screen.getByTestId("logout").click(); });
      mockedAuth.parseJwtPayload.mockReturnValue({
        userId: "bob-id", email: "bob@test.com", roles: ["User"], mustResetPassword: false,
      });
      await act(async () => { screen.getByTestId("login").click(); });

      expect(mockedClient.POST).toHaveBeenCalledTimes(1);
      expect(screen.getByTestId("user")).toHaveTextContent("null");
      expect(mockedAuth.setTokens).not.toHaveBeenCalled();

      await act(async () => { finishLogout(); await remoteLogout; });

      expect(mockedClient.POST).toHaveBeenNthCalledWith(2, "/api/auth/login", {
        body: { email: "a@b.com", password: "pw" },
      });
      expect(mockedAuth.setTokens).toHaveBeenCalledWith(newToken, "bob-refresh");
      expect(screen.getByTestId("user")).toHaveTextContent("bob@test.com");
    } finally {
      await act(async () => { finishLogout(); await remoteLogout; });
    }
  });

  it("login propagates timeout error to caller", async () => {
    const timeoutError = new DOMException("Signal timed out", "TimeoutError");
    mockedClient.POST.mockRejectedValueOnce(timeoutError);

    function ErrorCapture() {
      const ctx = useContext(AuthContext);
      const [error, setError] = useState<string>("none");
      return (
        <div>
          <span data-testid="error-type">{error}</span>
          <button
            data-testid="login-catch"
            onClick={() => ctx!.login("a@b.com", "pw").catch((e: unknown) =>
              setError(e instanceof DOMException ? e.name : "other"),
            )}
          />
        </div>
      );
    }

    render(
      <AuthProvider>
        <ErrorCapture />
      </AuthProvider>,
    );

    await act(async () => {
      screen.getByTestId("login-catch").click();
    });

    expect(screen.getByTestId("error-type")).toHaveTextContent("TimeoutError");
  });

  it("login propagates network error to caller", async () => {
    const networkError = new TypeError("Failed to fetch");
    mockedClient.POST.mockRejectedValueOnce(networkError);

    function ErrorCapture() {
      const ctx = useContext(AuthContext);
      const [error, setError] = useState<string>("none");
      return (
        <div>
          <span data-testid="error-type">{error}</span>
          <button
            data-testid="login-catch"
            onClick={() => ctx!.login("a@b.com", "pw").catch((e: unknown) =>
              setError(e instanceof TypeError ? e.message : "other"),
            )}
          />
        </div>
      );
    }

    render(
      <AuthProvider>
        <ErrorCapture />
      </AuthProvider>,
    );

    await act(async () => {
      screen.getByTestId("login-catch").click();
    });

    expect(screen.getByTestId("error-type")).toHaveTextContent("Failed to fetch");
  });

  it("token refresh listener updates user when new token is available", () => {
    let capturedRefreshListener: (() => void) | undefined;
    mockedAuth.addTokenRefreshListener.mockImplementation((cb: () => void) => {
      capturedRefreshListener = cb;
      return vi.fn();
    });

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    );

    expect(screen.getByTestId("user")).toHaveTextContent("null");

    // Simulate token refresh: new token now available in storage
    const token = makeJwt("refreshed@test.com", "User");
    mockedAuth.getAccessToken.mockReturnValue(token);
    mockedAuth.parseJwtPayload.mockReturnValue({
      userId: "refreshed-id",
      email: "refreshed@test.com",
      roles: ["User"],
      mustResetPassword: false,
    });

    act(() => {
      capturedRefreshListener!();
    });

    expect(screen.getByTestId("user")).toHaveTextContent("refreshed@test.com");
    expect(screen.getByTestId("must-reset")).toHaveTextContent("false");
  });

  it("token refresh listener clears user when no token is available", () => {
    let capturedRefreshListener: (() => void) | undefined;
    mockedAuth.addTokenRefreshListener.mockImplementation((cb: () => void) => {
      capturedRefreshListener = cb;
      return vi.fn();
    });

    // Start with a valid user
    const token = makeJwt("user@test.com", "User");
    mockedAuth.isAuthenticated.mockReturnValue(true);
    mockedAuth.getAccessToken.mockReturnValue(token);
    mockedAuth.parseJwtPayload.mockReturnValue({
      userId: "user-id",
      email: "user@test.com",
      roles: ["User"],
      mustResetPassword: false,
    });

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    );

    expect(screen.getByTestId("user")).toHaveTextContent("user@test.com");

    // Simulate token refresh failure: no token available
    mockedAuth.getAccessToken.mockReturnValue(null);
    mockedAuth.parseJwtPayload.mockReturnValue(null);

    act(() => {
      capturedRefreshListener!();
    });

    expect(screen.getByTestId("user")).toHaveTextContent("null");
    expect(screen.getByTestId("must-reset")).toHaveTextContent("false");
  });

  it("token refresh listener updates mustResetPassword from new token", () => {
    let capturedRefreshListener: (() => void) | undefined;
    mockedAuth.addTokenRefreshListener.mockImplementation((cb: () => void) => {
      capturedRefreshListener = cb;
      return vi.fn();
    });

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    );

    expect(screen.getByTestId("must-reset")).toHaveTextContent("false");

    // Simulate token refresh with mustResetPassword = true
    const token = makeJwt("user@test.com", "User");
    mockedAuth.getAccessToken.mockReturnValue(token);
    mockedAuth.parseJwtPayload.mockReturnValue({
      userId: "user-id",
      email: "user@test.com",
      roles: ["User"],
      mustResetPassword: true,
    });

    act(() => {
      capturedRefreshListener!();
    });

    expect(screen.getByTestId("must-reset")).toHaveTextContent("true");
  });

  it("login throws when API returns error in response", async () => {
    const apiError = { message: "Invalid credentials", status: 401 };
    mockedClient.POST.mockResolvedValueOnce({
      data: undefined,
      error: apiError,
    });

    function ErrorCapture() {
      const ctx = useContext(AuthContext);
      const [error, setError] = useState<string>("none");
      return (
        <div>
          <span data-testid="error-msg">{error}</span>
          <button
            data-testid="login-catch"
            onClick={() =>
              ctx!.login("a@b.com", "pw").catch((e: unknown) => {
                const err = e as { message: string };
                setError(err.message ?? "caught");
              })
            }
          />
        </div>
      );
    }

    render(
      <AuthProvider>
        <ErrorCapture />
      </AuthProvider>,
    );

    await act(async () => {
      screen.getByTestId("login-catch").click();
    });

    expect(screen.getByTestId("error-msg")).toHaveTextContent(
      "Invalid credentials",
    );
    expect(mockedAuth.setTokens).not.toHaveBeenCalled();
  });

  it("changePassword throws when API returns error in response", async () => {
    const apiError = { message: "Current password is incorrect", status: 400 };
    mockedClient.POST.mockResolvedValueOnce({
      data: undefined,
      error: apiError,
    });

    function ErrorCapture() {
      const ctx = useContext(AuthContext);
      const [error, setError] = useState<string>("none");
      return (
        <div>
          <span data-testid="error-msg">{error}</span>
          <button
            data-testid="change-pw-catch"
            onClick={() =>
              ctx!.changePassword("old", "new").catch((e: unknown) => {
                const err = e as { message: string };
                setError(err.message ?? "caught");
              })
            }
          />
        </div>
      );
    }

    render(
      <AuthProvider>
        <ErrorCapture />
      </AuthProvider>,
    );

    await act(async () => {
      screen.getByTestId("change-pw-catch").click();
    });

    expect(screen.getByTestId("error-msg")).toHaveTextContent(
      "Current password is incorrect",
    );
    expect(mockedAuth.setTokens).not.toHaveBeenCalled();
  });

  it("logout clears tokens even when POST /api/auth/logout rejects", async () => {
    const token = makeJwt("user@test.com", "User");
    mockedAuth.isAuthenticated.mockReturnValue(true);
    mockedAuth.getAccessToken.mockReturnValue(token);
    mockedAuth.parseJwtPayload.mockReturnValue({
      userId: "user-id",
      email: "user@test.com",
      roles: ["User"],
      mustResetPassword: false,
    });
    mockedClient.POST.mockRejectedValueOnce(new Error("Network error"));

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    );

    expect(screen.getByTestId("user")).toHaveTextContent("user@test.com");

    await act(async () => {
      screen.getByTestId("logout").click();
    });

    expect(mockedAuth.clearTokens).toHaveBeenCalled();
    expect(screen.getByTestId("user")).toHaveTextContent("null");
    expect(screen.getByTestId("must-reset")).toHaveTextContent("false");
  });

  it("provides null user when isAuthenticated but getAccessToken returns null", () => {
    mockedAuth.isAuthenticated.mockReturnValue(true);
    mockedAuth.getAccessToken.mockReturnValue(null);

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    );

    expect(screen.getByTestId("user")).toHaveTextContent("null");
    expect(screen.getByTestId("must-reset")).toHaveTextContent("false");
  });

  it("login does not set tokens when API returns neither error nor data", async () => {
    mockedClient.POST.mockResolvedValueOnce({
      data: undefined,
      error: undefined,
    });

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    );

    await act(async () => {
      screen.getByTestId("login").click();
    });

    expect(mockedAuth.setTokens).not.toHaveBeenCalled();
    expect(screen.getByTestId("user")).toHaveTextContent("null");
  });

  it("changePassword does not set tokens when API returns neither error nor data", async () => {
    mockedClient.POST.mockResolvedValueOnce({
      data: undefined,
      error: undefined,
    });

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    );

    await act(async () => {
      screen.getByTestId("change-pw").click();
    });

    expect(mockedAuth.setTokens).not.toHaveBeenCalled();
    expect(screen.getByTestId("user")).toHaveTextContent("null");
  });

  it("changePassword calls POST /api/auth/change-password and updates user", async () => {
    const newToken = makeJwt("a@b.com", "User");
    mockedClient.POST.mockResolvedValueOnce({
      data: {
        accessToken: newToken,
        refreshToken: "rt-new",
        mustResetPassword: false,
      },
      error: undefined,
    });
    mockedAuth.parseJwtPayload.mockReturnValue({
      userId: "user-id",
      email: "a@b.com",
      roles: ["User"],
      mustResetPassword: false,
    });

    render(
      <AuthProvider>
        <TestConsumer />
      </AuthProvider>,
    );

    await act(async () => {
      screen.getByTestId("change-pw").click();
    });

    expect(mockedClient.POST).toHaveBeenCalledWith(
      "/api/auth/change-password",
      {
        body: { currentPassword: "old", newPassword: "new" },
      },
    );
    expect(mockedAuth.setTokens).toHaveBeenCalledWith(newToken, "rt-new");
    expect(screen.getByTestId("user")).toHaveTextContent("a@b.com");
  });
});

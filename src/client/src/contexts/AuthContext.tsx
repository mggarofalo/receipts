import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { ReactNode } from "react";
import client from "@/lib/api-client";
import {
  clearTokens,
  getAccessToken,
  isAuthenticated as checkAuth,
  parseJwtPayload,
  setTokens,
  addTokenRefreshListener,
  addPasswordChangeRequiredListener,
} from "@/lib/auth";
import type { JwtPayload } from "@/lib/auth";
import { AuthContext } from "@/contexts/auth-context";

function getInitialUser(): JwtPayload | null {
  if (!checkAuth()) return null;
  const token = getAccessToken();
  return token ? parseJwtPayload(token) : null;
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const pendingLogout = useRef<Promise<void> | null>(null);
  const [user, setUser] = useState<JwtPayload | null>(getInitialUser);
  const [mustResetPassword, setMustResetPassword] = useState(
    () => getInitialUser()?.mustResetPassword ?? false,
  );

  useEffect(() => {
    const unsubRefresh = addTokenRefreshListener(() => {
      const token = getAccessToken();
      const parsed = token ? parseJwtPayload(token) : null;
      setUser(parsed);
      setMustResetPassword(parsed?.mustResetPassword ?? false);
    });
    const unsubPasswordChange = addPasswordChangeRequiredListener(() => {
      setMustResetPassword(true);
    });
    return () => {
      unsubRefresh();
      unsubPasswordChange();
    };
  }, []);

  const login = useCallback(async (email: string, password: string) => {
    // A delayed logout revokes the server's current refresh token. Finish that
    // request before allowing this tab to establish a replacement session.
    await pendingLogout.current;
    const { data, error } = await client.POST("/api/auth/login", {
      body: { email, password },
    });
    if (error) {
      throw error;
    }
    if (data) {
      setTokens(data.accessToken, data.refreshToken);
      setUser(parseJwtPayload(data.accessToken));
      setMustResetPassword(data.mustResetPassword ?? false);
    }
  }, []);

  const logout = useCallback(async () => {
    const token = getAccessToken();
    // End the local session before waiting for best-effort server revocation.
    clearTokens();
    setUser(null);
    setMustResetPassword(false);
    const completion = client.POST("/api/auth/logout", {
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    }).then(
      () => {},
      () => {}, // Local logout succeeds even if server revocation is unavailable.
    );
    pendingLogout.current = completion;
    try {
      await completion;
    } finally {
      if (pendingLogout.current === completion) pendingLogout.current = null;
    }
  }, []);

  const changePassword = useCallback(
    async (currentPassword: string, newPassword: string) => {
      const { data, error } = await client.POST(
        "/api/auth/change-password",
        {
          body: { currentPassword, newPassword },
        },
      );
      if (error) {
        throw error;
      }
      if (data) {
        setTokens(data.accessToken, data.refreshToken);
        setUser(parseJwtPayload(data.accessToken));
        setMustResetPassword(false);
      }
    },
    [],
  );

  const value = useMemo(
    () => ({
      user,
      isLoading: false,
      mustResetPassword,
      login,
      logout,
      changePassword,
    }),
    [user, mustResetPassword, login, logout, changePassword],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

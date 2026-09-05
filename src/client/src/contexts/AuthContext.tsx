import { useCallback, useLayoutEffect, useMemo, useRef, useState } from "react";
import type { ReactNode } from "react";
import { QueryClientProvider, type QueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import {
  clearTokens,
  getAccessToken,
  getSessionVersion,
  assertSessionCurrent,
  parseJwtPayload,
  setTokens,
  addSessionChangeListener,
  synchronizeStoredSession,
  addTokenRefreshListener,
  addPasswordChangeRequiredListener,
} from "@/lib/auth";
import type { JwtPayload } from "@/lib/auth";
import { AuthContext } from "@/contexts/auth-context";
import { createAppQueryClient } from "@/lib/query-client";
import { clearServerErrorPageFlag } from "@/lib/server-error-bus";
import { clearBufferedToasts } from "@/lib/signalr-toast-buffer";

interface SessionState {
  user: JwtPayload | null;
  mustResetPassword: boolean;
  version: number;
  queryClient: QueryClient;
}

function readUser(): JwtPayload | null {
  const token = getAccessToken();
  return token ? parseJwtPayload(token) : null;
}

interface AuthProviderProps {
  children: ReactNode;
  queryClientFactory?: () => QueryClient;
}

export function AuthProvider({ children, queryClientFactory = createAppQueryClient }: AuthProviderProps) {
  const pendingLogout = useRef<Promise<void> | null>(null);
  const authOperation = useRef(0);
  const [session, setSession] = useState<SessionState>(() => {
    const user = readUser();
    return {
      user,
      mustResetPassword: user?.mustResetPassword ?? false,
      version: getSessionVersion(),
      queryClient: queryClientFactory(),
    };
  });
  const currentSession = useRef(session);

  useLayoutEffect(() => {
    const publish = () => {
      const version = getSessionVersion();
      const previous = currentSession.current;
      const user = readUser();
      let queryClient = previous.queryClient;
      if (previous.version !== version) {
        // Clear the old cache before publishing the replacement identity. A
        // fresh client also contains late optimistic writes in the old cache.
        void queryClient.cancelQueries();
        queryClient.clear();
        queryClient = queryClientFactory();
        clearServerErrorPageFlag();
        clearBufferedToasts();
      }
      const next = { user, version, queryClient, mustResetPassword: user?.mustResetPassword ?? false };
      currentSession.current = next;
      setSession(next);
    };
    const unsubSession = addSessionChangeListener(publish);
    const unsubRefresh = addTokenRefreshListener(publish);
    const unsubPasswordChange = addPasswordChangeRequiredListener(() => {
      setSession((previous) => {
        const next = { ...previous, mustResetPassword: true };
        currentSession.current = next;
        return next;
      });
    });
    window.addEventListener("storage", synchronizeStoredSession);
    // Reconcile a transition between initial render and subscription.
    if (currentSession.current.version !== getSessionVersion()) publish();
    return () => {
      unsubSession();
      unsubRefresh();
      unsubPasswordChange();
      window.removeEventListener("storage", synchronizeStoredSession);
    };
  }, [queryClientFactory]);

  const login = useCallback(async (email: string, password: string) => {
    const operation = ++authOperation.current;
    const version = getSessionVersion();
    await pendingLogout.current;
    assertSessionCurrent(version);
    if (operation !== authOperation.current) throw new DOMException("Login superseded", "AbortError");
    const { data, error } = await client.POST("/api/auth/login", { body: { email, password } });
    assertSessionCurrent(version);
    if (operation !== authOperation.current) throw new DOMException("Login superseded", "AbortError");
    if (error) throw error;
    if (data) setTokens(data.accessToken, data.refreshToken);
  }, []);

  const logout = useCallback(async () => {
    authOperation.current += 1;
    const token = getAccessToken();
    clearTokens();
    const completion = client.POST("/api/auth/logout", {
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    }).then(() => {}, () => {});
    pendingLogout.current = completion;
    try {
      await completion;
    } finally {
      if (pendingLogout.current === completion) pendingLogout.current = null;
    }
  }, []);

  const changePassword = useCallback(async (currentPassword: string, newPassword: string) => {
    const operation = ++authOperation.current;
    const version = getSessionVersion();
    const { data, error } = await client.POST("/api/auth/change-password", {
      body: { currentPassword, newPassword },
    });
    assertSessionCurrent(version);
    if (operation !== authOperation.current) throw new DOMException("Password change superseded", "AbortError");
    if (error) throw error;
    if (data) setTokens(data.accessToken, data.refreshToken);
  }, []);

  const value = useMemo(() => ({
    user: session.user,
    isLoading: false,
    mustResetPassword: session.mustResetPassword,
    login,
    logout,
    changePassword,
  }), [session.user, session.mustResetPassword, login, logout, changePassword]);

  return (
    <AuthContext.Provider value={value}>
      <QueryClientProvider key={session.version} client={session.queryClient}>
        {children}
      </QueryClientProvider>
    </AuthContext.Provider>
  );
}

const LS_ACCESS_KEY = "receipts_access_token";
const LS_REFRESH_KEY = "receipts_refresh_token";

// Login, logout and password changes invalidate work from the previous session.
// Rotating tokens within the same session keeps its version unchanged.
let sessionVersion = 0;
let observedRefreshToken: string | null | undefined;

export function getSessionVersion(): number {
  const currentRefreshToken = getRefreshToken();
  // Another tab can replace credentials without calling this module's setters.
  // Treat that as a boundary; only our own token rotation preserves the version.
  if (observedRefreshToken !== undefined && observedRefreshToken !== currentRefreshToken) {
    sessionVersion += 1;
  }
  observedRefreshToken = currentRefreshToken;
  return sessionVersion;
}

type TokenRefreshListener = () => void;
const tokenRefreshListeners = new Set<TokenRefreshListener>();

export function addTokenRefreshListener(cb: TokenRefreshListener): () => void {
  tokenRefreshListeners.add(cb);
  return () => tokenRefreshListeners.delete(cb);
}

export function notifyTokenRefresh(): void {
  tokenRefreshListeners.forEach((cb) => cb());
}

type PasswordChangeRequiredListener = () => void;
const passwordChangeRequiredListeners = new Set<PasswordChangeRequiredListener>();

export function addPasswordChangeRequiredListener(cb: PasswordChangeRequiredListener): () => void {
  passwordChangeRequiredListeners.add(cb);
  return () => passwordChangeRequiredListeners.delete(cb);
}

export function notifyPasswordChangeRequired(): void {
  passwordChangeRequiredListeners.forEach((cb) => cb());
}

export function getAccessToken(): string | null {
  return localStorage.getItem(LS_ACCESS_KEY);
}

export function getRefreshToken(): string | null {
  return localStorage.getItem(LS_REFRESH_KEY);
}

export function setTokens(accessToken: string, refreshToken: string): void {
  sessionVersion += 1;
  observedRefreshToken = refreshToken;
  localStorage.setItem(LS_ACCESS_KEY, accessToken);
  localStorage.setItem(LS_REFRESH_KEY, refreshToken);
}

export function setRefreshedTokens(
  expectedSessionVersion: number,
  expectedRefreshToken: string,
  accessToken: string,
  refreshToken: string,
): boolean {
  if (getSessionVersion() !== expectedSessionVersion || getRefreshToken() !== expectedRefreshToken) {
    return false;
  }
  localStorage.setItem(LS_ACCESS_KEY, accessToken);
  localStorage.setItem(LS_REFRESH_KEY, refreshToken);
  observedRefreshToken = refreshToken;
  return true;
}

export function clearTokens(): void {
  sessionVersion += 1;
  observedRefreshToken = null;
  localStorage.removeItem(LS_ACCESS_KEY);
  localStorage.removeItem(LS_REFRESH_KEY);
}

export function isAuthenticated(): boolean {
  return getAccessToken() !== null;
}

export interface JwtPayload {
  userId: string;
  email: string;
  roles: string[];
  mustResetPassword: boolean;
}

export function parseJwtPayload(token: string): JwtPayload | null {
  try {
    const parts = token.split(".");
    if (parts.length !== 3) return null;
    const payload = JSON.parse(atob(parts[1]));
    const userId =
      payload.sub ??
      payload[
        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier"
      ] ??
      "";
    const email =
      payload.email ??
      payload[
        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"
      ] ??
      "";
    const roleClaim =
      payload.role ??
      payload["http://schemas.microsoft.com/ws/2008/06/identity/claims/role"];
    const roles = Array.isArray(roleClaim)
      ? roleClaim
      : roleClaim
        ? [roleClaim]
        : [];
    const mustResetPassword = payload.must_reset_password === "true" || payload.must_reset_password === true;
    return { userId, email, roles, mustResetPassword };
  } catch {
    return null;
  }
}

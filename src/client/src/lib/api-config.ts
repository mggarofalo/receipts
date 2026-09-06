// Append endpoint paths so a reverse-proxy base path is preserved for every transport.
export const apiBaseUrl = (import.meta.env.VITE_API_URL ?? "").replace(/\/+$/, "");
export const API_TIMEOUT_MS = 30_000;

export function apiUrl(path: `/${string}`): string {
  return `${apiBaseUrl}${path}`;
}

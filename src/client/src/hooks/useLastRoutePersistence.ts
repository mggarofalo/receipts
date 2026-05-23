import { useEffect, useRef } from "react";
import { useLocation, useNavigate } from "react-router";

/**
 * Persists the user's current route so a cold-open to "/" lands back where
 * they were last working. Closes the last DoD item in RECEIPTS-593.
 *
 * Behavior:
 * - On every authenticated location change, writes the pathname (with search)
 *   to localStorage — unless it's a route we don't want to be the landing
 *   target (root, auth flows).
 * - On the first mount only, if the current path is exactly "/" and a stored
 *   route exists and is still considered trackable, redirect to it.
 *
 * The hook is intended to mount inside `Layout`, which already lives behind
 * `ProtectedRoute` — so we don't second-guess auth state here.
 */
const STORAGE_KEY = "receipts:last-route";

const UNTRACKABLE_PREFIXES = [
  "/", // exact match check below; we don't store root
  "/login",
  "/change-password",
  "/onboarding",
];

export function isTrackableRoute(pathname: string): boolean {
  if (pathname === "/") return false;
  return !UNTRACKABLE_PREFIXES.some(
    (p) => p !== "/" && (pathname === p || pathname.startsWith(p + "/")),
  );
}

function readStoredRoute(): string | null {
  if (typeof window === "undefined") return null;
  try {
    const v = window.localStorage.getItem(STORAGE_KEY);
    if (!v) return null;
    // Cheap sanity check — must start with `/`, no protocol, no opaque host.
    if (!v.startsWith("/") || v.startsWith("//")) return null;
    return v;
  } catch {
    return null;
  }
}

function writeStoredRoute(value: string): void {
  if (typeof window === "undefined") return;
  try {
    window.localStorage.setItem(STORAGE_KEY, value);
  } catch {
    // localStorage may throw in privacy mode; failing closed is fine here.
  }
}

export function useLastRoutePersistence(): void {
  const location = useLocation();
  const navigate = useNavigate();
  // Only redirect on the very first mount of this hook in the session — we
  // never want to re-redirect mid-session if the user lands on / by clicking
  // the brand link.
  const didInitialRedirect = useRef(false);

  useEffect(() => {
    if (didInitialRedirect.current) return;
    didInitialRedirect.current = true;
    if (location.pathname !== "/") return;
    const stored = readStoredRoute();
    if (!stored) return;
    if (!isTrackableRoute(stored)) return;
    // `replace` so the redirect doesn't clutter the back-stack with `/`.
    navigate(stored, { replace: true });
    // We intentionally run only on first mount — listing `location.pathname`
    // / `navigate` would cause re-entry on every nav.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    if (!isTrackableRoute(location.pathname)) return;
    const path = location.pathname + (location.search ?? "");
    writeStoredRoute(path);
  }, [location.pathname, location.search]);
}

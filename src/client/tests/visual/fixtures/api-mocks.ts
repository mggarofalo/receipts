import type { Page, Route } from "@playwright/test";

// Deterministic API fixtures for visual-regression tests. Every endpoint the
// authenticated app touches at boot needs at least a "happy path" stub here
// or the page will sit on its loading skeleton forever. Add fixtures as
// new surfaces enter the snapshot suite.
//
// Conventions:
// - All timestamps anchor to 2026-05-24T12:00:00Z so relative-time strings
//   ("2h ago") stay frozen with page.clock.install().
// - User is a single non-admin account so admin-only surfaces stay
//   hidden in screenshots.
// - Empty arrays everywhere reduce visual surface area; tests that need
//   data should override specific routes via the `overrides` argument.

const FROZEN_NOW_ISO = "2026-05-24T12:00:00.000Z";

export const FIXTURE_USER = {
  id: "11111111-1111-1111-1111-111111111111",
  email: "vr-user@example.com",
  emailConfirmed: true,
  twoFactorEnabled: false,
  roles: ["User"],
};

const base64Url = (obj: unknown): string =>
  Buffer.from(JSON.stringify(obj))
    .toString("base64")
    .replace(/\+/g, "-")
    .replace(/\//g, "_")
    .replace(/=+$/, "");

/**
 * A structurally valid (unsigned) JWT.
 *
 * The client never verifies the signature — `parseJwtPayload` in
 * src/lib/auth.ts splits on "." and base64-decodes the payload — but it does
 * require three parts and a decodable body. An opaque placeholder string
 * decodes to null, which the auth context reads as "signed out" and bounces
 * every authenticated route to /login, so the token has to look real.
 */
export const FIXTURE_JWT = [
  base64Url({ alg: "none", typ: "JWT" }),
  base64Url({
    sub: FIXTURE_USER.id,
    email: FIXTURE_USER.email,
    role: FIXTURE_USER.roles,
    must_reset_password: false,
    // Far-future so nothing treats the session as expired.
    exp: 4102444800,
  }),
  "fixture-signature",
].join(".");

type RouteOverride = (route: Route) => Promise<unknown> | unknown;

export interface ApiMockOptions {
  /** Override the default response for a specific URL pattern. */
  overrides?: Record<string, RouteOverride>;
  /** Pretend YNAB is configured (defaults to false). */
  ynabConfigured?: boolean;
}

// Tiny helper so each handler can return JSON without ceremony.
const json = (body: unknown, status = 200): Parameters<Route["fulfill"]>[0] => ({
  status,
  contentType: "application/json",
  body: JSON.stringify(body),
});

export async function installApiMocks(page: Page, opts: ApiMockOptions = {}): Promise<void> {
  // Pin the clock so relative-time formatters output stable strings.
  await page.clock.install({ time: new Date(FROZEN_NOW_ISO) });

  const { ynabConfigured = false, overrides = {} } = opts;

  // Auth + user — checked at app boot
  await page.route("**/api/auth/me", (route) => route.fulfill(json(FIXTURE_USER)));
  await page.route("**/api/auth/login", (route) =>
    route.fulfill(
      json({ accessToken: FIXTURE_JWT, refreshToken: "fixture-refresh", user: FIXTURE_USER }),
    ),
  );

  // Reference data — empty lists keep layout predictable
  await page.route("**/api/accounts**", (route) => route.fulfill(json({ data: [], totalCount: 0 })));
  await page.route("**/api/cards**", (route) => route.fulfill(json({ data: [], totalCount: 0 })));
  await page.route("**/api/categories**", (route) => route.fulfill(json({ data: [], totalCount: 0 })));
  await page.route("**/api/subcategories**", (route) => route.fulfill(json({ data: [], totalCount: 0 })));
  await page.route("**/api/item-templates**", (route) => route.fulfill(json({ data: [], totalCount: 0 })));

  // Receipts — empty so EmptyState shows; tests can override per surface
  await page.route("**/api/receipts?*", (route) => route.fulfill(json({ data: [], totalCount: 0 })));
  await page.route("**/api/receipts/recent**", (route) => route.fulfill(json([])));

  // Dashboard widgets
  await page.route("**/api/dashboard/**", (route) => route.fulfill(json({ data: [], totalAmount: 0, totalCount: 0 })));
  await page.route("**/api/reports/**", (route) => route.fulfill(json({ data: [], totalCount: 0 })));

  // YNAB — controlled by opts.ynabConfigured
  await page.route("**/api/ynab/connection-status", (route) =>
    route.fulfill(json({ isConfigured: ynabConfigured, isConnected: ynabConfigured, lastSuccessfulSyncUtc: null })),
  );
  await page.route("**/api/ynab/status", (route) =>
    route.fulfill(
      json({
        isConfigured: ynabConfigured,
        isConnected: ynabConfigured,
        selectedBudgetId: ynabConfigured ? "budget-fixture" : null,
        lastSuccessUtc: null,
        lastFailureUtc: null,
        pushes24h: 0, successes24h: 0, failures24h: 0,
        pushes7d: 0, successes7d: 0, failures7d: 0,
        pushes30d: 0, successes30d: 0, failures30d: 0,
        rateLimit: {
          remainingRequests: 200,
          maxRequests: 200,
          requestsUsed: 0,
          windowResetAt: null,
          oldestRequestAt: null,
        },
      }),
    ),
  );
  await page.route("**/api/ynab/events**", (route) => route.fulfill(json({ data: [], totalCount: 0 })));
  await page.route("**/api/ynab/budgets", (route) => route.fulfill(json({ data: [] })));
  await page.route("**/api/ynab/rate-limit-status", (route) =>
    route.fulfill(
      json({
        remainingRequests: 200,
        maxRequests: 200,
        requestsUsed: 0,
        windowResetAt: null,
        oldestRequestAt: null,
      }),
    ),
  );
  await page.route("**/api/ynab/**", (route) => route.fulfill(json({ data: [], totalCount: 0 })));

  // API keys / audit / security — list endpoints return empty
  await page.route("**/api/api-keys**", (route) => route.fulfill(json({ data: [], totalCount: 0 })));
  await page.route("**/api/audit/**", (route) => route.fulfill(json({ data: [], totalCount: 0 })));
  await page.route("**/api/security/**", (route) => route.fulfill(json({ data: [], totalCount: 0 })));

  // SignalR — short-circuit to a 404 so the client falls back gracefully.
  // The connection-state badge in the sidebar will read "Offline" which
  // is its own deterministic visual state.
  await page.route("**/hubs/**", (route) => route.fulfill({ status: 404 }));

  // Caller overrides go last so they win on specific URL patterns.
  for (const [pattern, handler] of Object.entries(overrides)) {
    await page.route(pattern, (route) => Promise.resolve(handler(route)));
  }
}

/**
 * Configure localStorage to look like a logged-in user. Call before
 * navigating to the first page. Storage keys must match src/lib/auth.ts.
 */
export async function signInAsFixtureUser(page: Page): Promise<void> {
  // The token has to be passed in: addInitScript serialises the function and
  // sends it to the browser, so module-scope values aren't in its closure.
  await page.addInitScript(
    ([access, refresh]: [string, string]) => {
      localStorage.setItem("receipts_access_token", access);
      localStorage.setItem("receipts_refresh_token", refresh);
    },
    [FIXTURE_JWT, "fixture-refresh"] as [string, string],
  );
}

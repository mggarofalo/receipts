import { StrictMode } from "react";
import type { Mock } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router";
import { QueryClientProvider } from "@tanstack/react-query";
import { AuthContext, type AuthContextValue } from "@/contexts/auth-context";
import { createQueryClient } from "@/test/test-utils";
import type { components } from "@/generated/api";

// The real hook runs against a mocked API client, so these tests count the
// requests the page actually causes.
vi.mock("@/lib/api-client", () => ({
  default: { GET: vi.fn(), POST: vi.fn(), PUT: vi.fn(), DELETE: vi.fn() },
}));

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() },
}));

import client from "@/lib/api-client";
import { toast } from "sonner";
import {
  captureYnabCallbackArrival,
  clearYnabCallbackArrival,
  peekYnabCallbackArrival,
} from "@/lib/ynab-callback-arrival";
import YnabOAuthCallback from "./YnabOAuthCallback";

const CODE = "authorization-code-4f9a";
const STATE = "state-value-71c2";
const ARRIVAL = `?code=${CODE}&state=${STATE}`;

const oauthStatus: components["schemas"]["YnabConnectionStatusResponse"] = {
  isConfigured: true,
  isConnected: true,
  credentialSource: "oauth",
  oauthAvailable: true,
  reconnectRequired: false,
  oauthConnectedAtUtc: "2026-11-19T12:00:00Z",
  lastSuccessfulSyncUtc: null,
};

function authValue(roles: string[]): AuthContextValue {
  return {
    user: {
      userId: "user-1",
      email: "admin@example.com",
      roles,
      mustResetPassword: false,
    },
    isLoading: false,
    mustResetPassword: false,
    login: async () => {},
    logout: async () => {},
    changePassword: async () => {},
  };
}

function renderCallback(
  search: string,
  { roles = ["Admin"], strict = false } = {},
) {
  const router = createMemoryRouter(
    [
      { path: "/", element: <div data-testid="home" /> },
      { path: "/settings/ynab/callback", element: <YnabOAuthCallback /> },
      {
        path: "/settings/ynab",
        element: <div data-testid="ynab-settings">YNAB settings page</div>,
      },
    ],
    {
      initialEntries: ["/", `/settings/ynab/callback${search}`],
      initialIndex: 1,
    },
  );
  const tree = (
    <QueryClientProvider client={createQueryClient()}>
      <AuthContext.Provider value={authValue(roles)}>
        <RouterProvider router={router} />
      </AuthContext.Provider>
    </QueryClientProvider>
  );
  render(strict ? <StrictMode>{tree}</StrictMode> : tree);
  return router;
}

/** The address no longer carries the values, and Back does not return to them. */
async function expectArrivalRemovedFromHistory(
  router: ReturnType<typeof createMemoryRouter>,
) {
  await waitFor(() => {
    expect(router.state.location.pathname).toBe("/settings/ynab/callback");
    expect(router.state.location.search).toBe("");
  });
  expect(router.state.historyAction).toBe("REPLACE");
  await router.navigate(-1);
  await waitFor(() => expect(router.state.location.pathname).toBe("/"));
}

function expectValuesNotDisplayed() {
  expect(document.body.textContent).not.toContain(CODE);
  expect(document.body.textContent).not.toContain(STATE);
}

const postCalls = () =>
  (client.POST as Mock).mock.calls.filter(
    ([path]) => path === "/api/ynab/oauth/callback",
  );

beforeEach(() => {
  vi.clearAllMocks();
  clearYnabCallbackArrival();
});

describe("YnabOAuthCallback", () => {
  it("posts the code and state once, then goes to YNAB settings with a success notification", async () => {
    (client.POST as Mock).mockResolvedValue({
      data: oauthStatus,
      error: undefined,
    });

    const router = renderCallback(ARRIVAL);

    expect(await screen.findByTestId("ynab-settings")).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/settings/ynab");
    expect(router.state.location.search).toBe("");
    expect(toast.success).toHaveBeenCalledTimes(1);
    expect(toast.success).toHaveBeenCalledWith("Connected to YNAB");
    expect(postCalls()).toHaveLength(1);
    expect(postCalls()[0][1]).toEqual(
      expect.objectContaining({ body: { code: CODE, state: STATE } }),
    );
    // Neither the arrival address nor the callback page is left in history.
    await router.navigate(-1);
    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
  });

  it("uses the values captured at startup when the address was already rewritten before the app rendered", async () => {
    // What lib/sentry.ts does on a real return from YNAB, before Sentry and
    // the router exist: the router then never sees the query string.
    captureYnabCallbackArrival({
      location: {
        pathname: "/settings/ynab/callback",
        search: ARRIVAL,
        hash: "",
      } as Location,
      history: { state: null, replaceState: vi.fn() } as unknown as History,
    });
    (client.POST as Mock).mockResolvedValue({
      data: oauthStatus,
      error: undefined,
    });

    renderCallback("", { strict: true });

    expect(await screen.findByTestId("ynab-settings")).toBeInTheDocument();
    expect(postCalls()).toHaveLength(1);
    expect(postCalls()[0][1]).toEqual(
      expect.objectContaining({ body: { code: CODE, state: STATE } }),
    );
    // The page took ownership; a later visit without a page load finds nothing.
    expect(peekYnabCallbackArrival()).toBeNull();
  });

  it("removes the code and state from the address while the request is still running", async () => {
    (client.POST as Mock).mockReturnValue(new Promise(() => {}));

    const router = renderCallback(ARRIVAL);

    expect(
      await screen.findByText(/finishing the connection to ynab/i),
    ).toBeInTheDocument();
    expectValuesNotDisplayed();
    await expectArrivalRemovedFromHistory(router);
  });

  it("posts exactly once when React StrictMode runs the Effect twice", async () => {
    let resolvePost: (value: unknown) => void = () => {};
    (client.POST as Mock).mockReturnValue(
      new Promise((resolve) => {
        resolvePost = resolve;
      }),
    );

    const router = renderCallback(ARRIVAL, { strict: true });

    await waitFor(() => expect(router.state.location.search).toBe(""));
    expect(postCalls()).toHaveLength(1);

    resolvePost({ data: oauthStatus, error: undefined });
    expect(await screen.findByTestId("ynab-settings")).toBeInTheDocument();
    expect(postCalls()).toHaveLength(1);
    expect(toast.success).toHaveBeenCalledTimes(1);
  });

  it("says the connection was not made when YNAB reports the request was declined, without calling the API", async () => {
    const router = renderCallback(`?error=access_denied&state=${STATE}`);

    expect(
      await screen.findByText(/the connection was not made/i),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("link", { name: "Back to YNAB settings" }),
    ).toHaveAttribute("href", "/settings/ynab");
    expect(client.POST).not.toHaveBeenCalled();
    expect(toast.success).not.toHaveBeenCalled();
    expect(document.body.textContent).not.toContain("access_denied");
    expectValuesNotDisplayed();
    await expectArrivalRemovedFromHistory(router);
  });

  it("shows the server's detail and a way to start again when the attempt is refused with 400", async () => {
    (client.POST as Mock).mockResolvedValue({
      data: undefined,
      error: {
        status: 400,
        title: "Bad Request",
        detail: "The authorization attempt is not valid.",
      },
    });

    const router = renderCallback(ARRIVAL);

    expect(
      await screen.findByText("The authorization attempt is not valid."),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("link", { name: "Start again in YNAB settings" }),
    ).toHaveAttribute("href", "/settings/ynab");
    // A refused attempt is single-use: offering Retry would only fail again.
    expect(
      screen.queryByRole("button", { name: "Retry" }),
    ).not.toBeInTheDocument();
    expect(postCalls()).toHaveLength(1);
    expect(toast.success).not.toHaveBeenCalled();
    expectValuesNotDisplayed();
    await expectArrivalRemovedFromHistory(router);
  });

  it("offers Retry after a 503 and re-posts the same code and state", async () => {
    (client.POST as Mock)
      .mockResolvedValueOnce({ data: undefined, error: { status: 503 } })
      .mockResolvedValueOnce({ data: oauthStatus, error: undefined });

    const router = renderCallback(ARRIVAL);

    expect(
      await screen.findByText(/ynab could not be reached/i),
    ).toBeInTheDocument();
    expect(postCalls()).toHaveLength(1);
    // The address was already replaced; the retry uses the remembered values.
    expect(router.state.location.search).toBe("");
    expectValuesNotDisplayed();

    await userEvent.click(screen.getByRole("button", { name: "Retry" }));

    expect(await screen.findByTestId("ynab-settings")).toBeInTheDocument();
    expect(postCalls()).toHaveLength(2);
    for (const [, options] of postCalls()) {
      expect(options).toEqual(
        expect.objectContaining({ body: { code: CODE, state: STATE } }),
      );
    }
    expect(toast.success).toHaveBeenCalledWith("Connected to YNAB");
  });

  it("offers Retry after a network failure", async () => {
    (client.POST as Mock).mockRejectedValue(new TypeError("Failed to fetch"));

    renderCallback(ARRIVAL);

    expect(
      await screen.findByText(/ynab could not be reached/i),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Retry" })).toBeInTheDocument();
    expect(postCalls()).toHaveLength(1);
  });

  it.each([
    ["no parameters", ""],
    ["a code without a state", `?code=${CODE}`],
    ["a state without a code", `?state=${STATE}`],
  ])("shows a not-valid message for %s without calling the API", async (_name, search) => {
    const router = renderCallback(search);

    expect(
      await screen.findByText(/authorization attempt is not valid/i),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("link", { name: "Back to YNAB settings" }),
    ).toHaveAttribute("href", "/settings/ynab");
    expect(client.POST).not.toHaveBeenCalled();
    expectValuesNotDisplayed();
    await waitFor(() => expect(router.state.location.search).toBe(""));
  });

  it("does not call the API for a signed-in user who is not an Admin", async () => {
    const router = renderCallback(ARRIVAL, { roles: ["User"] });

    expect(
      await screen.findByText("Only an administrator can connect YNAB."),
    ).toBeInTheDocument();
    expect(client.POST).not.toHaveBeenCalled();
    await expectArrivalRemovedFromHistory(router);
  });
});

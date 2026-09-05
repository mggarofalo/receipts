import { useContext, useLayoutEffect, useState, type ReactNode } from "react";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import { useQueryClient, type QueryClient } from "@tanstack/react-query";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import {
  afterAll,
  afterEach,
  beforeAll,
  beforeEach,
  expect,
  it,
  vi,
} from "vitest";
import { AuthProvider } from "./AuthContext";
import { AuthContext, type AuthContextValue } from "./auth-context";
import { AppearanceProvider } from "./AppearanceContext";
import { ShortcutsProvider } from "./ShortcutsContext";
import { TooltipProvider } from "@/components/ui/tooltip";
import ApiKeys from "@/pages/ApiKeys";
import { useMyAuthAuditLog } from "@/hooks/useAuthAudit";
import { clearTokens, getAccessToken, setTokens } from "@/lib/auth";
import client from "@/lib/api-client";
import { createAppQueryClient } from "@/lib/query-client";
import { toast } from "sonner";
import {
  bufferToast,
  _flushForTesting,
  _resetForTesting,
} from "@/lib/signalr-toast-buffer";
import { useSessionMutation } from "@/hooks/useSessionMutation";

vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://session.test"));
vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));

const server = setupServer();
const token = (user: string) =>
  `e30.${btoa(JSON.stringify({ sub: user, email: user, role: "User" }))}.sig`;
const tokenResponse = (user: string) => ({
  accessToken: token(user),
  refreshToken: `${user}-refresh`,
  mustResetPassword: false,
  expiresIn: 3600,
  tokenType: "Bearer",
  scope: "",
});
const requestUser = (request: Request) =>
  request.headers.get("Authorization") === `Bearer ${token("Alice")}`
    ? "Alice"
    : "Bob";
let session: AuthContextValue;
let queryClient: QueryClient;
let sessionClients: QueryClient[];
let renders: Array<{ user: string; history: string }>;
let releasePendingResponses: Array<() => void>;

function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  releasePendingResponses.push(resolve);
  return { promise, resolve };
}

function History() {
  const auth = useContext(AuthContext)!;
  const history = useMyAuthAuditLog();
  const text =
    history.data?.map((entry) => entry.username).join(",") ?? "loading";
  useLayoutEffect(() => {
    renders.push({ user: auth.user!.email, history: text });
  });
  return <output aria-label="Security history">{text}</output>;
}

function Session() {
  const auth = useContext(AuthContext)!;
  useLayoutEffect(() => {
    session = auth;
  }, [auth]);
  const [draft] = useState(() => `${auth.user?.email ?? "anonymous"} draft`);
  return (
    <>
      <output aria-label="Current user">
        {auth.user?.email ?? "signed out"}
      </output>
      <output aria-label="Draft owner">{draft}</output>
      {auth.user && (
        <>
          <History />
          <ApiKeys />
        </>
      )}
    </>
  );
}

function mountSession(extra?: ReactNode) {
  return render(
    <AuthProvider queryClientFactory={createSessionClient}>
      <MemoryRouter>
        <AppearanceProvider>
          <ShortcutsProvider>
            <TooltipProvider>
              <Session />
              {extra}
            </TooltipProvider>
          </ShortcutsProvider>
        </AppearanceProvider>
      </MemoryRouter>
    </AuthProvider>,
  );
}

function createSessionClient() {
  const next = createAppQueryClient();
  next.setDefaultOptions({
    queries: { retry: false, staleTime: 300_000 },
    mutations: { retry: false },
  });
  sessionClients.push(next);
  queryClient = next;
  return next;
}

beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => {
  server.close();
  vi.unstubAllEnvs();
});
beforeEach(() => {
  clearTokens();
  _resetForTesting();
  vi.clearAllMocks();
  renders = [];
  releasePendingResponses = [];
  sessionClients = [];
  server.use(
    http.post("*/api/auth/login", async ({ request }) => {
      const { email } = (await request.json()) as { email: string };
      return HttpResponse.json(tokenResponse(email));
    }),
    http.post(
      "*/api/auth/logout",
      () => new HttpResponse(null, { status: 204 }),
    ),
    http.get("*/api/apikeys", ({ request }) =>
      HttpResponse.json([
        {
          id: "11111111-1111-1111-1111-111111111111",
          name: `${requestUser(request)} private key`,
          createdAt: "2026-01-01T00:00:00Z",
          isRevoked: false,
          bypassRateLimit: false,
        },
      ]),
    ),
    http.get("*/api/auth/audit/me", ({ request }) =>
      HttpResponse.json({
        data: [
          {
            id: "22222222-2222-2222-2222-222222222222",
            eventType: "Login",
            username: `${requestUser(request)} history`,
            success: true,
            timestamp: "2026-01-01T00:00:00Z",
          },
        ],
        total: 1,
        offset: 0,
        limit: 50,
      }),
    ),
  );
});
afterEach(() => {
  cleanup();
  releasePendingResponses.forEach((resolve) => resolve());
  sessionClients.forEach((client) => client.clear());
  clearTokens();
  server.resetHandlers();
});

async function showAlice() {
  setTokens(token("Alice"), "Alice-refresh");
  mountSession();
  await screen.findByText("Alice private key");
  await waitFor(() =>
    expect(screen.getByLabelText("Security history")).toHaveTextContent(
      "Alice history",
    ),
  );
}

it("never renders Alice's private metadata under Bob in the same SPA", async () => {
  await showAlice();

  await act(async () => {
    await session.logout();
  });
  await act(async () => {
    await session.login("Bob", "password");
  });

  expect(screen.queryByText("Alice private key")).not.toBeInTheDocument();
  expect(
    renders
      .filter((entry) => entry.user === "Bob")
      .some((entry) => entry.history.includes("Alice")),
  ).toBe(false);
  await screen.findByText("Bob private key");
  await waitFor(() =>
    expect(screen.getByLabelText("Security history")).toHaveTextContent(
      "Bob history",
    ),
  );
  expect(
    JSON.stringify(
      queryClient
        .getQueryCache()
        .getAll()
        .map((q) => q.state.data),
    ),
  ).not.toContain("Alice");
});

it("clears query and mutation state immediately on logout", async () => {
  await showAlice();
  const aliceClient = queryClient;
  await queryClient
    .getMutationCache()
    .build(queryClient, {
      mutationFn: async () => "Alice private mutation result",
    })
    .execute(undefined);

  await act(async () => {
    await session.logout();
  });

  expect(queryClient.getQueryCache().getAll()).toHaveLength(0);
  expect(queryClient.getMutationCache().getAll()).toHaveLength(0);
  expect(aliceClient.getQueryCache().getAll()).toHaveLength(0);
  expect(aliceClient.getMutationCache().getAll()).toHaveLength(0);
  expect(queryClient).not.toBe(aliceClient);
  expect(screen.getByLabelText("Current user")).toHaveTextContent("signed out");
  expect(screen.getByLabelText("Draft owner")).not.toHaveTextContent("Alice");
});

it("discards old-session buffered real-time notifications before Bob signs in", async () => {
  await showAlice();
  bufferToast("receipt", "created", 1, "other-user");

  await act(async () => {
    await session.logout();
    await session.login("Bob", "password");
  });
  _flushForTesting();

  expect(toast.info).not.toHaveBeenCalled();
});

it("publishes an external identity replacement with fresh data and local state", async () => {
  await showAlice();

  await act(async () => {
    localStorage.setItem("receipts_access_token", token("Bob"));
    localStorage.setItem("receipts_refresh_token", "Bob-refresh");
    window.dispatchEvent(
      new StorageEvent("storage", {
        key: "receipts_refresh_token",
        oldValue: "Alice-refresh",
        newValue: "Bob-refresh",
      }),
    );
  });

  expect(screen.getByLabelText("Current user")).toHaveTextContent("Bob");
  expect(screen.queryByText("Alice private key")).not.toBeInTheDocument();
  expect(screen.getByLabelText("Draft owner")).toHaveTextContent("Bob draft");
  await screen.findByText("Bob private key");
  expect(getAccessToken()).toBe(token("Bob"));
});

it("clears the same cache and visible identity when refresh failure expires the session", async () => {
  await showAlice();
  server.use(
    http.get("*/api/cards", () => new HttpResponse(null, { status: 401 })),
    http.post(
      "*/api/auth/refresh",
      () => new HttpResponse(null, { status: 401 }),
    ),
  );
  const originalLocation = window.location;
  Object.defineProperty(window, "location", {
    value: { href: "/" },
    configurable: true,
    writable: true,
  });
  try {
    await act(async () => {
      await client.GET("/api/cards");
    });

    expect(queryClient.getQueryCache().getAll()).toHaveLength(0);
    expect(screen.getByLabelText("Current user")).toHaveTextContent(
      "signed out",
    );
    expect(window.location.href).toBe("/login");
  } finally {
    Object.defineProperty(window, "location", {
      value: originalLocation,
      configurable: true,
      writable: true,
    });
  }
});

it.each(["login", "changePassword"] as const)(
  "a delayed prior-session %s cannot replace Bob or run its success navigation",
  async (operation) => {
    await showAlice();
    const gate = deferred();
    const started = deferred();
    const navigateOnSuccess = vi.fn();
    server.use(
      http.post(
        operation === "login"
          ? "*/api/auth/login"
          : "*/api/auth/change-password",
        async ({ request }) => {
          const body = (await request.json()) as { email?: string };
          if (body.email === "Bob")
            return HttpResponse.json(tokenResponse("Bob"));
          started.resolve();
          await gate.promise;
          return HttpResponse.json(tokenResponse("Alice"));
        },
      ),
    );
    let pending!: Promise<unknown>;
    await act(async () => {
      pending = (
        operation === "login"
          ? session.login("Alice", "password")
          : session.changePassword("old", "new")
      ).then(navigateOnSuccess, (error: unknown) => error);
      await started.promise;
    });
    await act(async () => {
      await session.login("Bob", "password");
    });

    let error: unknown;
    await act(async () => {
      gate.resolve();
      error = await pending;
    });

    expect(error).toMatchObject({ name: "AbortError" });
    expect(navigateOnSuccess).not.toHaveBeenCalled();
    expect(getAccessToken()).toBe(token("Bob"));
    expect(screen.getByLabelText("Current user")).toHaveTextContent("Bob");
  },
);

it("a slow Alice history request cannot repopulate Bob's cache", async () => {
  await showAlice();
  const gate = deferred();
  const started = deferred();
  server.use(
    http.get("*/api/auth/audit/me", async ({ request }) => {
      const user = requestUser(request);
      if (user === "Alice") {
        started.resolve();
        await gate.promise;
      }
      return HttpResponse.json({
        data: [
          {
            id: "22222222-2222-2222-2222-222222222222",
            eventType: "Login",
            username: `${user} delayed history`,
            success: true,
            timestamp: "2026-01-01T00:00:00Z",
          },
        ],
        total: 1,
        offset: 0,
        limit: 50,
      });
    }),
  );
  const pending = queryClient.invalidateQueries({ queryKey: ["auth-audit"] });
  await started.promise;
  await act(async () => {
    await session.logout();
    await session.login("Bob", "password");
  });
  await act(async () => {
    gate.resolve();
    await pending;
  });

  await waitFor(() =>
    expect(screen.getByLabelText("Security history")).toHaveTextContent(
      "Bob delayed history",
    ),
  );
  expect(
    JSON.stringify(
      queryClient
        .getQueryCache()
        .getAll()
        .map((q) => q.state.data),
    ),
  ).not.toContain("Alice");
  expect(
    renders
      .filter((entry) => entry.user === "Bob")
      .some((entry) => entry.history.includes("Alice")),
  ).toBe(false);
});

it("cancels an old API-key mutation without exposing its result or showing Bob an error toast", async () => {
  await showAlice();
  const user = userEvent.setup();
  const gate = deferred();
  const started = deferred();
  server.use(
    http.post("*/api/apikeys", async () => {
      started.resolve();
      await gate.promise;
      return HttpResponse.json({
        id: "33333333-3333-3333-3333-333333333333",
        name: "Alice generated key",
        rawKey: "Alice-secret",
        createdAt: "2026-01-01T00:00:00Z",
        bypassRateLimit: false,
      });
    }),
  );
  await user.click(screen.getByRole("button", { name: /new api key/i }));
  await user.type(
    screen.getByPlaceholderText(/paperless integration/i),
    "Alice generated key",
  );
  await user.click(screen.getByRole("button", { name: /create key/i }));
  await started.promise;
  const oldMutation = queryClient.getMutationCache().getAll()[0];
  await act(async () => {
    await session.logout();
    await session.login("Bob", "password");
  });
  await act(async () => {
    gate.resolve();
  });
  await waitFor(() => expect(oldMutation.state.status).not.toBe("pending"));

  expect(toast.error).not.toHaveBeenCalled();
  expect(toast.success).not.toHaveBeenCalled();
  expect(screen.queryByText("Alice-secret")).not.toBeInTheDocument();
  expect(queryClient.getMutationCache().getAll()).not.toContain(oldMutation);
  expect(getAccessToken()).toBe(token("Bob"));
});

it("contains a late optimistic cache write in the old client's cache and never dispatches it as Bob", async () => {
  const gate = deferred();
  const started = deferred();
  const dispatch = vi.fn(async () => "saved");
  function Editor() {
    const owner = useQueryClient();
    const mutation = useSessionMutation({
      mutationFn: dispatch,
      onMutate: async () => {
        started.resolve();
        await gate.promise;
        owner.setQueryData(["optimistic-private"], "Alice draft");
      },
    });
    return (
      <button onClick={() => mutation.mutate()}>Save optimistic draft</button>
    );
  }
  setTokens(token("Alice"), "Alice-refresh");
  mountSession(<Editor />);
  const aliceClient = queryClient;
  await userEvent
    .setup()
    .click(screen.getByRole("button", { name: "Save optimistic draft" }));
  await started.promise;
  const oldMutation = aliceClient.getMutationCache().getAll()[0];
  await act(async () => {
    await session.login("Bob", "password");
  });
  await act(async () => {
    gate.resolve();
  });
  await waitFor(() => expect(oldMutation.state.status).not.toBe("pending"));

  expect(aliceClient.getQueryData(["optimistic-private"])).toBe("Alice draft");
  expect(queryClient).not.toBe(aliceClient);
  expect(queryClient.getQueryData(["optimistic-private"])).toBeUndefined();
  expect(dispatch).not.toHaveBeenCalled();
  expect(getAccessToken()).toBe(token("Bob"));
});

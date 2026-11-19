import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";
import { renderHook, waitFor } from "@testing-library/react";
import {
  QueryClient,
  QueryClientProvider,
  QueryObserver,
} from "@tanstack/react-query";
import { createElement, type ReactNode } from "react";
import createClient, { type Middleware } from "openapi-fetch";

vi.mock("@/lib/api-client", () => ({
  default: {
    GET: vi.fn(),
    POST: vi.fn(),
    PUT: vi.fn(),
    DELETE: vi.fn(),
  },
}));

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() },
}));

import client from "@/lib/api-client";
import { toast } from "sonner";
import type { components } from "@/generated/api";
import { getRequestErrorPresentation } from "@/lib/request-error-policy";
import {
  useCompleteYnabAuthorization,
  useDisconnectYnab,
  useStartYnabAuthorization,
} from "./useYnabConnection";

type ConnectionStatus = components["schemas"]["YnabConnectionStatusResponse"];
type DisconnectResponse = components["schemas"]["YnabDisconnectResponse"];

const oauthStatus: ConnectionStatus = {
  isConfigured: true,
  isConnected: true,
  credentialSource: "oauth",
  oauthAvailable: true,
  reconnectRequired: false,
  oauthConnectedAtUtc: "2026-11-19T12:00:00Z",
  lastSuccessfulSyncUtc: null,
};

// Every cache family whose data depends on the credential in use.
const credentialDependentKeys = [
  ["ynab", "connection-status"],
  ["ynab", "status"],
  ["ynab", "budgets"],
  ["ynab", "settings", "budget"],
  ["ynab", "accounts", "budget-1"],
  ["ynab", "categories", "budget-1"],
  ["ynab", "account-mappings", "budget-1"],
  ["ynab", "envelope-mappings", "budget-1"],
  ["ynab", "stale-mappings", "budget-1"],
  ["ynab", "rate-limit-status"],
  ["ynab", "events", { offset: 0 }],
] as const;
const unrelatedKey = ["receipts", "list"] as const;

function seededClient() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: Infinity },
      mutations: { retry: false },
    },
  });
  for (const key of [...credentialDependentKeys, unrelatedKey]) {
    queryClient.setQueryData(key, { cached: true });
  }
  return queryClient;
}

function createWrapper(queryClient: QueryClient) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return createElement(
      QueryClientProvider,
      { client: queryClient },
      children,
    );
  };
}

function expectCredentialReadsInvalidated(queryClient: QueryClient) {
  for (const key of credentialDependentKeys) {
    expect(queryClient.getQueryState(key)?.isInvalidated, key.join("/")).toBe(
      true,
    );
  }
  expect(queryClient.getQueryState(unrelatedKey)?.isInvalidated).toBe(false);
}

function expectNothingInvalidated(queryClient: QueryClient) {
  for (const key of [...credentialDependentKeys, unrelatedKey]) {
    expect(queryClient.getQueryState(key)?.isInvalidated, key.join("/")).toBe(
      false,
    );
  }
}

// Run the supplied middleware on a real SDK Request to see which error owner
// it selects, without depending on a middleware object's identity.
async function requestPresentation(method: Mock, path: string) {
  const call = method.mock.calls.find(([url]) => url === path);
  const options = call?.[1] as { middleware?: Middleware[] };
  let observed: string | undefined;
  const probe = createClient<{
    "/probe": { get: { responses: { 204: { content?: never } } } };
  }>({
    baseUrl: "http://request-policy.test",
    fetch: async (request) => {
      observed = getRequestErrorPresentation(request);
      return new Response(null, { status: 204 });
    },
  });
  await probe.GET("/probe", { middleware: options.middleware });
  return observed;
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe("useStartYnabAuthorization", () => {
  it("posts to the authorization endpoint and returns the YNAB address", async () => {
    const queryClient = seededClient();
    (client.POST as Mock).mockResolvedValue({
      data: { authorizationUrl: "https://app.ynab.com/oauth/authorize?x=1" },
      error: undefined,
    });

    const { result } = renderHook(() => useStartYnabAuthorization(), {
      wrapper: createWrapper(queryClient),
    });

    await expect(result.current.mutateAsync()).resolves.toEqual({
      authorizationUrl: "https://app.ynab.com/oauth/authorize?x=1",
    });
    expect(client.POST).toHaveBeenCalledTimes(1);
    expect(client.POST).toHaveBeenCalledWith(
      "/api/ynab/oauth/authorization",
      expect.objectContaining({ middleware: expect.any(Array) }),
    );
    expect(
      await requestPresentation(
        client.POST as Mock,
        "/api/ynab/oauth/authorization",
      ),
    ).toBe("local");
    // Starting stores nothing, so no cached read is disturbed.
    expectNothingInvalidated(queryClient);
  });

  it("rejects with the server's problem document when OAuth is not configured", async () => {
    const problem = {
      status: 409,
      detail: "YNAB OAuth is not configured on this deployment",
    };
    (client.POST as Mock).mockResolvedValue({
      data: undefined,
      error: problem,
    });

    const { result } = renderHook(() => useStartYnabAuthorization(), {
      wrapper: createWrapper(seededClient()),
    });

    await expect(result.current.mutateAsync()).rejects.toBe(problem);
    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toBe(problem);
    // The settings card owns this message; the hook adds no toast.
    expect(toast.error).not.toHaveBeenCalled();
  });
});

describe("useCompleteYnabAuthorization", () => {
  it("posts the code and state, stores the returned status and invalidates every credential-dependent read", async () => {
    const queryClient = seededClient();
    (client.POST as Mock).mockResolvedValue({
      data: oauthStatus,
      error: undefined,
    });

    const { result } = renderHook(() => useCompleteYnabAuthorization(), {
      wrapper: createWrapper(queryClient),
    });

    await expect(
      result.current.mutateAsync({ code: "the-code", state: "the-state" }),
    ).resolves.toEqual(oauthStatus);

    expect(client.POST).toHaveBeenCalledTimes(1);
    expect(client.POST).toHaveBeenCalledWith(
      "/api/ynab/oauth/callback",
      expect.objectContaining({
        body: { code: "the-code", state: "the-state" },
        middleware: expect.any(Array),
      }),
    );
    expect(
      await requestPresentation(client.POST as Mock, "/api/ynab/oauth/callback"),
    ).toBe("local");
    expect(queryClient.getQueryData(["ynab", "connection-status"])).toEqual(
      oauthStatus,
    );
    expectCredentialReadsInvalidated(queryClient);
  });

  it("refetches an active credential-dependent read after connecting", async () => {
    const queryClient = seededClient();
    const budgetsRefetch = vi.fn().mockResolvedValue({ source: "refetch" });
    const unsubscribe = new QueryObserver(queryClient, {
      queryKey: ["ynab", "budgets"],
      queryFn: budgetsRefetch,
      staleTime: Infinity,
    }).subscribe(() => undefined);
    (client.POST as Mock).mockResolvedValue({
      data: oauthStatus,
      error: undefined,
    });

    const { result } = renderHook(() => useCompleteYnabAuthorization(), {
      wrapper: createWrapper(queryClient),
    });
    await result.current.mutateAsync({ code: "c", state: "s" });

    await waitFor(() => expect(budgetsRefetch).toHaveBeenCalledTimes(1));
    expect(queryClient.getQueryData(["ynab", "budgets"])).toEqual({
      source: "refetch",
    });
    unsubscribe();
  });

  it.each([
    {
      name: "a 400 refusal",
      error: {
        status: 400,
        detail: "The authorization attempt is not valid.",
      },
    },
    { name: "a bodiless 503", error: { status: 503 } },
  ])("rejects with $name, leaves the cached status alone and still repairs on settlement", async ({ error }) => {
    const queryClient = seededClient();
    (client.POST as Mock).mockResolvedValue({ data: undefined, error });

    const { result } = renderHook(() => useCompleteYnabAuthorization(), {
      wrapper: createWrapper(queryClient),
    });

    await expect(
      result.current.mutateAsync({ code: "c", state: "s" }),
    ).rejects.toBe(error);
    expect(queryClient.getQueryData(["ynab", "connection-status"])).toEqual({
      cached: true,
    });
    // The answer may have been lost after the server stored the connection.
    expectCredentialReadsInvalidated(queryClient);
    expect(toast.error).not.toHaveBeenCalled();
  });
});

describe("useDisconnectYnab", () => {
  it.each<DisconnectResponse["remoteRevocation"]>([
    "succeeded",
    "failed",
    "notAttempted",
  ])("deletes the connection, returns remoteRevocation=%s and invalidates every credential-dependent read", async (remoteRevocation) => {
    const queryClient = seededClient();
    const response: DisconnectResponse = { remoteRevocation };
    (client.DELETE as Mock).mockResolvedValue({
      data: response,
      error: undefined,
    });

    const { result } = renderHook(() => useDisconnectYnab(), {
      wrapper: createWrapper(queryClient),
    });

    await expect(result.current.mutateAsync()).resolves.toEqual(response);
    expect(client.DELETE).toHaveBeenCalledTimes(1);
    expect(client.DELETE).toHaveBeenCalledWith(
      "/api/ynab/oauth/connection",
      expect.objectContaining({ middleware: expect.any(Array) }),
    );
    expect(
      await requestPresentation(
        client.DELETE as Mock,
        "/api/ynab/oauth/connection",
      ),
    ).toBe("local");
    expectCredentialReadsInvalidated(queryClient);
  });

  it("rejects when the server refuses and adds no toast of its own", async () => {
    const error = { status: 403 };
    (client.DELETE as Mock).mockResolvedValue({ data: undefined, error });

    const { result } = renderHook(() => useDisconnectYnab(), {
      wrapper: createWrapper(seededClient()),
    });

    await expect(result.current.mutateAsync()).rejects.toBe(error);
    expect(toast.error).not.toHaveBeenCalled();
  });
});

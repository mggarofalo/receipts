vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://local-policy.test"));
import { useState, type ReactNode } from "react";
import {
  act,
  cleanup,
  render,
  renderHook,
  screen,
  waitFor,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClientProvider } from "@tanstack/react-query";
import { Link, MemoryRouter, Route, Routes } from "react-router";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { toast } from "sonner";
import client from "@/lib/api-client";
import { createAppQueryClient } from "@/lib/query-client";
import { localErrorPolicy } from "@/lib/request-error-policy";
import { clearTokens, setTokens } from "@/lib/auth";
import { clearServerErrorPageFlag } from "@/lib/server-error-bus";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import { Toaster } from "@/components/ui/sonner";
import { RootLayout } from "@/components/RootLayout";
import { AppearanceProvider } from "@/contexts/AppearanceContext";

let status: number;
let requests: number;
let gate: ReturnType<typeof deferred> | undefined;
function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}
const server = setupServer(
  http.get("*/api/accounts", () => {
    requests++;
    return HttpResponse.json(
      { status, detail: "Service unavailable" },
      { status },
    );
  }),
  http.post("*/api/receipts", async () => {
    requests++;
    await gate?.promise;
    return HttpResponse.json(
      { status, detail: "Caller-owned failure" },
      { status },
    );
  }),
);
const clients: ReturnType<typeof createAppQueryClient>[] = [];
function setup() {
  const queryClient = createAppQueryClient();
  clients.push(queryClient);
  function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    );
  }
  return { queryClient, wrapper: Wrapper };
}
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  status = 503;
  requests = 0;
  gate = undefined;
  clearServerErrorPageFlag();
  setTokens("alice-access", "alice-refresh");
});
afterEach(() => {
  gate?.resolve();
  cleanup();
  clients.splice(0).forEach((queryClient) => queryClient.clear());
  toast.dismiss();
  clearTokens();
  server.resetHandlers();
});

it("presents one global503 route without retry feedback, then one bridge toast for a second operation", async () => {
  const { queryClient } = setup();
  let operation = 0;
  function Page() {
    return (
      <button
        onClick={() => {
          void queryClient
            .fetchQuery({
              queryKey: ["default-request", ++operation],
              queryFn: async () => {
                const result = await client.GET("/api/accounts");
                if (result.error) throw result.error;
                return result.data;
              },
            })
            .catch(() => {});
        }}
      >
        Load accounts
      </button>
    );
  }
  render(
    <MemoryRouter initialEntries={["/work"]}>
      <AppearanceProvider>
        <QueryClientProvider client={queryClient}>
          <Routes>
            <Route element={<RootLayout />}>
              <Route path="/work" element={<Page />} />
              <Route
                path="/error/500"
                element={
                  <>
                    <h1>Global server error route</h1>
                    <Link to="/work">Return to work</Link>
                  </>
                }
              />
            </Route>
          </Routes>
        </QueryClientProvider>
      </AppearanceProvider>
    </MemoryRouter>,
  );
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Load accounts" }));
  await screen.findByRole("heading", { name: "Global server error route" });
  await waitFor(() =>
    expect(queryClient.getQueryState(["default-request", 1])?.status).toBe(
      "error",
    ),
  );
  expect(requests).toBe(1);
  expect(document.querySelectorAll("[data-sonner-toast]")).toHaveLength(0);
  await user.click(screen.getByRole("link", { name: "Return to work" }));
  await user.click(screen.getByRole("button", { name: "Load accounts" }));
  await waitFor(() =>
    expect(queryClient.getQueryState(["default-request", 2])?.status).toBe(
      "error",
    ),
  );
  expect(requests).toBe(2);
  expect(
    screen.queryByRole("heading", { name: "Global server error route" }),
  ).not.toBeInTheDocument();
  expect(
    await screen.findByText("A server error occurred. Please try again."),
  ).toBeVisible();
  expect(document.querySelectorAll("[data-sonner-toast]")).toHaveLength(1);
});

it.each([400, 503])(
  "preserves local mutation metadata through the session wrapper for%s and presents only its inline error",
  async (code) => {
    status = code;
    const { wrapper: Wrapper } = setup();
    function Editor() {
      const [error, setError] = useState("");
      const mutation = useSessionMutation({
        ...localErrorPolicy.mutation,
        mutationFn: async () => {
          const { data, error } = await client.POST("/api/receipts", {
            ...localErrorPolicy.request,
            body: { location: "Draft", date: "2026-09-01", taxAmount: 0 },
          });
          if (error) throw error;
          return data;
        },
        onError: () => setError("Caller-owned failure"),
      });
      return (
        <>
          <button onClick={() => mutation.mutate()}>Save draft</button>
          {error && <p role="alert">{error}</p>}
        </>
      );
    }
    render(
      <MemoryRouter>
        <AppearanceProvider>
          <Wrapper>
            <Routes>
              <Route element={<RootLayout />}>
                <Route path="/" element={<Editor />} />
                <Route
                  path="/error/500"
                  element={<h1>Global server error route</h1>}
                />
              </Route>
            </Routes>
          </Wrapper>
        </AppearanceProvider>
      </MemoryRouter>,
    );
    await userEvent.click(screen.getByRole("button", { name: "Save draft" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Caller-owned failure",
    );
    expect(
      screen.queryByRole("heading", { name: "Global server error route" }),
    ).not.toBeInTheDocument();
    expect(document.querySelectorAll("[data-sonner-toast]")).toHaveLength(0);
    expect(requests).toBe(1);
  },
);

it("suppresses a superseded local mutation callback and cache feedback", async () => {
  gate = deferred();
  const { wrapper, queryClient } = setup();
  const onError = vi.fn();
  render(
    <AppearanceProvider>
      <Toaster />
    </AppearanceProvider>,
  );
  const { result } = renderHook(
    () =>
      useSessionMutation({
        ...localErrorPolicy.mutation,
        mutationFn: async () => {
          const { data, error } = await client.POST("/api/receipts", {
            ...localErrorPolicy.request,
            body: { location: "Alice draft", date: "2026-09-01", taxAmount: 0 },
          });
          if (error) throw error;
          return data;
        },
        onError,
      }),
    { wrapper },
  );
  let completion!: Promise<unknown>;
  act(() => {
    completion = result.current
      .mutateAsync(undefined)
      .catch((error: unknown) => error);
  });
  await waitFor(() => expect(requests).toBe(1));
  await act(async () => {
    setTokens("bob-access", "bob-refresh");
    gate!.resolve();
    expect(await completion).toMatchObject({ name: "AbortError" });
  });
  await waitFor(() =>
    expect(queryClient.getMutationCache().getAll()[0]?.state.status).toBe(
      "error",
    ),
  );
  expect(onError).not.toHaveBeenCalled();
  expect(document.querySelectorAll("[data-sonner-toast]")).toHaveLength(0);
});

it("retains the ordinary network retry when a default query recovers", async () => {
  const { queryClient } = setup();
  server.use(
    http.get("*/api/accounts", () => {
      requests++;
      return requests === 1
        ? HttpResponse.error()
        : HttpResponse.json({ data: [], total: 0, offset: 0, limit: 50 });
    }),
  );
  render(
    <AppearanceProvider>
      <Toaster />
    </AppearanceProvider>,
  );
  const result = await queryClient.fetchQuery({
    queryKey: ["network-recovery"],
    queryFn: async () => {
      const { data, error } = await client.GET("/api/accounts");
      if (error) throw error;
      return data;
    },
  });
  expect(result).toMatchObject({ data: [], total: 0 });
  expect(requests).toBe(2);
  expect(document.querySelectorAll("[data-sonner-toast]")).toHaveLength(0);
});

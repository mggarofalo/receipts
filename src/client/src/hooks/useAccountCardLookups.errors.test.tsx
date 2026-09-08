vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://account-card-policy.test"));
import { type ReactNode } from "react";
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
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { toast, Toaster } from "sonner";
import {
  useAccountCards,
  useAccountsCards,
  useAllAccounts,
} from "./useAccounts";
import { useAllCards } from "./useCards";
import { createAppQueryClient } from "@/lib/query-client";
import { addServerErrorListener } from "@/lib/server-error-bus";
import { clearTokens, setTokens } from "@/lib/auth";

const server = setupServer();
let client: ReturnType<typeof createAppQueryClient>;
let stopErrors: () => void;
let serverErrors: ReturnType<typeof vi.fn<(status: number) => void>>;
const failure = () =>
  HttpResponse.json(
    { status: 503, detail: "Choices unavailable" },
    { status: 503 },
  );
function wrapper({ children }: { children: ReactNode }) {
  return (
    <QueryClientProvider client={client}>
      {children}
      <Toaster />
    </QueryClientProvider>
  );
}
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  localStorage.clear();
  setTokens("synthetic-access", "synthetic-refresh");
  client = createAppQueryClient();
  serverErrors = vi.fn();
  stopErrors = addServerErrorListener(serverErrors);
});
afterEach(() => {
  cleanup();
  client.clear();
  stopErrors();
  toast.dismiss();
  clearTokens();
  server.resetHandlers();
});

function Single() {
  const query = useAccountCards("account");
  return (
    <output aria-label="Single observer">
      {query.isError
        ? "unavailable"
        : query.data?.map((card) => card.name).join(",")}
    </output>
  );
}
function Multiple() {
  const query = useAccountsCards(["account"]);
  return (
    <>
      <output aria-label="Multiple observer">
        {query.isError
          ? "unavailable"
          : query.cardsByAccountId
              .get("account")
              ?.map((card) => card.name)
              .join(",")}
      </output>
      <button onClick={() => void query.refetch()}>Retry shared cards</button>
    </>
  );
}
it.each(["single-first", "multiple-first"] as const)(
  "keeps identical-key card observers locally owned in %s mount order",
  async (order) => {
    let failed = true;
    let reads = 0;
    server.use(
      http.get("*/api/accounts/account/cards", () => {
        reads += 1;
        return failed
          ? failure()
          : HttpResponse.json([
              {
                id: "card",
                accountId: "account",
                name: "Recovered card",
                cardCode: "1234",
                isActive: true,
              },
            ]);
      }),
    );
    render(
      order === "single-first" ? (
        <>
          <Single />
          <Multiple />
        </>
      ) : (
        <>
          <Multiple />
          <Single />
        </>
      ),
      { wrapper },
    );
    await waitFor(() =>
      expect(screen.getByLabelText("Single observer")).toHaveTextContent(
        "unavailable",
      ),
    );
    expect(screen.getByLabelText("Multiple observer")).toHaveTextContent(
      "unavailable",
    );
    expect(reads).toBe(1);
    expect(serverErrors).not.toHaveBeenCalled();
    expect(
      document.querySelectorAll('[data-sonner-toast][data-type="error"]'),
    ).toHaveLength(0);
    failed = false;
    await userEvent
      .setup()
      .click(screen.getByRole("button", { name: "Retry shared cards" }));
    await waitFor(() =>
      expect(screen.getByLabelText("Single observer")).toHaveTextContent(
        "Recovered card",
      ),
    );
    expect(screen.getByLabelText("Multiple observer")).toHaveTextContent(
      "Recovered card",
    );
    expect(reads).toBe(2);
    expect(
      client
        .getQueryCache()
        .findAll({ queryKey: ["cards", "byAccount", "account"], exact: true }),
    ).toHaveLength(1);
  },
);

it.each(["accounts", "cards"] as const)(
  "does not publish a partial %s catalog when its second page fails and recovers both pages on Retry",
  async (family) => {
    let failed = true;
    const offsets: number[] = [];
    const rows = Array.from({ length: 501 }, (_, i) => ({
      id: `row-${i}`,
      name: `Choice ${i}`,
      isActive: true,
      accountId: "account",
      cardCode: String(i),
    }));
    server.use(
      http.get(`*/api/${family}`, ({ request }) => {
        const offset = Number(new URL(request.url).searchParams.get("offset"));
        offsets.push(offset);
        return failed && offset === 500
          ? failure()
          : HttpResponse.json({
              data: rows.slice(offset, offset + 500),
              total: 501,
              offset,
              limit: 500,
            });
      }),
    );
    const useAll = family === "accounts" ? useAllAccounts : useAllCards;
    const { result } = renderHook(() => useAll(true), { wrapper });
    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.data).toBeUndefined();
    expect(offsets).toEqual([0, 500]);
    expect(serverErrors).not.toHaveBeenCalled();
    failed = false;
    await act(async () => {
      await result.current.refetch();
    });
    await waitFor(() => expect(result.current.data).toHaveLength(501));
    expect(result.current.data?.[500].id).toBe("row-500");
    expect(offsets).toEqual([0, 500, 0, 500]);
  },
);

it("cancels the prior account read and scopes stable Retry to the currently observed account", async () => {
  let release!: () => void;
  const held = new Promise<void>((resolve) => {
    release = resolve;
  });
  let oldSignal: AbortSignal | undefined;
  let failed = true;
  const requests: string[] = [];
  client.setQueryDefaults(["cards"], { gcTime: Infinity });
  server.use(
    http.get("*/api/accounts/:id/cards", async ({ params, request }) => {
      const id = String(params.id);
      requests.push(id);
      if (id === "old") {
        oldSignal = request.signal;
        await held;
      }
      return id === "current" && failed
        ? failure()
        : HttpResponse.json([
            {
              id: `${id}-card`,
              accountId: id,
              name: id,
              cardCode: "1234",
              isActive: true,
            },
          ]);
    }),
  );
  const { result, rerender } = renderHook(({ ids }) => useAccountsCards(ids), {
    wrapper,
    initialProps: { ids: ["old"] },
  });
  try {
    await waitFor(() => expect(oldSignal).toBeDefined());
    rerender({ ids: ["current"] });
    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(oldSignal?.aborted).toBe(true);
    const retry = result.current.refetch;
    rerender({ ids: ["current"] });
    expect(result.current.refetch).toBe(retry);
    failed = false;
    await act(async () => {
      await result.current.refetch();
    });
    await waitFor(() =>
      expect(result.current.cardsByAccountId.get("current")?.[0].name).toBe(
        "current",
      ),
    );
    expect(requests).toEqual(["old", "current", "current"]);
    await act(async () => {
      release();
    });
    expect(client.getQueryData(["cards", "byAccount", "old"])).toBeUndefined();
    expect(result.current.cardsByAccountId.has("old")).toBe(false);
  } finally {
    release();
  }
});

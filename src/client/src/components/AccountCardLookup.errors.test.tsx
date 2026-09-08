vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://account-card-errors.test"));
import { useLayoutEffect, useState, type ReactNode } from "react";
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { toast } from "sonner";
import { AuthProvider } from "@/contexts/AuthContext";
import { AppearanceProvider } from "@/contexts/AppearanceContext";
import { TooltipProvider } from "@/components/ui/tooltip";
import { RootLayout } from "@/components/RootLayout";
import { ReceiptTransactionForm } from "./ReceiptTransactionForm";
import { MergeCardsDialog } from "./MergeCardsDialog";
import Accounts from "@/pages/Accounts";
import { createAppQueryClient } from "@/lib/query-client";
import { useAccountsCards } from "@/hooks/useAccounts";
import client from "@/lib/api-client";
import { clearTokens, setTokens } from "@/lib/auth";
import {
  clearServerErrorPageFlag,
  markServerErrorPageShown,
} from "@/lib/server-error-bus";
import "@/test/setup-combobox-polyfills";

const account = { id: "account", name: "Historical account", isActive: true };
const card = {
  id: "card",
  accountId: account.id,
  name: "Historical card",
  cardCode: "1111",
  isActive: false,
};
const defaults = {
  accountId: account.id,
  cardId: card.id,
  amount: 8,
  date: "2024-01-15",
};
function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}
let gate: ReturnType<typeof deferred>;
let failure: "accounts" | "cards" | undefined;
let holdCards: boolean;
let cardsEmpty: boolean;
let writes: { path: string; body: unknown }[];
let holdCreate: boolean;
let holdRename: boolean;
let createGate: ReturnType<typeof deferred>;
let renameGate: ReturnType<typeof deferred>;
let mergeFailure: boolean;
let holdMerge: boolean;
let mergeGate: ReturnType<typeof deferred>;
let queryClient: ReturnType<typeof createAppQueryClient>;
let router: ReturnType<typeof createMemoryRouter>;
const unavailable = () =>
  HttpResponse.json(
    { status: 503, detail: "Account card lookup unavailable" },
    { status: 503 },
  );
const server = setupServer(
  http.get("*/api/accounts", async () => {
    const fail = failure === "accounts";
    if (fail) await gate.promise;
    return fail
      ? unavailable()
      : HttpResponse.json({ data: [account], total: 1, offset: 0, limit: 500 });
  }),
  http.get("*/api/accounts/:id/cards", async () => {
    const fail = failure === "cards";
    if (fail || holdCards) await gate.promise;
    return fail ? unavailable() : HttpResponse.json(cardsEmpty ? [] : [card]);
  }),
  http.post("*/api/cards/merge/preview", () =>
    HttpResponse.json({
      cardsToMove: 1,
      transactionsToRepoint: 0,
      trashedTransactionsToRepoint: 0,
      accountsToRemove: [account],
      conflicts: null,
    }),
  ),
  http.post("*/api/accounts", async ({ request }) => {
    const body = await request.json();
    writes.push({ path: "accounts", body });
    if (holdCreate) await createGate.promise;
    return HttpResponse.json({ id: "created", ...(body as object) });
  }),
  http.post("*/api/cards/merge", async ({ request }) => {
    writes.push({ path: "merge", body: await request.json() });
    if (holdMerge) await mergeGate.promise;
    if (mergeFailure) return unavailable();
    return HttpResponse.json({
      cardsMoved: 1,
      transactionsRepointed: 0,
      accountsRemoved: 1,
    });
  }),
  http.put("*/api/accounts/:id", async ({ request }) => {
    writes.push({ path: "rename", body: await request.json() });
    if (holdRename) await renameGate.promise;
    return new HttpResponse(null, { status: 204 });
  }),
  http.delete("*/api/accounts/:id", ({ params }) => {
    writes.push({ path: "discard", body: String(params.id) });
    return new HttpResponse(null, { status: 204 });
  }),
);
function renderFeature(feature: ReactNode) {
  queryClient = createAppQueryClient();
  router = createMemoryRouter([
    {
      element: <RootLayout />,
      children: [
        { path: "/", element: feature },
        { path: "/error/500", element: <h1>Global server error route</h1> },
        { path: "/left", element: <h1>Other page</h1> },
      ],
    },
  ]);
  render(
    <AppearanceProvider>
      <TooltipProvider>
        <AuthProvider queryClientFactory={() => queryClient}>
          <RouterProvider router={router} />
        </AuthProvider>
      </TooltipProvider>
    </AppearanceProvider>,
  );
}
function editor(onSubmit = vi.fn()) {
  renderFeature(
    <ReceiptTransactionForm
      mode="edit"
      defaultValues={defaults}
      onSubmit={onSubmit}
      onCancel={vi.fn()}
    />,
  );
  return onSubmit;
}
beforeAll(() => {
  if (!Element.prototype.hasPointerCapture) {
    Element.prototype.hasPointerCapture = () => false;
    Element.prototype.setPointerCapture = () => {};
    Element.prototype.releasePointerCapture = () => {};
  }
  server.listen({ onUnhandledRequest: "error" });
});
afterAll(() => server.close());
beforeEach(() => {
  gate = deferred();
  createGate = deferred();
  renameGate = deferred();
  holdCreate = false;
  holdRename = false;
  mergeFailure = false;
  holdMerge = false;
  mergeGate = deferred();
  failure = undefined;
  holdCards = false;
  cardsEmpty = false;
  writes = [];
  localStorage.clear();
  clearServerErrorPageFlag();
  setTokens(
    `header.${btoa(JSON.stringify({ sub: "alice", email: "alice@example.test", exp: 4102444800 }))}.signature`,
    "refresh",
  );
});
afterEach(() => {
  gate.resolve();
  createGate.resolve();
  renameGate.resolve();
  mergeGate.resolve();
  cleanup();
  router?.dispose();
  queryClient?.clear();
  toast.dismiss();
  clearTokens();
  server.resetHandlers();
  vi.restoreAllMocks();
});

it.each(["accounts", "cards"] as const)(
  "retains an edited transaction when its %s lookup refresh fails",
  async (kind) => {
    const onSubmit = editor();
    const user = userEvent.setup();
    await waitFor(() =>
      expect(screen.getByRole("combobox", { name: /^Card/ })).toHaveTextContent(
        "Historical card (inactive)",
      ),
    );
    const amount = screen.getByLabelText(/amount/i);
    await user.clear(amount);
    await user.type(amount, "17.42");
    failure = kind;
    let refresh!: Promise<void>;
    act(() => {
      refresh = queryClient.invalidateQueries({
        queryKey:
          kind === "accounts"
            ? ["accounts", "all"]
            : ["cards", "byAccount", account.id],
      });
    });
    await act(async () => {
      gate.resolve();
      await refresh;
    });
    expect(
      screen.queryByRole("heading", { name: "Global server error route" }),
    ).not.toBeInTheDocument();
    expect(amount).toBeInTheDocument();
    expect(amount).toHaveValue("17.42");
    expect(screen.getByRole("combobox", { name: /^Card/ })).toHaveTextContent(
      "Historical card (inactive)",
    );
    expect(
      await screen.findByRole("button", {
        name: new RegExp(`retry ${kind}`, "i"),
      }),
    ).toBeVisible();
    await user.click(
      screen.getByRole("button", { name: "Update Transaction" }),
    );
    expect(onSubmit).toHaveBeenCalledWith({ ...defaults, amount: 17.42 });
  },
);

it("shows an expanded account's failed card read as unavailable rather than an empty list", async () => {
  // Isolate false-empty rendering from the independently tested first-503 navigation.
  markServerErrorPageShown();
  failure = "cards";
  renderFeature(<Accounts />);
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: "Expand cards" }));
  await act(async () => {
    gate.resolve();
  });
  await waitFor(() =>
    expect(
      queryClient.getQueryState(["cards", "byAccount", account.id])?.status,
    ).toBe("error"),
  );
  expect(
    screen.queryByText("No cards linked to this account yet."),
  ).not.toBeInTheDocument();
  expect(
    screen.getByRole("button", { name: "Collapse cards" }),
  ).toHaveAttribute("aria-expanded", "true");
  expect(screen.getByRole("button", { name: /retry/i })).toBeVisible();
});

it("shows successful empty account cards without an unavailable warning", async () => {
  cardsEmpty = true;
  renderFeature(<Accounts />);
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: "Expand cards" }));
  expect(
    await screen.findByText("No cards linked to this account yet."),
  ).toBeVisible();
  expect(
    screen.queryByRole("button", { name: /retry/i }),
  ).not.toBeInTheDocument();
});

it("preserves a historical pair when no successful card lookup has disproved it", async () => {
  holdCards = true;
  const onSubmit = editor();
  const user = userEvent.setup();
  await waitFor(() =>
    expect(
      queryClient.getQueryState(["cards", "byAccount", account.id])
        ?.fetchStatus,
    ).toBe("fetching"),
  );
  await user.click(screen.getByRole("button", { name: "Update Transaction" }));
  expect(onSubmit).toHaveBeenCalledWith(defaults);
});

it("publishes successful-empty source proof after the first pending account read", async () => {
  function SourceProof() {
    const { cardsByAccountId } = useAccountsCards([account.id]);
    return (
      <output aria-label="Source membership proof">
        {cardsByAccountId.has(account.id)
          ? JSON.stringify(cardsByAccountId.get(account.id))
          : "unknown"}
      </output>
    );
  }
  holdCards = true;
  cardsEmpty = true;
  renderFeature(<SourceProof />);
  expect(screen.getByLabelText("Source membership proof")).toHaveTextContent(
    "unknown",
  );
  await waitFor(() =>
    expect(
      queryClient.getQueryState(["cards", "byAccount", account.id])
        ?.fetchStatus,
    ).toBe("fetching"),
  );
  gate.resolve();
  await waitFor(() =>
    expect(
      queryClient.getQueryState(["cards", "byAccount", account.id])?.status,
    ).toBe("success"),
  );
  await waitFor(() =>
    expect(screen.getByLabelText("Source membership proof")).toHaveTextContent(
      "[]",
    ),
  );
});

it("retains successful nonmembership proof after a later failed card refresh", async () => {
  markServerErrorPageShown();
  cardsEmpty = true;
  const onSubmit = editor();
  await waitFor(() =>
    expect(
      queryClient.getQueryData(["cards", "byAccount", account.id]),
    ).toEqual([]),
  );
  failure = "cards";
  gate.resolve();
  await act(async () => {
    await queryClient.invalidateQueries({
      queryKey: ["cards", "byAccount", account.id],
    });
  });
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Update Transaction" }));
  expect(onSubmit).not.toHaveBeenCalled();
  expect(
    await screen.findByText(/no longer belongs to this account/i),
  ).toBeVisible();
});

it("cannot create a new merge target from a failed source-card completeness read", async () => {
  markServerErrorPageShown();
  failure = "cards";
  renderFeature(
    <MergeCardsDialog open onOpenChange={vi.fn()} selectedCards={[card]} />,
  );
  const user = userEvent.setup();
  await user.click(screen.getByRole("radio", { name: "New account" }));
  await user.type(
    screen.getByLabelText("New account name"),
    "Unverified target",
  );
  gate.resolve();
  await waitFor(() =>
    expect(
      queryClient.getQueryState(["cards", "byAccount", account.id])?.status,
    ).toBe("error"),
  );
  // Exercise the submit boundary as well as the button: pressing Enter/native form
  // submission must not authorize dependent writes from unknown source membership.
  const dialog = screen.getByRole("dialog");
  const form = within(dialog)
    .getByLabelText("New account name")
    .closest("form")!;
  fireEvent.submit(form);
  await waitFor(() => expect(queryClient.isMutating()).toBe(0));
  expect(writes).toEqual([]);
  expect(within(dialog).getByRole("button", { name: "Merge" })).toBeDisabled();
});

// These cases exercise actual pending HTTP write continuations. The selection
// setter represents the owning Cards page updating props; no hook is mocked.
it.each(["close", "selection change", "source failure"] as const)(
  "does not start an obsolete merge after held account creation and %s",
  async (change) => {
    let changeSelection!: () => void;
    function MergeOwner() {
      const [open, setOpen] = useState(true);
      const [selected, setSelected] = useState([card]);
      useLayoutEffect(() => {
        changeSelection = () => setSelected([]);
      }, []);
      return (
        <MergeCardsDialog
          open={open}
          onOpenChange={setOpen}
          selectedCards={selected}
        />
      );
    }
    holdCreate = true;
    renderFeature(<MergeOwner />);
    const user = userEvent.setup();
    await user.click(screen.getByRole("radio", { name: "New account" }));
    await user.type(
      screen.getByLabelText("New account name"),
      "Pending target",
    );
    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Merge" })).toBeEnabled(),
    );
    await user.click(screen.getByRole("button", { name: "Merge" }));
    await waitFor(() =>
      expect(writes.filter((write) => write.path === "accounts")).toHaveLength(
        1,
      ),
    );
    if (change === "close")
      await user.click(screen.getByRole("button", { name: "Cancel" }));
    else if (change === "selection change") act(changeSelection);
    else {
      failure = "cards";
      gate.resolve();
      await act(async () => {
        await queryClient.invalidateQueries({
          queryKey: ["cards", "byAccount", account.id],
        });
      });
    }
    await act(async () => {
      createGate.resolve();
    });
    await waitFor(() => expect(queryClient.isMutating()).toBe(0));
    expect(writes.filter((write) => write.path === "merge")).toEqual([]);
    if (change === "close") {
      await waitFor(() =>
        expect(writes.filter((write) => write.path === "discard")).toEqual([
          { path: "discard", body: "created" },
        ]),
      );
    } else {
      expect(writes.filter((write) => write.path === "discard")).toEqual([]);
    }
    if (change === "source failure") {
      const sourceFailure = (await screen.findAllByRole("alert")).find(
        (alert) => /Source cards are unavailable/.test(alert.textContent ?? ""),
      );
      expect(sourceFailure).toBeDefined();
      failure = undefined;
      await user.click(
        within(sourceFailure!).getByRole("button", { name: "Retry" }),
      );
      await waitFor(() =>
        expect(screen.getByRole("button", { name: "Merge" })).toBeEnabled(),
      );
      expect(writes.filter((write) => write.path === "merge")).toEqual([]);
      await user.click(screen.getByRole("button", { name: "Merge" }));
      await waitFor(() =>
        expect(writes.filter((write) => write.path === "merge")).toHaveLength(
          1,
        ),
      );
      expect(writes.filter((write) => write.path === "accounts")).toHaveLength(
        1,
      );
    }
  },
);

it("does not start an obsolete merge after closing during a held target rename", async () => {
  function MergeOwner() {
    const [open, setOpen] = useState(true);
    return (
      <MergeCardsDialog
        open={open}
        onOpenChange={setOpen}
        selectedCards={[card]}
      />
    );
  }
  // Call-through instrumentation lets the test await the real HTTP client's
  // response-body completion; neither transport nor the owning hook is mocked.
  const renameSpy = vi.spyOn(client, "PUT");
  mergeFailure = true;
  renderFeature(<MergeOwner />);
  const user = userEvent.setup();
  await user.click(screen.getByRole("radio", { name: "New account" }));
  await user.type(screen.getByLabelText("New account name"), "Original target");
  await waitFor(() =>
    expect(screen.getByRole("button", { name: "Merge" })).toBeEnabled(),
  );
  await user.click(screen.getByRole("button", { name: "Merge" }));
  await waitFor(() =>
    expect(writes.filter((write) => write.path === "merge")).toHaveLength(1),
  );
  await waitFor(() => expect(queryClient.isMutating()).toBe(0));
  mergeFailure = false;
  holdRename = true;
  await user.clear(screen.getByLabelText("New account name"));
  await user.type(screen.getByLabelText("New account name"), "Renamed target");
  await waitFor(() =>
    expect(screen.getByRole("button", { name: "Merge" })).toBeEnabled(),
  );
  await user.click(screen.getByRole("button", { name: "Merge" }));
  await waitFor(() =>
    expect(writes.filter((write) => write.path === "rename")).toHaveLength(1),
  );
  const pendingRename = renameSpy.mock.results[0].value;
  await user.click(screen.getByRole("button", { name: "Cancel" }));
  await act(async () => {
    renameGate.resolve();
    await pendingRename;
  });
  await waitFor(() => expect(queryClient.isMutating()).toBe(0));
  expect(writes.filter((write) => write.path === "merge")).toHaveLength(1);
});

it("creates one target and merges once after accepted unchanged source proof", async () => {
  const onMergeComplete = vi.fn();
  renderFeature(
    <MergeCardsDialog
      open
      onOpenChange={vi.fn()}
      selectedCards={[card]}
      onMergeComplete={onMergeComplete}
    />,
  );
  const user = userEvent.setup();
  await user.click(screen.getByRole("radio", { name: "New account" }));
  await user.type(screen.getByLabelText("New account name"), "Verified target");
  await waitFor(() =>
    expect(screen.getByRole("button", { name: "Merge" })).toBeEnabled(),
  );
  await user.click(screen.getByRole("button", { name: "Merge" }));
  await waitFor(() => expect(onMergeComplete).toHaveBeenCalledOnce());
  expect(writes.filter((write) => write.path === "accounts")).toHaveLength(1);
  expect(writes.filter((write) => write.path === "merge")).toEqual([
    {
      path: "merge",
      body: {
        targetAccountId: "created",
        sourceCardIds: [card.id],
        ynabMappingWinnerAccountId: null,
      },
    },
  ]);
});

it("finishes one unchanged merge after account creation invalidates and holds the source revalidation", async () => {
  holdCreate = true;
  const onMergeComplete = vi.fn();
  renderFeature(
    <MergeCardsDialog
      open
      onOpenChange={vi.fn()}
      selectedCards={[card]}
      onMergeComplete={onMergeComplete}
    />,
  );
  const user = userEvent.setup();
  await user.click(screen.getByRole("radio", { name: "New account" }));
  await user.type(
    screen.getByLabelText("New account name"),
    "Revalidated target",
  );
  await waitFor(() =>
    expect(screen.getByRole("button", { name: "Merge" })).toBeEnabled(),
  );
  await user.click(screen.getByRole("button", { name: "Merge" }));
  await waitFor(() =>
    expect(writes.filter((write) => write.path === "accounts")).toHaveLength(1),
  );
  holdCards = true;
  await act(async () => {
    createGate.resolve();
  });
  await waitFor(() =>
    expect(
      queryClient.getQueryState(["cards", "byAccount", account.id])
        ?.fetchStatus,
    ).toBe("fetching"),
  );
  expect(writes.filter((write) => write.path === "merge")).toEqual([]);
  await act(async () => {
    holdCards = false;
    gate.resolve();
  });
  await waitFor(() => expect(onMergeComplete).toHaveBeenCalledOnce());
  expect(writes.filter((write) => write.path === "accounts")).toHaveLength(1);
  expect(writes.filter((write) => write.path === "merge")).toHaveLength(1);
});

it.each(["failure", "success"] as const)(
  "cleans up only a failed prepared target after navigation during dispatched merge %s",
  async (outcome) => {
    holdMerge = true;
    mergeFailure = outcome === "failure";
    const onMergeComplete = vi.fn();
    renderFeature(
      <MergeCardsDialog
        open
        onOpenChange={vi.fn()}
        selectedCards={[card]}
        onMergeComplete={onMergeComplete}
      />,
    );
    const user = userEvent.setup();
    await user.click(screen.getByRole("radio", { name: "New account" }));
    await user.type(
      screen.getByLabelText("New account name"),
      "Navigation target",
    );
    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Merge" })).toBeEnabled(),
    );
    await user.click(screen.getByRole("button", { name: "Merge" }));
    await waitFor(() =>
      expect(writes.filter((write) => write.path === "merge")).toHaveLength(1),
    );
    await act(async () => {
      await router.navigate("/left");
    });
    expect(screen.getByRole("heading", { name: "Other page" })).toBeVisible();
    expect(writes.filter((write) => write.path === "discard")).toEqual([]);
    await act(async () => {
      mergeGate.resolve();
    });
    await waitFor(() => expect(queryClient.isMutating()).toBe(0));
    if (outcome === "failure")
      await waitFor(() =>
        expect(writes.filter((write) => write.path === "discard")).toEqual([
          { path: "discard", body: "created" },
        ]),
      );
    else expect(writes.filter((write) => write.path === "discard")).toEqual([]);
    expect(onMergeComplete).not.toHaveBeenCalled();
  },
);

it.each(["existing", "new"] as const)(
  "does not submit %s target work from failed preview and recovers through explicit Retry",
  async (mode) => {
    let previewFailed = true;
    server.use(
      http.get("*/api/accounts", () =>
        HttpResponse.json({
          data: [
            account,
            { id: "target", name: "Target account", isActive: true },
          ],
          total: 2,
        }),
      ),
      http.post("*/api/cards/merge/preview", () =>
        previewFailed
          ? unavailable()
          : HttpResponse.json({
              cardsToMove: 1,
              transactionsToRepoint: 0,
              trashedTransactionsToRepoint: 0,
              accountsToRemove: [account],
              conflicts: null,
            }),
      ),
    );
    const onMergeComplete = vi.fn();
    renderFeature(
      <MergeCardsDialog
        open
        onOpenChange={vi.fn()}
        selectedCards={[card]}
        onMergeComplete={onMergeComplete}
      />,
    );
    const user = userEvent.setup();
    if (mode === "new") {
      await user.click(screen.getByRole("radio", { name: "New account" }));
      await user.type(
        screen.getByLabelText("New account name"),
        "Preview target",
      );
    } else {
      await user.click(
        screen.getByRole("combobox", { name: "Target account" }),
      );
      await user.click(
        await screen.findByRole("option", { name: "Target account" }),
      );
    }
    const error = (await screen.findAllByRole("alert")).find((alert) =>
      /preview.*unavailable/i.test(alert.textContent ?? ""),
    );
    expect(error).toBeDefined();
    const dialog = screen.getByRole("dialog");
    expect(
      within(dialog).getByRole("button", { name: "Merge" }),
    ).toBeDisabled();
    fireEvent.submit(dialog.querySelector("form")!);
    await waitFor(() => expect(queryClient.isMutating()).toBe(0));
    expect(writes).toEqual([]);
    expect(router.state.location.pathname).toBe("/");
    expect(
      document.querySelectorAll('[data-sonner-toast][data-type="error"]'),
    ).toHaveLength(0);
    previewFailed = false;
    await user.click(within(error!).getByRole("button", { name: "Retry" }));
    await waitFor(() =>
      expect(
        within(dialog).getByRole("button", { name: "Merge" }),
      ).toBeEnabled(),
    );
    await user.click(within(dialog).getByRole("button", { name: "Merge" }));
    await waitFor(() => expect(onMergeComplete).toHaveBeenCalledOnce());
    expect(writes.filter((write) => write.path === "accounts")).toHaveLength(
      mode === "new" ? 1 : 0,
    );
    expect(writes.filter((write) => write.path === "merge")).toHaveLength(1);
  },
);

import { useState } from "react";
import { act, cleanup, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import {
  afterAll,
  afterEach,
  beforeAll,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from "vitest";
import { QueryClientProvider } from "@tanstack/react-query";
import {
  createQueryClient,
  renderWithProviders,
  renderWithQueryClient,
} from "@/test/test-utils";
import "@/test/setup-combobox-polyfills";
import { ReceiptTransactionForm } from "./ReceiptTransactionForm";
import {
  TransactionsSection,
  type ReceiptTransaction,
} from "@/pages/new-receipt/TransactionsSection";

vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://selector.test"));
const server = setupServer();
const accounts = Array.from({ length: 52 }, (_, index) => ({
  id: `account-${index + 1}`,
  name: `Account ${index + 1}`,
  isActive: true,
}));
const cards = [
  {
    id: "card-1",
    accountId: "account-1",
    name: "First card",
    cardCode: "1111",
    isActive: true,
  },
  {
    id: "card-2",
    accountId: "account-2",
    name: "Second card",
    cardCode: "2222",
    isActive: true,
  },
  {
    id: "card-52",
    accountId: "account-52",
    name: "Last card",
    cardCode: "5252",
    isActive: true,
  },
  {
    id: "inactive-card",
    accountId: "account-1",
    name: "Historic card",
    cardCode: "0000",
    isActive: false,
  },
];
const requestedAccounts: string[] = [];
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
afterEach(() => {
  cleanup();
  server.resetHandlers();
});
beforeEach(() => {
  requestedAccounts.length = 0;
  server.use(
    http.get("http://selector.test/api/accounts", () =>
      HttpResponse.json({
        data: accounts.slice(0, 3),
        total: 3,
        offset: 0,
        limit: 500,
      }),
    ),
    http.get("http://selector.test/api/cards", () =>
      HttpResponse.json({
        data: cards.filter((card) => card.isActive),
        total: 3,
        offset: 0,
        limit: 500,
      }),
    ),
    http.get("http://selector.test/api/accounts/:id/cards", ({ params }) => {
      requestedAccounts.push(String(params.id));
      return HttpResponse.json(
        cards.filter((card) => card.accountId === params.id),
      );
    }),
  );
});

function Wizard({
  onSubmit,
}: {
  onSubmit: (rows: ReceiptTransaction[]) => void;
}) {
  const [rows, setRows] = useState<ReceiptTransaction[]>([]);
  return (
    <TransactionsSection
      transactions={rows}
      defaultDate="2024-01-15"
      onChange={(next) => {
        setRows(next);
        onSubmit(next);
      }}
    />
  );
}
function mount(kind: "detail" | "wizard", onSubmit = vi.fn()) {
  const queryClient = createQueryClient();
  renderWithProviders(
    <QueryClientProvider client={queryClient}>
      {kind === "detail" ? (
        <ReceiptTransactionForm
          mode="create"
          defaultValues={{ amount: 7, date: "2024-01-15" }}
          onSubmit={onSubmit}
          onCancel={vi.fn()}
        />
      ) : (
        <Wizard onSubmit={onSubmit} />
      )}
    </QueryClientProvider>,
  );
  return {
    queryClient,
    onSubmit,
    submit: () =>
      screen.getByRole("button", {
        name: kind === "detail" ? "Add Transaction" : "Add",
      }),
  };
}

async function select(
  user: ReturnType<typeof userEvent.setup>,
  field: string,
  option: string,
) {
  const control = screen.getByRole("combobox", {
    name: new RegExp(`^${field}`),
  });
  await waitFor(() => expect(control).toBeEnabled());
  await user.click(control);
  await user.click(
    await screen.findByRole("option", {
      name: new RegExp(`^${option}${field === "Account" ? "$" : ""}`),
    }),
  );
}

describe.each(["detail", "wizard"] as const)(
  "%s transaction account cascade",
  (kind) => {
    it("puts Account first and waits for an account before enabling Card", async () => {
      mount(kind);
      await waitFor(() =>
        expect(
          screen.getByRole("combobox", { name: /^Account/ }),
        ).not.toHaveAttribute("aria-busy", "true"),
      );
      expect(screen.getAllByRole("combobox")[0]).toHaveAccessibleName(
        /^Account/,
      );
      expect(screen.getByRole("combobox", { name: /^Card/ })).toBeDisabled();
    });

    it("clears an incompatible card when the account changes and refuses to submit it", async () => {
      const user = userEvent.setup();
      const { onSubmit, submit } = mount(kind);
      await select(user, "Account", "Account 1");
      await select(user, "Card", "First card");
      await select(user, "Account", "Account 2");
      expect(
        screen.getByRole("combobox", { name: /^Card/ }),
      ).not.toHaveTextContent("First card");
      await user.click(submit());
      expect(onSubmit).not.toHaveBeenCalled();
      expect(await screen.findByText("Card is required")).toBeInTheDocument();
    });
  },
);

describe.each(["detail", "wizard"] as const)("%s cascade recovery", (kind) => {
  it("selects beyond account 50 and submits only a card scoped to that account", async () => {
    server.use(
      http.get("http://selector.test/api/accounts", () =>
        HttpResponse.json({
          data: accounts,
          total: accounts.length,
          offset: 0,
          limit: 500,
        }),
      ),
    );
    const user = userEvent.setup();
    const { onSubmit, submit } = mount(kind);
    await select(user, "Account", "Account 52");
    await user.click(screen.getByRole("combobox", { name: /^Card/ }));
    await screen.findByRole("option", { name: /^Last card/ });
    expect(
      screen.queryByRole("option", { name: /^First card/ }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole("option", { name: /^Second card/ }),
    ).not.toBeInTheDocument();
    await user.click(screen.getByRole("option", { name: /^Last card/ }));
    if (kind === "wizard")
      await user.type(screen.getByLabelText(/amount/i), "7");
    await user.click(submit());
    await waitFor(() => expect(onSubmit).toHaveBeenCalled());
    const submitted =
      kind === "wizard"
        ? onSubmit.mock.calls[0][0][0]
        : onSubmit.mock.calls[0][0];
    expect(submitted).toMatchObject({
      accountId: "account-52",
      cardId: "card-52",
      amount: 7,
    });
    expect(requestedAccounts).toContain("account-52");
    if (kind === "wizard")
      expect(screen.getByRole("combobox", { name: /^Account/ })).toHaveFocus();
  });
  it("explains an empty account with a route to create a card", async () => {
    const user = userEvent.setup();
    mount(kind);
    await select(user, "Account", "Account 3");
    expect(await screen.findByRole("status")).toHaveTextContent(
      "This account has no active cards",
    );
    expect(screen.getByRole("link", { name: "Create a card" })).toHaveAttribute(
      "href",
      "/cards",
    );
    expect(screen.getByRole("combobox", { name: /^Card/ })).toBeDisabled();
  });
  it("retries a failed scoped card request", async () => {
    let attempts = 0;
    server.use(
      http.get("http://selector.test/api/accounts/:id/cards", () => {
        attempts += 1;
        return attempts === 1
          ? HttpResponse.json({ detail: "unavailable" }, { status: 503 })
          : HttpResponse.json([cards[0]]);
      }),
    );
    const user = userEvent.setup();
    mount(kind);
    await select(user, "Account", "Account 1");
    await screen.findByText(/Could not load this account/);
    await user.click(screen.getByRole("button", { name: "Retry cards" }));
    await select(user, "Card", "First card");
    expect(screen.getByRole("combobox", { name: /^Card/ })).toHaveTextContent(
      "First card",
    );
    expect(attempts).toBe(2);
  });
  it("ignores a late response for the previous account", async () => {
    let release!: () => void;
    const pending = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.get(
        "http://selector.test/api/accounts/account-1/cards",
        async () => {
          await pending;
          return HttpResponse.json([cards[0]]);
        },
      ),
    );
    try {
      const user = userEvent.setup();
      const { queryClient } = mount(kind);
      queryClient.setQueryDefaults(["cards"], { gcTime: Infinity });
      await select(user, "Account", "Account 1");
      expect(screen.getByRole("combobox", { name: /^Card/ })).toBeDisabled();
      await select(user, "Account", "Account 2");
      await select(user, "Card", "Second card");
      release();
      await waitFor(() =>
        expect(
          queryClient.getQueryData(["cards", "byAccount", "account-1"]),
        ).toEqual([cards[0]]),
      );
      await user.click(screen.getByRole("combobox", { name: /^Card/ }));
      expect(
        screen.queryByRole("option", { name: /^First card/ }),
      ).not.toBeInTheDocument();
      expect(
        screen.getByRole("option", { name: /^Second card/ }),
      ).toBeInTheDocument();
    } finally {
      release();
    }
  });
});

it("preserves an inactive current card and inactive account when editing historical amounts", async () => {
  server.use(
    http.get("http://selector.test/api/accounts", () =>
      HttpResponse.json({
        data: [{ ...accounts[0], isActive: false }],
        total: 1,
      }),
    ),
  );
  const user = userEvent.setup();
  const onSubmit = vi.fn();
  renderWithQueryClient(
    <ReceiptTransactionForm
      mode="edit"
      defaultValues={{
        accountId: "account-1",
        cardId: "inactive-card",
        amount: 8,
        date: "2024-01-15",
      }}
      onSubmit={onSubmit}
      onCancel={vi.fn()}
    />,
  );
  await waitFor(() =>
    expect(screen.getByRole("combobox", { name: /^Card/ })).toHaveTextContent(
      "Historic card (inactive)",
    ),
  );
  expect(screen.getByRole("combobox", { name: /^Account/ })).toHaveTextContent(
    "Account 1",
  );
  await user.click(screen.getByRole("button", { name: "Update Transaction" }));
  expect(onSubmit.mock.calls[0][0]).toMatchObject({
    accountId: "account-1",
    cardId: "inactive-card",
    amount: 8,
  });
});

it("keeps the current card while its lookup fails", async () => {
  server.use(
    http.get("http://selector.test/api/accounts/:id/cards", () =>
      HttpResponse.json({ detail: "unavailable" }, { status: 503 }),
    ),
  );
  const user = userEvent.setup();
  const onSubmit = vi.fn();
  renderWithQueryClient(
    <ReceiptTransactionForm
      mode="edit"
      defaultValues={{
        accountId: "account-1",
        cardId: "inactive-card",
        amount: 8,
        date: "2024-01-15",
      }}
      onSubmit={onSubmit}
      onCancel={vi.fn()}
    />,
  );
  await screen.findByText(/Could not load this account/);
  await user.click(screen.getByRole("button", { name: "Update Transaction" }));
  expect(onSubmit.mock.calls[0][0]).toMatchObject({
    accountId: "account-1",
    cardId: "inactive-card",
  });
});

describe.each(["detail", "wizard"] as const)(
  "%s keyboard selection",
  (kind) => {
    it("supports searching and selecting Account then Card without pointer-only choices", async () => {
      const user = userEvent.setup();
      const { onSubmit, submit } = mount(kind);
      const account = screen.getByRole("combobox", { name: /^Account/ });
      await waitFor(() => expect(account).toBeEnabled());
      account.focus();
      await user.keyboard("{Enter}");
      await user.type(
        await screen.findByPlaceholderText("Search accounts..."),
        "Account 2",
      );
      await user.keyboard("{Enter}");
      const card = screen.getByRole("combobox", { name: /^Card/ });
      await waitFor(() => expect(card).toBeEnabled());
      card.focus();
      await user.keyboard("{Enter}");
      await user.type(
        await screen.findByPlaceholderText("Search cards..."),
        "Second card",
      );
      await user.keyboard("{Enter}");
      if (kind === "wizard")
        await user.type(screen.getByLabelText(/amount/i), "7");
      await user.click(submit());
      await waitFor(() => expect(onSubmit).toHaveBeenCalled());
      const submitted =
        kind === "wizard"
          ? onSubmit.mock.calls[0][0][0]
          : onSubmit.mock.calls[0][0];
      expect(submitted).toMatchObject({
        accountId: "account-2",
        cardId: "card-2",
      });
    });
  },
);

describe.each(["detail", "wizard"] as const)(
  "%s remote card reassignment",
  (kind) => {
    it("requires a compatible selection when a successful refetch disproves the current pair", async () => {
      let moved = false;
      server.use(
        http.get("http://selector.test/api/accounts/account-1/cards", () =>
          HttpResponse.json(moved ? [] : [cards[0]]),
        ),
      );
      const user = userEvent.setup();
      const { queryClient, onSubmit, submit } = mount(kind);
      await select(user, "Account", "Account 1");
      await select(user, "Card", "First card");
      if (kind === "wizard")
        await user.type(screen.getByLabelText(/amount/i), "7");
      moved = true;
      await act(async () => {
        await queryClient.invalidateQueries({ queryKey: ["cards"] });
      });
      await user.click(submit());
      expect(onSubmit).not.toHaveBeenCalled();
      expect(
        await screen.findByText(/no longer belongs to this account/i),
      ).toBeInTheDocument();
      await select(user, "Account", "Account 2");
      await select(user, "Card", "Second card");
      await user.click(submit());
      await waitFor(() => expect(onSubmit).toHaveBeenCalledOnce());
      const submitted =
        kind === "wizard"
          ? onSubmit.mock.calls[0][0][0]
          : onSubmit.mock.calls[0][0];
      expect(submitted).toMatchObject({
        accountId: "account-2",
        cardId: "card-2",
      });
    });
  },
);

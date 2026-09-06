import {
  act,
  cleanup,
  renderHook,
  screen,
  waitFor,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClientProvider } from "@tanstack/react-query";
import { http, HttpResponse } from "msw";
import { toast } from "sonner";
import { clearTokens, setTokens } from "@/lib/auth";
import { createAppQueryClient } from "@/lib/query-client";
import { renderWithProviders } from "@/test/test-utils";
import { server } from "@/test/msw/server";
import { useCategorizeReceiptItems } from "./useCategorizeReceiptItems";
import UncategorizedItems from "@/components/reports/UncategorizedItems";
import "@/test/setup-combobox-polyfills";

vi.hoisted(() =>
  vi.stubEnv("VITE_API_URL", "http://categorize-projection.test"),
);
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  localStorage.clear();
  clearTokens();
});
afterEach(() => {
  cleanup();
  server.resetHandlers();
  toast.dismiss();
  vi.restoreAllMocks();
});

it.each(["transport failure", "success"] as const)(
  "awaits all receipt groups and refreshes the real report after %s",
  async (outcome) => {
    const user = userEvent.setup();
    const queryClient = createAppQueryClient();
    const success = vi.spyOn(toast, "success");
    const failure = vi.spyOn(toast, "error");
    const first = {
      id: "a",
      receiptId: "r1",
      description: "Apples",
      receiptItemCode: null,
      quantity: 1,
      unitPrice: 2,
      totalAmount: 2,
      category: "Uncategorized",
      subcategory: null,
    };
    const second = {
      ...first,
      id: "b",
      receiptId: "r2",
      description: "Bananas",
    };
    let stored = [first, second];
    let reads = 0;
    const groups: string[][] = [];
    let release!: () => void;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.get("*/api/reports/uncategorized-items", () => {
        reads++;
        return HttpResponse.json({ totalCount: stored.length, items: stored });
      }),
      http.get("*/api/categories", () =>
        HttpResponse.json({
          data: [{ id: "food", name: "Food", isActive: true }],
          total: 1,
          offset: 0,
          limit: 500,
        }),
      ),
      http.get("*/api/subcategories", () =>
        HttpResponse.json({ data: [], total: 0, offset: 0, limit: 500 }),
      ),
      http.put("*/api/receipt-items/batch", async ({ request }) => {
        const body = (await request.json()) as {
          id: string;
          category: string;
          subcategory: string | null;
        }[];
        groups.push(body.map((item) => item.id));
        expect(
          body.every(
            (item) => item.category === "Food" && item.subcategory === null,
          ),
        ).toBe(true);
        if (body[0].id === "a" && outcome === "transport failure")
          return HttpResponse.error();
        if (body[0].id === "b") await held;
        stored = stored.filter(
          (item) => !body.some((updated) => updated.id === item.id),
        );
        return new HttpResponse(null, { status: 204 });
      }),
    );
    try {
      renderWithProviders(
        <QueryClientProvider client={queryClient}>
          <UncategorizedItems />
        </QueryClientProvider>,
      );
      await user.click(
        await screen.findByRole("checkbox", {
          name: "Select all items on this page",
        }),
      );
      await user.click(screen.getByText("Select category..."));
      await user.click(await screen.findByRole("option", { name: "Food" }));
      const readsBefore = reads;
      await user.click(
        screen.getByRole("button", { name: "Apply to Selected" }),
      );
      await waitFor(() => expect(groups).toHaveLength(2));
      expect(
        screen.getByRole("button", { name: "Applying..." }),
      ).toBeDisabled();
      expect(reads).toBe(readsBefore);
      expect(success).not.toHaveBeenCalled();
      await act(async () => release());
      if (outcome === "transport failure") {
        await waitFor(() =>
          expect(failure).toHaveBeenCalledWith("Failed to update items"),
        );
        await waitFor(() =>
          expect(screen.queryByText("Bananas")).not.toBeInTheDocument(),
        );
        expect(screen.getByText("Apples")).toBeInTheDocument();
        expect(
          screen.getByRole("checkbox", { name: "Select Apples" }),
        ).toBeChecked();
        expect(
          screen.getByRole("button", { name: "Apply to Selected" }),
        ).toBeEnabled();
        expect(success).not.toHaveBeenCalled();
      } else {
        expect(await screen.findByText("All Categorized")).toBeInTheDocument();
        expect(success).toHaveBeenCalledWith("Items categorized successfully");
        expect(failure).not.toHaveBeenCalled();
      }
      expect(reads).toBeGreaterThan(readsBefore);
      expect(groups).toEqual(expect.arrayContaining([["a"], ["b"]]));
    } finally {
      release();
      cleanup();
      queryClient.clear();
    }
  },
);

it.each(["success", "failure"] as const)(
  "suppresses old-session categorization %s callbacks and cache repair",
  async (outcome) => {
    const queryClient = createAppQueryClient();
    queryClient.setQueryData(["reports", "uncategorized-items"], {
      totalCount: 1,
    });
    const onSuccess = vi.fn();
    const onError = vi.fn();
    let release!: () => void;
    let received = false;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.put("*/api/receipt-items/batch", async () => {
        received = true;
        await held;
        return outcome === "success"
          ? new HttpResponse(null, { status: 204 })
          : HttpResponse.json(
              { status: 409, detail: "Obsolete group failed" },
              { status: 409 },
            );
      }),
    );
    const wrapper = ({ children }: { children: React.ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    );
    const { result, unmount } = renderHook(
      () => useCategorizeReceiptItems({ onSuccess, onError }),
      { wrapper },
    );
    try {
      let pending!: Promise<unknown>;
      act(() => {
        pending = result.current
          .mutateAsync({
            items: [
              {
                id: "old",
                receiptId: "receipt",
                description: "Old item",
                receiptItemCode: null,
                quantity: 1,
                unitPrice: 1,
                totalAmount: 1,
                category: "Uncategorized",
                subcategory: null,
              },
            ],
            category: "Food",
            subcategory: null,
          })
          .catch((error: unknown) => error);
      });
      await waitFor(() => expect(received).toBe(true));
      act(() =>
        setTokens("replacement-access", `replacement-refresh-${outcome}`),
      );
      await expect(pending).resolves.toMatchObject({ name: "AbortError" });
      await act(async () => release());
      expect(onSuccess).not.toHaveBeenCalled();
      expect(onError).not.toHaveBeenCalled();
      expect(
        queryClient.getQueryState(["reports", "uncategorized-items"])
          ?.isInvalidated,
      ).toBe(false);
    } finally {
      release();
      unmount();
      queryClient.clear();
    }
  },
);

it("keeps a stable mutation result on rerender and invokes the current completion callback", async () => {
  const queryClient = createAppQueryClient();
  const first = vi.fn();
  const current = vi.fn();
  const wrapper = ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
  server.use(
    http.put(
      "*/api/receipt-items/batch",
      () => new HttpResponse(null, { status: 204 }),
    ),
  );
  const { result, rerender, unmount } = renderHook(
    ({ onSuccess }) => useCategorizeReceiptItems({ onSuccess }),
    { wrapper, initialProps: { onSuccess: first } },
  );
  try {
    const stable = result.current;
    rerender({ onSuccess: current });
    expect(result.current).toBe(stable);
    await act(async () =>
      result.current.mutateAsync({
        items: [
          {
            id: "item",
            receiptId: "receipt",
            description: "Item",
            receiptItemCode: null,
            quantity: 1,
            unitPrice: 1,
            totalAmount: 1,
            category: "Uncategorized",
            subcategory: null,
          },
        ],
        category: "Food",
        subcategory: null,
      }),
    );
    expect(first).not.toHaveBeenCalled();
    expect(current).toHaveBeenCalledOnce();
  } finally {
    unmount();
    queryClient.clear();
  }
});

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
import { taxonomyFixture } from "@/test/taxonomy-fixture";

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
      totalAmountCurrency: "USD",
      classificationState: "missing_assignment",
      assignmentId: null,
      categoryId: null,
      taxonomyRevisionId: null,
    };
    const second = {
      ...first,
      id: "b",
      receiptId: "r2",
      description: "Bananas",
    };
    let stored = [first, second];
    let reads = 0;
    const writes: { id: string; body: Record<string, unknown> }[] = [];
    let release!: () => void;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.get("*/api/reports/uncategorized-items", () => {
        reads++;
        return HttpResponse.json({ totalCount: stored.length, items: stored });
      }),
      http.get("*/api/taxonomy", () => HttpResponse.json(taxonomyFixture)),
      http.put(
        "*/api/receipt-items/:id/classification",
        async ({ request, params }) => {
          const body = (await request.json()) as Record<string, unknown>;
          const id = String(params.id);
          writes.push({ id, body });
          expect(body).toMatchObject({
            categoryId: taxonomyFixture.nodes[0].id,
            taxonomyRevisionId: taxonomyFixture.id,
            expectedAssignmentId: null,
          });
          if (id === "a" && outcome === "transport failure")
            return HttpResponse.error();
          if (id === "b") await held;
          stored = stored.filter((item) => item.id !== id);
          return new HttpResponse(null, { status: 204 });
        },
      ),
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
      await user.click(
        await screen.findByRole("combobox", {
          name: "Category for selected items",
        }),
      );
      await user.click(await screen.findByRole("option", { name: "Food" }));
      const readsBefore = reads;
      await user.click(
        screen.getByRole("button", { name: "Apply to Selected" }),
      );
      await waitFor(() => expect(writes).toHaveLength(2));
      expect(
        screen.getByRole("button", { name: "Applying..." }),
      ).toBeDisabled();
      expect(reads).toBe(readsBefore);
      expect(success).not.toHaveBeenCalled();
      await act(async () => release());
      if (outcome === "transport failure") {
        await waitFor(() =>
          expect(failure).toHaveBeenCalledExactlyOnceWith(
            "1 item classifications could not be confirmed. Refresh and review before retrying.",
          ),
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
      expect(writes.map((write) => write.id).sort()).toEqual(["a", "b"]);
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
      http.put("*/api/receipt-items/:id/classification", async () => {
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
                totalAmountCurrency: "USD",
                category: "",
                classificationState: "missing_assignment",
                assignmentId: null,
                categoryId: null,
                taxonomyRevisionId: null,
              },
            ],
            choice: {
              categoryId: taxonomyFixture.nodes[0].id,
              taxonomyRevisionId: taxonomyFixture.id,
              path: [taxonomyFixture.nodes[0]],
            },
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
      "*/api/receipt-items/:id/classification",
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
            totalAmountCurrency: "USD",
            category: "",
            classificationState: "missing_assignment",
            assignmentId: null,
            categoryId: null,
            taxonomyRevisionId: null,
          },
        ],
        choice: {
          categoryId: taxonomyFixture.nodes[0].id,
          taxonomyRevisionId: taxonomyFixture.id,
          path: [taxonomyFixture.nodes[0]],
        },
      }),
    );
    expect(first).not.toHaveBeenCalled();
    expect(current).toHaveBeenCalledOnce();
  } finally {
    unmount();
    queryClient.clear();
  }
});

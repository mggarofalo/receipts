import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClientProvider } from "@tanstack/react-query";
import { http, HttpResponse } from "msw";
import { toast } from "sonner";
import { clearTokens } from "@/lib/auth";
import { createAppQueryClient } from "@/lib/query-client";
import { server } from "@/test/msw/server";
import { renderWithProviders } from "@/test/test-utils";
import "@/test/setup-combobox-polyfills";
import Subcategories from "./Subcategories";

vi.hoisted(() =>
  vi.stubEnv("VITE_API_URL", "http://subcategory-conflict.test"),
);
vi.mock("@/hooks/usePermission", () => ({
  usePermission: () => ({ isAdmin: () => true }),
}));

beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  localStorage.clear();
  clearTokens();
});
afterEach(() => {
  cleanup();
  toast.dismiss();
  server.resetHandlers();
  vi.restoreAllMocks();
});

async function openConflict() {
  const user = userEvent.setup();
  const queryClient = createAppQueryClient();
  const invalidate = vi.spyOn(queryClient, "invalidateQueries");
  const success = vi.spyOn(toast, "success");
  let reads = 0;
  let deletes = 0;
  server.use(
    http.get("*/api/categories", () =>
      HttpResponse.json({
        data: [{ id: "food", name: "Food", isActive: true }],
        total: 1,
        offset: 0,
        limit: 500,
      }),
    ),
    http.get("*/api/subcategories", () => {
      reads++;
      return HttpResponse.json({
        data: [
          { id: "shared", name: "Shared", categoryId: "food", isActive: true },
        ],
        total: 1,
        offset: 0,
        limit: 50,
      });
    }),
    http.delete("*/api/subcategories/shared", () => {
      deletes++;
      return HttpResponse.json(
        {
          type: "about:blank",
          title: "Conflict",
          status: 409,
          detail:
            "23 receipt items use Food / Shared, including items in trash.",
          receiptItemCount: 23,
          affectedReceipts: [
            {
              id: "live",
              date: "2026-09-01",
              location: "Live shop",
              isDeleted: false,
            },
            {
              id: "trash",
              date: "2026-08-01",
              location: "Trashed shop",
              isDeleted: true,
            },
            { id: "legacy", date: "2026-07-01", location: "Legacy shop" },
          ],
        },
        { status: 409, headers: { "Content-Type": "application/json" } },
      );
    }),
  );
  renderWithProviders(
    <QueryClientProvider client={queryClient}>
      <Subcategories />
    </QueryClientProvider>,
  );
  await user.click(await screen.findByRole("button", { name: "Expand Food" }));
  const row = screen.getByText("Shared").closest("tr")!;
  await user.click(within(row).getByRole("button", { name: "Delete" }));
  const confirmation = screen.getByRole("alertdialog", {
    name: "Delete Subcategory?",
  });
  const cacheBefore = queryClient.getQueriesData({
    queryKey: ["subcategories"],
  });
  const readsBefore = reads;
  await user.click(
    within(confirmation).getByRole("button", { name: "Delete" }),
  );
  const dialog = await screen.findByRole("dialog", {
    name: "Cannot Delete Subcategory",
  });
  return {
    user,
    dialog,
    queryClient,
    invalidate,
    success,
    cacheBefore,
    readsBefore,
    getReads: () => reads,
    getDeletes: () => deletes,
  };
}

it("passes actual 409 extensions to the dialog and renders trashed receipts without a broken detail link", async () => {
  const result = await openConflict();
  try {
    expect(
      within(result.dialog).getByText(/Trashed shop.*\(in trash\)/),
    ).toBeInTheDocument();
    expect(
      within(result.dialog).queryByRole("link", { name: /Trashed shop/ }),
    ).not.toBeInTheDocument();
    expect(
      within(result.dialog).getByRole("link", { name: /Live shop/ }),
    ).toHaveAttribute("href", "/receipts/live");
    await result.user.click(
      within(result.dialog).getByRole("link", { name: /Live shop/ }),
    );
    await waitFor(() =>
      expect(
        screen.queryByRole("dialog", { name: "Cannot Delete Subcategory" }),
      ).not.toBeInTheDocument(),
    );
  } finally {
    cleanup();
    result.queryClient.clear();
  }
});

it("distinguishes item counts from receipt examples and leaves the rejected deletion cached", async () => {
  const result = await openConflict();
  try {
    expect(result.dialog).toHaveTextContent("23 receipt item(s)");
    expect(result.dialog).toHaveTextContent("Counts include items in trash");
    expect(result.dialog).toHaveTextContent(
      "restore trashed items or receipts before editing them",
    );
    expect(result.dialog).toHaveTextContent(
      "Showing up to 20 affected receipts. A receipt can contain multiple matching items.",
    );
    expect(result.dialog).not.toHaveTextContent("20 more receipt(s)");
    expect(within(result.dialog).getAllByRole("listitem")).toHaveLength(3);
    expect(result.getDeletes()).toBe(1);
    expect(result.getReads()).toBe(result.readsBefore);
    expect(result.invalidate).not.toHaveBeenCalled();
    expect(result.success).not.toHaveBeenCalled();
    expect(
      result.queryClient.getQueriesData({ queryKey: ["subcategories"] }),
    ).toEqual(result.cacheBefore);
  } finally {
    cleanup();
    result.queryClient.clear();
  }
});

it("keeps old-server examples without isDeleted navigable", async () => {
  const result = await openConflict();
  try {
    expect(
      within(result.dialog).getByRole("link", { name: /Legacy shop/ }),
    ).toHaveAttribute("href", "/receipts/legacy");
    await result.user.click(
      within(result.dialog).getByRole("link", { name: /Legacy shop/ }),
    );
    await waitFor(() =>
      expect(
        screen.queryByRole("dialog", { name: "Cannot Delete Subcategory" }),
      ).not.toBeInTheDocument(),
    );
  } finally {
    cleanup();
    result.queryClient.clear();
  }
});

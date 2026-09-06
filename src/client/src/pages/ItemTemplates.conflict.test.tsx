import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClientProvider } from "@tanstack/react-query";
import { http, HttpResponse } from "msw";
import { Toaster, toast } from "sonner";
import { clearTokens } from "@/lib/auth";
import { createAppQueryClient } from "@/lib/query-client";
import { server } from "@/test/msw/server";
import { itemTemplates } from "@/test/msw/fixtures/item-templates";
import { renderWithProviders } from "@/test/test-utils";
import "@/test/setup-combobox-polyfills";
import ItemTemplates from "./ItemTemplates";

vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://template-conflict.test"));

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
});

it("retains a conflicted template draft and only closes after a deliberate successful retry", async () => {
  const user = userEvent.setup();
  const queryClient = createAppQueryClient();
  const invalidate = vi.spyOn(queryClient, "invalidateQueries");
  const detail =
    "This template changed while matching. Review the current template and retry your edit.";
  let stored = { ...itemTemplates[0] };
  let reads = 0;
  const writes: Record<string, unknown>[] = [];
  server.use(
    http.get("*/api/item-templates", () => {
      reads++;
      return HttpResponse.json({
        data: [stored],
        total: 1,
        offset: 0,
        limit: 50,
      });
    }),
    http.put("*/api/item-templates/:id", async ({ request }) => {
      const body = (await request.json()) as Record<string, unknown>;
      writes.push(body);
      if (writes.length === 1) {
        return HttpResponse.json(
          { type: "about:blank", title: "Conflict", status: 409, detail },
          {
            status: 409,
            headers: { "Content-Type": "application/json" },
          },
        );
      }
      stored = { ...stored, name: String(body.name) };
      return new HttpResponse(null, { status: 204 });
    }),
  );

  try {
    renderWithProviders(
      <QueryClientProvider client={queryClient}>
        <ItemTemplates />
        <Toaster />
      </QueryClientProvider>,
    );
    const row = (await screen.findByText("Whole Milk")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: /edit/i }));
    const dialog = screen.getByRole("dialog", { name: "Edit Item Template" });
    const name = within(dialog).getByRole("textbox", { name: /^name/i });
    await user.clear(name);
    await user.type(name, "Organic Milk draft");
    const submit = within(dialog).getByRole("button", {
      name: /update template/i,
    });
    const cacheBefore = queryClient.getQueriesData({
      queryKey: ["itemTemplates", "list"],
    });
    const readsBefore = reads;
    await user.click(submit);

    // Uses the actual mutation/cache/global-error path, including the rendered server detail.
    expect(await screen.findByText(detail)).toBeInTheDocument();
    await waitFor(() => expect(submit).toBeEnabled());
    expect(screen.getByRole("dialog", { name: "Edit Item Template" })).toBe(
      dialog,
    );
    expect(name).toHaveValue("Organic Milk draft");
    expect(screen.queryByText("Item template updated")).not.toBeInTheDocument();
    expect(invalidate).not.toHaveBeenCalled();
    expect(
      queryClient.getQueriesData({ queryKey: ["itemTemplates", "list"] }),
    ).toEqual(cacheBefore);
    expect(reads).toBe(readsBefore);
    expect(writes).toHaveLength(1);
    expect(writes[0]).toMatchObject({
      id: stored.id,
      name: "Organic Milk draft",
    });

    await user.click(submit);
    await waitFor(() =>
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument(),
    );
    expect(
      await screen.findByText("Item template updated"),
    ).toBeInTheDocument();
    expect(await screen.findByText("Organic Milk draft")).toBeInTheDocument();
    expect(writes).toHaveLength(2);
    expect(writes[1]).toEqual(writes[0]);
    expect(reads).toBeGreaterThan(readsBefore);
  } finally {
    cleanup();
    queryClient.clear();
    invalidate.mockRestore();
  }
});

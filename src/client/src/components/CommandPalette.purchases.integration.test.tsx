import type { components } from "@/generated/api";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { Routes, Route } from "react-router";
import { http, HttpResponse } from "msw";
import { server } from "@/test/msw/server";
import { renderWithQueryClient } from "@/test/test-utils";
import { CommandPalette } from "./CommandPalette";
import PurchaseSearch from "@/pages/PurchaseSearch";
vi.mock("@/lib/command-palette/entity-results", () => ({
  useEntityResults: () => [],
}));
const purchase: components["schemas"]["PurchaseSearchItem"] = {
  id: "item1",
  receiptId: "receipt1",
  date: "2025-07-04",
  description: "Beef hot dogs",
  location: "Market",
  quantity: 2,
  unitPrice: 3.4995,
  unitPriceCurrency: "USD",
  totalAmount: 7,
  totalAmountCurrency: "USD",
};
function Journey() {
  const [open, setOpen] = useState(true);
  return (
    <>
      <CommandPalette open={open} onOpenChange={setOpen} />
      <Routes>
        <Route path="/" element={<p>Home</p>} />
        <Route path="/search" element={<PurchaseSearch />} />
        <Route path="/receipts/:id" element={<h1>Original receipt</h1>} />
      </Routes>
    </>
  );
}
beforeAll(() => {
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
});
beforeEach(() => {
  localStorage.clear();
  Element.prototype.scrollIntoView = vi.fn();
  server.use(
    http.get("*/api/search/purchases", ({ request }) =>
      HttpResponse.json({
        items: [
          purchase,
          {
            ...purchase,
            id: "older-item",
            receiptId: "older-receipt",
            description: "Turkey hot dogs",
            date: "2020-01-15",
          },
        ],
        total: 2,
        offset: Number(new URL(request.url).searchParams.get("offset")),
        limit: Number(new URL(request.url).searchParams.get("limit")),
      }),
    ),
  );
});
it("supports keyboard navigation from universal search to the historical purchase table", async () => {
  const user = userEvent.setup();
  renderWithQueryClient(<Journey />);
  await user.type(screen.getByRole("combobox"), "hot dogs");
  await screen.findByRole("option", { name: /Beef hot dogs/ });
  const all = screen.getByRole("option", {
    name: /See all 2 matching purchases/,
  });
  expect(screen.getAllByRole("option").map((row) => row.textContent)).toEqual([
    expect.stringContaining("Beef hot dogs"),
    expect.stringContaining("Turkey hot dogs"),
    "See all 2 matching purchases",
  ]);
  await user.keyboard("{End}");
  expect(all).toHaveAttribute("aria-selected", "true");
  await user.keyboard("{Enter}");
  expect(all).not.toBeInTheDocument();
  expect(
    await screen.findByRole("table", { name: "Purchase results" }),
  ).toBeInTheDocument();
  expect(screen.getByRole("textbox", { name: "Search purchases" })).toHaveValue(
    "hot dogs",
  );
  expect(
    screen.getAllByRole("link", { name: "View receipt" })[0],
  ).toHaveAttribute("href", "/receipts/receipt1");
  await user.click(screen.getAllByRole("link", { name: "View receipt" })[0]);
  expect(
    await screen.findByRole("heading", { name: "Original receipt" }),
  ).toBeInTheDocument();
});
it("opens a purchase's receipt with Enter from its keyboard-selected preview", async () => {
  const user = userEvent.setup();
  renderWithQueryClient(<Journey />);
  await user.type(screen.getByRole("combobox"), "hot dogs");
  await screen.findByRole("option", { name: /Beef hot dogs/ });
  await user.keyboard("{Home}");
  await user.keyboard("{Enter}");
  expect(
    await screen.findByRole("heading", { name: "Original receipt" }),
  ).toBeInTheDocument();
});

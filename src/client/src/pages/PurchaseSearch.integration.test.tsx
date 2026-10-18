import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import { server } from "@/test/msw/server";
import { renderWithQueryClient } from "@/test/test-utils";
import PurchaseSearch from "./PurchaseSearch";

it("finds hot dogs through the real query hook and renders a date-descending evidence table", async () => {
  const requests: URL[] = [];
  server.use(
    http.get("*/api/search/purchases", ({ request }) => {
      requests.push(new URL(request.url));
      return HttpResponse.json({
        total: 2,
        offset: 0,
        limit: 25,
        items: [
          {
            id: "new-item",
            receiptId: "new-receipt",
            date: "2025-07-04",
            description: "Beef hot dogs",
            location: "Market",
            receiptItemCode: "HD12",
            normalizedDescription: "Hot dogs",
            quantity: 2,
            unitPrice: 3.4995,
            unitPriceCurrency: "USD",
            totalAmount: 7,
            totalAmountCurrency: "USD",
          },
          {
            id: "old-item",
            receiptId: "old-receipt",
            date: "2020-01-15",
            description: "Turkey hot dogs",
            location: "Old store",
            quantity: 1,
            unitPrice: 2.99,
            unitPriceCurrency: "USD",
            totalAmount: 2.99,
            totalAmountCurrency: "USD",
          },
        ],
      });
    }),
  );
  renderWithQueryClient(<PurchaseSearch />, { route: "/search" });
  await userEvent.type(
    screen.getByRole("textbox", { name: "Search purchases" }),
    "hot dogs",
  );
  await userEvent.click(screen.getByRole("button", { name: "Search" }));
  const table = await screen.findByRole("table", { name: "Purchase results" });
  const rows = within(table).getAllByRole("row");
  expect(rows).toHaveLength(3);
  expect(
    within(rows[0])
      .getAllByRole("columnheader")
      .map((cell) => cell.textContent),
  ).toEqual([
    "Receipt date",
    "Item",
    "Store",
    "Unit price",
    "Quantity",
    "Line total",
    "Receipt",
  ]);
  expect(rows[1]).toHaveTextContent("Beef hot dogs");
  expect(rows[1]).toHaveTextContent(/2025/);
  expect(rows[1]).toHaveTextContent(/USD\s3\.4995/);
  expect(
    within(rows[1]).getByRole("link", { name: "View receipt" }),
  ).toHaveAttribute("href", "/receipts/new-receipt");
  expect(rows[2]).toHaveTextContent("Turkey hot dogs");
  expect(rows[2]).toHaveTextContent(/2020/);
  expect(
    within(rows[2]).getByRole("link", { name: "View receipt" }),
  ).toHaveAttribute("href", "/receipts/old-receipt");
  expect(requests).toHaveLength(1);
  expect(Object.fromEntries(requests[0].searchParams)).toEqual({
    q: "hot dogs",
    offset: "0",
    limit: "25",
  });
});

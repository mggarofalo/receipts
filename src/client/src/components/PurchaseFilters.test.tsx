import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { PurchaseFilters } from "./PurchaseFilters";
import { EMPTY_PURCHASE_FILTERS } from "@/lib/purchase-table";
it("applies one normalized filter draft only on submit and clears fields together", async () => {
  const onApply = vi.fn();
  const user = userEvent.setup();
  render(
    <PurchaseFilters
      initialValues={EMPTY_PURCHASE_FILTERS}
      onApply={onApply}
    />,
  );
  await user.type(
    screen.getByRole("textbox", { name: "Store contains" }),
    "  Market  ",
  );
  fireEvent.change(screen.getByLabelText("Minimum unit price"), {
    target: { value: "1.2345" },
  });
  fireEvent.change(screen.getByLabelText("Maximum unit price"), {
    target: { value: "2.3456" },
  });
  expect(onApply).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Apply filters" }));
  await waitFor(() =>
    expect(onApply).toHaveBeenCalledWith(
      {
        ...EMPTY_PURCHASE_FILTERS,
        location: "Market",
        minUnitPrice: "1.2345",
        maxUnitPrice: "2.3456",
      },
      expect.anything(),
    ),
  );
  await user.click(screen.getByRole("button", { name: "Clear filters" }));
  expect(onApply).toHaveBeenLastCalledWith(EMPTY_PURCHASE_FILTERS);
  expect(screen.getByLabelText("Store contains")).toHaveValue("");
  expect(screen.getByLabelText("Minimum unit price")).toHaveValue(null);
});
it("rejects inverted price bounds without applying any draft", async () => {
  const onApply = vi.fn();
  render(
    <PurchaseFilters
      initialValues={EMPTY_PURCHASE_FILTERS}
      onApply={onApply}
    />,
  );
  fireEvent.change(screen.getByLabelText("Minimum unit price"), {
    target: { value: "2" },
  });
  fireEvent.change(screen.getByLabelText("Maximum unit price"), {
    target: { value: "1" },
  });
  await userEvent.click(screen.getByRole("button", { name: "Apply filters" }));
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Maximum price must not be less than minimum price.",
  );
  expect(onApply).not.toHaveBeenCalled();
});

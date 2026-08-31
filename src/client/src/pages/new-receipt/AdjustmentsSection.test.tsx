import { useState } from "react";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import "@/test/setup-combobox-polyfills";
import {
  AdjustmentsSection,
  type ReceiptAdjustment,
} from "./AdjustmentsSection";

vi.mock("@/hooks/useFormShortcuts", () => ({ useFormShortcuts: vi.fn() }));
vi.mock("@/hooks/useEnumMetadata", () => ({
  useEnumMetadata: () => ({
    adjustmentTypes: [
      { value: "Tip", label: "Tip" },
      { value: "Discount", label: "Discount" },
      { value: "Other", label: "Other" },
    ],
    adjustmentTypeLabels: {
      Tip: "Tip",
      Discount: "Discount",
      Other: "Other",
    },
  }),
}));

function Harness({ initial = [] }: { initial?: ReceiptAdjustment[] }) {
  const [adjustments, setAdjustments] = useState(initial);
  return <AdjustmentsSection adjustments={adjustments} onChange={setAdjustments} />;
}

async function chooseType(user: ReturnType<typeof userEvent.setup>, label: string) {
  await user.click(screen.getByRole("combobox", { name: /^Type/ }));
  await user.click(await screen.findByText(label));
}

describe("AdjustmentsSection", () => {
  it("renders an empty state with a zero signed total", () => {
    renderWithProviders(<Harness />);

    expect(screen.getByText("Adjustments")).toBeInTheDocument();
    expect(screen.getByText("Total: $0.00")).toBeInTheDocument();
    expect(screen.queryByRole("table")).not.toBeInTheDocument();
  });

  it("adds an adjustment through the form", async () => {
    const user = userEvent.setup();
    renderWithProviders(<Harness />);

    await user.click(screen.getByRole("button", { name: /add adjustment/i }));
    await chooseType(user, "Tip");
    const amount = screen.getByRole("textbox", { name: /^Amount/ });
    await user.clear(amount);
    await user.type(amount, "5");
    await user.click(screen.getByRole("button", { name: /^add adjustment$/i }));

    expect(await screen.findByRole("cell", { name: "Tip" })).toBeInTheDocument();
    expect(screen.getByText("Total: $5.00")).toBeInTheDocument();
  });

  it("edits an existing adjustment", async () => {
    const user = userEvent.setup();
    renderWithProviders(
      <Harness initial={[{ id: "a1", type: "Tip", amount: 5 }]} />,
    );

    await user.click(screen.getByRole("button", { name: "Edit adjustment" }));
    const amount = screen.getByRole("textbox", { name: /^Amount/ });
    await user.clear(amount);
    await user.type(amount, "7");
    await user.click(screen.getByRole("button", { name: /update adjustment/i }));

    expect(await screen.findByText("Total: $7.00")).toBeInTheDocument();
  });

  it("removes an adjustment", async () => {
    const user = userEvent.setup();
    renderWithProviders(
      <Harness initial={[{ id: "a1", type: "Tip", amount: 5 }]} />,
    );

    await user.click(screen.getByRole("button", { name: "Remove adjustment" }));

    expect(screen.getByText("Total: $0.00")).toBeInTheDocument();
    expect(screen.queryByRole("table")).not.toBeInTheDocument();
  });

  it("renders labels and sums positive and negative amounts", () => {
    renderWithProviders(
      <Harness
        initial={[
          { id: "a1", type: "Tip", amount: 5 },
          { id: "a2", type: "Discount", amount: -2 },
        ]}
      />,
    );

    expect(screen.getByRole("cell", { name: "Tip" })).toBeInTheDocument();
    expect(screen.getByRole("cell", { name: "Discount" })).toBeInTheDocument();
    expect(screen.getByText("-$2.00")).toBeInTheDocument();
    expect(screen.getByText("Total: $3.00")).toBeInTheDocument();
  });

  it("requires a description when Other is selected", async () => {
    const user = userEvent.setup();
    renderWithProviders(<Harness />);

    await user.click(screen.getByRole("button", { name: /add adjustment/i }));
    await chooseType(user, "Other");
    const amount = screen.getByRole("textbox", { name: /^Amount/ });
    await user.clear(amount);
    await user.type(amount, "3");
    await user.click(screen.getByRole("button", { name: /^add adjustment$/i }));

    expect(
      await screen.findByText("Description is required when type is 'other'"),
    ).toBeInTheDocument();
    expect(screen.queryByRole("table")).not.toBeInTheDocument();
  });
});

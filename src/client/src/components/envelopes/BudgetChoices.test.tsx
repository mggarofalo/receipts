import { useState } from "react";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import "@/test/setup-combobox-polyfills";
import {
  EnvelopeBadge,
  EnvelopeOverrideMenu,
  OccasionChip,
  PaidFromSelect,
} from "./BudgetChoices";

beforeAll(() => {
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.setPointerCapture ??= () => {};
  Element.prototype.releasePointerCapture ??= () => {};
});

const envelopes = [
  { id: "env-groceries", name: "Groceries", isActive: true },
  { id: "env-gifts", name: "Gifts", isActive: true },
  { id: "env-old", name: "Old budget", isActive: false },
];

const occasions = [
  { id: "occ-birthday", name: "Birthday", isActive: true },
  { id: "occ-trip", name: "Beach trip", isActive: false },
];

describe("PaidFromSelect", () => {
  it("is hidden when there is no envelope to choose", () => {
    renderWithProviders(
      <PaidFromSelect
        envelopes={[{ id: "env-old", name: "Old budget", isActive: false }]}
        value={null}
        onChange={vi.fn()}
      />,
    );
    expect(
      screen.queryByRole("combobox", { name: "Paid from" }),
    ).not.toBeInTheDocument();
  });

  it("offers Auto and the active envelopes, and reports a choice", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithProviders(
      <PaidFromSelect envelopes={envelopes} value={null} onChange={onChange} />,
    );
    const select = screen.getByRole("combobox", { name: "Paid from" });
    expect(select).toHaveTextContent("Auto");

    await user.click(select);
    expect(screen.getByRole("option", { name: "Auto" })).toBeVisible();
    expect(screen.getByRole("option", { name: "Groceries" })).toBeVisible();
    expect(
      screen.queryByRole("option", { name: /Old budget/ }),
    ).not.toBeInTheDocument();
    await user.click(screen.getByRole("option", { name: "Gifts" }));

    expect(onChange).toHaveBeenCalledWith("env-gifts");
  });

  it("reports Auto as no envelope", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithProviders(
      <PaidFromSelect
        envelopes={envelopes}
        value="env-gifts"
        onChange={onChange}
      />,
    );
    await user.click(screen.getByRole("combobox", { name: "Paid from" }));
    await user.click(screen.getByRole("option", { name: "Auto" }));
    expect(onChange).toHaveBeenCalledWith(null);
  });

  it("keeps a stored archived envelope visible but not choosable", async () => {
    const user = userEvent.setup();
    renderWithProviders(
      <PaidFromSelect
        envelopes={envelopes}
        value="env-old"
        onChange={vi.fn()}
      />,
    );
    const select = screen.getByRole("combobox", { name: "Paid from" });
    expect(select).toHaveTextContent("Old budget (archived)");
    await user.click(select);
    expect(
      screen.getByRole("option", { name: "Old budget (archived)" }),
    ).toHaveAttribute("aria-disabled", "true");
  });
});

describe("OccasionChip", () => {
  it("is hidden when no occasion is active and none is stored", () => {
    renderWithProviders(
      <OccasionChip
        occasions={[{ id: "occ-trip", name: "Beach trip", isActive: false }]}
        value={null}
        onChange={vi.fn()}
      />,
    );
    expect(
      screen.queryByRole("button", { name: "Add occasion" }),
    ).not.toBeInTheDocument();
  });

  it("offers the active occasions and reports a choice", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithProviders(
      <OccasionChip occasions={occasions} value={null} onChange={onChange} />,
    );
    await user.click(screen.getByRole("button", { name: "Add occasion" }));
    expect(
      screen.queryByRole("menuitemradio", { name: "Beach trip" }),
    ).not.toBeInTheDocument();
    await user.click(screen.getByRole("menuitemradio", { name: "Birthday" }));
    expect(onChange).toHaveBeenCalledWith("occ-birthday");
  });

  it("names the chosen occasion and removes it", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithProviders(
      <OccasionChip
        occasions={occasions}
        value="occ-birthday"
        onChange={onChange}
      />,
    );
    expect(
      screen.getByRole("button", { name: "Occasion: Birthday" }),
    ).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Remove occasion" }));
    expect(onChange).toHaveBeenCalledWith(null);
  });

  it("returns focus to the chip after removing the occasion", async () => {
    const user = userEvent.setup();
    function Harness() {
      const [value, setValue] = useState<string | null>("occ-birthday");
      return (
        <OccasionChip occasions={occasions} value={value} onChange={setValue} />
      );
    }
    renderWithProviders(<Harness />);

    await user.click(screen.getByRole("button", { name: "Remove occasion" }));

    expect(
      screen.queryByRole("button", { name: "Remove occasion" }),
    ).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add occasion" })).toHaveFocus();
  });

  it("still shows a stored archived occasion", () => {
    renderWithProviders(
      <OccasionChip
        occasions={occasions}
        value="occ-trip"
        onChange={vi.fn()}
      />,
    );
    expect(
      screen.getByRole("button", { name: "Occasion: Beach trip" }),
    ).toBeVisible();
    expect(
      screen.getByRole("button", { name: "Remove occasion" }),
    ).toBeVisible();
  });
});

describe("EnvelopeBadge", () => {
  it("shows nothing while the envelope is not known yet", () => {
    const { container } = renderWithProviders(
      <EnvelopeBadge
        envelopes={envelopes}
        envelopeId={undefined}
        receiptEnvelopeId="env-groceries"
      />,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it("shows nothing for an item paid from the receipt's envelope", () => {
    const { container } = renderWithProviders(
      <EnvelopeBadge
        envelopes={envelopes}
        envelopeId="env-groceries"
        receiptEnvelopeId="env-groceries"
      />,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it("names a different envelope", () => {
    renderWithProviders(
      <EnvelopeBadge
        envelopes={envelopes}
        envelopeId="env-gifts"
        receiptEnvelopeId="env-groceries"
      />,
    );
    expect(screen.getByText("Gifts")).toBeVisible();
    // Screen readers hear what the badge means, not just a name.
    expect(screen.getByText("Gifts")).toHaveTextContent("Paid from Gifts");
  });

  it("marks an item with no envelope", () => {
    renderWithProviders(
      <EnvelopeBadge
        envelopes={envelopes}
        envelopeId={null}
        receiptEnvelopeId={null}
      />,
    );
    expect(screen.getByText("No envelope")).toBeVisible();
  });

  it("does not invent a name for an envelope it cannot find", () => {
    renderWithProviders(
      <EnvelopeBadge
        envelopes={envelopes}
        envelopeId="env-missing"
        receiptEnvelopeId="env-groceries"
      />,
    );
    expect(screen.getByText("Unknown envelope")).toBeVisible();
    expect(screen.getByText("Unknown envelope")).toHaveTextContent(
      "Paid from Unknown envelope",
    );
  });
});

describe("EnvelopeOverrideMenu", () => {
  it("offers Automatic and the active envelopes for the row", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithProviders(
      <EnvelopeOverrideMenu
        envelopes={envelopes}
        value={null}
        onChange={onChange}
        itemLabel="Milk"
      />,
    );
    await user.click(
      screen.getByRole("button", { name: "More actions for Milk" }),
    );
    expect(screen.getByText("Paid from")).toBeVisible();
    expect(
      screen.getByRole("menuitemradio", { name: "Automatic" }),
    ).toHaveAttribute("aria-checked", "true");
    expect(
      screen.queryByRole("menuitemradio", { name: "Old budget" }),
    ).not.toBeInTheDocument();
    await user.click(screen.getByRole("menuitemradio", { name: "Gifts" }));
    expect(onChange).toHaveBeenCalledWith("env-gifts");
  });

  it("returns an overridden row to automatic", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    renderWithProviders(
      <EnvelopeOverrideMenu
        envelopes={envelopes}
        value="env-gifts"
        onChange={onChange}
        itemLabel="Milk"
      />,
    );
    await user.click(
      screen.getByRole("button", { name: "More actions for Milk" }),
    );
    expect(
      screen.getByRole("menuitemradio", { name: "Gifts" }),
    ).toHaveAttribute("aria-checked", "true");
    await user.click(screen.getByRole("menuitemradio", { name: "Automatic" }));
    expect(onChange).toHaveBeenCalledWith(null);
  });
});

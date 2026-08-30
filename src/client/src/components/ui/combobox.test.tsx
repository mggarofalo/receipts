import { describe, it, expect, vi, beforeAll, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Combobox, type ComboboxOption } from "./combobox";

// Polyfill ResizeObserver and scrollIntoView for radix-ui / cmdk in jsdom
beforeAll(() => {
  if (typeof window.ResizeObserver === "undefined") {
    window.ResizeObserver = class {
      observe() {}
      unobserve() {}
      disconnect() {}
    } as unknown as typeof ResizeObserver;
  }

  // cmdk calls scrollIntoView on list items, which jsdom doesn't implement
  if (!Element.prototype.scrollIntoView) {
    Element.prototype.scrollIntoView = vi.fn();
  }
});

const options: ComboboxOption[] = [
  { value: "apple", label: "Apple" },
  { value: "banana", label: "Banana" },
  { value: "cherry", label: "Cherry" },
];

describe("Combobox", () => {
  const defaultProps = {
    options,
    value: "",
    onValueChange: vi.fn(),
    placeholder: "Select fruit...",
    searchPlaceholder: "Search fruit...",
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("renders with placeholder when no value is selected", () => {
    render(<Combobox {...defaultProps} />);

    expect(screen.getByRole("combobox")).toHaveTextContent("Select fruit...");
  });

  it("displays selected option label when value matches an option", () => {
    render(<Combobox {...defaultProps} value="banana" />);

    expect(screen.getByRole("combobox")).toHaveTextContent("Banana");
  });

  it("opens popover on click and shows options", async () => {
    const user = userEvent.setup();
    render(<Combobox {...defaultProps} />);

    const trigger = screen.getByRole("combobox");
    await user.click(trigger);

    await waitFor(() => {
      expect(screen.getByText("Apple")).toBeInTheDocument();
      expect(screen.getByText("Banana")).toBeInTheDocument();
      expect(screen.getByText("Cherry")).toBeInTheDocument();
    });
  });

  it("selects an option and calls onValueChange", async () => {
    const onValueChange = vi.fn();
    const user = userEvent.setup();

    render(<Combobox {...defaultProps} onValueChange={onValueChange} />);

    await user.click(screen.getByRole("combobox"));

    await waitFor(() => {
      expect(screen.getByText("Cherry")).toBeInTheDocument();
    });

    await user.click(screen.getByText("Cherry"));

    expect(onValueChange).toHaveBeenCalledWith("cherry");
  });

  it("shows loading text when loading prop is true", () => {
    render(<Combobox {...defaultProps} loading />);

    expect(screen.getByRole("combobox")).toHaveTextContent("Loading...");
  });

  it("shows raw value when value does not match any option", () => {
    render(<Combobox {...defaultProps} value="custom-value" />);

    expect(screen.getByRole("combobox")).toHaveTextContent("custom-value");
  });

  it("filters options when typing in the search input", async () => {
    const user = userEvent.setup();
    render(<Combobox {...defaultProps} />);

    await user.click(screen.getByRole("combobox"));

    await waitFor(() => {
      expect(screen.getByText("Apple")).toBeInTheDocument();
    });

    await user.type(screen.getByPlaceholderText("Search fruit..."), "app");

    await waitFor(() => {
      expect(screen.getByText("Apple")).toBeInTheDocument();
      expect(screen.queryByText("Banana")).not.toBeInTheDocument();
      expect(screen.queryByText("Cherry")).not.toBeInTheDocument();
    });
  });

  it("searches and selects an option beyond the first 50 results", async () => {
    const manyOptions: ComboboxOption[] = Array.from(
      { length: 75 },
      (_, index) => ({
        value: `category-${index + 1}`,
        label: `Category ${String(index + 1).padStart(2, "0")}`,
      }),
    );
    const onValueChange = vi.fn();
    const user = userEvent.setup();

    render(
      <Combobox
        {...defaultProps}
        options={manyOptions}
        onValueChange={onValueChange}
        searchPlaceholder="Search categories..."
      />,
    );

    await user.click(screen.getByRole("combobox"));
    await user.type(
      screen.getByPlaceholderText("Search categories..."),
      "Category 75",
    );

    const category = await screen.findByText("Category 75");
    await user.click(category);

    expect(onValueChange).toHaveBeenCalledWith("category-75");
  });

  it("shows empty message when no options match the search", async () => {
    const user = userEvent.setup();
    render(
      <Combobox {...defaultProps} emptyMessage="Nothing found." />,
    );

    await user.click(screen.getByRole("combobox"));

    await waitFor(() => {
      expect(screen.getByText("Apple")).toBeInTheDocument();
    });

    await user.type(screen.getByPlaceholderText("Search fruit..."), "xyz");

    await waitFor(() => {
      expect(screen.getByText("Nothing found.")).toBeInTheDocument();
    });
  });

  it("clears search and shows all options after selecting a filtered item", async () => {
    const onValueChange = vi.fn();
    const user = userEvent.setup();
    render(<Combobox {...defaultProps} onValueChange={onValueChange} />);

    // Open and type to filter
    await user.click(screen.getByRole("combobox"));
    await waitFor(() => {
      expect(screen.getByText("Apple")).toBeInTheDocument();
    });
    await user.type(screen.getByPlaceholderText("Search fruit..."), "ban");

    await waitFor(() => {
      expect(screen.getByText("Banana")).toBeInTheDocument();
      expect(screen.queryByText("Apple")).not.toBeInTheDocument();
    });

    // Select the filtered item
    await user.click(screen.getByText("Banana"));
    expect(onValueChange).toHaveBeenCalledWith("banana");

    // Reopen — all options should be visible (search was cleared)
    await user.click(screen.getByRole("combobox"));

    await waitFor(() => {
      expect(screen.getByText("Apple")).toBeInTheDocument();
      expect(screen.getByText("Banana")).toBeInTheDocument();
      expect(screen.getByText("Cherry")).toBeInTheDocument();
    });
  });

  it("clears search when popover closes without selection", async () => {
    const user = userEvent.setup();
    render(<Combobox {...defaultProps} />);

    // Open and type to filter
    await user.click(screen.getByRole("combobox"));
    await waitFor(() => {
      expect(screen.getByText("Apple")).toBeInTheDocument();
    });
    await user.type(screen.getByPlaceholderText("Search fruit..."), "ban");

    await waitFor(() => {
      expect(screen.getByText("Banana")).toBeInTheDocument();
      expect(screen.queryByText("Apple")).not.toBeInTheDocument();
    });

    // Close without selecting (press Escape)
    await user.keyboard("{Escape}");

    // Reopen — all options should be visible (search was cleared)
    await user.click(screen.getByRole("combobox"));

    await waitFor(() => {
      expect(screen.getByText("Apple")).toBeInTheDocument();
      expect(screen.getByText("Banana")).toBeInTheDocument();
      expect(screen.getByText("Cherry")).toBeInTheDocument();
    });
  });

  it("forwards aria-label to the underlying button trigger", () => {
    render(
      <Combobox
        {...defaultProps}
        aria-label="Filter by entity type"
      />,
    );

    expect(
      screen.getByRole("combobox", { name: "Filter by entity type" }),
    ).toBeInTheDocument();
  });

  it("filters by sublabel when present", async () => {
    const optionsWithSublabels: ComboboxOption[] = [
      { value: "milk", label: "Milk", sublabel: "Dairy" },
      { value: "bread", label: "Bread", sublabel: "Bakery" },
      { value: "apple", label: "Apple", sublabel: "Produce" },
    ];
    const user = userEvent.setup();
    render(
      <Combobox
        {...defaultProps}
        options={optionsWithSublabels}
        searchPlaceholder="Search items..."
      />,
    );

    await user.click(screen.getByRole("combobox"));
    await waitFor(() => {
      expect(screen.getByText("Milk")).toBeInTheDocument();
    });

    // Type the sublabel text — should match the item with that sublabel
    await user.type(screen.getByPlaceholderText("Search items..."), "dairy");

    await waitFor(() => {
      expect(screen.getByText("Milk")).toBeInTheDocument();
      expect(screen.queryByText("Bread")).not.toBeInTheDocument();
      expect(screen.queryByText("Apple")).not.toBeInTheDocument();
    });
  });
});

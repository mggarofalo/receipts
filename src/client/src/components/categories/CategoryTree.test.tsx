import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { CategoryTree } from "./CategoryTree";

const nodes = [
  { id: "food", name: "Food", archived: false },
  { id: "dairy", name: "Dairy", parentId: "food", archived: false },
  { id: "milk", name: "Milk", parentId: "dairy", archived: false },
  { id: "plant", name: "Plant", archived: false },
  { id: "plant-milk", name: "Milk", parentId: "plant", archived: false },
  { id: "old", name: "Old", parentId: "food", archived: true },
];

describe("CategoryTree", () => {
  it("starts with collapsed roots and supports arrow keys, Home, End and typeahead", async () => {
    render(<CategoryTree nodes={nodes} />);
    const food = screen.getByRole("treeitem", { name: "Food" });
    expect(screen.getAllByRole("treeitem")).toHaveLength(2);
    expect(food).toHaveAttribute("aria-expanded", "false");
    food.focus();
    await userEvent.keyboard("{ArrowRight}{ArrowRight}");
    const dairy = screen.getByRole("treeitem", { name: "Food / Dairy" });
    expect(dairy).toHaveFocus();
    expect(dairy).toHaveAttribute("aria-level", "2");
    await userEvent.keyboard("{ArrowRight}{ArrowDown}");
    expect(
      screen.getByRole("treeitem", { name: "Food / Dairy / Milk" }),
    ).toHaveFocus();
    await userEvent.keyboard("{ArrowLeft}{ArrowLeft}");
    expect(dairy).toHaveFocus();
    expect(dairy).toHaveAttribute("aria-expanded", "false");
    await userEvent.keyboard("{Home}p");
    expect(screen.getByRole("treeitem", { name: "Plant" })).toHaveFocus();
    await userEvent.keyboard("{Home}{End}");
    expect(screen.getByRole("treeitem", { name: "Plant" })).toHaveFocus();
  });

  it("reveals matching duplicate labels and ancestors, then restores browse expansion", async () => {
    const { rerender } = render(<CategoryTree nodes={nodes} />);
    await userEvent.click(screen.getByRole("button", { name: "Expand Food" }));
    rerender(<CategoryTree nodes={nodes} search="milk" />);
    expect(screen.getAllByRole("treeitem")).toHaveLength(5);
    expect(
      screen.getByRole("treeitem", { name: "Food / Dairy / Milk" }),
    ).toHaveAttribute("aria-level", "3");
    expect(
      screen.getByRole("treeitem", { name: "Plant / Milk" }),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Collapse all" })).toBeDisabled();
    rerender(<CategoryTree nodes={nodes} />);
    expect(
      screen.getByRole("treeitem", { name: "Food / Dairy" }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("treeitem", { name: "Food / Dairy / Milk" }),
    ).not.toBeInTheDocument();
  });

  it("shows archived categories explicitly and reports no matches", async () => {
    const { rerender } = render(
      <CategoryTree nodes={nodes} showArchived search="old" />,
    );
    expect(
      screen.getByRole("treeitem", { name: "Food / Old" }),
    ).toHaveTextContent("Archived");
    expect(screen.getByRole("treeitem", { name: "Food" })).toBeInTheDocument();
    rerender(<CategoryTree nodes={nodes} search="old" />);
    expect(screen.getByText("No matching categories.")).toBeInTheDocument();
    rerender(<CategoryTree nodes={nodes} showArchived />);
    await userEvent.click(screen.getByRole("button", { name: "Expand all" }));
    expect(screen.getAllByRole("treeitem")).toHaveLength(6);
    await userEvent.click(screen.getByRole("button", { name: "Collapse all" }));
    expect(screen.getAllByRole("treeitem")).toHaveLength(2);
  });

  it("keeps collapsed large branches out of the DOM and searches the complete tree", () => {
    const large = [
      nodes[0],
      ...Array.from({ length: 2000 }, (_, i) => ({
        id: `child-${i}`,
        name: `Category ${i}`,
        parentId: "food",
        archived: false,
      })),
    ];
    const { rerender } = render(<CategoryTree nodes={large} />);
    expect(screen.getAllByRole("treeitem")).toHaveLength(1);
    rerender(<CategoryTree nodes={large} search="Category 1999" />);
    expect(screen.getAllByRole("treeitem")).toHaveLength(2);
    expect(
      screen.getByRole("treeitem", { name: "Food / Category 1999" }),
    ).toHaveAttribute("aria-setsize", "1");
  });

  it("uses one tree tab stop and exposes actions only in the focused row's tab sequence", async () => {
    render(
      <CategoryTree
        nodes={nodes}
        renderActions={(node, _path, focused) => (
          <button tabIndex={focused ? 0 : -1}>Edit {node.name}</button>
        )}
      />,
    );
    const food = screen.getByRole("treeitem", { name: "Food" });
    const plant = screen.getByRole("treeitem", { name: "Plant" });
    expect(food).toHaveAttribute("tabindex", "0");
    expect(plant).toHaveAttribute("tabindex", "-1");
    food.focus();
    await userEvent.keyboard("{ArrowDown}");
    expect(plant).toHaveFocus();
    expect(screen.getByRole("button", { name: "Edit Plant" })).toHaveAttribute(
      "tabindex",
      "0",
    );
    expect(screen.getByRole("button", { name: "Edit Food" })).toHaveAttribute(
      "tabindex",
      "-1",
    );
  });
});

it("offers the current empty state without obsolete legacy navigation", () => {
  render(<CategoryTree nodes={[]} />);
  expect(screen.getByText("No categories yet.")).toBeInTheDocument();
  expect(
    screen.queryByText(/legacy receipt categories/i),
  ).not.toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Expand all" })).toBeDisabled();
});

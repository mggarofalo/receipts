import "@/test/setup-combobox-polyfills";
import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createRef } from "react";
import { CategoryPathPicker } from "./CategoryPathPicker";
import type { CategoryRevision } from "@/lib/category-paths";

const revision: CategoryRevision = {
  id: "revision",
  sequence: 1,
  recordedAt: "2026-10-26T00:00:00Z",
  actor: "admin",
  reason: "Initial",
  operationId: "operation",
  affectedCount: 4,
  nodes: [
    { id: "food", name: "Food", archived: false },
    { id: "fruit", parentId: "food", name: "Fruit", archived: false },
    { id: "apple", parentId: "fruit", name: "Apple", archived: false },
    { id: "archived", name: "Old", archived: true },
  ],
};

describe("CategoryPathPicker", () => {
  it("searches full paths and returns stable category and revision IDs", async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    render(
      <CategoryPathPicker
        revision={revision}
        onValueChange={onValueChange}
        aria-label="Category"
      />,
    );
    await user.click(screen.getByRole("combobox", { name: "Category" }));
    await user.type(
      screen.getByPlaceholderText("Search category paths…"),
      "Food Fruit Apple",
    );
    await user.click(
      screen.getByRole("option", { name: "Food / Fruit / Apple" }),
    );
    expect(onValueChange).toHaveBeenCalledWith({
      categoryId: "apple",
      taxonomyRevisionId: "revision",
      path: revision.nodes.slice(0, 3),
    });
  });

  it("allows parent and explicit unclassified choices without a second review", async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    render(
      <CategoryPathPicker revision={revision} onValueChange={onValueChange} />,
    );
    await user.click(screen.getByRole("combobox"));
    expect(
      screen.queryByRole("option", { name: "Old" }),
    ).not.toBeInTheDocument();
    await user.click(screen.getByRole("option", { name: /^Food$/ }));
    expect(onValueChange).toHaveBeenLastCalledWith({
      categoryId: "food",
      taxonomyRevisionId: "revision",
      path: [revision.nodes[0]],
    });
    await user.click(screen.getByRole("combobox"));
    await user.click(screen.getByRole("option", { name: "Unclassified" }));
    expect(onValueChange).toHaveBeenLastCalledWith({
      categoryId: null,
      taxonomyRevisionId: "revision",
      path: [],
    });
  });

  it("preserves the selected historical path through refresh and lookup failures", () => {
    const onValueChange = vi.fn();
    const value = {
      categoryId: "old",
      taxonomyRevisionId: "old-revision",
      path: [{ id: "old", name: "Historical name", archived: false }],
    };
    const ref = createRef<HTMLButtonElement>();
    const { rerender } = render(
      <CategoryPathPicker
        ref={ref}
        revision={revision}
        value={value}
        onValueChange={onValueChange}
        aria-describedby="category-help"
      />,
    );
    expect(screen.getByRole("combobox")).toHaveTextContent("Historical name");
    expect(ref.current).toHaveAttribute("aria-describedby", "category-help");
    rerender(
      <CategoryPathPicker value={value} onValueChange={onValueChange} error />,
    );
    expect(screen.getByRole("combobox")).toHaveTextContent("Historical name");
    expect(screen.getByRole("combobox")).toBeDisabled();
    expect(onValueChange).not.toHaveBeenCalled();
  });

  it("exposes loading and initial error states without inventing a choice", () => {
    const onValueChange = vi.fn();
    const { rerender } = render(
      <CategoryPathPicker onValueChange={onValueChange} loading />,
    );
    expect(screen.getByRole("combobox")).toHaveTextContent(
      "Loading categories…",
    );
    expect(screen.getByRole("combobox")).toHaveAttribute("aria-busy", "true");
    rerender(<CategoryPathPicker onValueChange={onValueChange} error />);
    expect(screen.getByRole("combobox")).toHaveTextContent(
      "Categories unavailable",
    );
    expect(screen.getByRole("combobox")).toBeDisabled();
  });
});

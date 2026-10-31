import "@/test/setup-combobox-polyfills";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createRef } from "react";
import type { CategoryNode, CategoryRevision } from "@/lib/category-paths";
import { renderWithQueryClient } from "@/test/test-utils";
import mockClient, { resetMockClient } from "@/test/mock-api-client";
import { CategoryPathPicker } from "./CategoryPathPicker";

vi.mock("@/lib/api-client", () => ({ default: mockClient }));

const RECENT_KEY = "category-picker:recent";

const food: CategoryNode = {
  id: "food",
  name: "Food",
  definition: "Things you eat",
  archived: false,
};
const fruit: CategoryNode = {
  id: "fruit",
  parentId: "food",
  name: "Fruit",
  archived: false,
};
const apple: CategoryNode = {
  id: "apple",
  parentId: "fruit",
  name: "Apple",
  archived: false,
};
const banana: CategoryNode = {
  id: "banana",
  parentId: "fruit",
  name: "Banana",
  archived: false,
};
const dairy: CategoryNode = {
  id: "dairy",
  parentId: "food",
  name: "Dairy",
  definition: "Milk, cheese and yogurt",
  archived: false,
};
const home: CategoryNode = { id: "home", name: "Home", archived: false };
const homeApple: CategoryNode = {
  id: "home-apple",
  parentId: "home",
  name: "Apple",
  definition: "Devices and accessories",
  archived: false,
};
const archived: CategoryNode = { id: "archived", name: "Old", archived: true };

const revision: CategoryRevision = {
  id: "revision",
  sequence: 1,
  recordedAt: "2026-10-26T00:00:00Z",
  actor: "admin",
  reason: "Initial",
  operationId: "operation",
  affectedCount: 8,
  nodes: [food, fruit, apple, banana, dairy, home, homeApple, archived],
};

function choice(...path: CategoryNode[]) {
  return {
    categoryId: path.at(-1)?.id ?? null,
    taxonomyRevisionId: revision.id,
    path,
  };
}

const searchBox = () =>
  screen.getByRole("combobox", { name: "Search category paths" });

function optionNames() {
  return within(screen.getByRole("listbox", { name: "Categories" }))
    .getAllByRole("option")
    .map((option) => option.getAttribute("aria-label"));
}

function expectActive(name: string) {
  expect(searchBox()).toHaveAttribute(
    "aria-activedescendant",
    screen.getByRole("option", { name }).id,
  );
}

/** The "use the parent itself" row: visible name, accessible full path. */
function expectParentRow(path: string, name: string) {
  const row = screen.getByRole("option", { name: path });
  expect(row).toHaveTextContent(name);
  expect(row).toHaveTextContent("Use without a subcategory");
}

async function openPicker(
  user: ReturnType<typeof userEvent.setup>,
  name?: string,
) {
  await user.click(
    name
      ? screen.getByRole("combobox", { name })
      : screen.getByRole("combobox"),
  );
  return searchBox();
}

describe("CategoryPathPicker", () => {
  beforeEach(() => {
    resetMockClient();
    localStorage.clear();
  });

  describe("browsing", () => {
    it("opens at the roots, one level at a time, without archived paths", async () => {
      const user = userEvent.setup();
      render(
        <CategoryPathPicker revision={revision} onValueChange={vi.fn()} />,
      );
      await openPicker(user);
      expect(optionNames()).toEqual([
        "Unclassified",
        "Food, 2 subcategories",
        "Home, 1 subcategory",
      ]);
      expect(screen.queryByRole("option", { name: /Old/ })).toBeNull();
      expectActive("Unclassified");
    });

    it("drills into a branch on click and offers the parent itself first", async () => {
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      render(
        <CategoryPathPicker
          revision={revision}
          onValueChange={onValueChange}
        />,
      );
      await openPicker(user);
      await user.click(
        screen.getByRole("option", { name: "Food, 2 subcategories" }),
      );
      expect(optionNames()).toEqual([
        "Food",
        "Food / Dairy",
        "Fruit, 2 subcategories",
      ]);
      expectParentRow("Food", "Food");
      expectActive("Food / Dairy");
      expect(onValueChange).not.toHaveBeenCalled();

      await user.click(screen.getByRole("option", { name: "Food" }));
      expect(onValueChange).toHaveBeenCalledWith(choice(food));
      expect(screen.queryByRole("listbox")).not.toBeInTheDocument();
    });

    it("navigates in and out with ArrowRight, ArrowLeft and Backspace", async () => {
      const user = userEvent.setup();
      render(
        <CategoryPathPicker revision={revision} onValueChange={vi.fn()} />,
      );
      const input = await openPicker(user);
      expect(input).toHaveFocus();

      await user.keyboard("{ArrowDown}{ArrowRight}");
      expect(optionNames()[0]).toBe("Food");
      expectActive("Food / Dairy");

      await user.keyboard("{ArrowDown}{ArrowRight}");
      expect(optionNames()).toEqual([
        "Food / Fruit",
        "Food / Fruit / Apple",
        "Food / Fruit / Banana",
      ]);

      await user.keyboard("{ArrowLeft}");
      expect(optionNames()[0]).toBe("Food");
      expectActive("Fruit, 2 subcategories");

      await user.keyboard("{Backspace}");
      expect(optionNames()[0]).toBe("Unclassified");
      expectActive("Food, 2 subcategories");
    });

    it("climbs back with the back button and the breadcrumb", async () => {
      const user = userEvent.setup();
      render(
        <CategoryPathPicker revision={revision} onValueChange={vi.fn()} />,
      );
      await openPicker(user);
      await user.click(
        screen.getByRole("option", { name: "Food, 2 subcategories" }),
      );
      await user.click(
        screen.getByRole("option", { name: "Fruit, 2 subcategories" }),
      );
      const crumbs = screen.getByRole("navigation", { name: "Category path" });
      expect(within(crumbs).getByText("Fruit")).toHaveAttribute(
        "aria-current",
        "location",
      );

      await user.click(within(crumbs).getByRole("button", { name: "Food" }));
      expect(optionNames()[0]).toBe("Food");

      await user.click(
        screen.getByRole("button", { name: "Back to all categories" }),
      );
      expect(optionNames()[0]).toBe("Unclassified");
      expect(
        screen.queryByRole("navigation", { name: "Category path" }),
      ).toBeNull();
    });

    it("lets Enter and Space on the panel's buttons press the button, not the active option", async () => {
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      renderWithQueryClient(
        <CategoryPathPicker
          revision={revision}
          value={choice(food, fruit, apple)}
          onValueChange={onValueChange}
          canCreate
        />,
      );
      await openPicker(user);
      expectActive("Food / Fruit / Apple");

      screen.getByRole("button", { name: "Back to Food" }).focus();
      await user.keyboard("{Enter}");
      expect(optionNames()[0]).toBe("Food");
      expect(onValueChange).not.toHaveBeenCalled();

      await user.click(
        screen.getByRole("option", { name: "Fruit, 2 subcategories" }),
      );
      within(screen.getByRole("navigation", { name: "Category path" }))
        .getByRole("button", { name: "Food" })
        .focus();
      await user.keyboard(" ");
      expect(optionNames()[0]).toBe("Food");
      expect(onValueChange).not.toHaveBeenCalled();

      screen.getByRole("button", { name: "Create category in Food" }).focus();
      await user.keyboard("{Enter}");
      expect(
        screen.getByRole("textbox", { name: "New category" }),
      ).toBeInTheDocument();
      expect(onValueChange).not.toHaveBeenCalled();
    });

    it("keeps focus in the search box after keyboard-pressing Back or a crumb", async () => {
      const user = userEvent.setup();
      render(
        <CategoryPathPicker
          revision={revision}
          value={choice(food, fruit, apple)}
          onValueChange={vi.fn()}
        />,
      );
      await openPicker(user);
      screen.getByRole("button", { name: "Back to Food" }).focus();
      await user.keyboard("{Enter}");
      expect(optionNames()[0]).toBe("Food");
      expect(document.activeElement).toBe(searchBox());

      await user.keyboard("{ArrowDown}{ArrowDown}{ArrowRight}");
      expect(optionNames()[0]).toBe("Food / Fruit");
      within(screen.getByRole("navigation", { name: "Category path" }))
        .getByRole("button", { name: "Food" })
        .focus();
      await user.keyboard(" ");
      expect(optionNames()[0]).toBe("Food");
      expect(document.activeElement).toBe(searchBox());
      // The panel still answers the keyboard from there.
      await user.keyboard("{ArrowLeft}");
      expect(optionNames()[0]).toBe("Unclassified");
    });

    it("labels the back button with the parent level", async () => {
      const user = userEvent.setup();
      render(
        <CategoryPathPicker
          revision={revision}
          value={choice(food, fruit)}
          onValueChange={vi.fn()}
        />,
      );
      await openPicker(user);
      await user.click(screen.getByRole("button", { name: "Back to Food" }));
      expectActive("Fruit, 2 subcategories");
    });

    it("moves with Home and End and stops at the ends", async () => {
      const user = userEvent.setup();
      render(
        <CategoryPathPicker revision={revision} onValueChange={vi.fn()} />,
      );
      await openPicker(user);
      await user.keyboard("{ArrowUp}");
      expectActive("Unclassified");
      await user.keyboard("{End}");
      expectActive("Home, 1 subcategory");
      await user.keyboard("{ArrowDown}");
      expectActive("Home, 1 subcategory");
      await user.keyboard("{Home}");
      expectActive("Unclassified");
    });

    it("announces the level and its size", async () => {
      const user = userEvent.setup();
      render(
        <CategoryPathPicker revision={revision} onValueChange={vi.fn()} />,
      );
      await openPicker(user);
      expect(screen.getByRole("status")).toHaveTextContent(
        "All categories, 2 groups",
      );
      await user.keyboard("{ArrowDown}{ArrowRight}");
      expect(screen.getByRole("status")).toHaveTextContent(
        "Food, 2 subcategories",
      );
      await user.type(searchBox(), "apple");
      expect(screen.getByRole("status")).toHaveTextContent("2 matches");
      await user.clear(searchBox());
      await user.type(searchBox(), "zzzz");
      expect(screen.getByRole("status")).toHaveTextContent(
        "No categories match zzzz",
      );
    });
  });

  describe("opening beside the current value", () => {
    it("opens inside a chosen branch with that branch active", async () => {
      const user = userEvent.setup();
      render(
        <CategoryPathPicker
          revision={revision}
          value={choice(food, fruit)}
          onValueChange={vi.fn()}
        />,
      );
      await openPicker(user);
      expectParentRow("Food / Fruit", "Fruit");
      expectActive("Food / Fruit");
    });

    it("opens in a leaf's level with the leaf active", async () => {
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      render(
        <CategoryPathPicker
          revision={revision}
          value={choice(food, fruit, banana)}
          onValueChange={onValueChange}
        />,
      );
      await openPicker(user);
      expect(optionNames()[0]).toBe("Food / Fruit");
      expectActive("Food / Fruit / Banana");
      await user.keyboard("{ArrowUp}{Enter}");
      expect(onValueChange).toHaveBeenCalledWith(choice(food, fruit, apple));
    });

    it("opens at the roots with Unclassified active for an unclassified value", async () => {
      const user = userEvent.setup();
      render(
        <CategoryPathPicker
          revision={revision}
          value={{
            categoryId: null,
            taxonomyRevisionId: revision.id,
            path: [],
          }}
          onValueChange={vi.fn()}
        />,
      );
      expect(screen.getByRole("combobox")).toHaveTextContent("Unclassified");
      await openPicker(user);
      expectActive("Unclassified");
    });
  });

  describe("searching", () => {
    it("searches the whole tree and returns stable category and revision IDs", async () => {
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      render(
        <CategoryPathPicker
          revision={revision}
          onValueChange={onValueChange}
          aria-label="Category"
        />,
      );
      await openPicker(user, "Category");
      await user.type(searchBox(), "Food Fruit Apple");
      await user.click(
        screen.getByRole("option", { name: "Food / Fruit / Apple" }),
      );
      expect(onValueChange).toHaveBeenCalledWith({
        categoryId: "apple",
        taxonomyRevisionId: "revision",
        path: [food, fruit, apple],
      });
    });

    it("disambiguates duplicate leaf names by path and selects with Enter", async () => {
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      render(
        <CategoryPathPicker
          revision={revision}
          onValueChange={onValueChange}
        />,
      );
      await openPicker(user);
      await user.type(searchBox(), "apple");
      expect(
        screen.getByRole("option", { name: "Food / Fruit / Apple" }),
      ).toBeInTheDocument();
      expect(
        screen.getByRole("option", { name: "Home / Apple" }),
      ).toBeInTheDocument();

      await user.clear(searchBox());
      await user.type(searchBox(), "Home Apple");
      expect(
        screen.queryByRole("option", { name: "Food / Fruit / Apple" }),
      ).not.toBeInTheDocument();
      expectActive("Home / Apple");
      await user.keyboard("{Enter}");
      expect(onValueChange).toHaveBeenCalledWith(choice(home, homeApple));
      expect(screen.queryByRole("option")).not.toBeInTheDocument();
    });

    it("matches definition text and shows why", async () => {
      const user = userEvent.setup();
      render(
        <CategoryPathPicker revision={revision} onValueChange={vi.fn()} />,
      );
      await openPicker(user);
      await user.type(searchBox(), "cheese");
      expect(optionNames()).toEqual(["Food / Dairy"]);
      const option = screen.getByRole("option", { name: "Food / Dairy" });
      expect(within(option).getByText("cheese").tagName).toBe("MARK");
    });

    it("tolerates a one-letter typo", async () => {
      const user = userEvent.setup();
      render(
        <CategoryPathPicker revision={revision} onValueChange={vi.fn()} />,
      );
      await openPicker(user);
      // A transposition: no prefix, substring or in-order letters match it.
      await user.type(searchBox(), "bnaana");
      expect(optionNames()).toEqual(["Food / Fruit / Banana"]);
      await user.clear(searchBox());
      await user.type(searchBox(), "appel");
      expect(optionNames()).toEqual(
        expect.arrayContaining(["Food / Fruit / Apple", "Home / Apple"]),
      );
    });

    it("makes branches selectable in results and browses into them with ArrowRight", async () => {
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      render(
        <CategoryPathPicker
          revision={revision}
          onValueChange={onValueChange}
        />,
      );
      await openPicker(user);
      await user.type(searchBox(), "fruit");
      expectActive("Food / Fruit");
      await user.keyboard("{ArrowRight}");
      expect(searchBox()).toHaveValue("");
      expect(optionNames()).toEqual([
        "Food / Fruit",
        "Food / Fruit / Apple",
        "Food / Fruit / Banana",
      ]);
      expect(onValueChange).not.toHaveBeenCalled();

      await user.type(searchBox(), "fruit");
      await user.keyboard("{Enter}");
      expect(onValueChange).toHaveBeenCalledWith(choice(food, fruit));
    });

    it("keeps Backspace editing the query instead of climbing a level", async () => {
      const user = userEvent.setup();
      render(
        <CategoryPathPicker
          revision={revision}
          value={choice(food, fruit)}
          onValueChange={vi.fn()}
        />,
      );
      await openPicker(user);
      await user.type(searchBox(), "ap{Backspace}");
      expect(searchBox()).toHaveValue("a");
      await user.keyboard("{Backspace}");
      expect(searchBox()).toHaveValue("");
      expectParentRow("Food / Fruit", "Fruit");
    });

    it("never puts Unclassified ahead of real matches for a short query", async () => {
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      const clothing = { id: "clothing", name: "Clothing", archived: false };
      const cleaning = { id: "cleaning", name: "Cleaning", archived: false };
      const uncle = { id: "uncle", name: "Uncle gifts", archived: false };
      render(
        <CategoryPathPicker
          revision={{ ...revision, nodes: [clothing, cleaning, uncle] }}
          onValueChange={onValueChange}
        />,
      );
      await openPicker(user);
      await user.type(searchBox(), "cl");
      expect(optionNames()).not.toContain("Unclassified");
      await user.keyboard("{Enter}");
      expect(onValueChange).toHaveBeenCalledWith(
        expect.objectContaining({ categoryId: "cleaning" }),
      );

      await openPicker(user);
      await user.type(searchBox(), "uncl");
      expect(optionNames()).toEqual(["Uncle gifts", "Unclassified"]);
      expect(screen.getByRole("status")).toHaveTextContent("2 matches");
      await user.clear(searchBox());
      await user.type(searchBox(), "un");
      expect(optionNames()).not.toContain("Unclassified");
      await user.clear(searchBox());
      await user.type(searchBox(), "uncl gifts");
      expect(optionNames()).toEqual(["Uncle gifts"]);
    });

    it("finds Unclassified by name", async () => {
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      render(
        <CategoryPathPicker
          revision={revision}
          onValueChange={onValueChange}
        />,
      );
      await openPicker(user);
      await user.type(searchBox(), "unclass");
      await user.keyboard("{Enter}");
      expect(onValueChange).toHaveBeenCalledWith({
        categoryId: null,
        taxonomyRevisionId: "revision",
        path: [],
      });
    });

    it("limits a huge tree to 50 rendered results and asks for more letters", async () => {
      const nodes: CategoryNode[] = [];
      for (let group = 0; group < 25; group++) {
        nodes.push({
          id: `g${group}`,
          name: `Group ${group}`,
          archived: false,
        });
        for (let item = 0; item < 48; item++)
          nodes.push({
            id: `g${group}-i${item}`,
            parentId: `g${group}`,
            name: `Item ${group}-${item}`,
            archived: false,
          });
      }
      expect(nodes.length).toBeGreaterThan(1000);
      const user = userEvent.setup();
      render(
        <CategoryPathPicker
          revision={{ ...revision, nodes }}
          onValueChange={vi.fn()}
        />,
      );
      await openPicker(user);
      expect(optionNames()).toHaveLength(26);

      fireEvent.change(searchBox(), { target: { value: "item" } });
      expect(optionNames()).toHaveLength(50);
      const note = screen.getByText(
        "Showing 50 of 1200. Keep typing to narrow.",
      );
      expect(
        screen.getByRole("listbox", { name: "Categories" }),
      ).not.toContainElement(note);
      expect(screen.getByRole("status")).toHaveTextContent("1200 matches");

      fireEvent.change(searchBox(), { target: { value: "item 7-31" } });
      expect(optionNames()[0]).toBe("Group 7 / Item 7-31");
      expect(screen.queryByText(/Keep typing/)).toBeNull();
    });
  });

  describe("choices", () => {
    it("allows parent and explicit unclassified choices without a second review", async () => {
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      render(
        <CategoryPathPicker
          revision={revision}
          onValueChange={onValueChange}
        />,
      );
      await openPicker(user);
      await user.click(
        screen.getByRole("option", { name: "Food, 2 subcategories" }),
      );
      await user.click(screen.getByRole("option", { name: "Food" }));
      expect(onValueChange).toHaveBeenLastCalledWith(choice(food));

      await openPicker(user);
      await user.click(screen.getByRole("option", { name: "Unclassified" }));
      expect(onValueChange).toHaveBeenLastCalledWith({
        categoryId: null,
        taxonomyRevisionId: "revision",
        path: [],
      });
    });

    it("can hide Unclassified for consumers that require a stable category ID", async () => {
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      render(
        <CategoryPathPicker
          revision={revision}
          onValueChange={onValueChange}
          allowUnclassified={false}
        />,
      );
      await openPicker(user);
      expect(
        screen.queryByRole("option", { name: "Unclassified" }),
      ).not.toBeInTheDocument();
      await user.type(searchBox(), "unclassified");
      expect(
        screen.queryByRole("option", { name: "Unclassified" }),
      ).not.toBeInTheDocument();
      await user.clear(searchBox());
      await user.type(searchBox(), "apple");
      await user.click(
        screen.getByRole("option", { name: "Food / Fruit / Apple" }),
      );
      expect(onValueChange).toHaveBeenCalledWith(
        expect.objectContaining({ categoryId: "apple" }),
      );
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
        <CategoryPathPicker
          value={value}
          onValueChange={onValueChange}
          error
        />,
      );
      expect(screen.getByRole("combobox")).toHaveTextContent("Historical name");
      expect(screen.getByRole("combobox")).toBeDisabled();
      expect(onValueChange).not.toHaveBeenCalled();
    });

    it("opens at the roots when the value is no longer in the tree", async () => {
      const user = userEvent.setup();
      render(
        <CategoryPathPicker
          revision={revision}
          value={{
            categoryId: "old",
            taxonomyRevisionId: "old-revision",
            path: [{ id: "old", name: "Historical name", archived: false }],
          }}
          onValueChange={vi.fn()}
        />,
      );
      await openPicker(user);
      expect(optionNames()[0]).toBe("Unclassified");
    });

    it("shows the leaf with compact ancestry on the trigger", () => {
      render(
        <CategoryPathPicker
          revision={revision}
          value={choice(food, fruit, apple)}
          onValueChange={vi.fn()}
        />,
      );
      const trigger = screen.getByRole("combobox");
      expect(trigger).toHaveTextContent("Apple");
      expect(trigger).toHaveTextContent("Food / Fruit");
      expect(trigger).toHaveAttribute("title", "Food / Fruit / Apple");
    });

    it("closes for good when it becomes unavailable while open", async () => {
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      const { rerender } = render(
        <CategoryPathPicker
          revision={revision}
          onValueChange={onValueChange}
        />,
      );
      await openPicker(user);
      expect(screen.getByRole("listbox")).toBeInTheDocument();
      rerender(
        <CategoryPathPicker
          revision={revision}
          onValueChange={onValueChange}
          disabled
        />,
      );
      expect(screen.queryByRole("listbox")).not.toBeInTheDocument();
      expect(screen.getByRole("combobox")).toBeDisabled();
      rerender(
        <CategoryPathPicker
          revision={revision}
          onValueChange={onValueChange}
        />,
      );
      expect(screen.queryByRole("listbox")).not.toBeInTheDocument();
      expect(screen.getByRole("combobox")).toHaveAttribute(
        "aria-expanded",
        "false",
      );

      await openPicker(user);
      rerender(
        <CategoryPathPicker
          revision={revision}
          onValueChange={onValueChange}
          loading
        />,
      );
      expect(screen.queryByRole("listbox")).not.toBeInTheDocument();
      rerender(
        <CategoryPathPicker
          revision={revision}
          onValueChange={onValueChange}
        />,
      );
      expect(screen.queryByRole("listbox")).not.toBeInTheDocument();
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

  describe("recent choices", () => {
    it("offers recent choices from this browser at the root, skipping unknown IDs", async () => {
      localStorage.setItem(
        RECENT_KEY,
        JSON.stringify(["banana", "gone", "home-apple"]),
      );
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      render(
        <CategoryPathPicker
          revision={revision}
          onValueChange={onValueChange}
        />,
      );
      await openPicker(user);
      const recent = screen.getByRole("group", { name: "Recent" });
      expect(
        within(recent)
          .getAllByRole("option")
          .map((option) => option.getAttribute("aria-label")),
      ).toEqual(["Food / Fruit / Banana (recent)", "Home / Apple (recent)"]);
      expect(
        screen.getByRole("group", { name: "All categories" }),
      ).toBeVisible();
      await user.click(
        screen.getByRole("option", { name: "Home / Apple (recent)" }),
      );
      expect(onValueChange).toHaveBeenCalledWith(choice(home, homeApple));
    });

    it("remembers a choice for next time, most recent first", async () => {
      localStorage.setItem(RECENT_KEY, JSON.stringify(["banana"]));
      const user = userEvent.setup();
      render(
        <CategoryPathPicker revision={revision} onValueChange={vi.fn()} />,
      );
      await openPicker(user);
      await user.type(searchBox(), "cheese");
      await user.keyboard("{Enter}");

      await openPicker(user);
      expect(
        within(screen.getByRole("group", { name: "Recent" }))
          .getAllByRole("option")
          .map((option) => option.getAttribute("aria-label")),
      ).toEqual(["Food / Dairy (recent)", "Food / Fruit / Banana (recent)"]);
    });
  });

  describe("suggestions", () => {
    it("opens at the top with the current choice active when it is suggested", async () => {
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      render(
        <CategoryPathPicker
          revision={revision}
          value={choice(food, fruit, banana)}
          onValueChange={onValueChange}
          suggestions={[{ categoryId: "dairy" }, { categoryId: "banana" }]}
        />,
      );
      await openPicker(user);
      expect(
        within(screen.getByRole("group", { name: "Suggested" }))
          .getAllByRole("option")
          .map((option) => option.getAttribute("aria-label")),
      ).toEqual([
        "Food / Dairy (suggested)",
        "Food / Fruit / Banana (suggested)",
      ]);
      expect(
        screen.getByRole("option", { name: "Food, 2 subcategories" }),
      ).toBeInTheDocument();
      expectActive("Food / Fruit / Banana (suggested)");
      await user.keyboard("{Enter}");
      expect(onValueChange).toHaveBeenCalledWith(choice(food, fruit, banana));
    });

    it("opens beside the current choice when it is not suggested", async () => {
      const user = userEvent.setup();
      render(
        <CategoryPathPicker
          revision={revision}
          value={choice(food, fruit, apple)}
          onValueChange={vi.fn()}
          suggestions={[{ categoryId: "dairy" }, { categoryId: "banana" }]}
        />,
      );
      await openPicker(user);
      expect(
        screen.queryByRole("group", { name: "Suggested" }),
      ).not.toBeInTheDocument();
      expect(optionNames()[0]).toBe("Food / Fruit");
      expectActive("Food / Fruit / Apple");
    });

    it("opens at the top with the first known suggestion active when nothing is chosen", async () => {
      const user = userEvent.setup();
      render(
        <CategoryPathPicker
          revision={revision}
          onValueChange={vi.fn()}
          suggestions={[
            { categoryId: "missing" },
            { categoryId: "dairy" },
            { categoryId: "dairy" },
            { categoryId: "banana" },
          ]}
        />,
      );
      await openPicker(user);
      expect(
        within(screen.getByRole("group", { name: "Suggested" }))
          .getAllByRole("option")
          .map((option) => option.getAttribute("aria-label")),
      ).toEqual([
        "Food / Dairy (suggested)",
        "Food / Fruit / Banana (suggested)",
      ]);
      expectActive("Food / Dairy (suggested)");
    });

    it("opens beside the current choice when it is suggested only beyond the shown three", async () => {
      const user = userEvent.setup();
      render(
        <CategoryPathPicker
          revision={revision}
          value={choice(food, fruit, banana)}
          onValueChange={vi.fn()}
          suggestions={["dairy", "apple", "home-apple", "banana"].map(
            (categoryId) => ({ categoryId }),
          )}
        />,
      );
      await openPicker(user);
      expect(
        screen.queryByRole("group", { name: "Suggested" }),
      ).not.toBeInTheDocument();
      expect(optionNames()[0]).toBe("Food / Fruit");
      expectActive("Food / Fruit / Banana");
    });

    it("opens beside the current choice when no suggestion is in the tree", async () => {
      const user = userEvent.setup();
      render(
        <CategoryPathPicker
          revision={revision}
          value={choice(food, fruit, banana)}
          onValueChange={vi.fn()}
          suggestions={[{ categoryId: "missing" }]}
        />,
      );
      await openPicker(user);
      expect(
        screen.queryByRole("group", { name: "Suggested" }),
      ).not.toBeInTheDocument();
      expect(optionNames()[0]).toBe("Food / Fruit");
      expectActive("Food / Fruit / Banana");
    });

    it("puts known suggestions first, in order, with their confidence", async () => {
      localStorage.setItem(RECENT_KEY, JSON.stringify(["banana", "apple"]));
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      render(
        <CategoryPathPicker
          revision={revision}
          onValueChange={onValueChange}
          suggestions={[
            { categoryId: "banana", confidence: 0.82 },
            { categoryId: "missing", confidence: 0.99 },
            { categoryId: "dairy", reason: "Approved mapping" },
          ]}
        />,
      );
      await openPicker(user);
      expect(optionNames()).toEqual([
        "Food / Fruit / Banana (suggested, 82% confidence)",
        "Food / Dairy (suggested)",
        "Food / Fruit / Apple (recent)",
        "Unclassified",
        "Food, 2 subcategories",
        "Home, 1 subcategory",
      ]);
      expect(screen.getByText("Food, Approved mapping")).toBeInTheDocument();
      expectActive("Food / Fruit / Banana (suggested, 82% confidence)");
      await user.keyboard("{Enter}");
      expect(onValueChange).toHaveBeenCalledWith(choice(food, fruit, banana));
    });

    it("shows at most three suggestions", async () => {
      const user = userEvent.setup();
      render(
        <CategoryPathPicker
          revision={revision}
          onValueChange={vi.fn()}
          suggestions={["apple", "banana", "dairy", "home-apple"].map(
            (categoryId) => ({ categoryId }),
          )}
        />,
      );
      await openPicker(user);
      expect(
        within(screen.getByRole("group", { name: "Suggested" })).getAllByRole(
          "option",
        ),
      ).toHaveLength(3);
      expect(
        screen.queryByRole("option", { name: "Home / Apple (suggested)" }),
      ).toBeNull();
    });
  });

  describe("on a phone", () => {
    const original = window.matchMedia;
    beforeEach(() => {
      window.matchMedia = (query: string) =>
        ({
          matches: query === "(max-width: 900px)",
          media: query,
          onchange: null,
          addEventListener: () => {},
          removeEventListener: () => {},
          addListener: () => {},
          removeListener: () => {},
          dispatchEvent: () => false,
        }) as MediaQueryList;
    });
    afterEach(() => {
      window.matchMedia = original;
    });

    it("opens a sheet without raising the keyboard and still selects", async () => {
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      render(
        <CategoryPathPicker
          revision={revision}
          onValueChange={onValueChange}
        />,
      );
      await user.click(screen.getByRole("combobox"));
      const sheet = await screen.findByRole("dialog", {
        name: "Choose category",
      });
      expect(searchBox()).not.toHaveFocus();
      expect(sheet).toContainElement(searchBox());
      await user.click(
        within(sheet).getByRole("option", { name: "Home, 1 subcategory" }),
      );
      await user.click(
        within(sheet).getByRole("option", { name: "Home / Apple" }),
      );
      expect(onValueChange).toHaveBeenCalledWith(choice(home, homeApple));
      await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    });
    it("keeps focus on the panel, not the search box, after Back or a crumb", async () => {
      const user = userEvent.setup();
      render(
        <CategoryPathPicker
          revision={revision}
          value={choice(food, fruit, apple)}
          onValueChange={vi.fn()}
        />,
      );
      await user.click(screen.getByRole("combobox"));
      const sheet = await screen.findByRole("dialog", {
        name: "Choose category",
      });
      const panel = sheet.querySelector("[data-category-browser]");
      within(sheet).getByRole("button", { name: "Back to Food" }).focus();
      await user.keyboard("{Enter}");
      expect(optionNames()[0]).toBe("Food");
      expect(document.activeElement).toBe(panel);

      await user.click(
        within(sheet).getByRole("option", { name: "Fruit, 2 subcategories" }),
      );
      within(screen.getByRole("navigation", { name: "Category path" }))
        .getByRole("button", { name: "Food" })
        .focus();
      await user.keyboard("{Enter}");
      expect(optionNames()[0]).toBe("Food");
      expect(document.activeElement).toBe(panel);
      expect(searchBox()).not.toHaveFocus();
    });

    it("sends a hardware keyboard's typing to the search box", async () => {
      const user = userEvent.setup();
      render(
        <CategoryPathPicker revision={revision} onValueChange={vi.fn()} />,
      );
      await user.click(screen.getByRole("combobox"));
      const sheet = await screen.findByRole("dialog", {
        name: "Choose category",
      });
      const panel = sheet.querySelector("[data-category-browser]");
      await waitFor(() => expect(panel).toHaveFocus());
      await user.keyboard("ban");
      expect(searchBox()).toHaveFocus();
      expect(searchBox()).toHaveValue("ban");
      expect(optionNames()).toEqual(["Food / Fruit / Banana"]);
    });
  });

  describe("creation", () => {
    it("creates beneath the chosen stable parent, selects the returned path, and keeps draft fields", async () => {
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      mockClient.POST.mockImplementation(async (_path, { body }) => ({
        data: {
          ...revision,
          id: "revision-2",
          nodes: [
            ...revision.nodes,
            {
              id: body.changes[0].categoryId,
              parentId: "fruit",
              name: "Berries",
              archived: false,
            },
          ],
        },
        error: undefined,
        response: new Response(null, { status: 200 }),
      }));
      renderWithQueryClient(
        <div>
          <input aria-label="Item description" defaultValue="Fresh berries" />
          <CategoryPathPicker
            revision={revision}
            value={choice(food, fruit)}
            onValueChange={onValueChange}
            canCreate
          />
        </div>,
      );
      await user.click(screen.getByRole("combobox", { name: /Fruit/ }));
      await user.click(
        screen.getByRole("button", { name: "Create category in Fruit" }),
      );
      expect(screen.getByRole("combobox", { name: "Parent" })).toHaveValue(
        "fruit",
      );
      await user.type(
        screen.getByRole("textbox", { name: "New category" }),
        "Berries",
      );
      await user.click(
        screen.getByRole("button", { name: "Create and select" }),
      );
      const createdId =
        mockClient.POST.mock.calls[0][1].body.changes[0].categoryId;
      await waitFor(() =>
        expect(onValueChange).toHaveBeenCalledWith({
          categoryId: createdId,
          taxonomyRevisionId: "revision-2",
          path: [
            food,
            fruit,
            {
              id: createdId,
              parentId: "fruit",
              name: "Berries",
              archived: false,
            },
          ],
        }),
      );
      expect(mockClient.POST.mock.calls[0][1].body.changes[0]).toMatchObject({
        kind: "create",
        parentId: "fruit",
        name: "Berries",
      });
      expect(
        screen.getByRole("textbox", { name: "Item description" }),
      ).toHaveValue("Fresh berries");
    });

    it("offers to create an unmatched search term in the current level", async () => {
      const user = userEvent.setup();
      renderWithQueryClient(
        <CategoryPathPicker
          revision={revision}
          value={choice(food, dairy)}
          onValueChange={vi.fn()}
          canCreate
        />,
      );
      await user.click(screen.getByRole("combobox", { name: /Dairy/ }));
      await user.type(searchBox(), "Kefir drinks");
      expect(
        screen.getByText("No categories match “Kefir drinks”."),
      ).toBeVisible();
      const list = screen.getByRole("listbox", { name: "Categories" });
      expect(list).not.toContainElement(
        screen.getByRole("button", { name: "Create “Kefir drinks”" }),
      );
      expect(within(list).queryAllByRole("button")).toEqual([]);
      await user.click(
        screen.getByRole("button", { name: "Create “Kefir drinks”" }),
      );
      expect(screen.getByRole("textbox", { name: "New category" })).toHaveValue(
        "Kefir drinks",
      );
      expect(screen.getByRole("combobox", { name: "Parent" })).toHaveValue(
        "food",
      );
    });

    it("refreshes after a revision conflict and requires review before a fresh operation", async () => {
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      const refreshed = {
        ...revision,
        id: "revision-2",
        nodes: revision.nodes.map((node) =>
          node.id === "fruit" ? { ...node, archived: true } : node,
        ),
      };
      mockClient.POST.mockResolvedValueOnce({
        data: undefined,
        error: { status: 409, detail: "Revision changed" },
        response: new Response(null, { status: 409 }),
      }).mockImplementationOnce(async (_path, { body }) => ({
        data: {
          ...refreshed,
          id: "revision-3",
          nodes: [
            ...revision.nodes,
            {
              id: body.changes[0].categoryId,
              name: "Household",
              archived: false,
            },
          ],
        },
        error: undefined,
        response: new Response(null, { status: 200 }),
      }));
      mockClient.GET.mockResolvedValue({
        data: refreshed,
        error: undefined,
        response: new Response(null, { status: 200 }),
      });
      renderWithQueryClient(
        <CategoryPathPicker
          revision={revision}
          value={choice(food, fruit)}
          canCreate
          onValueChange={onValueChange}
        />,
      );
      await user.click(screen.getByRole("combobox"));
      await user.click(
        screen.getByRole("button", { name: "Create category in Fruit" }),
      );
      await user.type(
        screen.getByRole("textbox", { name: "New category" }),
        "Household",
      );
      await user.click(
        screen.getByRole("button", { name: "Create and select" }),
      );
      expect(await screen.findByRole("alert")).toHaveTextContent(
        "Revision changed",
      );
      expect(
        screen.getByRole("button", { name: "Retry creation" }),
      ).toBeDisabled();
      await user.click(screen.getByRole("button", { name: "Refresh tree" }));
      expect(await screen.findByRole("status")).toHaveTextContent(
        "Review the parent",
      );
      expect(screen.getByRole("textbox", { name: "New category" })).toHaveValue(
        "Household",
      );
      expect(screen.getByText("Choose an active parent.")).toBeInTheDocument();
      expect(
        screen.getByRole("button", { name: "Create and select" }),
      ).toBeDisabled();
      await user.selectOptions(
        screen.getByRole("combobox", { name: "Parent" }),
        "",
      );
      await user.click(
        screen.getByRole("button", { name: "Create and select" }),
      );
      await waitFor(() =>
        expect(onValueChange).toHaveBeenCalledWith(
          expect.objectContaining({
            categoryId:
              mockClient.POST.mock.calls[1][1].body.changes[0].categoryId,
            taxonomyRevisionId: "revision-3",
          }),
        ),
      );
      const first = mockClient.POST.mock.calls[0][1].body;
      const second = mockClient.POST.mock.calls[1][1].body;
      expect(second.expectedRevisionId).toBe("revision-2");
      expect(second.operationId).not.toBe(first.operationId);
      expect(second.changes[0].parentId).toBeNull();
    });

    it("shows create errors inline and retries with the same operation", async () => {
      const user = userEvent.setup();
      const onValueChange = vi.fn();
      mockClient.POST.mockResolvedValueOnce({
        data: undefined,
        error: { status: 503, detail: "Temporarily unavailable" },
        response: new Response(null, { status: 503 }),
      }).mockImplementationOnce(async (_path, { body }) => ({
        data: {
          ...revision,
          id: "revision-2",
          nodes: [
            ...revision.nodes,
            { id: body.changes[0].categoryId, name: "Other", archived: false },
          ],
        },
        error: undefined,
        response: new Response(null, { status: 200 }),
      }));
      renderWithQueryClient(
        <CategoryPathPicker
          revision={revision}
          canCreate
          onValueChange={onValueChange}
        />,
      );
      await user.click(screen.getByRole("combobox"));
      await user.click(screen.getByRole("button", { name: "Create category" }));
      await user.type(
        screen.getByRole("textbox", { name: "New category" }),
        "Other",
      );
      await user.click(
        screen.getByRole("button", { name: "Create and select" }),
      );
      expect(await screen.findByRole("alert")).toHaveTextContent(
        "Temporarily unavailable",
      );
      await user.click(screen.getByRole("button", { name: "Retry creation" }));
      await waitFor(() => expect(onValueChange).toHaveBeenCalled());
      expect(mockClient.POST.mock.calls[1][1].body).toEqual(
        mockClient.POST.mock.calls[0][1].body,
      );
    });

    it("keeps creation hidden when the caller has no Admin permission", async () => {
      const user = userEvent.setup();
      render(
        <CategoryPathPicker
          revision={revision}
          onValueChange={vi.fn()}
          canCreate={false}
        />,
      );
      await openPicker(user);
      expect(
        screen.queryByRole("button", { name: /Create category/ }),
      ).not.toBeInTheDocument();
      await user.type(searchBox(), "zzzz");
      expect(screen.queryByRole("button", { name: /Create/ })).toBeNull();
      await user.clear(searchBox());
      expect(
        screen.getByRole("option", { name: "Unclassified" }),
      ).toBeInTheDocument();
      expect(
        screen.getByRole("option", { name: "Food, 2 subcategories" }),
      ).toBeInTheDocument();
    });
  });
});

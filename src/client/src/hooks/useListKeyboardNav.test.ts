import { renderHook, act } from "@testing-library/react";
import { useListKeyboardNav } from "./useListKeyboardNav";

// Mock react-hotkeys-hook to capture and manually trigger registered hotkeys
const hotkeysMap = new Map<string, () => void>();
vi.mock("react-hotkeys-hook", () => ({
  useHotkeys: vi.fn(
    (
      keys: string,
      callback: (e: KeyboardEvent) => void,
      options: Record<string, unknown>,
    ) => {
      if (options?.enabled !== false) {
        hotkeysMap.set(keys, () =>
          callback(new KeyboardEvent("keydown") as KeyboardEvent),
        );
      }
    },
  ),
}));

type Item = { id: string; name: string };
const items: Item[] = [
  { id: "1", name: "A" },
  { id: "2", name: "B" },
  { id: "3", name: "C" },
];

describe("useListKeyboardNav", () => {
  beforeEach(() => {
    hotkeysMap.clear();
  });

  it("returns initial focusedIndex of -1", () => {
    const { result } = renderHook(() =>
      useListKeyboardNav({
        items,
        getId: (item) => item.id,
      }),
    );
    expect(result.current.focusedIndex).toBe(-1);
    expect(result.current.focusedId).toBeNull();
  });

  it("provides setFocusedIndex to manually set focus", () => {
    const { result } = renderHook(() =>
      useListKeyboardNav({
        items,
        getId: (item) => item.id,
      }),
    );

    act(() => {
      result.current.setFocusedIndex(1);
    });

    expect(result.current.focusedIndex).toBe(1);
    expect(result.current.focusedId).toBe("2");
  });

  it("resets focusedIndex when items length changes", () => {
    const { result, rerender } = renderHook(
      ({ items: hookItems }) =>
        useListKeyboardNav({
          items: hookItems,
          getId: (item) => item.id,
        }),
      { initialProps: { items } },
    );

    act(() => {
      result.current.setFocusedIndex(2);
    });
    expect(result.current.focusedIndex).toBe(2);

    rerender({ items: [items[0]] });
    expect(result.current.focusedIndex).toBe(-1);
  });

  it("returns a tableRef", () => {
    const { result } = renderHook(() =>
      useListKeyboardNav({
        items,
        getId: (item) => item.id,
      }),
    );
    expect(result.current.tableRef).toBeDefined();
  });

  describe("containerProps", () => {
    it("has role grid, tabIndex 0, and aria-label defaulting to 'list'", () => {
      const { result } = renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
        }),
      );
      expect(result.current.containerProps.role).toBe("grid");
      expect(result.current.containerProps.tabIndex).toBe(0);
      expect(result.current.containerProps["aria-label"]).toBe("list");
    });

    it("uses listId as aria-label when provided", () => {
      const { result } = renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          listId: "accounts",
        }),
      );
      expect(result.current.containerProps["aria-label"]).toBe("accounts");
    });

    it("aria-activedescendant is undefined when focusedIndex is -1", () => {
      const { result } = renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          listId: "accounts",
        }),
      );
      expect(result.current.containerProps["aria-activedescendant"]).toBeUndefined();
    });

    it("aria-activedescendant points to focused row dom id when item is focused", () => {
      const { result } = renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          listId: "accounts",
        }),
      );

      act(() => {
        result.current.setFocusedIndex(1);
      });

      // focusedId = "2" → dom id = "accounts-row-2"
      expect(result.current.containerProps["aria-activedescendant"]).toBe("accounts-row-2");
    });

    it("aria-activedescendant uses 'list-row-' prefix when listId is omitted", () => {
      const { result } = renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
        }),
      );

      act(() => {
        result.current.setFocusedIndex(0);
      });

      expect(result.current.containerProps["aria-activedescendant"]).toBe("list-row-1");
    });
  });

  describe("getRowProps", () => {
    it("returns id and role for a given item id", () => {
      const { result } = renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          listId: "receipts",
        }),
      );
      const props = result.current.getRowProps("42");
      expect(props.id).toBe("receipts-row-42");
      expect(props.role).toBe("row");
    });

    it("uses 'list-row-' prefix when listId is omitted", () => {
      const { result } = renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
        }),
      );
      const props = result.current.getRowProps("99");
      expect(props.id).toBe("list-row-99");
      expect(props.role).toBe("row");
    });
  });

  describe("j / ArrowDown hotkey", () => {
    it("moves focus from -1 to 0", () => {
      const { result } = renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
        }),
      );

      expect(result.current.focusedIndex).toBe(-1);

      act(() => {
        hotkeysMap.get("j, ArrowDown")?.();
      });

      expect(result.current.focusedIndex).toBe(0);
      expect(result.current.focusedId).toBe("1");
    });

    it("moves focus down incrementally", () => {
      const { result } = renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
        }),
      );

      act(() => {
        hotkeysMap.get("j, ArrowDown")?.();
      });
      expect(result.current.focusedIndex).toBe(0);

      act(() => {
        hotkeysMap.get("j, ArrowDown")?.();
      });
      expect(result.current.focusedIndex).toBe(1);
      expect(result.current.focusedId).toBe("2");
    });

    it("clamps at the last item index", () => {
      const { result } = renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
        }),
      );

      // Move to the last item
      act(() => {
        result.current.setFocusedIndex(2);
      });
      expect(result.current.focusedIndex).toBe(2);

      // Try to move past the end
      act(() => {
        hotkeysMap.get("j, ArrowDown")?.();
      });
      expect(result.current.focusedIndex).toBe(2);
      expect(result.current.focusedId).toBe("3");
    });

    it("is not registered when items list is empty", () => {
      renderHook(() =>
        useListKeyboardNav({
          items: [],
          getId: (item: Item) => item.id,
        }),
      );

      expect(hotkeysMap.has("j, ArrowDown")).toBe(false);
    });

    it("is not registered when enabled is false", () => {
      renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          enabled: false,
        }),
      );

      expect(hotkeysMap.has("j, ArrowDown")).toBe(false);
    });
  });

  describe("k / ArrowUp hotkey", () => {
    it("moves focus up from index 2 to 1", () => {
      const { result } = renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
        }),
      );

      act(() => {
        result.current.setFocusedIndex(2);
      });

      act(() => {
        hotkeysMap.get("k, ArrowUp")?.();
      });

      expect(result.current.focusedIndex).toBe(1);
      expect(result.current.focusedId).toBe("2");
    });

    it("clamps at index 0 and does not go below", () => {
      const { result } = renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
        }),
      );

      act(() => {
        result.current.setFocusedIndex(0);
      });

      act(() => {
        hotkeysMap.get("k, ArrowUp")?.();
      });

      expect(result.current.focusedIndex).toBe(0);
      expect(result.current.focusedId).toBe("1");
    });

    it("moves from -1 to 0 (Math.max(-1-1,0) = 0)", () => {
      const { result } = renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
        }),
      );

      expect(result.current.focusedIndex).toBe(-1);

      act(() => {
        hotkeysMap.get("k, ArrowUp")?.();
      });

      expect(result.current.focusedIndex).toBe(0);
    });

    it("is not registered when items list is empty", () => {
      renderHook(() =>
        useListKeyboardNav({
          items: [],
          getId: (item: Item) => item.id,
        }),
      );

      expect(hotkeysMap.has("k, ArrowUp")).toBe(false);
    });
  });

  describe("Enter hotkey", () => {
    it("calls onOpen with the focused item", () => {
      const onOpen = vi.fn();
      vi.useFakeTimers();

      const { result } = renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          onOpen,
        }),
      );

      // Focus index must be >= 0 for Enter to be enabled
      act(() => {
        result.current.setFocusedIndex(1);
      });

      act(() => {
        hotkeysMap.get("Enter")?.();
      });

      // onOpen is called via setTimeout(..., 0)
      vi.runAllTimers();

      expect(onOpen).toHaveBeenCalledWith(items[1]);
      vi.useRealTimers();
    });

    it("is not registered when focusedIndex is -1", () => {
      const onOpen = vi.fn();
      renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          onOpen,
        }),
      );

      // focusedIndex starts at -1 so Enter should not be registered
      expect(hotkeysMap.has("Enter")).toBe(false);
    });

    it("is not registered when onOpen is not provided", () => {
      const { result } = renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
        }),
      );

      act(() => {
        result.current.setFocusedIndex(0);
      });

      expect(hotkeysMap.has("Enter")).toBe(false);
    });
  });

  describe("Space hotkey", () => {
    it("calls onToggleSelect with the focused item", () => {
      const onToggleSelect = vi.fn();

      const { result } = renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          onToggleSelect,
        }),
      );

      act(() => {
        result.current.setFocusedIndex(2);
      });

      act(() => {
        hotkeysMap.get("Space")?.();
      });

      expect(onToggleSelect).toHaveBeenCalledWith(items[2]);
    });

    it("is not registered when focusedIndex is -1", () => {
      const onToggleSelect = vi.fn();
      renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          onToggleSelect,
        }),
      );

      expect(hotkeysMap.has("Space")).toBe(false);
    });

    it("is not registered when onToggleSelect is not provided", () => {
      const { result } = renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
        }),
      );

      act(() => {
        result.current.setFocusedIndex(0);
      });

      expect(hotkeysMap.has("Space")).toBe(false);
    });
  });

  describe("x hotkey (Gmail-style select toggle)", () => {
    it("calls onToggleSelect with the focused item", () => {
      const onToggleSelect = vi.fn();

      const { result } = renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          onToggleSelect,
        }),
      );

      act(() => {
        result.current.setFocusedIndex(1);
      });

      act(() => {
        hotkeysMap.get("x")?.();
      });

      expect(onToggleSelect).toHaveBeenCalledWith(items[1]);
    });

    it("is not registered when focusedIndex is -1", () => {
      const onToggleSelect = vi.fn();
      renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          onToggleSelect,
        }),
      );
      expect(hotkeysMap.has("x")).toBe(false);
    });

    it("is not registered when onToggleSelect is not provided", () => {
      const { result } = renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
        }),
      );
      act(() => {
        result.current.setFocusedIndex(0);
      });
      expect(hotkeysMap.has("x")).toBe(false);
    });
  });

  describe("/ hotkey (focus search)", () => {
    it("calls onFocusSearch", () => {
      const onFocusSearch = vi.fn();

      renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          onFocusSearch,
        }),
      );

      act(() => {
        hotkeysMap.get("/")?.();
      });

      expect(onFocusSearch).toHaveBeenCalledOnce();
    });

    it("is not registered when onFocusSearch is not provided", () => {
      renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
        }),
      );
      expect(hotkeysMap.has("/")).toBe(false);
    });

    it("does not depend on focusedIndex", () => {
      // / focuses the search bar regardless of whether a row is focused.
      const onFocusSearch = vi.fn();
      renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          onFocusSearch,
        }),
      );
      // No setFocusedIndex call — focus is still -1.
      act(() => {
        hotkeysMap.get("/")?.();
      });
      expect(onFocusSearch).toHaveBeenCalledOnce();
    });
  });

  describe("Delete hotkey", () => {
    it("calls onDelete when items are selected", () => {
      const onDelete = vi.fn();
      const selected = new Set(["1", "2"]);

      renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          onDelete,
          selected,
        }),
      );

      act(() => {
        hotkeysMap.get("Delete")?.();
      });

      expect(onDelete).toHaveBeenCalledOnce();
    });

    it("is not registered when selected set is empty", () => {
      const onDelete = vi.fn();
      const selected = new Set<string>();

      renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          onDelete,
          selected,
        }),
      );

      expect(hotkeysMap.has("Delete")).toBe(false);
    });

    it("is not registered when onDelete is not provided", () => {
      const selected = new Set(["1"]);

      renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          selected,
        }),
      );

      expect(hotkeysMap.has("Delete")).toBe(false);
    });

    it("is not registered when selected is not provided", () => {
      const onDelete = vi.fn();

      renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          onDelete,
        }),
      );

      expect(hotkeysMap.has("Delete")).toBe(false);
    });
  });

  describe("mod+a (Ctrl+A) hotkey", () => {
    it("calls onSelectAll when not all items are selected", () => {
      const onSelectAll = vi.fn();
      const onDeselectAll = vi.fn();
      const selected = new Set(["1"]);

      renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          onSelectAll,
          onDeselectAll,
          selected,
        }),
      );

      act(() => {
        hotkeysMap.get("mod+a")?.();
      });

      expect(onSelectAll).toHaveBeenCalledOnce();
      expect(onDeselectAll).not.toHaveBeenCalled();
    });

    it("calls onDeselectAll when all items are selected", () => {
      const onSelectAll = vi.fn();
      const onDeselectAll = vi.fn();
      const selected = new Set(["1", "2", "3"]);

      renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          onSelectAll,
          onDeselectAll,
          selected,
        }),
      );

      act(() => {
        hotkeysMap.get("mod+a")?.();
      });

      expect(onDeselectAll).toHaveBeenCalledOnce();
      expect(onSelectAll).not.toHaveBeenCalled();
    });

    it("calls onSelectAll when no items are selected", () => {
      const onSelectAll = vi.fn();
      const onDeselectAll = vi.fn();
      const selected = new Set<string>();

      renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          onSelectAll,
          onDeselectAll,
          selected,
        }),
      );

      act(() => {
        hotkeysMap.get("mod+a")?.();
      });

      expect(onSelectAll).toHaveBeenCalledOnce();
      expect(onDeselectAll).not.toHaveBeenCalled();
    });

    it("is not registered when onSelectAll is missing", () => {
      const onDeselectAll = vi.fn();
      const selected = new Set(["1"]);

      renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          onDeselectAll,
          selected,
        }),
      );

      expect(hotkeysMap.has("mod+a")).toBe(false);
    });

    it("is not registered when onDeselectAll is missing", () => {
      const onSelectAll = vi.fn();
      const selected = new Set(["1"]);

      renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          onSelectAll,
          selected,
        }),
      );

      expect(hotkeysMap.has("mod+a")).toBe(false);
    });

    it("does nothing when selected is not provided", () => {
      const onSelectAll = vi.fn();
      const onDeselectAll = vi.fn();

      renderHook(() =>
        useListKeyboardNav({
          items,
          getId: (item) => item.id,
          onSelectAll,
          onDeselectAll,
        }),
      );

      // mod+a should be registered (enabled check only looks at onSelectAll && onDeselectAll)
      act(() => {
        hotkeysMap.get("mod+a")?.();
      });

      // But the handler returns early because selected is undefined
      expect(onSelectAll).not.toHaveBeenCalled();
      expect(onDeselectAll).not.toHaveBeenCalled();
    });
  });
});

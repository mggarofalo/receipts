import { useState, useCallback, useRef, useMemo } from "react";
import { useHotkeys } from "react-hotkeys-hook";

export interface UseListKeyboardNavOptions<T> {
  items: T[];
  getId: (item: T) => string;
  /** Stable string prefix used to build per-row DOM ids, e.g. "accounts". */
  listId?: string;
  enabled?: boolean;
  onOpen?: (item: T) => void;
  onDelete?: () => void;
  onSelectAll?: () => void;
  onDeselectAll?: () => void;
  onToggleSelect?: (item: T) => void;
  /**
   * Called when the user presses `/` (Vim/Gmail-style search activation).
   * The page is responsible for focusing whatever input it considers
   * the list's search field.
   */
  onFocusSearch?: () => void;
  selected?: Set<string>;
}

export function useListKeyboardNav<T>({
  items,
  getId,
  listId,
  enabled = true,
  onOpen,
  onDelete,
  onSelectAll,
  onDeselectAll,
  onToggleSelect,
  onFocusSearch,
  selected,
}: UseListKeyboardNavOptions<T>) {
  const [focusedIndex, setFocusedIndex] = useState(-1);
  const tableRef = useRef<HTMLDivElement>(null);
  const [prevItemsLength, setPrevItemsLength] = useState(items.length);

  // Reset focus when items change (adjusting state when a prop changes)
  if (prevItemsLength !== items.length) {
    setPrevItemsLength(items.length);
    setFocusedIndex(-1);
  }

  const scrollToRow = useCallback((index: number) => {
    const row = tableRef.current?.querySelectorAll("tbody tr")[index];
    row?.scrollIntoView({ block: "nearest" });
  }, []);

  // j / ArrowDown — move down
  useHotkeys(
    "j, ArrowDown",
    () => {
      setFocusedIndex((prev) => {
        const next = Math.min(prev + 1, items.length - 1);
        scrollToRow(next);
        return next;
      });
    },
    { enabled: enabled && items.length > 0, enableOnFormTags: false },
    [items.length, scrollToRow],
  );

  // k / ArrowUp — move up
  useHotkeys(
    "k, ArrowUp",
    () => {
      setFocusedIndex((prev) => {
        const next = Math.max(prev - 1, 0);
        scrollToRow(next);
        return next;
      });
    },
    { enabled: enabled && items.length > 0, enableOnFormTags: false },
    [items.length, scrollToRow],
  );

  // Enter — open focused item (deferred so the keydown event doesn't
  // propagate into the newly-mounted dialog and trigger an immediate submit)
  useHotkeys(
    "Enter",
    (e) => {
      if (focusedIndex >= 0 && focusedIndex < items.length && onOpen) {
        e.preventDefault();
        e.stopPropagation();
        const item = items[focusedIndex];
        setTimeout(() => onOpen(item), 0);
      }
    },
    {
      enabled: enabled && !!onOpen && focusedIndex >= 0,
      enableOnFormTags: false,
    },
    [focusedIndex, items, onOpen],
  );

  // Space — toggle selection on focused item (standard a11y idiom).
  useHotkeys(
    "Space",
    () => {
      if (focusedIndex >= 0 && focusedIndex < items.length && onToggleSelect) {
        onToggleSelect(items[focusedIndex]);
      }
    },
    {
      enabled: enabled && !!onToggleSelect && focusedIndex >= 0,
      enableOnFormTags: false,
      preventDefault: true,
    },
    [focusedIndex, items, onToggleSelect],
  );

  // x — toggle selection on focused item (Gmail/Vim "select message" idiom).
  // Registered as a separate hotkey from Space so tests that target one or
  // the other key can interrogate them independently.
  useHotkeys(
    "x",
    () => {
      if (focusedIndex >= 0 && focusedIndex < items.length && onToggleSelect) {
        onToggleSelect(items[focusedIndex]);
      }
    },
    {
      enabled: enabled && !!onToggleSelect && focusedIndex >= 0,
      enableOnFormTags: false,
      preventDefault: true,
    },
    [focusedIndex, items, onToggleSelect],
  );

  // / — focus the list's search input (Vim/Gmail idiom). The page wires
  // the actual focus call via onFocusSearch since useListKeyboardNav does
  // not know about page-specific search components.
  useHotkeys(
    "/",
    () => {
      onFocusSearch?.();
    },
    {
      enabled: enabled && !!onFocusSearch,
      enableOnFormTags: false,
      preventDefault: true,
    },
    [onFocusSearch],
  );

  // Delete — trigger delete action
  useHotkeys(
    "Delete",
    () => {
      if (onDelete && selected && selected.size > 0) {
        onDelete();
      }
    },
    {
      enabled: enabled && !!onDelete && !!selected && selected.size > 0,
      enableOnFormTags: false,
    },
    [onDelete, selected],
  );

  // Ctrl+A — select all / deselect all
  useHotkeys(
    "mod+a",
    () => {
      if (!selected || !onSelectAll || !onDeselectAll) return;
      if (selected.size === items.length) {
        onDeselectAll();
      } else {
        onSelectAll();
      }
    },
    {
      enabled: enabled && !!onSelectAll && !!onDeselectAll,
      enableOnFormTags: false,
      preventDefault: true,
    },
    [selected, items.length, onSelectAll, onDeselectAll],
  );

  const focusedId =
    focusedIndex >= 0 && focusedIndex < items.length
      ? getId(items[focusedIndex])
      : null;

  /**
   * Build a stable DOM id for a given item id so that aria-activedescendant
   * can point to the focused row element.
   */
  const rowDomId = useCallback(
    (itemId: string) =>
      listId ? `${listId}-row-${itemId}` : `list-row-${itemId}`,
    [listId],
  );

  /**
   * Props to spread onto the scroll-container <div> that wraps the table.
   * Makes the container programmatically focusable and announces the active
   * row to screen readers via aria-activedescendant.
   *
   * tabIndex={0} is required: aria-activedescendant is only processed by
   * assistive technology when the owning element has DOM focus. Without
   * tabIndex the container is not in the tab order and AT never observes
   * the focus events needed to read aria-activedescendant.
   */
  const containerProps = useMemo(
    () => ({
      role: "grid" as const,
      tabIndex: 0,
      "aria-label": listId ?? "list",
      "aria-activedescendant": focusedId ? rowDomId(focusedId) : undefined,
    }),
    [focusedId, listId, rowDomId],
  );

  /**
   * Returns props to spread onto a data <TableRow> so the element has a
   * stable id and the correct ARIA role for grid navigation.
   */
  const getRowProps = useCallback(
    (itemId: string) => ({
      id: rowDomId(itemId),
      role: "row" as const,
    }),
    [rowDomId],
  );

  return useMemo(
    () => ({
      focusedIndex,
      setFocusedIndex,
      tableRef,
      focusedId,
      containerProps,
      getRowProps,
    }),
    [focusedIndex, setFocusedIndex, tableRef, focusedId, containerProps, getRowProps],
  );
}

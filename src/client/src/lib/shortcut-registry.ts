export type ShortcutCategory = "Global" | "List Navigation" | "Forms";

export interface ShortcutDefinition {
  keys: string;
  label: string;
  category: ShortcutCategory;
}

export const shortcuts: ShortcutDefinition[] = [
  // Global
  { keys: "?", label: "Show keyboard shortcuts", category: "Global" },
  { keys: "Ctrl+K", label: "Open command palette", category: "Global" },
  {
    keys: "Shift+N",
    label: "Create new item (context-aware)",
    category: "Global",
  },

  // List Navigation
  {
    keys: "j / ArrowDown",
    label: "Move focus down",
    category: "List Navigation",
  },
  { keys: "k / ArrowUp", label: "Move focus up", category: "List Navigation" },
  {
    keys: "Enter",
    label: "Open / edit focused item",
    category: "List Navigation",
  },
  {
    keys: "Space / x",
    label: "Toggle selection on focused row",
    category: "List Navigation",
  },
  {
    keys: "/",
    label: "Focus the search field",
    category: "List Navigation",
  },
  {
    keys: "Delete",
    label: "Delete selected items",
    category: "List Navigation",
  },
  {
    keys: "Ctrl+A",
    label: "Select / deselect all",
    category: "List Navigation",
  },

  // Forms
  { keys: "Ctrl+Enter", label: "Submit form", category: "Forms" },
];

export function getShortcutsByCategory(): Map<
  ShortcutCategory,
  ShortcutDefinition[]
> {
  const map = new Map<ShortcutCategory, ShortcutDefinition[]>();
  for (const shortcut of shortcuts) {
    const list = map.get(shortcut.category) ?? [];
    list.push(shortcut);
    map.set(shortcut.category, list);
  }
  return map;
}

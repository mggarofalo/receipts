import { useId, useMemo, useRef, useState, type ReactNode } from "react";
import { ChevronDown, ChevronRight } from "lucide-react";
import { Button } from "@/components/ui/button";

interface Category {
  id: string;
  name: string;
  parentId?: string | null;
  archived: boolean;
  definition?: string | null;
}

interface Props<T extends Category> {
  nodes: T[];
  search?: string;
  showArchived?: boolean;
  renderActions?: (node: T, path: string, focused: boolean) => ReactNode;
}

/** A flat DOM tree avoids recursive rendering and unbounded horizontal indentation. */
export function CategoryTree<T extends Category>({
  nodes,
  search = "",
  showArchived = false,
  renderActions,
}: Props<T>) {
  const helpId = useId();
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [focusedId, setFocusedId] = useState<string>();
  const items = useRef(new Map<string, HTMLLIElement>());
  const typeahead = useRef({ value: "", at: 0 });
  const term = search.trim().toLocaleLowerCase();
  const model = useMemo(() => {
    const available = nodes.filter((node) => showArchived || !node.archived);
    const byId = new Map(available.map((node) => [node.id, node]));
    const children = new Map<string | null, T[]>();
    for (const node of available) {
      const parent =
        node.parentId && byId.has(node.parentId) ? node.parentId : null;
      const siblings = children.get(parent) ?? [];
      siblings.push(node);
      children.set(parent, siblings);
    }
    for (const siblings of children.values())
      siblings.sort(
        (a, b) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id),
      );
    const ordered: {
      node: T;
      path: string;
      level: number;
      parent: string | null;
    }[] = [];
    const stack = (children.get(null) ?? [])
      .map((node) => ({
        node,
        path: node.name,
        level: 1,
        parent: null as string | null,
      }))
      .reverse();
    const seen = new Set<string>();
    while (stack.length) {
      const row = stack.pop()!;
      if (seen.has(row.node.id)) continue;
      seen.add(row.node.id);
      ordered.push(row);
      const descendants = children.get(row.node.id) ?? [];
      for (let i = descendants.length - 1; i >= 0; i--)
        stack.push({
          node: descendants[i],
          path: `${row.path} / ${descendants[i].name}`,
          level: row.level + 1,
          parent: row.node.id,
        });
    }
    const included = new Set<string>();
    if (term) {
      const parents = new Map(ordered.map((row) => [row.node.id, row.parent]));
      for (const row of ordered) {
        if (!row.path.toLocaleLowerCase().includes(term)) continue;
        let id: string | null = row.node.id;
        while (id && !included.has(id)) {
          included.add(id);
          id = parents.get(id) ?? null;
        }
      }
    }
    const positions = new Map<string, { position: number; size: number }>();
    for (const siblings of children.values()) {
      const matching = term
        ? siblings.filter((node) => included.has(node.id))
        : siblings;
      matching.forEach((node, index) =>
        positions.set(node.id, { position: index + 1, size: matching.length }),
      );
    }
    return { ordered, children, included, positions };
  }, [nodes, showArchived, term]);
  const visible = useMemo(() => {
    const shown = new Set<string>();
    return model.ordered.filter((row) => {
      const include = term
        ? model.included.has(row.node.id)
        : !row.parent || (shown.has(row.parent) && expanded.has(row.parent));
      if (include) shown.add(row.node.id);
      return include;
    });
  }, [model, term, expanded]);
  const activeId = visible.some((row) => row.node.id === focusedId)
    ? focusedId
    : visible[0]?.node.id;
  function focus(id: string) {
    setFocusedId(id);
    items.current.get(id)?.focus();
  }
  function toggle(id: string) {
    setExpanded((previous) => {
      const next = new Set(previous);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }
  return (
    <div className="min-w-0 space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        <Button
          variant="outline"
          size="sm"
          disabled={!!term || !nodes.length}
          onClick={() =>
            setExpanded(
              new Set(
                Array.from(model.children.keys()).filter(
                  (id): id is string => id !== null,
                ),
              ),
            )
          }
        >
          Expand all
        </Button>
        <Button
          variant="outline"
          size="sm"
          disabled={!!term || !nodes.length}
          onClick={() => {
            setExpanded(new Set());
            setFocusedId(undefined);
          }}
        >
          Collapse all
        </Button>
        <span className="text-sm text-muted-foreground" role="status">
          {visible.length} of {model.ordered.length} categories shown
          {term ? " · Matching paths and ancestors" : ""}
        </span>
      </div>
      <p id={helpId} className="text-sm text-muted-foreground">
        Use arrow keys to browse and expand categories, Home or End to jump, and
        Tab for category actions. Type a name to jump to a visible category.
      </p>
      {!visible.length && (
        <p>{nodes.length ? "No matching categories." : "No categories yet."}</p>
      )}
      <ul
        role="tree"
        aria-label="Categories"
        aria-describedby={helpId}
        className="max-h-[65dvh] overflow-y-auto rounded-md border"
      >
        {visible.map((row, index) => {
          const { node, path, level, parent } = row;
          const position = model.positions.get(node.id)!;
          const hasChildren = !!model.children.get(node.id)?.length;
          const open = !!term || expanded.has(node.id);
          const focused = activeId === node.id;
          return (
            <li
              key={node.id}
              role="treeitem"
              aria-label={path}
              aria-selected={focused}
              aria-level={level}
              aria-posinset={position.position}
              aria-setsize={position.size}
              aria-expanded={hasChildren ? open : undefined}
              tabIndex={focused ? 0 : -1}
              ref={(element) => {
                if (element) items.current.set(node.id, element);
                else items.current.delete(node.id);
              }}
              onFocus={() => setFocusedId(node.id)}
              onKeyDown={(event) => {
                if (event.target !== event.currentTarget) return;
                let target: string | undefined;
                switch (event.key) {
                  case "ArrowDown":
                    target = visible[index + 1]?.node.id;
                    break;
                  case "ArrowUp":
                    target = visible[index - 1]?.node.id;
                    break;
                  case "Home":
                    target = visible[0]?.node.id;
                    break;
                  case "End":
                    target = visible.at(-1)?.node.id;
                    break;
                  case "ArrowRight":
                    if (hasChildren && !open) toggle(node.id);
                    else if (hasChildren) target = visible[index + 1]?.node.id;
                    break;
                  case "ArrowLeft":
                    if (hasChildren && open && !term) toggle(node.id);
                    else target = parent ?? undefined;
                    break;
                  case "Enter":
                  case " ":
                    if (hasChildren && !term) toggle(node.id);
                    break;
                  default: {
                    if (
                      event.key.length !== 1 ||
                      event.ctrlKey ||
                      event.altKey ||
                      event.metaKey
                    )
                      return;
                    const now = Date.now();
                    const value =
                      (now - typeahead.current.at < 700
                        ? typeahead.current.value
                        : "") + event.key.toLocaleLowerCase();
                    typeahead.current = { value, at: now };
                    target = [
                      ...visible.slice(index + 1),
                      ...visible.slice(0, index + 1),
                    ].find((candidate) =>
                      candidate.node.name.toLocaleLowerCase().startsWith(value),
                    )?.node.id;
                  }
                }
                event.preventDefault();
                if (target) focus(target);
              }}
              style={{
                paddingInlineStart: `${Math.min(level - 1, 6) * 0.75 + 0.75}rem`,
              }}
              className="flex min-w-0 flex-col gap-2 border-b p-3 last:border-b-0 focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-ring sm:flex-row sm:items-center sm:justify-between"
            >
              <div className="flex min-w-0 items-start gap-2">
                {hasChildren ? (
                  <button
                    type="button"
                    tabIndex={-1}
                    disabled={!!term}
                    aria-label={`${open ? "Collapse" : "Expand"} ${path}`}
                    className="mt-0.5 shrink-0 rounded focus-visible:outline-2 focus-visible:outline-ring disabled:opacity-60"
                    onClick={() => {
                      focus(node.id);
                      toggle(node.id);
                    }}
                  >
                    {open ? (
                      <ChevronDown aria-hidden="true" size={20} />
                    ) : (
                      <ChevronRight aria-hidden="true" size={20} />
                    )}
                  </button>
                ) : (
                  <span className="w-5 shrink-0" />
                )}
                <div className="min-w-0">
                  <p className="break-words font-medium">
                    {node.name}
                    {node.archived && (
                      <span className="ml-2 text-sm text-muted-foreground">
                        Archived
                      </span>
                    )}
                  </p>
                  {level > 1 && (
                    <p className="break-words text-xs text-muted-foreground">
                      {path.slice(0, -(node.name.length + 3))}
                    </p>
                  )}
                  {node.definition && (
                    <p className="mt-1 break-words text-sm text-muted-foreground">
                      {node.definition}
                    </p>
                  )}
                  <details className="mt-1 text-sm text-muted-foreground">
                    <summary
                      tabIndex={focused ? 0 : -1}
                      className="cursor-pointer"
                    >
                      Category details
                    </summary>
                    <p className="break-all">{node.id}</p>
                  </details>
                </div>
              </div>
              {renderActions?.(node, path, focused)}
            </li>
          );
        })}
      </ul>
    </div>
  );
}

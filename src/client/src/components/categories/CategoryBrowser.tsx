import {
  Fragment,
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent,
  type ReactNode,
} from "react";
import {
  Check,
  ChevronLeft,
  ChevronRight,
  Plus,
  Search,
  Sparkles,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import type { CategoryChoice, CategoryPath } from "@/lib/category-paths";
import {
  searchCategories,
  searchTokens,
  type CategoryHit,
  type CategoryIndex,
  type MatchRange,
  rootHueStyle,
} from "@/lib/category-tree";
import {
  getRecentCategoryIds,
  rememberRecentCategory,
} from "@/lib/recent-categories";
import { cn } from "@/lib/utils";
import { CategoryMonogram } from "./CategoryMonogram";

/** A candidate offered before browsing, e.g. from Jev or an approved mapping. */
export interface CategorySuggestion {
  categoryId: string;
  /** 0–1. Omit when the source has no calibrated confidence. */
  confidence?: number;
  /** Short provenance shown beside the path, e.g. "Approved mapping". */
  reason?: string;
}

type Option =
  | {
      kind: "pick";
      key: string;
      row: CategoryPath;
      source: "browse" | "self" | "search" | "suggested" | "recent";
      hit?: CategoryHit;
      suggestion?: CategorySuggestion;
    }
  | { kind: "open"; key: string; row: CategoryPath; childCount: number }
  | { kind: "unclassified"; key: string };

interface Section {
  id: string;
  heading?: string;
  layout?: "chips";
  divided?: boolean;
  options: Option[];
}

const SUGGESTION_LIMIT = 3;
const RECENT_LIMIT = 5;
const PAGE_STEP = 8;

function plural(count: number, one: string, many: string) {
  return `${count} ${count === 1 ? one : many}`;
}

function browseOption(index: CategoryIndex, row: CategoryPath): Option {
  const childCount = index.childrenOf(row.node.id).length;
  return childCount > 0
    ? { kind: "open", key: `open:${row.node.id}`, row, childCount }
    : { kind: "pick", key: `pick:${row.node.id}`, row, source: "browse" };
}

function optionName(option: Option) {
  if (option.kind === "unclassified") return "Unclassified";
  if (option.kind === "open")
    return `${option.row.node.name}, ${plural(option.childCount, "subcategory", "subcategories")}`;
  if (option.source === "recent") return `${option.row.label} (recent)`;
  if (option.source === "suggested") {
    const confidence = option.suggestion?.confidence;
    return confidence === undefined
      ? `${option.row.label} (suggested)`
      : `${option.row.label} (suggested, ${Math.round(confidence * 100)}% confidence)`;
  }
  return option.row.label;
}

/**
 * Where the panel opens: at the top beside the suggestions when there are any,
 * else beside the current choice, so a correction is one step.
 */
function initialPlace(
  index: CategoryIndex,
  value?: CategoryChoice,
  suggestions?: CategorySuggestion[],
) {
  const suggested = (suggestions ?? [])
    .map((suggestion) => suggestion.categoryId)
    .filter((id) => index.byId.has(id));
  if (suggested.length > 0) {
    const active =
      value?.categoryId && suggested.includes(value.categoryId)
        ? value.categoryId
        : suggested[0];
    return { levelId: null, activeKey: `suggested:${active}` };
  }
  if (value?.categoryId === null)
    return { levelId: null, activeKey: "unclassified" };
  const row = value?.categoryId ? index.byId.get(value.categoryId) : undefined;
  if (!row) return { levelId: null, activeKey: null };
  if (index.childrenOf(row.node.id).length > 0)
    return { levelId: row.node.id, activeKey: `self:${row.node.id}` };
  return {
    levelId: row.node.parentId ?? null,
    activeKey: `pick:${row.node.id}`,
  };
}

function Highlight({ text, ranges }: { text: string; ranges: MatchRange[] }) {
  if (ranges.length === 0) return text;
  const parts: ReactNode[] = [];
  let cursor = 0;
  for (const [start, end] of ranges) {
    if (start > cursor) parts.push(text.slice(cursor, start));
    parts.push(<mark key={start}>{text.slice(start, end)}</mark>);
    cursor = end;
  }
  parts.push(text.slice(cursor));
  return parts;
}

/** Trims a long definition to the window around its first match. */
function definitionExcerpt(text: string, ranges: MatchRange[]) {
  const first = ranges[0]?.[0] ?? 0;
  const start = first > 40 ? text.lastIndexOf(" ", first - 24) + 1 : 0;
  return {
    text: (start > 0 ? "…" : "") + text.slice(start),
    ranges: ranges.map(
      ([from, to]) =>
        [
          from - start + (start > 0 ? 1 : 0),
          to - start + (start > 0 ? 1 : 0),
        ] as const,
    ),
  };
}

/**
 * A keyboard-pressed back or breadcrumb button unmounts with the level it led
 * from; move focus first so the panel keeps receiving keys.
 */
function keepPanelFocus(from: HTMLElement, touch: boolean) {
  const panel = from.closest<HTMLElement>("[data-category-browser]");
  (touch ? panel : panel?.querySelector<HTMLElement>("input"))?.focus();
}

function ancestry(row: CategoryPath) {
  return row.path
    .slice(0, -1)
    .map((node) => node.name)
    .join(" / ");
}

export function CategoryBrowser({
  index,
  value,
  allowUnclassified,
  suggestions,
  touch = false,
  onSelect,
  onCreate,
}: {
  index: CategoryIndex;
  value?: CategoryChoice;
  allowUnclassified: boolean;
  suggestions?: CategorySuggestion[];
  /** Touch layout: larger rows and no on-screen keyboard until asked for. */
  touch?: boolean;
  onSelect: (categoryId: string | null) => void;
  onCreate?: (parentId: string | null, name: string) => void;
}) {
  const baseId = useId();
  const inputRef = useRef<HTMLInputElement>(null);
  const [query, setQuery] = useState("");
  const [place, setPlace] = useState(() => ({
    levelId: initialPlace(index, value, suggestions).levelId,
    direction: "none" as "in" | "out" | "none",
  }));
  const [activeKey, setActiveKey] = useState(
    () => initialPlace(index, value, suggestions).activeKey,
  );
  const [recentIds] = useState(getRecentCategoryIds);

  // A level can disappear under us when the revision refreshes while open.
  const levelRow = place.levelId ? index.byId.get(place.levelId) : undefined;
  const levelId = levelRow ? levelRow.node.id : null;
  const searching = searchTokens(query).length > 0;

  const { sections, total } = useMemo(() => {
    if (searching) {
      const { hits, total } = searchCategories(index, query);
      const options: Option[] = hits.map((hit) => ({
        kind: "pick",
        key: `hit:${hit.row.node.id}`,
        row: hit.row,
        source: "search",
        hit,
      }));
      const tokens = searchTokens(query);
      // Never ahead of real matches: "cl" + Enter must not clear a category.
      if (
        allowUnclassified &&
        tokens.length === 1 &&
        tokens[0].length >= 3 &&
        "unclassified".startsWith(tokens[0])
      )
        options.push({ kind: "unclassified", key: "unclassified" });
      return {
        sections: [{ id: "results", options }] as Section[],
        total: total + (options.length > hits.length ? 1 : 0),
      };
    }
    if (levelRow) {
      const children = index.childrenOf(levelRow.node.id);
      const self: Option = {
        kind: "pick",
        key: `self:${levelRow.node.id}`,
        row: levelRow,
        source: "self",
      };
      return {
        sections: [
          { id: "self", options: [self] },
          {
            id: "children",
            divided: true,
            options: children.map((row) => browseOption(index, row)),
          },
        ] as Section[],
        total: children.length,
      };
    }
    const result: Section[] = [];
    const suggestedIds = new Set<string>();
    const suggested: Option[] = [];
    for (const suggestion of suggestions ?? []) {
      const row = index.byId.get(suggestion.categoryId);
      if (!row || suggestedIds.has(row.node.id)) continue;
      suggestedIds.add(row.node.id);
      suggested.push({
        kind: "pick",
        key: `suggested:${row.node.id}`,
        row,
        source: "suggested",
        suggestion,
      });
      if (suggested.length === SUGGESTION_LIMIT) break;
    }
    if (suggested.length)
      result.push({
        id: "suggested",
        heading: "Suggested",
        options: suggested,
      });
    const recent: Option[] = recentIds
      .filter((id) => !suggestedIds.has(id))
      .flatMap((id) => {
        const row = index.byId.get(id);
        return row
          ? [
              {
                kind: "pick",
                key: `recent:${id}`,
                row,
                source: "recent",
              } as const,
            ]
          : [];
      })
      .slice(0, RECENT_LIMIT);
    if (recent.length)
      result.push({
        id: "recent",
        heading: "Recent",
        layout: "chips",
        options: recent,
      });
    const roots = index.childrenOf(null);
    result.push({
      id: "roots",
      heading: result.length ? "All categories" : undefined,
      options: [
        ...(allowUnclassified
          ? [{ kind: "unclassified", key: "unclassified" } as const]
          : []),
        ...roots.map((row) => browseOption(index, row)),
      ],
    });
    return { sections: result, total: roots.length };
  }, [
    index,
    query,
    searching,
    levelRow,
    suggestions,
    recentIds,
    allowUnclassified,
  ]);

  const flat = useMemo(
    () => sections.flatMap((section) => section.options),
    [sections],
  );
  const positions = useMemo(
    () => new Map(flat.map((option, at) => [option.key, at])),
    [flat],
  );
  const activeIndex =
    flat.length === 0 ? -1 : (positions.get(activeKey ?? "") ?? 0);
  const active = flat[activeIndex];
  const optionId = (option: Option) => `${baseId}-${option.key}`;
  const activeId = active ? optionId(active) : undefined;
  const listKey = searching ? "search" : (levelId ?? "root");
  // Hovering must not scroll the list out from under the pointer.
  const hovered = useRef(false);

  useEffect(() => {
    if (hovered.current) {
      hovered.current = false;
      return;
    }
    if (activeId)
      document.getElementById(activeId)?.scrollIntoView({ block: "nearest" });
  }, [activeId, listKey]);

  function choose(categoryId: string | null) {
    if (categoryId) rememberRecentCategory(categoryId);
    onSelect(categoryId);
  }

  function enter(row: CategoryPath) {
    const first = index.childrenOf(row.node.id)[0];
    setQuery("");
    setPlace({ levelId: row.node.id, direction: "in" });
    setActiveKey(
      first ? browseOption(index, first).key : `self:${row.node.id}`,
    );
  }

  function goTo(targetId: string | null) {
    const from = levelId;
    setPlace({ levelId: targetId, direction: "out" });
    // Land on the branch we just left so the way back in is one keystroke.
    let cursor = from ? index.byId.get(from) : undefined;
    while (cursor && (cursor.node.parentId ?? null) !== targetId)
      cursor = cursor.node.parentId
        ? index.byId.get(cursor.node.parentId)
        : undefined;
    setActiveKey(cursor ? `open:${cursor.node.id}` : null);
  }

  function activate(option: Option) {
    if (option.kind === "unclassified") choose(null);
    else if (option.kind === "open") enter(option.row);
    else choose(option.row.node.id);
  }

  function move(position: number) {
    const next = flat[Math.min(Math.max(position, 0), flat.length - 1)];
    if (next) setActiveKey(next.key);
  }

  function onKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    // Buttons (back, breadcrumbs, create) keep their own Enter and Space.
    if (
      event.target instanceof HTMLButtonElement &&
      (event.key === "Enter" || event.key === " ")
    )
      return;
    const inInput = event.target === inputRef.current;
    const caretAtEnd =
      !inInput ||
      inputRef.current!.selectionStart === inputRef.current!.value.length;
    switch (event.key) {
      case "ArrowDown":
        move(activeIndex + 1);
        break;
      case "ArrowUp":
        move(activeIndex - 1);
        break;
      case "PageDown":
        move(activeIndex + PAGE_STEP);
        break;
      case "PageUp":
        move(activeIndex - PAGE_STEP);
        break;
      case "Home":
      case "End":
        if (query) return;
        move(event.key === "Home" ? 0 : flat.length - 1);
        break;
      case "Enter":
        if (!active) return;
        activate(active);
        break;
      case "ArrowRight":
        if (!active || active.kind === "unclassified" || !caretAtEnd) return;
        if (index.childrenOf(active.row.node.id).length === 0) return;
        if (active.kind === "pick" && active.source === "self") return;
        enter(active.row);
        break;
      case "ArrowLeft":
      case "Backspace":
        // A held Backspace that just emptied the query must not climb levels.
        if (query || !levelRow || event.repeat) return;
        goTo(levelRow.node.parentId ?? null);
        break;
      default:
        // Type-to-search from anywhere in the panel.
        if (
          !inInput &&
          event.key.length === 1 &&
          !event.ctrlKey &&
          !event.metaKey &&
          !event.altKey
        )
          inputRef.current?.focus();
        return;
    }
    event.preventDefault();
  }

  const status = searching
    ? total === 0
      ? `No categories match ${query.trim()}`
      : plural(total, "match", "matches")
    : levelRow
      ? `${levelRow.label}, ${plural(total, "subcategory", "subcategories")}`
      : `All categories, ${plural(total, "group", "groups")}`;
  const hintId = `${baseId}-hint`;
  const crumbs = levelRow ? levelRow.path : [];
  const shownCrumbs =
    crumbs.length > 3 ? [crumbs[0], null, ...crumbs.slice(-2)] : crumbs;
  const rowHeight = touch ? "min-h-12" : "min-h-9";

  function renderOption(option: Option, chip: boolean) {
    const at = positions.get(option.key)!;
    const isActive = at === activeIndex;
    const row = option.kind === "unclassified" ? undefined : option.row;
    const root = row?.path[0];
    const current =
      option.kind === "unclassified"
        ? value?.categoryId === null
        : option.kind === "pick" && value?.categoryId === option.row.node.id;
    const showMonogram =
      option.kind === "unclassified" ||
      (option.kind === "pick" &&
        option.source !== "browse" &&
        option.source !== "self") ||
      (row && row.path.length === 1);
    return (
      // Focus stays in the search box (aria-activedescendant); the panel
      // owns keyboard handling for every option.
      // eslint-disable-next-line jsx-a11y/click-events-have-key-events
      <div
        key={option.key}
        id={optionId(option)}
        role="option"
        tabIndex={-1}
        aria-selected={isActive}
        aria-label={optionName(option)}
        title={row?.node.definition ?? row?.label}
        data-active={isActive}
        data-chip={chip || undefined}
        className={cn(
          "cat-option flex cursor-pointer select-none items-center gap-2.5 text-sm",
          root && "cat-hue",
          chip
            ? "rounded-full border py-1 pr-3 pl-1"
            : cn("rounded-sm px-2 py-1.5", rowHeight),
        )}
        style={rootHueStyle(root)}
        onPointerMove={() => {
          if (isActive) return;
          hovered.current = true;
          setActiveKey(option.key);
        }}
        onMouseDown={(event) => event.preventDefault()}
        onClick={() => activate(option)}
      >
        {showMonogram && (
          <CategoryMonogram
            root={root}
            className={chip ? "h-5 w-5 rounded-full text-[0.65rem]" : undefined}
          />
        )}
        {chip && row ? (
          <span className="max-w-[12rem] truncate">{row.node.name}</span>
        ) : (
          <span className="min-w-0 flex-1">
            <OptionText option={option} />
          </span>
        )}
        {current && <Check className="h-4 w-4 shrink-0 text-primary" />}
        {option.kind === "pick" &&
          option.suggestion?.confidence !== undefined && (
            <Confidence value={option.suggestion.confidence} />
          )}
        {(option.kind === "open" ||
          (option.kind === "pick" &&
            option.source === "search" &&
            index.childrenOf(option.row.node.id).length > 0)) && (
          <span className="flex shrink-0 items-center gap-0.5 text-xs tabular-nums text-muted-foreground">
            {option.kind === "open"
              ? option.childCount
              : index.childrenOf(option.row.node.id).length}
            <ChevronRight className="h-4 w-4" />
          </span>
        )}
      </div>
    );
  }

  return (
    // Keys bubble here from the search box and the breadcrumb buttons.
    // eslint-disable-next-line jsx-a11y/no-static-element-interactions
    <div
      data-category-browser=""
      tabIndex={-1}
      className="flex min-h-0 flex-1 flex-col focus-visible:outline-none"
      onKeyDown={onKeyDown}
    >
      <div className="flex items-center gap-2 border-b px-3 py-1">
        <Search className="h-4 w-4 shrink-0 opacity-50" />
        <input
          ref={inputRef}
          role="combobox"
          aria-expanded="true"
          aria-controls={`${baseId}-list`}
          aria-activedescendant={activeId}
          aria-autocomplete="list"
          aria-label="Search category paths"
          aria-describedby={touch ? undefined : hintId}
          autoComplete="off"
          spellCheck={false}
          // eslint-disable-next-line jsx-a11y/no-autofocus -- the panel exists to be typed into
          autoFocus={!touch}
          placeholder="Search category paths…"
          value={query}
          onChange={(event) => {
            setQuery(event.target.value);
            setActiveKey(null);
            // Only a level change slides; starting or ending a search does not.
            setPlace((current) => ({ ...current, direction: "none" }));
          }}
          className={cn(
            "flex w-full rounded-md bg-transparent text-sm placeholder:text-muted-foreground",
            touch ? "h-10 text-base" : "h-8",
          )}
        />
      </div>
      {levelRow && !searching && (
        <div
          className="cat-hue flex items-center gap-1 border-b px-1.5 py-1"
          style={rootHueStyle(crumbs[0])}
        >
          <Button
            type="button"
            variant="ghost"
            size="icon"
            className={touch ? "h-10 w-10" : "h-7 w-7"}
            aria-label={`Back to ${crumbs.at(-2)?.name ?? "all categories"}`}
            onMouseDown={(event) => event.preventDefault()}
            onClick={(event) => {
              keepPanelFocus(event.currentTarget, touch);
              goTo(levelRow.node.parentId ?? null);
            }}
          >
            <ChevronLeft className="h-4 w-4" />
          </Button>
          <CategoryMonogram
            root={crumbs[0]}
            className="h-5 w-5 text-[0.65rem]"
          />
          <nav
            aria-label="Category path"
            className="flex min-w-0 items-center text-sm"
          >
            {shownCrumbs.map((node, i) => (
              <Fragment key={node?.id ?? "gap"}>
                {i > 0 && (
                  <span
                    aria-hidden="true"
                    className="px-1 text-muted-foreground"
                  >
                    /
                  </span>
                )}
                {node === null ? (
                  <span className="text-muted-foreground">…</span>
                ) : node.id === levelRow.node.id ? (
                  <span
                    aria-current="location"
                    className="truncate font-medium"
                  >
                    {node.name}
                  </span>
                ) : (
                  <button
                    type="button"
                    className="truncate rounded-sm px-0.5 text-muted-foreground hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
                    onMouseDown={(event) => event.preventDefault()}
                    onClick={(event) => {
                      keepPanelFocus(event.currentTarget, touch);
                      goTo(node.id);
                    }}
                  >
                    {node.name}
                  </button>
                )}
              </Fragment>
            ))}
          </nav>
        </div>
      )}
      <div
        key={listKey}
        className={cn(
          "min-h-0 flex-1 overflow-y-auto overscroll-contain p-1",
          place.direction === "in" &&
            "motion-safe:animate-in motion-safe:fade-in-0 motion-safe:slide-in-from-right-3",
          place.direction === "out" &&
            "motion-safe:animate-in motion-safe:fade-in-0 motion-safe:slide-in-from-left-3",
          "duration-150",
        )}
      >
        <div id={`${baseId}-list`} role="listbox" aria-label="Categories">
          {sections.map((section) => {
            const headingId = `${baseId}-${section.id}`;
            const chips = section.layout === "chips";
            return (
              <div
                key={section.id}
                role={section.heading ? "group" : undefined}
                aria-labelledby={section.heading ? headingId : undefined}
                className={cn(
                  "py-0.5",
                  section.divided && "mt-0.5 border-t pt-1",
                )}
              >
                {section.heading && (
                  <div
                    id={headingId}
                    className="flex items-center gap-1.5 px-2 pt-1.5 pb-1 text-xs font-medium text-muted-foreground"
                  >
                    {section.id === "suggested" && (
                      <Sparkles className="h-3 w-3" />
                    )}
                    {section.heading}
                  </div>
                )}
                <div
                  className={
                    chips ? "flex flex-wrap gap-1.5 px-2 pb-1" : undefined
                  }
                >
                  {section.options.map((option) => renderOption(option, chips))}
                </div>
              </div>
            );
          })}
        </div>
        {searching && flat.length === 0 && (
          <div className="px-3 py-6 text-center text-sm text-muted-foreground">
            <p>No categories match “{query.trim()}”.</p>
            {onCreate && (
              <Button
                type="button"
                variant="link"
                size="sm"
                onClick={() => onCreate(levelId, query.trim())}
              >
                Create “{query.trim()}”
              </Button>
            )}
          </div>
        )}
        {searching && total > flat.length && (
          <p className="px-2 py-1.5 text-xs text-muted-foreground">
            Showing {flat.length} of {total}. Keep typing to narrow.
          </p>
        )}
      </div>
      <p role="status" className="sr-only">
        {status}
      </p>
      {(onCreate || !touch) && (
        <div className="flex items-center justify-between gap-2 border-t p-1">
          {onCreate ? (
            <Button
              type="button"
              variant="ghost"
              size="sm"
              className={cn("min-w-0 justify-start", touch && "h-11")}
              onClick={() => onCreate(levelId, "")}
            >
              <Plus className="mr-2 h-4 w-4" />
              <span className="truncate">
                Create category{levelRow ? ` in ${levelRow.node.name}` : ""}
              </span>
            </Button>
          ) : (
            <span />
          )}
          {!touch && (
            <p
              id={hintId}
              className="hidden shrink-0 gap-2 px-2 text-xs text-muted-foreground sm:flex"
            >
              <span>
                <Kbd>↵</Kbd> choose
              </span>
              <span>
                <Kbd>→</Kbd> open
              </span>
              <span>
                <Kbd>←</Kbd> back
              </span>
            </p>
          )}
        </div>
      )}
    </div>
  );
}

function Kbd({ children }: { children: ReactNode }) {
  return (
    <kbd className="rounded border bg-muted px-1 font-sans text-[0.7rem]">
      {children}
    </kbd>
  );
}

function Confidence({ value }: { value: number }) {
  const percent = Math.round(Math.min(Math.max(value, 0), 1) * 100);
  return (
    <span
      aria-hidden="true"
      className="flex shrink-0 items-center gap-1.5 text-xs tabular-nums text-muted-foreground"
    >
      <span className="h-1 w-8 overflow-hidden rounded-full bg-muted">
        <span
          className="block h-full rounded-full bg-[var(--cat-ink,var(--accent))]"
          style={{ width: `${percent}%` }}
        />
      </span>
      {percent}%
    </span>
  );
}

function OptionText({ option }: { option: Option }) {
  if (option.kind === "unclassified")
    return (
      <>
        <span className="block font-medium">Unclassified</span>
        <span className="block truncate text-xs text-muted-foreground">
          No category yet
        </span>
      </>
    );
  const { row } = option;
  if (option.kind === "pick" && option.source === "self")
    return (
      <>
        <span className="block font-medium">{row.node.name}</span>
        <span className="block truncate text-xs text-muted-foreground">
          Use without a subcategory
        </span>
      </>
    );
  const hit = option.kind === "pick" ? option.hit : undefined;
  const definition = hit?.definition
    ? definitionExcerpt(hit.definition.text, hit.definition.ranges)
    : undefined;
  const secondary =
    option.kind === "pick" && option.source !== "browse"
      ? [ancestry(row), option.suggestion?.reason].filter(Boolean).join(", ")
      : row.node.definition;
  return (
    <>
      <span className="block break-words font-medium">
        <Highlight text={row.node.name} ranges={hit?.nameRanges ?? []} />
      </span>
      {secondary && (
        <span className="block truncate text-xs text-muted-foreground">
          {secondary}
        </span>
      )}
      {definition && (
        <span className="block truncate text-xs text-muted-foreground italic">
          <Highlight text={definition.text} ranges={definition.ranges} />
        </span>
      )}
    </>
  );
}

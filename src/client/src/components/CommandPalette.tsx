import { Fragment, useCallback, useContext, useMemo, useState } from "react";
import { useLocation, useNavigate } from "react-router";
import { ChevronDown, Star } from "lucide-react";
import { toast } from "sonner";
import {
  CommandDialog,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
  CommandSeparator,
  CommandShortcut,
} from "@/components/ui/command";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { useAppearance } from "@/hooks/useAppearance";
import { useAuth } from "@/hooks/useAuth";
import { usePermission } from "@/hooks/usePermission";
import { useBackupExport } from "@/hooks/useBackup";
import { usePurgeTrash } from "@/hooks/useTrash";
import {
  fetchAllReceiptIds,
  useBulkPushYnabTransactions,
} from "@/hooks/useYnab";
import { ShortcutsContext } from "@/contexts/shortcuts-context";
import { COMMANDS } from "@/lib/command-palette/commands";
import {
  COMMAND_GROUP_LABELS,
  type Command,
  type CommandContext,
  type CommandGroupId,
} from "@/lib/command-palette/types";
import { useEntityResults } from "@/lib/command-palette/entity-results";
import {
  addRecent,
  getPinned,
  getRecent,
  togglePinned,
} from "@/lib/command-palette/command-history";
import { cn } from "@/lib/utils";

const ENTITY_GROUP_VISIBLE_LIMIT = 8;

interface CommandPaletteProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

const GROUP_ORDER: CommandGroupId[] = [
  "create",
  "actions",
  "navigate",
  "reports",
  "preferences",
];

/**
 * Build the cmdk `value` for a row. The Pinned/Recent sections may render
 * the same command that also appears in its regular group during filtering,
 * so we prefix those two sections to keep per-row identity unique. Regular
 * groups use the raw base value — prefixing them would make group names like
 * "navigate" and "preferences" accidentally matchable.
 */
function commandSearchValue(cmd: Command, section: string): string {
  const base = [cmd.id, cmd.label, ...(cmd.keywords ?? [])]
    .join(" ")
    .toLowerCase();
  return section === "pinned" || section === "recent"
    ? `${section}:${base}`
    : base;
}

/** Spell out keyboard-shortcut glyphs so screen readers don't read them as symbols. */
function spokenShortcut(shortcut: string): string {
  return shortcut
    .replace(/⇧\s*/g, "Shift+")
    .replace(/⌘\s*/g, "Command+")
    .replace(/⌃\s*/g, "Control+")
    .replace(/⌥\s*/g, "Option+")
    .replace(/↵/g, "Enter")
    .trim();
}

/** Map ids from localStorage back into Command objects, dropping unknown ids. */
function resolveCommands(ids: string[], registry: Command[]): Command[] {
  const byId = new Map(registry.map((c) => [c.id, c]));
  return ids
    .map((id) => byId.get(id))
    .filter((c): c is Command => c !== undefined);
}

export function CommandPalette({ open, onOpenChange }: CommandPaletteProps) {
  const navigate = useNavigate();
  const location = useLocation();
  const { setPalette } = useAppearance();
  const { logout } = useAuth();
  const { isAdmin } = usePermission();
  const shortcutsCtx = useContext(ShortcutsContext);
  const [query, setQuery] = useState("");
  const [expandedGroups, setExpandedGroups] = useState<Set<string>>(new Set());
  const [pinnedIds, setPinnedIds] = useState<string[]>(() => getPinned());
  const [recentIds, setRecentIds] = useState<string[]>(() => getRecent());
  const [confirmingEmptyTrash, setConfirmingEmptyTrash] = useState(false);

  // Destructure stable callbacks — TanStack Query guarantees mutate/mutateAsync
  // are stable refs, but the surrounding mutation object is not.
  const { mutate: bulkPushYnabMutate } = useBulkPushYnabTransactions();
  const { mutate: backupExportMutate } = useBackupExport();
  const { mutateAsync: purgeTrashMutateAsync, isPending: purgeTrashPending } =
    usePurgeTrash();

  const admin = isAdmin();
  const close = useCallback(() => onOpenChange(false), [onOpenChange]);
  const openShortcutsHelp = useCallback(
    () => shortcutsCtx?.setHelpOpen(true),
    [shortcutsCtx],
  );

  const syncYnab = useCallback(() => {
    void (async () => {
      try {
        const { ids } = await fetchAllReceiptIds();
        if (ids.length === 0) {
          toast.info("No receipts to sync");
          return;
        }
        bulkPushYnabMutate(ids);
      } catch {
        toast.error("Failed to load receipts for YNAB sync");
      }
    })();
  }, [bulkPushYnabMutate]);

  const exportBackup = useCallback(() => {
    backupExportMutate();
  }, [backupExportMutate]);

  const confirmEmptyTrash = useCallback(() => {
    setConfirmingEmptyTrash(true);
  }, []);

  const handleConfirmEmptyTrash = useCallback(async () => {
    try {
      await purgeTrashMutateAsync();
      setConfirmingEmptyTrash(false);
      onOpenChange(false);
    } catch {
      // Purge failed — keep the palette open so the user can retry. The
      // error toast is surfaced by usePurgeTrash.onError.
      setConfirmingEmptyTrash(false);
    }
  }, [purgeTrashMutateAsync, onOpenChange]);

  const ctx = useMemo<CommandContext>(
    () => ({
      navigate,
      close,
      currentPath: location.pathname,
      setPalette,
      logout,
      openShortcutsHelp,
      syncYnab,
      exportBackup,
      confirmEmptyTrash,
    }),
    [
      navigate,
      close,
      location.pathname,
      setPalette,
      logout,
      openShortcutsHelp,
      syncYnab,
      exportBackup,
      confirmEmptyTrash,
    ],
  );

  const visibleCommands = useMemo(
    () => COMMANDS.filter((c) => !c.requiresAdmin || admin),
    [admin],
  );

  const commandsByGroup = useMemo(() => {
    const map: Record<CommandGroupId, Command[]> = {
      create: [],
      actions: [],
      navigate: [],
      reports: [],
      preferences: [],
    };
    for (const cmd of visibleCommands) map[cmd.group].push(cmd);
    return map;
  }, [visibleCommands]);

  const pinnedSet = useMemo(() => new Set(pinnedIds), [pinnedIds]);

  const pinnedCommands = useMemo(
    () => resolveCommands(pinnedIds, visibleCommands),
    [pinnedIds, visibleCommands],
  );

  const recentCommands = useMemo(
    () =>
      resolveCommands(
        recentIds.filter((id) => !pinnedSet.has(id)),
        visibleCommands,
      ),
    [recentIds, pinnedSet, visibleCommands],
  );

  const entityGroups = useEntityResults({ isAdmin: admin, query });

  const showEntities = query.trim().length > 0;
  const showTopSections = !showEntities;

  function toggleGroupExpanded(id: string) {
    setExpandedGroups((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  const runCommand = useCallback(
    (cmd: Command) => {
      setRecentIds(addRecent(cmd.id));
      Promise.resolve(cmd.run(ctx)).catch((err) => {
        console.error(`Command ${cmd.id} failed:`, err);
      });
    },
    [ctx],
  );

  const handleTogglePin = useCallback((id: string) => {
    setPinnedIds(togglePinned(id));
  }, []);

  const renderCommandItem = useCallback(
    (cmd: Command, section: string) => {
      const Icon = cmd.icon;
      const showShiftN =
        cmd.group === "create" && cmd.targetPath === location.pathname;
      const shortcutGlyph = showShiftN ? "⇧ N" : cmd.shortcut ?? null;
      const pinned = pinnedSet.has(cmd.id);
      return (
        <CommandItem
          key={`${section}:${cmd.id}`}
          value={commandSearchValue(cmd, section)}
          onSelect={() => runCommand(cmd)}
        >
          <Icon aria-hidden="true" className="mr-2 h-4 w-4" />
          <span>{cmd.label}</span>
          <div className="ml-auto flex items-center gap-2">
            {shortcutGlyph ? (
              <CommandShortcut className="ml-0">
                <span aria-hidden="true">{shortcutGlyph}</span>
                <span className="sr-only">{spokenShortcut(shortcutGlyph)}</span>
              </CommandShortcut>
            ) : null}
            <button
              type="button"
              aria-label={pinned ? `Unpin ${cmd.label}` : `Pin ${cmd.label}`}
              aria-pressed={pinned}
              onMouseDown={(e) => e.preventDefault()}
              onClick={(e) => {
                e.stopPropagation();
                handleTogglePin(cmd.id);
              }}
              className={cn(
                "rounded p-1 text-muted-foreground hover:bg-accent hover:text-accent-foreground focus-visible:outline focus-visible:outline-2 focus-visible:outline-ring transition-opacity",
                pinned ? "opacity-100" : "opacity-40 hover:opacity-100",
              )}
            >
              <Star
                aria-hidden="true"
                className={cn("h-3.5 w-3.5", pinned && "fill-current")}
              />
            </button>
          </div>
        </CommandItem>
      );
    },
    [pinnedSet, runCommand, handleTogglePin, location.pathname],
  );

  return (
    <>
    <CommandDialog open={open} onOpenChange={onOpenChange}>
      <CommandInput
        placeholder="Type a command or search…"
        value={query}
        onValueChange={setQuery}
      />
      <CommandList>
        <CommandEmpty>
          No matches. Try a different word or press Esc to close.
        </CommandEmpty>

        {showTopSections && pinnedCommands.length > 0 && (
          <CommandGroup heading="Pinned">
            {pinnedCommands.map((cmd) => renderCommandItem(cmd, "pinned"))}
          </CommandGroup>
        )}

        {showTopSections && recentCommands.length > 0 && (
          <Fragment>
            {pinnedCommands.length > 0 && <CommandSeparator />}
            <CommandGroup heading="Recent">
              {recentCommands.map((cmd) => renderCommandItem(cmd, "recent"))}
            </CommandGroup>
          </Fragment>
        )}

        {GROUP_ORDER.map((groupId, index) => {
          // When the Pinned section is visible, hide those commands from
          // their regular group to avoid rendering the same row twice.
          // Recent is intentionally not hidden — it's a brief MRU hint, not
          // a replacement for the command's canonical home.
          const commands = showTopSections
            ? commandsByGroup[groupId].filter((cmd) => !pinnedSet.has(cmd.id))
            : commandsByGroup[groupId];
          if (commands.length === 0) return null;
          const hasTopSections =
            showTopSections &&
            (pinnedCommands.length > 0 || recentCommands.length > 0);
          return (
            <Fragment key={groupId}>
              {(hasTopSections || index > 0) && <CommandSeparator />}
              <CommandGroup heading={COMMAND_GROUP_LABELS[groupId]}>
                {commands.map((cmd) => renderCommandItem(cmd, groupId))}
              </CommandGroup>
            </Fragment>
          );
        })}

        {showEntities &&
          entityGroups.map((group) => {
            if (group.items.length === 0) return null;
            const Icon = group.icon;
            const expanded = expandedGroups.has(group.id);
            const visibleItems = expanded
              ? group.items
              : group.items.slice(0, ENTITY_GROUP_VISIBLE_LIMIT);
            const hiddenCount = group.items.length - visibleItems.length;
            return (
              <Fragment key={group.id}>
                <CommandSeparator />
                <CommandGroup heading={group.heading}>
                  {visibleItems.map((item) => (
                    <CommandItem
                      key={item.id}
                      value={item.searchValue}
                      onSelect={() => {
                        close();
                        navigate(item.href);
                      }}
                    >
                      <Icon
                        aria-hidden="true"
                        className="mr-2 h-4 w-4 text-muted-foreground"
                      />
                      <span className="truncate">{item.label}</span>
                      {item.meta ? (
                        <span className="ml-2 truncate font-mono text-xs text-muted-foreground">
                          {item.meta}
                        </span>
                      ) : null}
                    </CommandItem>
                  ))}
                  {hiddenCount > 0 && (
                    <CommandItem
                      key={`${group.id}:more`}
                      value={`show-more-${group.id}`}
                      onSelect={() => toggleGroupExpanded(group.id)}
                      className="text-muted-foreground"
                      aria-expanded={expanded}
                    >
                      <ChevronDown aria-hidden="true" className="mr-2 h-4 w-4" />
                      <span>
                        Show {hiddenCount} more {group.heading.toLowerCase()}
                      </span>
                    </CommandItem>
                  )}
                </CommandGroup>
              </Fragment>
            );
          })}
      </CommandList>
    </CommandDialog>
    <AlertDialog
      open={confirmingEmptyTrash}
      onOpenChange={(next) => {
        if (!purgeTrashPending) setConfirmingEmptyTrash(next);
      }}
    >
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>Empty Trash?</AlertDialogTitle>
          <AlertDialogDescription>
            This will permanently delete every item in the Recycle Bin. This
            action cannot be undone.
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel disabled={purgeTrashPending}>
            Cancel
          </AlertDialogCancel>
          <AlertDialogAction
            variant="destructive"
            disabled={purgeTrashPending}
            onClick={(e) => {
              e.preventDefault();
              void handleConfirmEmptyTrash();
            }}
          >
            {purgeTrashPending ? "Emptying…" : "Empty Trash"}
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
    </>
  );
}

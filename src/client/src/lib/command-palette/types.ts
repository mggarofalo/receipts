import type { ComponentType, SVGProps } from "react";
import type { NavigateFunction } from "react-router";

export type CommandGroupId =
  | "create"
  | "actions"
  | "navigate"
  | "reports"
  | "preferences";

export interface CommandContext {
  navigate: NavigateFunction;
  close: () => void;
  currentPath: string;
  setPalette: (palette: "graphite" | "paper") => void;
  logout: () => Promise<void>;
  openShortcutsHelp: () => void;
  syncYnab: () => void;
  exportBackup: () => void;
  confirmEmptyTrash: () => void;
}

export interface Command {
  id: string;
  label: string;
  group: CommandGroupId;
  icon: ComponentType<SVGProps<SVGSVGElement>>;
  keywords?: string[];
  shortcut?: string;
  requiresAdmin?: boolean;
  /** Path compared against `currentPath` to decide whether the ⇧N hint applies. */
  targetPath?: string;
  run: (ctx: CommandContext) => void | Promise<void>;
}

export const COMMAND_GROUP_LABELS: Record<CommandGroupId, string> = {
  create: "Create",
  actions: "Actions",
  navigate: "Go to",
  reports: "Reports",
  preferences: "Preferences",
};

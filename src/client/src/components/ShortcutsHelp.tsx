import { useContext } from "react";
import { ShortcutsContext } from "@/contexts/shortcuts-context";
import { getShortcutsByCategory } from "@/lib/shortcut-registry";
import { formatShortcutLabel } from "@/lib/keyboard-labels";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";

export function ShortcutsHelp() {
  const ctx = useContext(ShortcutsContext);
  if (!ctx) return null;

  const grouped = getShortcutsByCategory();

  return (
    <Dialog open={ctx.helpOpen} onOpenChange={ctx.setHelpOpen}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>Keyboard Shortcuts</DialogTitle>
        </DialogHeader>
        <div className="space-y-6">
          {[...grouped.entries()].map(([category, items]) => (
            <div key={category}>
              <h3 className="text-sm font-semibold text-muted-foreground mb-2">
                {category}
              </h3>
              <div className="space-y-1">
                {items.map((shortcut) => (
                  <div
                    key={shortcut.keys}
                    className="flex items-center justify-between py-1"
                  >
                    <span className="text-sm">{shortcut.label}</span>
                    <kbd className="pointer-events-none inline-flex h-5 items-center gap-1 rounded border bg-muted px-1.5 font-mono text-[10px] font-medium text-muted-foreground">
                      {formatShortcutLabel(shortcut.keys)}
                    </kbd>
                  </div>
                ))}
              </div>
            </div>
          ))}
        </div>
      </DialogContent>
    </Dialog>
  );
}

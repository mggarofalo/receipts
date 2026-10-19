/** Label the primary modifier used by mod shortcuts; this does not change bindings. */
export function primaryModifierLabel(): "Ctrl" | "Command" {
  // Match the platform convention used by react-hotkeys-hook's `mod` binding.
  // Default to Ctrl when rendering without a browser or with an unknown platform.
  const platform = typeof navigator === "undefined" ? "" : navigator.platform;
  return /Mac|iPhone|iPad|iPod/i.test(platform) ? "Command" : "Ctrl";
}

/** The registry's Ctrl prefix denotes the cross-platform primary modifier. */
export function formatShortcutLabel(shortcut: string): string {
  return shortcut.replace(/^Ctrl\+/, `${primaryModifierLabel()}+`);
}

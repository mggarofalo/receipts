import { useCallback, useContext } from "react";
import { useNavigate } from "react-router";
import { useHotkeys } from "react-hotkeys-hook";
import { ShortcutsContext } from "@/contexts/shortcuts-context";
import { useKeyboardShortcut } from "@/hooks/useKeyboardShortcut";

export function useGlobalShortcuts() {
  const ctx = useContext(ShortcutsContext);
  const helpOpen = ctx?.helpOpen;
  const setHelpOpen = ctx?.setHelpOpen;
  const navigate = useNavigate();

  // ? — Toggle help modal (useKeyboardShortcut because react-hotkeys-hook
  // doesn't match the "?" key)
  const toggleHelp = useCallback(
    () => setHelpOpen?.(!helpOpen),
    [helpOpen, setHelpOpen],
  );
  useKeyboardShortcut({
    key: "?",
    ctrlOrMeta: false,
    handler: toggleHelp,
    enableOnFormTags: false,
  });

  // Ctrl+K is handled by GlobalSearchDialog via useKeyboardShortcut

  // Shift+N — Dispatch new-item event
  useHotkeys(
    "shift+n",
    () => {
      window.dispatchEvent(new CustomEvent("shortcut:new-item"));
    },
    { enableOnFormTags: false, preventDefault: true },
  );

  // G+<key> nav chords — match the sidebar kbd hints.
  useHotkeys("g>d", () => navigate("/"), {
    enableOnFormTags: false,
    preventDefault: true,
  });
  useHotkeys("g>r", () => navigate("/receipts"), {
    enableOnFormTags: false,
    preventDefault: true,
  });
  useHotkeys("g>p", () => navigate("/reports"), {
    enableOnFormTags: false,
    preventDefault: true,
  });
}

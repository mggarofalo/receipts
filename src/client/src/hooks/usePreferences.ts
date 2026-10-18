import { useSyncExternalStore } from "react";

const STORAGE_KEY = "receipts:preferences";
const CHANGED_EVENT = "receipts:preferences-changed";
const DEFAULT_SHOW_KEYBOARD_HINTS = true;

// Keep all mounted consumers consistent even when persistence is unavailable.
// A different stored value (or a cross-tab storage event) supersedes this fallback.
let unsavedPreference:
  | { raw: string | null | undefined; value: boolean }
  | undefined;

function getSnapshot(): boolean {
  if (typeof window === "undefined") return DEFAULT_SHOW_KEYBOARD_HINTS;
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (unsavedPreference && raw === unsavedPreference.raw) {
      return unsavedPreference.value;
    }
    unsavedPreference = undefined;
    const parsed: unknown = raw ? JSON.parse(raw) : null;
    if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) {
      const value = (parsed as { showKeyboardHints?: unknown })
        .showKeyboardHints;
      if (typeof value === "boolean") return value;
    }
  } catch {
    return unsavedPreference?.value ?? DEFAULT_SHOW_KEYBOARD_HINTS;
  }
  return DEFAULT_SHOW_KEYBOARD_HINTS;
}

function subscribe(onChange: () => void): () => void {
  function onStorage(event: StorageEvent) {
    if (event.key !== null && event.key !== STORAGE_KEY) return;
    // sessionStorage has its own storage events and must not reset this setting.
    if (event.storageArea && event.storageArea !== window.localStorage) return;
    unsavedPreference = undefined;
    onChange();
  }
  window.addEventListener("storage", onStorage);
  window.addEventListener(CHANGED_EVENT, onChange);
  return () => {
    window.removeEventListener("storage", onStorage);
    window.removeEventListener(CHANGED_EVENT, onChange);
  };
}

function setShowKeyboardHints(showKeyboardHints: boolean): void {
  if (typeof window === "undefined") return;
  let raw: string | null | undefined;
  try {
    raw = window.localStorage.getItem(STORAGE_KEY);
    // Ignore obsolete weekStart data on read and retire it on the next write.
    window.localStorage.setItem(
      STORAGE_KEY,
      JSON.stringify({ showKeyboardHints }),
    );
    unsavedPreference = undefined;
  } catch {
    unsavedPreference = { raw, value: showKeyboardHints };
  }
  // Native storage events only reach other documents, not sibling consumers.
  window.dispatchEvent(new Event(CHANGED_EVENT));
}

/** Browser-local preferences shared by Settings and every hint consumer. */
export function usePreferences() {
  const showKeyboardHints = useSyncExternalStore(
    subscribe,
    getSnapshot,
    () => DEFAULT_SHOW_KEYBOARD_HINTS,
  );
  return { preferences: { showKeyboardHints }, setShowKeyboardHints };
}

import { useCallback, useEffect, useMemo, useState } from "react";

/**
 * Settings → Preferences tab state. Browser-scoped, localStorage-backed —
 * lighter than a DB-column-per-preference and good enough for the per-device
 * settings that don't need to sync across devices.
 *
 * Closes the smaller Preferences slice of RECEIPTS-739. Sync-across-devices
 * for these can be added later by upgrading the storage layer; the consumer
 * API stays the same.
 */
export type WeekStart = "sunday" | "monday";

interface Preferences {
  weekStart: WeekStart;
  showKeyboardHints: boolean;
}

const DEFAULTS: Preferences = {
  weekStart: "sunday",
  showKeyboardHints: true,
};

const STORAGE_KEY = "receipts:preferences";

function read(): Preferences {
  if (typeof window === "undefined") return DEFAULTS;
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (!raw) return DEFAULTS;
    const parsed: unknown = JSON.parse(raw);
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
      return DEFAULTS;
    }
    const obj = parsed as Partial<Preferences>;
    return {
      weekStart:
        obj.weekStart === "monday" || obj.weekStart === "sunday"
          ? obj.weekStart
          : DEFAULTS.weekStart,
      showKeyboardHints:
        typeof obj.showKeyboardHints === "boolean"
          ? obj.showKeyboardHints
          : DEFAULTS.showKeyboardHints,
    };
  } catch {
    return DEFAULTS;
  }
}

function write(prefs: Preferences): void {
  if (typeof window === "undefined") return;
  try {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(prefs));
  } catch {
    // Privacy mode / disabled storage — degrade silently.
  }
}

interface UsePreferencesReturn {
  preferences: Preferences;
  setWeekStart: (v: WeekStart) => void;
  setShowKeyboardHints: (v: boolean) => void;
}

export function usePreferences(): UsePreferencesReturn {
  const [preferences, setPreferences] = useState<Preferences>(() => read());

  // Cross-tab sync: storage events fire on other tabs when localStorage
  // changes in this one, so the user gets consistent state without a refresh.
  useEffect(() => {
    function onStorage(e: StorageEvent) {
      if (e.key === STORAGE_KEY) {
        setPreferences(read());
      }
    }
    window.addEventListener("storage", onStorage);
    return () => window.removeEventListener("storage", onStorage);
  }, []);

  const setWeekStart = useCallback((weekStart: WeekStart) => {
    setPreferences((prev) => {
      const next = { ...prev, weekStart };
      write(next);
      return next;
    });
  }, []);

  const setShowKeyboardHints = useCallback((showKeyboardHints: boolean) => {
    setPreferences((prev) => {
      const next = { ...prev, showKeyboardHints };
      write(next);
      return next;
    });
  }, []);

  return useMemo(
    () => ({ preferences, setWeekStart, setShowKeyboardHints }),
    [preferences, setWeekStart, setShowKeyboardHints],
  );
}

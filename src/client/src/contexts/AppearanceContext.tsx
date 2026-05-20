import { useCallback, useMemo, useState, type ReactNode } from "react";
import {
  applySetting,
  readAppearance,
  type Appearance,
} from "@/lib/appearance";
import { AppearanceContext } from "./appearance-context";

/**
 * Owns the four appearance preferences and keeps `<html>` data-attributes
 * plus localStorage in sync. Initial state matches what the anti-FOUC
 * script in index.html already applied before first paint.
 */
export function AppearanceProvider({ children }: { children: ReactNode }) {
  const [appearance, setAppearance] = useState<Appearance>(readAppearance);

  const update = useCallback(
    <K extends keyof Appearance>(key: K, value: Appearance[K]) => {
      applySetting(key, value);
      setAppearance((prev) => ({ ...prev, [key]: value }));
    },
    [],
  );

  const value = useMemo(
    () => ({
      ...appearance,
      setPalette: (v: Appearance["palette"]) => update("palette", v),
      setDensity: (v: Appearance["density"]) => update("density", v),
      setPaper: (v: Appearance["paper"]) => update("paper", v),
      setMotion: (v: Appearance["motion"]) => update("motion", v),
    }),
    [appearance, update],
  );

  return (
    <AppearanceContext.Provider value={value}>
      {children}
    </AppearanceContext.Provider>
  );
}

import { createContext } from "react";
import type { Appearance } from "@/lib/appearance";

export interface AppearanceContextValue extends Appearance {
  setPalette: (value: Appearance["palette"]) => void;
  setDensity: (value: Appearance["density"]) => void;
  setPaper: (value: Appearance["paper"]) => void;
  setMotion: (value: Appearance["motion"]) => void;
}

export const AppearanceContext = createContext<AppearanceContextValue | null>(
  null,
);

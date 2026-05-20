import { useContext } from "react";
import { AppearanceContext } from "@/contexts/appearance-context";
import type { AppearanceContextValue } from "@/contexts/appearance-context";

export function useAppearance(): AppearanceContextValue {
  const context = useContext(AppearanceContext);
  if (!context) {
    throw new Error("useAppearance must be used within an AppearanceProvider");
  }
  return context;
}

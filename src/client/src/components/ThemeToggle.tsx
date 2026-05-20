import { Palette } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { useAppearance } from "@/hooks/useAppearance";

/**
 * Topbar appearance control. Exposes the four design-system preferences
 * (palette, density, paper intensity, motion) until the full
 * Settings → Appearance panel ships in a later phase.
 */
export function ThemeToggle() {
  const {
    palette,
    density,
    paper,
    motion,
    setPalette,
    setDensity,
    setPaper,
    setMotion,
  } = useAppearance();

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="icon" className="h-8 w-8">
          <Palette className="h-4 w-4" />
          <span className="sr-only">Appearance settings</span>
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-44">
        <DropdownMenuLabel>Palette</DropdownMenuLabel>
        <DropdownMenuRadioGroup
          value={palette}
          onValueChange={(v) => setPalette(v as typeof palette)}
        >
          <DropdownMenuRadioItem value="graphite">
            Graphite
          </DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="paper">Paper</DropdownMenuRadioItem>
        </DropdownMenuRadioGroup>

        <DropdownMenuSeparator />
        <DropdownMenuLabel>Density</DropdownMenuLabel>
        <DropdownMenuRadioGroup
          value={density}
          onValueChange={(v) => setDensity(v as typeof density)}
        >
          <DropdownMenuRadioItem value="compact">Compact</DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="comfortable">
            Comfortable
          </DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="spacious">
            Spacious
          </DropdownMenuRadioItem>
        </DropdownMenuRadioGroup>

        <DropdownMenuSeparator />
        <DropdownMenuLabel>Paper intensity</DropdownMenuLabel>
        <DropdownMenuRadioGroup
          value={paper}
          onValueChange={(v) => setPaper(v as typeof paper)}
        >
          <DropdownMenuRadioItem value="soft">Soft</DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="none">None</DropdownMenuRadioItem>
        </DropdownMenuRadioGroup>

        <DropdownMenuSeparator />
        <DropdownMenuLabel>Motion</DropdownMenuLabel>
        <DropdownMenuRadioGroup
          value={motion}
          onValueChange={(v) => setMotion(v as typeof motion)}
        >
          <DropdownMenuRadioItem value="subtle">Subtle</DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="none">None</DropdownMenuRadioItem>
        </DropdownMenuRadioGroup>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

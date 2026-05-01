import { Sparkles } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import {
  Tooltip,
  TooltipContent,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import type { ConfidenceLevel } from "./types";

interface ConfidenceIndicatorProps {
  confidence: ConfidenceLevel | undefined;
  className?: string;
}

interface ChipConfig {
  label: string;
  ariaLabel: string;
  className: string;
  tooltip: React.ReactNode;
}

const CHIP_CONFIG: Record<"low" | "medium", ChipConfig> = {
  low: {
    label: "AI: low",
    ariaLabel: "AI confidence rating: low",
    className:
      "border-amber-500 bg-amber-50 text-amber-700 dark:bg-amber-950 dark:text-amber-400",
    tooltip: (
      <>
        Claude was <em>low confidence</em> extracting this field. Verify before
        saving.
      </>
    ),
  },
  medium: {
    label: "AI: medium",
    ariaLabel: "AI confidence rating: medium",
    className:
      "border-amber-200 bg-muted text-muted-foreground dark:border-amber-900",
    tooltip: (
      <>
        Claude was <em>medium confidence</em> extracting this field. A quick
        glance recommended.
      </>
    ),
  },
};

export function ConfidenceIndicator({
  confidence,
  className,
}: ConfidenceIndicatorProps) {
  // "none" means the source receipt did not contain this field at all — there is no
  // value to indicate confidence in. Treat the same as "high" / undefined: render nothing.
  if (!confidence || confidence === "high" || confidence === "none") {
    return null;
  }

  const config = CHIP_CONFIG[confidence];

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <Badge
          variant="outline"
          aria-label={config.ariaLabel}
          tabIndex={-1}
          className={`cursor-default gap-1 ${config.className} ${className ?? ""}`}
        >
          <Sparkles className="size-3" aria-hidden="true" />
          {config.label}
        </Badge>
      </TooltipTrigger>
      <TooltipContent>{config.tooltip}</TooltipContent>
    </Tooltip>
  );
}

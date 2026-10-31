import { useRef } from "react";
import { MoreHorizontal, Tag, Wallet, X } from "lucide-react";
import { Badge } from "@/components/ui/badge";
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
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { cn } from "@/lib/utils";

interface Choice {
  id: string;
  name: string;
  isActive: boolean;
}

// Radix Select and radio groups reserve the empty string, so "none" needs a token.
const AUTO = "auto";

/**
 * Archived choices cannot be picked again, but one already stored stays
 * listed so the control can show it.
 */
function selectable(choices: Choice[], current: string | null) {
  return choices.filter((choice) => choice.isActive || choice.id === current);
}

function nameOf(choices: Choice[], id: string | null) {
  return choices.find((choice) => choice.id === id)?.name;
}

/** The receipt's quiet "Paid from" selector. Auto lets each item type decide. */
export function PaidFromSelect({
  envelopes,
  value,
  onChange,
  disabled,
}: {
  envelopes: Choice[];
  value: string | null;
  onChange: (envelopeId: string | null) => void;
  disabled?: boolean;
}) {
  const options = selectable(envelopes, value);
  if (options.length === 0) return null;
  return (
    <Select
      value={value ?? AUTO}
      onValueChange={(next) => onChange(next === AUTO ? null : next)}
      disabled={disabled}
    >
      <SelectTrigger
        size="sm"
        aria-label="Paid from"
        className="gap-1 border-dashed text-xs"
      >
        <Wallet className="h-3.5 w-3.5 text-muted-foreground" />
        <span className="text-muted-foreground">Paid from:</span>
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value={AUTO}>Auto</SelectItem>
        {options.map((envelope) => (
          <SelectItem
            key={envelope.id}
            value={envelope.id}
            disabled={!envelope.isActive}
          >
            {envelope.isActive ? envelope.name : `${envelope.name} (archived)`}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}

/** An optional occasion tag: a chip that reads "+ Occasion" until one is chosen. */
export function OccasionChip({
  occasions,
  value,
  onChange,
  disabled,
}: {
  occasions: Choice[];
  value: string | null;
  onChange: (occasionId: string | null) => void;
  disabled?: boolean;
}) {
  const triggerRef = useRef<HTMLButtonElement>(null);
  const name = nameOf(occasions, value);
  const options = selectable(occasions, value);
  if (!value && options.length === 0) return null;
  return (
    <div className="inline-flex items-center">
      <DropdownMenu>
        <DropdownMenuTrigger asChild disabled={disabled}>
          <Button
            ref={triggerRef}
            type="button"
            variant="outline"
            size="sm"
            aria-label={name ? `Occasion: ${name}` : "Add occasion"}
            className={cn(
              "h-8 rounded-full text-xs",
              !value && "border-dashed text-muted-foreground",
              value && "rounded-r-none border-r-0",
            )}
          >
            <Tag className="h-3.5 w-3.5" />
            {name ?? "Occasion"}
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="start" className="max-h-72 overflow-y-auto">
          <DropdownMenuRadioGroup
            value={value ?? ""}
            onValueChange={(next) => onChange(next || null)}
          >
            {options.map((occasion) => (
              <DropdownMenuRadioItem
                key={occasion.id}
                value={occasion.id}
                disabled={!occasion.isActive}
              >
                {occasion.name}
              </DropdownMenuRadioItem>
            ))}
          </DropdownMenuRadioGroup>
        </DropdownMenuContent>
      </DropdownMenu>
      {value && (
        <Button
          type="button"
          variant="outline"
          size="sm"
          aria-label="Remove occasion"
          disabled={disabled}
          onClick={() => {
            onChange(null);
            // The remove button disappears; keep focus on the chip.
            triggerRef.current?.focus();
          }}
          className="h-8 rounded-l-none rounded-r-full px-2"
        >
          <X className="h-3 w-3" />
        </Button>
      )}
    </div>
  );
}

/**
 * Marks an item paid from a different envelope than its receipt, or with none
 * at all. Undefined means not known yet (no item type chosen), so no badge.
 */
export function EnvelopeBadge({
  envelopes,
  envelopeId,
  receiptEnvelopeId,
}: {
  envelopes: Choice[];
  envelopeId: string | null | undefined;
  receiptEnvelopeId: string | null;
}) {
  if (envelopeId === undefined) return null;
  if (envelopeId !== null && envelopeId === receiptEnvelopeId) return null;
  return (
    <div className="mt-1">
      {envelopeId === null ? (
        <Badge
          variant="outline"
          className="border-dashed text-muted-foreground"
          title="No envelope: choose one in the row menu, or set a default for this item type"
        >
          <Wallet />
          No envelope
        </Badge>
      ) : (
        <Badge variant="secondary" title="Paid from a different envelope">
          <Wallet />
          <span className="sr-only">Paid from </span>
          {nameOf(envelopes, envelopeId) ?? "Unknown envelope"}
        </Badge>
      )}
    </div>
  );
}

/** The row menu's per-item override. Automatic follows the receipt, then the item type. */
export function EnvelopeOverrideMenu({
  envelopes,
  value,
  onChange,
  disabled,
  itemLabel,
}: {
  envelopes: Choice[];
  value: string | null;
  onChange: (envelopeId: string | null) => void;
  disabled?: boolean;
  itemLabel: string;
}) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild disabled={disabled}>
        <Button
          type="button"
          variant="ghost"
          size="icon"
          aria-label={`More actions for ${itemLabel}`}
        >
          <MoreHorizontal className="h-4 w-4" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="max-h-80 overflow-y-auto">
        <DropdownMenuLabel>Paid from</DropdownMenuLabel>
        <DropdownMenuSeparator />
        <DropdownMenuRadioGroup
          value={value ?? AUTO}
          onValueChange={(next) => onChange(next === AUTO ? null : next)}
        >
          <DropdownMenuRadioItem value={AUTO}>Automatic</DropdownMenuRadioItem>
          {selectable(envelopes, value).map((envelope) => (
            <DropdownMenuRadioItem
              key={envelope.id}
              value={envelope.id}
              disabled={!envelope.isActive}
            >
              {envelope.name}
            </DropdownMenuRadioItem>
          ))}
        </DropdownMenuRadioGroup>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

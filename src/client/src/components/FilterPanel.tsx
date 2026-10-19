import { useState } from "react";
import { ChevronDown, ChevronRight, Filter, Save, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { DateInput } from "@/components/ui/date-input";
import { Label } from "@/components/ui/label";
import { Badge } from "@/components/ui/badge";
import { Combobox } from "@/components/ui/combobox";
import {
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
} from "@/components/ui/collapsible";
import type { FilterPreset } from "@/lib/search";

export interface SelectFilterField {
  type: "select";
  key: string;
  label: string;
  options: string[];
}

export interface DateRangeFilterField {
  type: "dateRange";
  key: string;
  label: string;
}

export interface NumberRangeFilterField {
  type: "numberRange";
  key: string;
  label: string;
}

export interface BooleanFilterField {
  type: "boolean";
  key: string;
  label: string;
}

export type FilterField =
  | SelectFilterField
  | DateRangeFilterField
  | NumberRangeFilterField
  | BooleanFilterField;

export type FilterValues = Record<string, unknown>;

interface FilterPanelProps {
  fields: FilterField[];
  values: FilterValues;
  onChange: (values: FilterValues) => void;
  savedFilters?: FilterPreset[];
  onSaveFilter?: (name: string) => void;
  onDeleteFilter?: (id: string) => void;
  onLoadFilter?: (preset: FilterPreset) => void;
}

function countActiveFilters(
  fields: FilterField[],
  values: FilterValues,
): number {
  let count = 0;
  for (const field of fields) {
    const val = values[field.key];
    if (field.type === "select" && val && val !== "all") count++;
    if (field.type === "boolean" && val && val !== "all") count++;
    if (field.type === "dateRange") {
      const range = val as { from?: string; to?: string } | undefined;
      if (range?.from || range?.to) count++;
    }
    if (field.type === "numberRange") {
      const range = val as { min?: number; max?: number } | undefined;
      if (range?.min !== undefined || range?.max !== undefined) count++;
    }
  }
  return count;
}

export function FilterPanel({
  fields,
  values,
  onChange,
  savedFilters,
  onSaveFilter,
  onDeleteFilter,
  onLoadFilter,
}: FilterPanelProps) {
  const [open, setOpen] = useState(false);
  const [presetName, setPresetName] = useState("");
  const activeCount = countActiveFilters(fields, values);

  function updateField(key: string, value: unknown) {
    onChange({ ...values, [key]: value });
  }

  function clearAll() {
    const cleared: FilterValues = {};
    for (const field of fields) {
      if (field.type === "select" || field.type === "boolean") {
        cleared[field.key] = "all";
      } else {
        cleared[field.key] = undefined;
      }
    }
    onChange(cleared);
  }

  return (
    <Collapsible open={open} onOpenChange={setOpen}>
      <div className="flex items-center gap-2">
        <CollapsibleTrigger asChild>
          <Button variant="outline" size="sm" className="gap-1.5">
            <Filter className="h-3.5 w-3.5" />
            Filters
            {activeCount > 0 && (
              <Badge variant="secondary" className="ml-1 text-xs">
                {activeCount}
              </Badge>
            )}
            {open ? (
              <ChevronDown className="h-3.5 w-3.5" />
            ) : (
              <ChevronRight className="h-3.5 w-3.5" />
            )}
          </Button>
        </CollapsibleTrigger>
        {activeCount > 0 && (
          <Button variant="ghost" size="sm" onClick={clearAll}>
            Clear all
          </Button>
        )}
      </div>

      <CollapsibleContent className="mt-3">
        <div className="flex flex-wrap gap-4 rounded-md border p-4">
          {fields.map((field) => (
            <div key={field.key} className="min-w-[180px] space-y-1.5">
              <Label className="text-xs">{field.label}</Label>
              {field.type === "select" && (
                <Combobox
                  aria-label={field.label}
                  options={[
                    { value: "all", label: "All" },
                    ...field.options.map((opt) => ({
                      value: opt,
                      label: opt,
                    })),
                  ]}
                  value={(values[field.key] as string) ?? "all"}
                  onValueChange={(v) => updateField(field.key, v)}
                  placeholder="All"
                  searchPlaceholder="Search..."
                  className="h-8 text-xs"
                />
              )}
              {field.type === "boolean" && (
                <Combobox
                  aria-label={field.label}
                  options={[
                    { value: "all", label: "All" },
                    { value: "true", label: "Yes" },
                    { value: "false", label: "No" },
                  ]}
                  value={(values[field.key] as string) ?? "all"}
                  onValueChange={(v) => updateField(field.key, v)}
                  placeholder="All"
                  searchPlaceholder="Search..."
                  className="h-8 text-xs"
                />
              )}
              {field.type === "dateRange" && (
                <div className="flex gap-2">
                  <DateInput
                    className="h-8 text-xs"
                    aria-label={`${field.label} from`}
                    placeholder="From"
                    value={
                      (values[field.key] as { from?: string } | undefined)
                        ?.from ?? ""
                    }
                    onChange={(v) =>
                      updateField(field.key, {
                        ...(values[field.key] as object),
                        from: v || undefined,
                      })
                    }
                  />
                  <DateInput
                    className="h-8 text-xs"
                    aria-label={`${field.label} to`}
                    placeholder="To"
                    value={
                      (values[field.key] as { to?: string } | undefined)?.to ??
                      ""
                    }
                    onChange={(v) =>
                      updateField(field.key, {
                        ...(values[field.key] as object),
                        to: v || undefined,
                      })
                    }
                  />
                </div>
              )}
              {field.type === "numberRange" && (
                <div className="flex gap-2">
                  <Input
                    type="number"
                    className="h-8 text-xs"
                    aria-label={`${field.label} minimum`}
                    placeholder="Min"
                    value={
                      (values[field.key] as { min?: number } | undefined)
                        ?.min ?? ""
                    }
                    onChange={(e) =>
                      updateField(field.key, {
                        ...(values[field.key] as object),
                        min:
                          e.target.value !== ""
                            ? Number(e.target.value)
                            : undefined,
                      })
                    }
                  />
                  <Input
                    type="number"
                    className="h-8 text-xs"
                    aria-label={`${field.label} maximum`}
                    placeholder="Max"
                    value={
                      (values[field.key] as { max?: number } | undefined)
                        ?.max ?? ""
                    }
                    onChange={(e) =>
                      updateField(field.key, {
                        ...(values[field.key] as object),
                        max:
                          e.target.value !== ""
                            ? Number(e.target.value)
                            : undefined,
                      })
                    }
                  />
                </div>
              )}
            </div>
          ))}
        </div>

        {(savedFilters || onSaveFilter) && (
          <div className="mt-3 flex flex-wrap items-center gap-2">
            {onSaveFilter && (
              <div className="flex gap-1.5">
                <Input
                  aria-label="Preset name"
                  placeholder="Preset name..."
                  value={presetName}
                  onChange={(e) => setPresetName(e.target.value)}
                  className="h-8 w-40 text-xs"
                />
                <Button
                  variant="outline"
                  size="sm"
                  className="h-8 gap-1"
                  disabled={!presetName.trim()}
                  onClick={() => {
                    onSaveFilter(presetName.trim());
                    setPresetName("");
                  }}
                >
                  <Save className="h-3 w-3" />
                  Save
                </Button>
              </div>
            )}
            {savedFilters?.map((preset) => (
              <div key={preset.id} className="flex items-center gap-1">
                <Button
                  variant="secondary"
                  size="sm"
                  className="h-7 text-xs"
                  onClick={() => onLoadFilter?.(preset)}
                >
                  {preset.name}
                </Button>
                {onDeleteFilter && (
                  <button
                    className="rounded p-0.5 text-muted-foreground hover:text-destructive"
                    onClick={() => onDeleteFilter(preset.id)}
                    aria-label={`Delete preset ${preset.name}`}
                  >
                    <Trash2 className="h-3 w-3" />
                  </button>
                )}
              </div>
            ))}
          </div>
        )}
      </CollapsibleContent>
    </Collapsible>
  );
}

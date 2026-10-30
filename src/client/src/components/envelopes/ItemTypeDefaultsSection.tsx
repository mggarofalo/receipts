import { useMemo, useState } from "react";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { CategoryPathPicker } from "@/components/categories/CategoryPathPicker";
import { useCategoryTaxonomy } from "@/hooks/useCategoryTaxonomy";
import {
  useDeleteItemTypeEnvelopeDefault,
  useItemTypeEnvelopeDefaults,
  useSetItemTypeEnvelopeDefault,
  type Envelope,
} from "@/hooks/useEnvelopes";
import { categoryPaths, type CategoryChoice } from "@/lib/category-paths";

interface ItemTypeDefaultsSectionProps {
  envelopes: Envelope[];
}

/**
 * Item type → default envelope. A purchase takes the default of its nearest mapped
 * item-type ancestor when saved; recorded envelopes never move afterwards.
 */
export function ItemTypeDefaultsSection({
  envelopes,
}: ItemTypeDefaultsSectionProps) {
  const taxonomy = useCategoryTaxonomy();
  const { defaults, isLoading, isError } = useItemTypeEnvelopeDefaults();
  const setDefault = useSetItemTypeEnvelopeDefault();
  const deleteDefault = useDeleteItemTypeEnvelopeDefault();
  const [choice, setChoice] = useState<CategoryChoice>();
  const [envelopeId, setEnvelopeId] = useState("");
  const busy = setDefault.isPending || deleteDefault.isPending;

  const labels = useMemo(
    () =>
      new Map(
        categoryPaths(taxonomy.data?.nodes ?? []).map((row) => [
          row.node.id,
          row.label,
        ]),
      ),
    [taxonomy.data],
  );
  const envelopeNames = useMemo(
    () => new Map(envelopes.map((envelope) => [envelope.id, envelope.name])),
    [envelopes],
  );
  const activeEnvelopes = envelopes.filter((envelope) => envelope.isActive);
  const rows = defaults
    .map((row) => ({
      ...row,
      label: labels.get(row.categoryId) ?? "Archived or unknown item type",
    }))
    .sort((a, b) => a.label.localeCompare(b.label));

  return (
    <Card>
      <CardHeader>
        <CardTitle>Item type defaults</CardTitle>
        <CardDescription>
          The envelope a purchase uses when its receipt has no &ldquo;paid
          from&rdquo; envelope. Child item types inherit their nearest
          ancestor&rsquo;s default. Changing a default affects purchases saved
          or reclassified afterwards; recorded purchases keep their envelope.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="flex flex-col gap-2 sm:flex-row sm:items-end">
          <div className="w-full sm:max-w-sm">
            <span className="text-sm font-medium">Item type</span>
            <CategoryPathPicker
              revision={taxonomy.data}
              value={choice}
              onValueChange={setChoice}
              loading={taxonomy.isLoading}
              error={taxonomy.isError}
              allowUnclassified={false}
              disabled={busy}
              aria-label="Item type"
            />
          </div>
          <Select
            value={envelopeId}
            onValueChange={setEnvelopeId}
            disabled={busy}
          >
            <SelectTrigger
              className="w-full sm:max-w-xs"
              aria-label="Default envelope"
            >
              <SelectValue placeholder="Select envelope" />
            </SelectTrigger>
            <SelectContent>
              {activeEnvelopes.map((envelope) => (
                <SelectItem key={envelope.id} value={envelope.id}>
                  {envelope.name}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Button
            disabled={busy || !choice?.categoryId || !envelopeId}
            onClick={() => {
              if (!choice?.categoryId || !envelopeId) return;
              setDefault.mutate(
                { categoryId: choice.categoryId, envelopeId },
                { onSuccess: () => setChoice(undefined) },
              );
            }}
          >
            Save default
          </Button>
        </div>
        {isLoading ? (
          <p className="text-sm text-muted-foreground">Loading…</p>
        ) : isError ? (
          <p className="text-sm text-destructive">
            Item type defaults are unavailable.
          </p>
        ) : rows.length === 0 ? (
          <p className="text-sm text-muted-foreground">
            No defaults yet. Purchases without a default need an envelope before
            they can be pushed to YNAB.
          </p>
        ) : (
          <ul className="divide-y rounded-md border">
            {rows.map((row) => (
              <li
                key={row.categoryId}
                className="flex flex-col gap-2 p-3 text-sm sm:flex-row sm:items-center"
              >
                <span className="min-w-0 flex-1 break-words">{row.label}</span>
                <span className="min-w-0 break-words font-medium">
                  {envelopeNames.get(row.envelopeId) ?? "Unknown envelope"}
                </span>
                <Button
                  variant="ghost"
                  size="sm"
                  disabled={busy}
                  onClick={() => deleteDefault.mutate(row.categoryId)}
                  aria-label={`Remove default for ${row.label}`}
                >
                  Remove
                </Button>
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}

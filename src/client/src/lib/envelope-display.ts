import type { CategoryNode } from "@/lib/category-paths";

interface EnvelopeRef {
  id: string;
  isActive: boolean;
}

interface DefaultRef {
  categoryId: string;
  envelopeId: string;
}

/**
 * Previews the envelope an unsaved item will be recorded with, following the
 * server's rule (item override ?? receipt paid-from ?? nearest mapped item-type
 * default, skipping archived envelopes). The server records the real value at
 * save; this only decides what the entry page shows. `undefined` means the
 * item type is not chosen yet, so the server may still fill it.
 */
export function previewItemEnvelope(
  item: { envelopeId?: string | null; typePath?: CategoryNode[] },
  paidFromEnvelopeId: string | null,
  defaults: DefaultRef[],
  envelopes: EnvelopeRef[],
): string | null | undefined {
  if (item.envelopeId) return item.envelopeId;
  if (paidFromEnvelopeId) return paidFromEnvelopeId;
  if (!item.typePath?.length) return undefined;
  const active = new Set(envelopes.filter((e) => e.isActive).map((e) => e.id));
  const byCategory = new Map(
    defaults
      .filter((row) => active.has(row.envelopeId))
      .map((row) => [row.categoryId, row.envelopeId]),
  );
  for (const node of [...item.typePath].reverse()) {
    const envelopeId = byCategory.get(node.id);
    if (envelopeId) return envelopeId;
  }
  return null;
}

/**
 * The envelope a receipt is paid from: its explicit choice, else the one most
 * of its items share (first seen wins a tie). Callers count an overridden item
 * by the envelope it would get automatically, so the override is badged
 * rather than moving the majority.
 * Items are badged only where they differ from it, so a receipt paid from one
 * envelope shows no badges at all.
 */
export function receiptEnvelope(
  paidFromEnvelopeId: string | null,
  itemEnvelopeIds: (string | null | undefined)[],
): string | null {
  if (paidFromEnvelopeId) return paidFromEnvelopeId;
  // Map iteration follows insertion order, so the first seen wins a tie.
  const counts = new Map<string, number>();
  for (const id of itemEnvelopeIds) {
    if (id) counts.set(id, (counts.get(id) ?? 0) + 1);
  }
  let best: string | null = null;
  for (const [id, count] of counts) {
    if (best === null || count > counts.get(best)!) best = id;
  }
  return best;
}

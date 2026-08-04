import type { components } from "@/generated/api";

export type AuditLog = components["schemas"]["AuditLogDto"];
export type AuthAuditLog = components["schemas"]["AuthAuditEntryDto"];

export interface FieldChange {
  field: string;
  oldValue: string | null;
  newValue: string | null;
}

/**
 * Reads a string property from a record, accepting either camelCase or
 * PascalCase keys. The backend serializes `FieldChange` with PascalCase keys
 * (`FieldName`/`OldValue`/`NewValue`); historically the client only looked for
 * camelCase, so every audit row's changes silently parsed to `[]`.
 */
function pickString(
  rec: Record<string, unknown>,
  ...keys: string[]
): string | null {
  for (const key of keys) {
    const value = rec[key];
    if (typeof value === "string") return value;
  }
  return null;
}

export function parseChanges(changesJson: string): FieldChange[] {
  try {
    const parsed: unknown = JSON.parse(changesJson);
    if (!Array.isArray(parsed)) return [];
    const changes: FieldChange[] = [];
    for (const entry of parsed) {
      if (typeof entry !== "object" || entry === null) continue;
      const rec = entry as Record<string, unknown>;
      const field = pickString(rec, "field", "fieldName", "FieldName");
      if (field === null) continue;
      changes.push({
        field,
        oldValue: pickString(rec, "oldValue", "OldValue"),
        newValue: pickString(rec, "newValue", "NewValue"),
      });
    }
    return changes;
  } catch {
    return [];
  }
}

/**
 * Badge styling for an audit action.
 *
 * Accepts both the raw enum name the API actually sends (`AuditService` projects
 * `entity.Action.ToString()`, giving "Create"/"Delete") and the past-tense display labels from
 * the enum-metadata endpoint ("Created"/"Deleted"). Only the past-tense forms were handled
 * originally, so every real response fell through to the neutral default and a deletion never
 * rendered as destructive (RECEIPTS-890).
 */
export function actionBadgeVariant(
  action: string,
): "default" | "secondary" | "destructive" | "outline" {
  switch (action) {
    case "Create":
    case "Created":
      return "default";
    case "Update":
    case "Updated":
      return "secondary";
    case "Delete":
    case "Deleted":
      return "destructive";
    case "Restore":
    case "Restored":
      return "outline";
    // Merge and Split both destroy or detach structure an admin may need to trace back.
    case "Merge":
    case "Merged":
    case "Split":
      return "outline";
    default:
      return "secondary";
  }
}

export function formatAuditTimestamp(iso: string): string {
  return new Date(iso).toLocaleString();
}

export function relativeTime(iso: string): string {
  const now = Date.now();
  const then = new Date(iso).getTime();
  const diffSeconds = Math.floor((now - then) / 1000);

  if (diffSeconds < 60) return "just now";
  const diffMinutes = Math.floor(diffSeconds / 60);
  if (diffMinutes < 60) return `${diffMinutes}m ago`;
  const diffHours = Math.floor(diffMinutes / 60);
  if (diffHours < 24) return `${diffHours}h ago`;
  const diffDays = Math.floor(diffHours / 24);
  if (diffDays < 30) return `${diffDays}d ago`;
  return formatAuditTimestamp(iso);
}

export function truncateId(id: string, length = 8): string {
  return id.length > length ? `${id.slice(0, length)}...` : id;
}

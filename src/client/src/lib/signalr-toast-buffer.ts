import { toast } from "sonner";

const FLUSH_DELAY_MS = 5_000;

export type ToastOrigin = "api-key" | "other-session" | "other-user";

interface BufferEntry {
  count: number;
}

const buffer = new Map<string, BufferEntry>();
let flushTimer: ReturnType<typeof setTimeout> | null = null;

const pluralExceptions: Record<string, string> = {
  category: "categories",
  subcategory: "subcategories",
};

function pluralize(name: string, count: number): string {
  if (count === 1) return name;
  return pluralExceptions[name] ?? `${name}s`;
}

function makeKey(
  entityType: string,
  changeType: string,
  origin: ToastOrigin,
): string {
  return `${entityType}::${changeType}::${origin}`;
}

const originSuffix: Record<ToastOrigin, string> = {
  "api-key": "via your API key",
  "other-session": "in another of your sessions",
  "other-user": "by another user",
};

function flush(): void {
  flushTimer = null;

  for (const [key, entry] of buffer) {
    const [entityType, changeType, origin] = key.split("::");
    const displayName = pluralize(entityType, entry.count);
    const suffix = originSuffix[origin as ToastOrigin] ?? "by another user";
    const action =
      changeType === "created"
        ? "created"
        : changeType === "deleted"
          ? "deleted"
          : "updated";

    if (entry.count === 1) {
      toast.info(`A ${displayName} was ${action} ${suffix}`);
    } else {
      toast.info(`${entry.count} ${displayName} were ${action} ${suffix}`);
    }
  }

  buffer.clear();
}

export function bufferToast(
  entityType: string,
  changeType: string,
  count: number,
  origin: ToastOrigin = "other-user",
): void {
  const key = makeKey(entityType, changeType, origin);
  const existing = buffer.get(key);

  if (existing) {
    existing.count += count;
  } else {
    buffer.set(key, { count });
  }

  if (flushTimer === null) {
    flushTimer = setTimeout(flush, FLUSH_DELAY_MS);
  }
}

/** Drop notifications belonging to a session that has ended. */
export function clearBufferedToasts(): void {
  if (flushTimer !== null) {
    clearTimeout(flushTimer);
    flushTimer = null;
  }
  buffer.clear();
}

/** @deprecated Use clearBufferedToasts for lifecycle cleanup. */
export const _resetForTesting = clearBufferedToasts;

/** Exposed for testing — triggers an immediate flush. */
export function _flushForTesting(): void {
  if (flushTimer !== null) {
    clearTimeout(flushTimer);
    flushTimer = null;
  }
  flush();
}

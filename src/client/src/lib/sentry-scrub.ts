import type {
  Breadcrumb,
  BrowserOptions,
  Event,
  ReplayFrameEvent,
} from "@sentry/react";

// @sentry/react does not re-export these two names; take them from the option
// signatures so they always match the installed SDK.
export type SentrySpanJSON = Parameters<
  NonNullable<BrowserOptions["beforeSendSpan"]>
>[0];
export type SentryTransactionEvent = Parameters<
  NonNullable<BrowserOptions["beforeSendTransaction"]>
>[0];

/**
 * Keeps the YNAB OAuth callback's query and fragment out of everything Sentry
 * sends. YNAB returns the browser to `/settings/ynab/callback` with a
 * single-use authorization code and state (or `error=…`) in the query string.
 * `ynab-callback-arrival.ts` rewrites the address before Sentry starts; these
 * functions are the second line: they rewrite any string Sentry is about to
 * send that still names that address with a query or fragment (the browser's
 * own navigation-timing entry keeps the original address, for example).
 */

// The path followed by a query or fragment, wherever it appears in a string:
// a bare path, an absolute URL, or an address embedded in a longer message.
const PLAIN = /(\/settings\/ynab\/callback\/?)[?#][^\s"'<>)\]]*/gi;
// The same address percent-encoded inside another URL's parameter.
const ENCODED =
  /(%2Fsettings%2Fynab%2Fcallback(?:%2F)?)(?:%3F|%23)[^&#\s"'<>)\]]*/gi;
const MENTIONS_CALLBACK = /(?:\/|%2F)settings(?:\/|%2F)ynab(?:\/|%2F)callback/i;

/** Removes the query and fragment from every mention of the callback address. */
export function scrubYnabCallbackUrl(value: string): string {
  if (!MENTIONS_CALLBACK.test(value)) return value;
  return value.replace(PLAIN, "$1").replace(ENCODED, "$1");
}

function mentionsCallback(value: unknown): boolean {
  return typeof value === "string" && MENTIONS_CALLBACK.test(value);
}

// Keys that hold a URL, and the sibling keys that hold that URL's query or
// fragment on their own (so they carry no path to recognise them by).
const URL_KEYS = ["url", "http.url", "url.full", "from", "to"];
const DETACHED_QUERY_KEYS = [
  "query_string",
  "http.query",
  "http.fragment",
  "url.query",
  "url.fragment",
];

/**
 * Rewrites every string inside `value` in place and, for a record whose URL is
 * the callback address, removes the detached query/fragment fields beside it.
 */
function scrubDeep(
  value: unknown,
  seen: WeakSet<object> = new WeakSet(),
  depth = 0,
): void {
  if (value === null || typeof value !== "object") return;
  // Sentry payloads are JSON-shaped. Anything else (a DOM node or class
  // instance an application handed to Sentry before normalisation) is left
  // alone rather than walked or altered.
  if (depth > MAX_DEPTH || seen.has(value) || Object.isFrozen(value)) return;
  seen.add(value);

  if (Array.isArray(value)) {
    for (let index = 0; index < value.length; index++) {
      const item: unknown = value[index];
      if (typeof item === "string") value[index] = scrubYnabCallbackUrl(item);
      else scrubDeep(item, seen, depth + 1);
    }
    return;
  }

  const prototype: unknown = Object.getPrototypeOf(value);
  if (prototype !== Object.prototype && prototype !== null) return;

  const record = value as Record<string, unknown>;
  if (URL_KEYS.some((key) => mentionsCallback(record[key]))) {
    for (const key of DETACHED_QUERY_KEYS) delete record[key];
  }
  for (const key of Object.keys(record)) {
    const item = record[key];
    if (typeof item === "string") record[key] = scrubYnabCallbackUrl(item);
    else scrubDeep(item, seen, depth + 1);
  }
}

const MAX_DEPTH = 12;

/**
 * For `beforeSend`, `beforeSendTransaction` and as an event processor (which
 * also sees replay and feedback events). Covers `request.url`,
 * `request.query_string`, the transaction name, tags, extra, contexts
 * (including the trace context's data), breadcrumbs, exception values, spans
 * (description and data attributes) and a replay event's `urls`.
 */
export function scrubYnabCallbackEvent<T extends Event>(event: T): T {
  scrubDeep(event);
  return event;
}

/** For `beforeSendSpan`: spans sent on their own rather than in a transaction. */
export function scrubYnabCallbackSpan(span: SentrySpanJSON): SentrySpanJSON {
  scrubDeep(span);
  return span;
}

/**
 * For the replay integration's `beforeAddRecordingEvent`: navigation,
 * performance and network entries, and replay breadcrumbs.
 */
export function scrubYnabCallbackReplayEvent(
  event: ReplayFrameEvent,
): ReplayFrameEvent {
  scrubDeep(event);
  return event;
}

/**
 * For `beforeBreadcrumb`: the message and the top-level string fields of the
 * data (`from`, `to`, `url`, …), which is where navigation, fetch and XHR
 * breadcrumbs keep addresses. Deeper values are raw application objects at
 * this point and are not walked here; they are covered when the breadcrumb is
 * sent inside an event. Returns the same object when nothing changes.
 */
export function scrubYnabCallbackBreadcrumb(breadcrumb: Breadcrumb): Breadcrumb {
  const message =
    breadcrumb.message === undefined
      ? undefined
      : scrubYnabCallbackUrl(breadcrumb.message);
  let data = breadcrumb.data;
  if (data) {
    const isCallback = URL_KEYS.some((key) => mentionsCallback(data?.[key]));
    let copy: Record<string, unknown> | undefined;
    for (const key of Object.keys(data)) {
      const item: unknown = data[key];
      if (isCallback && DETACHED_QUERY_KEYS.includes(key)) {
        copy ??= { ...data };
        delete copy[key];
      } else if (typeof item === "string") {
        const scrubbed = scrubYnabCallbackUrl(item);
        if (scrubbed !== item) {
          copy ??= { ...data };
          copy[key] = scrubbed;
        }
      }
    }
    data = copy ?? data;
  }
  if (message === breadcrumb.message && data === breadcrumb.data) {
    return breadcrumb;
  }
  return { ...breadcrumb, message, data };
}

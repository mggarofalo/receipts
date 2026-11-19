import { describe, expect, it } from "vitest";
import type { ErrorEvent, Event, ReplayFrameEvent } from "@sentry/react";
import type {
  SentrySpanJSON as SpanJSON,
  SentryTransactionEvent as TransactionEvent,
} from "./sentry-scrub";
import {
  scrubYnabCallbackBreadcrumb,
  scrubYnabCallbackEvent,
  scrubYnabCallbackReplayEvent,
  scrubYnabCallbackSpan,
  scrubYnabCallbackUrl,
} from "./sentry-scrub";

const CODE = "secret-code-4f9a";
const STATE = "secret-state-71c2";
const QUERY = `code=${CODE}&state=${STATE}`;
const ORIGIN = "https://receipts.example.com";
const ARRIVAL_PATH = `/settings/ynab/callback?${QUERY}`;
const ARRIVAL_URL = `${ORIGIN}${ARRIVAL_PATH}`;
const CLEAN_URL = `${ORIGIN}/settings/ynab/callback`;

function expectNoSecrets(payload: unknown) {
  const sent = JSON.stringify(payload);
  expect(sent).not.toContain(CODE);
  expect(sent).not.toContain(STATE);
  expect(sent).not.toContain("access_denied");
  expect(sent).not.toMatch(/ynab\/callback\/?[?#]/);
}

describe("scrubYnabCallbackUrl", () => {
  it.each([
    [ARRIVAL_PATH, "/settings/ynab/callback"],
    [ARRIVAL_URL, CLEAN_URL],
    [`${ORIGIN}/settings/ynab/callback/?${QUERY}`, `${CLEAN_URL}/`],
    [`/settings/ynab/callback#${QUERY}`, "/settings/ynab/callback"],
    [`/settings/ynab/callback?${QUERY}#top`, "/settings/ynab/callback"],
    [
      `/settings/ynab/callback?error=access_denied&state=${STATE}`,
      "/settings/ynab/callback",
    ],
    [
      `Failed to load ${ARRIVAL_URL} (timeout)`,
      `Failed to load ${CLEAN_URL} (timeout)`,
    ],
    [
      `GET ${ARRIVAL_URL} then ${ARRIVAL_PATH}`,
      `GET ${CLEAN_URL} then /settings/ynab/callback`,
    ],
    [
      `/login?returnTo=${encodeURIComponent(ARRIVAL_PATH)}&x=1`,
      "/login?returnTo=%2Fsettings%2Fynab%2Fcallback&x=1",
    ],
  ])("removes the query and fragment from %s", (input, expected) => {
    expect(scrubYnabCallbackUrl(input)).toBe(expected);
  });

  it.each([
    "/settings/ynab/callback",
    "/settings/ynab?tab=mappings",
    "/receipts?filter=open#row-3",
    `${ORIGIN}/search?q=settings/ynab`,
    // The API endpoint is a different address and carries no query.
    `${ORIGIN}/api/ynab/oauth/callback`,
    "",
  ])("leaves %s untouched", (input) => {
    expect(scrubYnabCallbackUrl(input)).toBe(input);
  });
});

describe("scrubYnabCallbackEvent", () => {
  it("scrubs an error event: request, transaction name, tags, breadcrumbs, exception and extra", () => {
    const event: ErrorEvent = {
      type: undefined,
      message: `Unhandled at ${ARRIVAL_URL}`,
      transaction: ARRIVAL_PATH,
      request: {
        url: ARRIVAL_URL,
        query_string: QUERY,
        headers: { Referer: ARRIVAL_URL, "User-Agent": "test" },
      },
      tags: { url: ARRIVAL_URL, route: "/settings/ynab/callback" },
      extra: { location: { href: ARRIVAL_URL }, visited: [ARRIVAL_PATH] },
      exception: {
        values: [{ type: "Error", value: `Request to ${ARRIVAL_URL} failed` }],
      },
      breadcrumbs: [
        {
          category: "navigation",
          data: { from: ARRIVAL_PATH, to: "/settings/ynab/callback" },
        },
        {
          category: "fetch",
          data: { url: ARRIVAL_URL, "http.query": `?${QUERY}`, method: "GET" },
        },
      ],
    };

    const scrubbed = scrubYnabCallbackEvent(event);

    expectNoSecrets(scrubbed);
    expect(scrubbed.request).toEqual({
      url: CLEAN_URL,
      headers: { Referer: CLEAN_URL, "User-Agent": "test" },
    });
    expect(scrubbed.transaction).toBe("/settings/ynab/callback");
    expect(scrubbed.tags).toEqual({
      url: CLEAN_URL,
      route: "/settings/ynab/callback",
    });
    expect(scrubbed.breadcrumbs).toEqual([
      {
        category: "navigation",
        data: {
          from: "/settings/ynab/callback",
          to: "/settings/ynab/callback",
        },
      },
      { category: "fetch", data: { url: CLEAN_URL, method: "GET" } },
    ]);
    expect(scrubbed.exception?.values?.[0].value).toBe(
      `Request to ${CLEAN_URL} failed`,
    );
  });

  it("scrubs a transaction: request, trace context data, and each span's description and URL attributes", () => {
    const span = (overrides: Partial<SpanJSON>): SpanJSON => ({
      span_id: "a1",
      trace_id: "t1",
      start_timestamp: 1,
      data: {},
      ...overrides,
    });
    const event: TransactionEvent = {
      type: "transaction",
      transaction: "/settings/ynab/callback",
      request: { url: ARRIVAL_URL, query_string: { code: CODE, state: STATE } },
      contexts: {
        trace: {
          span_id: "root",
          trace_id: "t1",
          op: "pageload",
          origin: "auto.pageload.react.reactrouter_v7",
          data: {
            "sentry.origin": "auto.pageload.react.reactrouter_v7",
            "sentry.source": "route",
            url: ARRIVAL_URL,
            "url.full": ARRIVAL_URL,
            "url.query": `?${QUERY}`,
          },
        },
      },
      spans: [
        span({
          op: "browser.request",
          description: ARRIVAL_URL,
          origin: "auto.ui.browser.metrics",
          data: { "sentry.origin": "auto.ui.browser.metrics" },
        }),
        span({
          op: "http.client",
          description: `GET ${ARRIVAL_URL}`,
          data: {
            url: ARRIVAL_URL,
            "http.url": ARRIVAL_URL,
            "url.full": ARRIVAL_URL,
            "http.query": `?${QUERY}`,
            "http.fragment": `#${QUERY}`,
            "http.method": "GET",
            "server.address": "receipts.example.com",
          },
        }),
        span({
          op: "http.client",
          description: `POST ${ORIGIN}/api/ynab/oauth/callback`,
          data: {
            url: `${ORIGIN}/api/ynab/oauth/callback`,
            "http.method": "POST",
          },
        }),
      ],
    };

    const scrubbed = scrubYnabCallbackEvent(event);

    expectNoSecrets(scrubbed);
    expect(scrubbed.request).toEqual({ url: CLEAN_URL });
    expect(scrubbed.contexts?.trace?.data).toEqual({
      "sentry.origin": "auto.pageload.react.reactrouter_v7",
      "sentry.source": "route",
      url: CLEAN_URL,
      "url.full": CLEAN_URL,
    });
    expect(scrubbed.spans?.[0].description).toBe(CLEAN_URL);
    expect(scrubbed.spans?.[1]).toEqual(
      expect.objectContaining({
        description: `GET ${CLEAN_URL}`,
        data: {
          url: CLEAN_URL,
          "http.url": CLEAN_URL,
          "url.full": CLEAN_URL,
          "http.method": "GET",
          "server.address": "receipts.example.com",
        },
      }),
    );
    // The API request is another address and is left as it was.
    expect(scrubbed.spans?.[2].data).toEqual({
      url: `${ORIGIN}/api/ynab/oauth/callback`,
      "http.method": "POST",
    });
  });

  it("scrubs the addresses a replay event lists", () => {
    // The shape the replay integration sends (`ReplayEvent` in @sentry/core,
    // which @sentry/react does not re-export).
    const event: Event & {
      urls: string[];
      replay_id: string;
      segment_id: number;
      replay_type: "session" | "buffer";
      error_ids: string[];
      trace_ids: string[];
    } = {
      type: "replay_event",
      replay_id: "r1",
      segment_id: 0,
      replay_type: "session",
      error_ids: [],
      trace_ids: [],
      urls: [ARRIVAL_URL, `${ORIGIN}/settings/ynab`],
      request: { url: ARRIVAL_URL },
    };

    const scrubbed = scrubYnabCallbackEvent(event);

    expectNoSecrets(scrubbed);
    expect(scrubbed.urls).toEqual([CLEAN_URL, `${ORIGIN}/settings/ynab`]);
  });

  it("scrubs an arrival that only reports a declined request", () => {
    const declined = `${ORIGIN}/settings/ynab/callback?error=access_denied&state=${STATE}`;
    const event: ErrorEvent = {
      type: undefined,
      request: { url: declined, query_string: `error=access_denied&state=${STATE}` },
      breadcrumbs: [{ category: "navigation", data: { from: declined, to: "/" } }],
    };

    const scrubbed = scrubYnabCallbackEvent(event);

    expectNoSecrets(scrubbed);
    expect(scrubbed.request).toEqual({ url: CLEAN_URL });
  });

  it("leaves an event for a different route exactly as it was", () => {
    const build = (): TransactionEvent => ({
      type: "transaction",
      transaction: "/receipts",
      request: {
        url: `${ORIGIN}/receipts?filter=open#row-3`,
        query_string: "filter=open",
      },
      tags: { url: `${ORIGIN}/settings/ynab?tab=mappings` },
      breadcrumbs: [
        { category: "navigation", data: { from: "/search?q=milk", to: "/receipts?filter=open" } },
      ],
      spans: [
        {
          span_id: "a1",
          trace_id: "t1",
          start_timestamp: 1,
          description: `GET ${ORIGIN}/api/receipts?offset=0&limit=50`,
          data: {
            url: `${ORIGIN}/api/receipts?offset=0&limit=50`,
            "http.query": "?offset=0&limit=50",
          },
        },
      ],
    });

    expect(scrubYnabCallbackEvent(build())).toEqual(build());
  });

  it("does not walk or alter objects that are not plain data", () => {
    class Holder {
      href = ARRIVAL_URL;
    }
    const holder = new Holder();
    const cyclic: Record<string, unknown> = { url: ARRIVAL_URL };
    cyclic.self = cyclic;
    const event: ErrorEvent = { type: undefined, extra: { holder, cyclic } };

    scrubYnabCallbackEvent(event);

    expect(holder.href).toBe(ARRIVAL_URL);
    expect(cyclic.url).toBe(CLEAN_URL);
  });
});

describe("scrubYnabCallbackSpan", () => {
  it("scrubs a span sent on its own", () => {
    const scrubbed = scrubYnabCallbackSpan({
      span_id: "a1",
      trace_id: "t1",
      start_timestamp: 1,
      op: "ui.interaction.click",
      description: ARRIVAL_URL,
      data: { "url.full": ARRIVAL_URL, "url.query": `?${QUERY}` },
    });

    expectNoSecrets(scrubbed);
    expect(scrubbed.description).toBe(CLEAN_URL);
    expect(scrubbed.data).toEqual({ "url.full": CLEAN_URL });
  });
});

describe("scrubYnabCallbackReplayEvent", () => {
  it("scrubs a recorded history navigation", () => {
    const event: ReplayFrameEvent = {
      type: 5,
      timestamp: 1,
      data: {
        tag: "performanceSpan",
        payload: {
          op: "navigation.push",
          description: "/settings/ynab/callback",
          startTimestamp: 1,
          endTimestamp: 1,
          data: { previous: ARRIVAL_PATH },
        },
      },
    };

    const scrubbed = scrubYnabCallbackReplayEvent(event);

    expectNoSecrets(scrubbed);
    expect(scrubbed.data.payload).toEqual(
      expect.objectContaining({
        description: "/settings/ynab/callback",
        data: { previous: "/settings/ynab/callback" },
      }),
    );
  });

  it("scrubs the page-load performance entry, a network entry and a replay breadcrumb", () => {
    const events: ReplayFrameEvent[] = [
      {
        type: 5,
        timestamp: 1,
        data: {
          tag: "performanceSpan",
          payload: {
            op: "navigation.navigate",
            description: ARRIVAL_URL,
            startTimestamp: 1,
            endTimestamp: 2,
            data: { size: 1200, duration: 80 },
          },
        },
      },
      {
        type: 5,
        timestamp: 2,
        data: {
          tag: "performanceSpan",
          payload: {
            op: "resource.fetch",
            description: ARRIVAL_URL,
            startTimestamp: 2,
            endTimestamp: 3,
            data: {
              method: "GET",
              statusCode: 200,
              request: { headers: { referer: ARRIVAL_URL } },
            },
          },
        },
      },
      {
        type: 5,
        timestamp: 3,
        data: {
          tag: "breadcrumb",
          payload: {
            timestamp: 3,
            type: "default",
            category: "navigation",
            data: { from: ARRIVAL_PATH, to: "/settings/ynab" },
          },
        },
      },
    ];

    const scrubbed = events.map(scrubYnabCallbackReplayEvent);

    expectNoSecrets(scrubbed);
    expect(scrubbed[0].data.payload).toEqual(
      expect.objectContaining({ description: CLEAN_URL }),
    );
    expect(scrubbed[1].data.payload).toEqual(
      expect.objectContaining({
        description: CLEAN_URL,
        data: {
          method: "GET",
          statusCode: 200,
          request: { headers: { referer: CLEAN_URL } },
        },
      }),
    );
  });

  it("leaves a recording event for another route exactly as it was", () => {
    const build = (): ReplayFrameEvent => ({
      type: 5,
      timestamp: 1,
      data: {
        tag: "performanceSpan",
        payload: {
          op: "navigation.push",
          description: "/receipts?filter=open",
          startTimestamp: 1,
          endTimestamp: 1,
          data: { previous: "/search?q=milk#top" },
        },
      },
    });

    expect(scrubYnabCallbackReplayEvent(build())).toEqual(build());
  });
});

describe("scrubYnabCallbackBreadcrumb", () => {
  it("drops the code and state from a navigation away from the YNAB callback address", () => {
    const original = {
      category: "navigation",
      data: { from: ARRIVAL_PATH, to: "/settings/ynab/callback" },
    };

    const scrubbed = scrubYnabCallbackBreadcrumb(original);

    expect(scrubbed.data).toEqual({
      from: "/settings/ynab/callback",
      to: "/settings/ynab/callback",
    });
    expectNoSecrets(scrubbed);
    // The caller's object is not altered.
    expect(original.data.from).toBe(ARRIVAL_PATH);
  });

  it("drops them from an absolute address, a fragment, a detached query and the message", () => {
    const scrubbed = scrubYnabCallbackBreadcrumb({
      category: "fetch",
      message: `GET ${ARRIVAL_URL}`,
      data: {
        to: ARRIVAL_URL,
        url: `/settings/ynab/callback#${QUERY}`,
        "http.query": `?${QUERY}`,
        "http.fragment": `#${QUERY}`,
        status_code: 200,
      },
    });

    expectNoSecrets(scrubbed);
    expect(scrubbed).toEqual({
      category: "fetch",
      message: `GET ${CLEAN_URL}`,
      data: {
        to: CLEAN_URL,
        url: "/settings/ynab/callback",
        status_code: 200,
      },
    });
  });

  it("returns other breadcrumbs unchanged", () => {
    const navigation = {
      category: "navigation",
      data: { from: "/receipts?filter=open", to: "/search?q=milk" },
    };
    const request = {
      category: "fetch",
      data: {
        url: "/api/receipts?offset=0",
        "http.query": "?offset=0",
      },
    };
    const message = { category: "console", message: "hello" };

    expect(scrubYnabCallbackBreadcrumb(navigation)).toBe(navigation);
    expect(scrubYnabCallbackBreadcrumb(request)).toBe(request);
    expect(scrubYnabCallbackBreadcrumb(message)).toBe(message);
  });
});

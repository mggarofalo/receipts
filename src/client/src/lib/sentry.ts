import * as Sentry from "@sentry/react";
import { useEffect } from "react";
import {
  useLocation,
  useNavigationType,
  createRoutesFromChildren,
  matchRoutes,
  createBrowserRouter,
} from "react-router";
import {
  scrubYnabCallbackBreadcrumb,
  scrubYnabCallbackEvent,
  scrubYnabCallbackReplayEvent,
  scrubYnabCallbackSpan,
} from "@/lib/sentry-scrub";
import { captureYnabCallbackArrival } from "@/lib/ynab-callback-arrival";

const dsn = import.meta.env.VITE_SENTRY_DSN as string | undefined;

// Before anything below reads `window.location`: take the authorization code
// and state out of the address YNAB returned the browser to. Session replay's
// page metadata copies `location.href` and offers no hook to rewrite it, so
// the address has to be clean before Sentry starts. This must stay ahead of
// `Sentry.init` and of the router's creation in main.tsx.
captureYnabCallbackArrival();

if (dsn) {
  Sentry.init({
    dsn,
    // Second line of defence for the same address: rewrite whatever is about
    // to be sent. See sentry-scrub.ts.
    beforeBreadcrumb: scrubYnabCallbackBreadcrumb,
    beforeSend: scrubYnabCallbackEvent,
    beforeSendTransaction: scrubYnabCallbackEvent,
    beforeSendSpan: scrubYnabCallbackSpan,
    environment: (import.meta.env.VITE_SENTRY_ENVIRONMENT as string) || "development",
    release: `receipts-frontend@${__APP_VERSION__}`,

    integrations: [
      Sentry.reactRouterV7BrowserTracingIntegration({
        useEffect,
        useLocation,
        useNavigationType,
        createRoutesFromChildren,
        matchRoutes,
      }),
      Sentry.replayIntegration({
        maskAllText: true,
        blockAllMedia: true,
        beforeAddRecordingEvent: scrubYnabCallbackReplayEvent,
      }),
      Sentry.feedbackIntegration({
        autoInject: true,
        colorScheme: "system",
      }),
    ],

    tracesSampleRate: import.meta.env.PROD ? 0.2 : 1.0,
    replaysSessionSampleRate: 0.01,
    replaysOnErrorSampleRate: 1.0,
  });
  // `beforeSend*` do not see replay or feedback events; an event processor
  // sees every event type (a replay event lists the addresses it visited).
  Sentry.addEventProcessor(scrubYnabCallbackEvent);
}

export const sentryCreateBrowserRouter = dsn
  ? Sentry.wrapCreateBrowserRouterV7(createBrowserRouter)
  : createBrowserRouter;

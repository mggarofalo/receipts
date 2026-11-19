import { useCallback, useEffect, useRef, useState } from "react";
import { Link, useLocation, useNavigate } from "react-router";
import { toast } from "sonner";
import { usePageTitle } from "@/hooks/usePageTitle";
import { usePermission } from "@/hooks/usePermission";
import { useCompleteYnabAuthorization } from "@/hooks/useYnabConnection";
import {
  extractErrorMessage,
  parseProblemDetails,
} from "@/lib/problem-details";
import {
  clearYnabCallbackArrival,
  parseYnabCallbackArrival,
  peekYnabCallbackArrival,
} from "@/lib/ynab-callback-arrival";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Spinner } from "@/components/ui/spinner";
import { PageHead } from "@/components/primitives";

const SETTINGS_PATH = "/settings/ynab";
const NOT_VALID_MESSAGE =
  "This YNAB authorization attempt is not valid. Start again from YNAB settings.";

type Phase =
  | { kind: "working" }
  /** A definite refusal: this code and state cannot be used again. */
  | { kind: "rejected"; message: string }
  /** YNAB or the server could not be reached; the same pair may be re-posted. */
  | { kind: "unavailable" };

function classifyFailure(error: unknown): Phase {
  const status = parseProblemDetails(error)?.status;
  if (status === 403) {
    return {
      kind: "rejected",
      message: "Only an administrator can connect YNAB.",
    };
  }
  if (
    typeof status === "number" &&
    status >= 400 &&
    status < 500 &&
    status !== 408 &&
    status !== 429
  ) {
    return {
      kind: "rejected",
      message: extractErrorMessage(error) ?? NOT_VALID_MESSAGE,
    };
  }
  return { kind: "unavailable" };
}

function BackToSettings({ label }: { label: string }) {
  return (
    <Link to={SETTINGS_PATH} className="text-primary hover:underline">
      {label}
    </Link>
  );
}

/**
 * Landing route for YNAB's OAuth redirect (`/settings/ynab/callback`).
 *
 * The authorization code and state arrive in the query string. On a page load
 * `captureYnabCallbackArrival` (run from lib/sentry.ts at startup) moves them
 * into memory and rewrites the address; otherwise this page copies them into
 * component state on the first render and replaces the address at once. Either
 * way neither stays in the address bar or in session history, and they are
 * never rendered or logged.
 */
export default function YnabOAuthCallback() {
  usePageTitle("YNAB connection");
  const location = useLocation();
  const navigate = useNavigate();
  const { isAdmin } = usePermission();
  const { mutateAsync: completeAuthorization } =
    useCompleteYnabAuthorization();

  // Captured once: after the address is replaced the query string is gone, and
  // a retry still needs the same pair. On a real return from YNAB the values
  // were already taken out of the address at startup (before error monitoring
  // could read it); the query string is the fallback for an in-app navigation.
  // The initializer only reads, because StrictMode runs it twice.
  const [arrival] = useState(
    () => peekYnabCallbackArrival() ?? parseYnabCallbackArrival(location.search),
  );
  useEffect(() => {
    // This component's state now owns the values; a later visit to this route
    // without a page load must not find them again.
    clearYnabCallbackArrival();
  }, []);
  const admin = isAdmin();
  const canComplete =
    admin && !arrival.declined && !!arrival.code && !!arrival.state;

  const [phase, setPhase] = useState<Phase>({ kind: "working" });

  // Synchronises the browser's address/history with the captured values.
  const hasQuery = location.search !== "" || location.hash !== "";
  useEffect(() => {
    if (hasQuery) {
      void navigate(
        { pathname: location.pathname, search: "", hash: "" },
        { replace: true },
      );
    }
  }, [hasQuery, location.pathname, navigate]);

  const mountedRef = useRef(false);
  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  const submit = useCallback(() => {
    if (!arrival.code || !arrival.state) return;
    // The promise, not the mutation observer, drives this page: StrictMode's
    // simulated unmount detaches the observer from a mutation started on mount.
    completeAuthorization({ code: arrival.code, state: arrival.state }).then(
      () => {
        if (!mountedRef.current) return;
        toast.success("Connected to YNAB");
        void navigate(SETTINGS_PATH, { replace: true });
      },
      (error: unknown) => {
        if (!mountedRef.current) return;
        setPhase(classifyFailure(error));
      },
    );
  }, [arrival.code, arrival.state, completeAuthorization, navigate]);

  // Arriving from YNAB is the event; there is no user gesture to attach the
  // request to. The server treats an attempt as single-use, so a second POST
  // would be refused with 400. StrictMode re-runs this Effect on mount (refs
  // survive that), so the ref makes the automatic POST happen exactly once.
  // Only the Retry button may send the pair again.
  const postedRef = useRef(false);
  useEffect(() => {
    if (!canComplete || postedRef.current) return;
    postedRef.current = true;
    submit();
  }, [canComplete, submit]);

  function handleRetry() {
    if (phase.kind !== "unavailable") return;
    setPhase({ kind: "working" });
    submit();
  }

  let body;
  if (arrival.declined) {
    body = (
      <Alert>
        <AlertDescription>
          <p>
            The connection was not made. YNAB reported that the request was not
            approved, so nothing has changed.
          </p>
          <BackToSettings label="Back to YNAB settings" />
        </AlertDescription>
      </Alert>
    );
  } else if (!arrival.code || !arrival.state) {
    body = (
      <Alert variant="destructive">
        <AlertDescription>
          <p>{NOT_VALID_MESSAGE}</p>
          <BackToSettings label="Back to YNAB settings" />
        </AlertDescription>
      </Alert>
    );
  } else if (!admin) {
    body = (
      <Alert variant="destructive">
        <AlertDescription>
          <p>Only an administrator can connect YNAB.</p>
          <BackToSettings label="Back to YNAB settings" />
        </AlertDescription>
      </Alert>
    );
  } else if (phase.kind === "rejected") {
    body = (
      <Alert variant="destructive">
        <AlertDescription>
          <p>{phase.message}</p>
          <BackToSettings label="Start again in YNAB settings" />
        </AlertDescription>
      </Alert>
    );
  } else if (phase.kind === "unavailable") {
    body = (
      <Alert variant="destructive">
        <AlertDescription>
          <p>
            YNAB could not be reached, so the connection is not finished yet.
            You can try again without starting over.
          </p>
          <div className="flex flex-wrap items-center gap-4">
            <Button type="button" variant="outline" size="sm" onClick={handleRetry}>
              Retry
            </Button>
            <BackToSettings label="Back to YNAB settings" />
          </div>
        </AlertDescription>
      </Alert>
    );
  } else {
    body = (
      <div className="flex items-center gap-2" role="status" aria-live="polite">
        <Spinner className="h-4 w-4" />
        <span className="text-sm text-muted-foreground">
          Finishing the connection to YNAB…
        </span>
      </div>
    );
  }

  return (
    <>
      <PageHead title="YNAB connection" />
      <Card>
        <CardHeader>
          <CardTitle>Connect to YNAB</CardTitle>
          <CardDescription>
            The outcome of the request made at YNAB.
          </CardDescription>
        </CardHeader>
        <CardContent>{body}</CardContent>
      </Card>
    </>
  );
}

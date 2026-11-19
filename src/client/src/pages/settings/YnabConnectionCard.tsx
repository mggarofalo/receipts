import { useState, type ReactNode } from "react";
import { Link } from "react-router";
import { RequestFailure } from "@/components/RequestFailure";
import { usePermission } from "@/hooks/usePermission";
import type { YnabCredentialSource } from "@/hooks/useYnab";
import {
  useDisconnectYnab,
  useStartYnabAuthorization,
  type YnabRemoteRevocation,
} from "@/hooks/useYnabConnection";
import { extractErrorMessage } from "@/lib/problem-details";
import { navigateToExternal } from "@/lib/external-navigation";
import { formatDate } from "@/lib/format";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Alert, AlertDescription } from "@/components/ui/alert";
import {
  AlertDialog,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";

function formatRelativeTime(dateStr: string): string {
  const diffMs = Date.now() - new Date(dateStr).getTime();
  const diffMin = Math.floor(diffMs / 60000);
  const diffHrs = Math.floor(diffMs / 3600000);
  const diffDays = Math.floor(diffMs / 86400000);

  if (diffMin < 1) return "just now";
  if (diffMin < 60) return `${diffMin}m ago`;
  if (diffHrs < 24) return `${diffHrs}h ago`;
  return `${diffDays}d ago`;
}

const DISCONNECT_OUTCOME: Record<
  YnabRemoteRevocation,
  { message: string; needsAttention: boolean }
> = {
  succeeded: {
    message:
      "YNAB is disconnected. The stored connection was removed and YNAB accepted the request to revoke this application's access.",
    needsAttention: false,
  },
  failed: {
    message:
      "The stored connection was removed, but YNAB could not be asked to revoke access. Remove this application's access in YNAB's account settings.",
    needsAttention: true,
  },
  notAttempted: {
    message:
      "YNAB is disconnected. YNAB was not asked to revoke access; if this application still appears in YNAB's account settings, remove its access there.",
    needsAttention: false,
  },
};

export interface YnabConnectionCardProps {
  isLoading: boolean;
  isError: boolean;
  isFetching: boolean;
  retry: () => void;
  isConfigured: boolean;
  isConnected: boolean;
  credentialSource: YnabCredentialSource;
  oauthAvailable: boolean;
  reconnectRequired: boolean;
  oauthConnectedAtUtc: string | null;
  lastSuccessfulSyncUtc: string | null;
}

/**
 * Connection state for the deployment-wide YNAB credential, plus the action
 * that fits it. Only an Admin is offered connect, reconnect or disconnect; the
 * server enforces the same rule.
 */
export function YnabConnectionCard({
  isLoading,
  isError,
  isFetching,
  retry,
  isConfigured,
  isConnected,
  credentialSource,
  oauthAvailable,
  reconnectRequired,
  oauthConnectedAtUtc,
  lastSuccessfulSyncUtc,
}: YnabConnectionCardProps) {
  const { isAdmin } = usePermission();
  const admin = isAdmin();
  const startAuthorization = useStartYnabAuthorization();
  const disconnect = useDisconnectYnab();
  const [confirmingDisconnect, setConfirmingDisconnect] = useState(false);

  const busy = startAuthorization.isPending || disconnect.isPending;

  function handleConnect() {
    if (!admin || busy) return;
    startAuthorization.mutate(undefined, {
      onSuccess: ({ authorizationUrl }) => {
        navigateToExternal(authorizationUrl);
      },
    });
  }

  function handleConfirmDisconnect() {
    setConfirmingDisconnect(false);
    if (!admin || busy) return;
    disconnect.mutate();
  }

  const lastSync = lastSuccessfulSyncUtc
    ? `Last sync: ${formatRelativeTime(lastSuccessfulSyncUtc)}`
    : "No syncs yet";

  let state: ReactNode;
  let canConnect = false;
  let connectLabel = "Connect to YNAB";
  let canDisconnect = false;
  let adminHint: string | null = null;

  if (reconnectRequired) {
    canConnect = oauthAvailable;
    connectLabel = "Reconnect";
    canDisconnect = true;
    adminHint = "An administrator needs to reconnect YNAB.";
    state = (
      <div className="flex flex-wrap items-center gap-3">
        <Badge variant="destructive">Reconnect required</Badge>
        <span className="min-w-[16rem] flex-1 text-sm text-muted-foreground">
          YNAB no longer accepts the stored connection, so YNAB features are
          unavailable until an administrator reconnects. Disconnect clears this
          state instead.
        </span>
      </div>
    );
  } else if (credentialSource === "oauth") {
    canDisconnect = true;
    state = isConnected ? (
      <div className="flex flex-wrap items-center gap-3">
        <Badge className="bg-green-100 text-green-800 hover:bg-green-100 border-green-300">
          Connected
        </Badge>
        <span className="text-sm text-muted-foreground">
          Connected with OAuth
          {oauthConnectedAtUtc
            ? ` on ${formatDate(oauthConnectedAtUtc)}`
            : ""}
        </span>
        <span className="text-sm text-muted-foreground">{lastSync}</span>
      </div>
    ) : (
      <div className="flex flex-wrap items-center gap-3">
        <Badge variant="destructive">Disconnected</Badge>
        <span className="min-w-[16rem] flex-1 text-sm text-muted-foreground">
          Connected with OAuth
          {oauthConnectedAtUtc
            ? ` on ${formatDate(oauthConnectedAtUtc)}`
            : ""}
          , but YNAB could not be reached just now.
        </span>
      </div>
    );
  } else if (isConfigured) {
    canConnect = oauthAvailable;
    if (oauthAvailable) {
      adminHint =
        "An administrator can connect with OAuth, the recommended replacement for the personal access token.";
    }
    state = isConnected ? (
      <div className="flex flex-wrap items-center gap-3">
        <Badge className="bg-green-100 text-green-800 hover:bg-green-100 border-green-300">
          Connected
        </Badge>
        <span className="text-sm text-muted-foreground">
          Connected with a personal access token (<code>YNAB_PAT</code>)
        </span>
        <span className="text-sm text-muted-foreground">{lastSync}</span>
      </div>
    ) : (
      <div className="flex flex-wrap items-center gap-3">
        <Badge variant="destructive">Disconnected</Badge>
        <span className="min-w-[16rem] flex-1 text-sm text-muted-foreground">
          The <code>YNAB_PAT</code> personal access token is set but the
          connection failed. Check the token.
        </span>
      </div>
    );
  } else if (oauthAvailable) {
    canConnect = true;
    adminHint = "An administrator can connect YNAB.";
    state = (
      <div className="flex flex-wrap items-center gap-3">
        <Badge variant="outline" className="text-muted-foreground">
          Not connected
        </Badge>
        <span className="min-w-[16rem] flex-1 text-sm text-muted-foreground">
          YNAB is not connected yet.
        </span>
      </div>
    );
  } else {
    state = (
      <div className="flex flex-wrap items-center gap-3">
        <Badge variant="outline" className="text-muted-foreground">
          Not Configured
        </Badge>
        <span className="min-w-[16rem] flex-1 text-sm text-muted-foreground">
          The operator of this deployment enables the integration by adding the
          YNAB OAuth settings (recommended) or by setting the{" "}
          <code>YNAB_PAT</code> environment variable.
        </span>
      </div>
    );
  }

  const startError = startAuthorization.isError
    ? (extractErrorMessage(startAuthorization.error) ??
      "The YNAB connection could not be started. Try again.")
    : null;
  const disconnectError = disconnect.isError
    ? (extractErrorMessage(disconnect.error) ??
      "YNAB could not be disconnected. Try again.")
    : null;
  const disconnectOutcome = disconnect.data
    ? DISCONNECT_OUTCOME[disconnect.data.remoteRevocation]
    : null;

  return (
    <Card>
      <CardHeader>
        <CardTitle>Connection Status</CardTitle>
        <CardDescription>
          Current status of your YNAB integration.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {isError ? (
          <RequestFailure
            message="YNAB connection status is unavailable."
            retry={retry}
            isRetrying={isFetching}
          />
        ) : isLoading ? (
          <div className="flex items-center gap-2">
            <Spinner className="h-4 w-4" />
            <span className="text-sm text-muted-foreground">
              Checking connection...
            </span>
          </div>
        ) : (
          <>
            {state}
            {canConnect &&
              admin &&
              !reconnectRequired &&
              credentialSource === "personalAccessToken" && (
                <p className="text-sm text-muted-foreground">
                  Connecting with OAuth is recommended. Once connected, it is
                  used instead of the personal access token.
                </p>
              )}
            {!admin && adminHint && (
              <p className="text-sm text-muted-foreground">{adminHint}</p>
            )}
          </>
        )}

        {disconnectOutcome && (
          <Alert
            variant={disconnectOutcome.needsAttention ? "destructive" : "default"}
          >
            <AlertDescription>{disconnectOutcome.message}</AlertDescription>
          </Alert>
        )}
        {startError && (
          <Alert variant="destructive">
            <AlertDescription>{startError}</AlertDescription>
          </Alert>
        )}
        {disconnectError && (
          <Alert variant="destructive">
            <AlertDescription>{disconnectError}</AlertDescription>
          </Alert>
        )}

        <div className="flex flex-wrap items-center gap-4">
          {admin && !isError && !isLoading && canConnect && (
            <Button
              type="button"
              className="min-w-[10rem]"
              onClick={handleConnect}
              disabled={busy}
            >
              {startAuthorization.isPending ? "Opening YNAB…" : connectLabel}
            </Button>
          )}
          {admin && !isError && !isLoading && canDisconnect && (
            <Button
              type="button"
              variant="outline"
              className="min-w-[10rem]"
              onClick={() => setConfirmingDisconnect(true)}
              disabled={busy}
            >
              {disconnect.isPending ? "Disconnecting…" : "Disconnect"}
            </Button>
          )}
          <Link
            to="/privacy"
            className="min-w-[8rem] text-sm text-primary hover:underline"
          >
            Privacy policy
          </Link>
        </div>
      </CardContent>

      <AlertDialog
        open={confirmingDisconnect}
        onOpenChange={setConfirmingDisconnect}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Disconnect YNAB?</AlertDialogTitle>
            <AlertDialogDescription>
              This asks YNAB to revoke this application's access and removes the
              stored connection. YNAB features stop working for every user until
              an administrator connects again, unless a personal access token is
              configured for this deployment.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <Button
              type="button"
              variant="destructive"
              onClick={handleConfirmDisconnect}
            >
              Disconnect
            </Button>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </Card>
  );
}

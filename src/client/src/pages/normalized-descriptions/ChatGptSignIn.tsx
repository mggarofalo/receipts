import { useState } from "react";
import { useChatGptSignIn } from "@/hooks/useChatGptSignIn";
import { Button } from "@/components/ui/button";

const messages: Record<string, string> = {
  starting: "Preparing ChatGPT sign-in…",
  waiting:
    "Complete sign-in in the OpenAI window. This page checks for completion automatically.",
  success:
    "ChatGPT connected. Select a model below to request suggestions. Model access depends on your plan.",
  cancelled:
    "Sign-in cancelled. Credentials may already have been saved; refresh status or explicitly disconnect to remove them.",
  expired:
    "Sign-in expired. Confirm account ownership again to start a fresh sign-in.",
  rejected: "Sign-in was rejected. Confirm your account and try again.",
  busy: "Another ChatGPT operation is active. Wait for it to finish or cancel it before trying again.",
  reconnect:
    "ChatGPT needs a fresh sign-in. Confirm account ownership and reconnect.",
  restart:
    "The sign-in session is no longer available. Confirm account ownership to start again.",
  disconnected: "ChatGPT disconnected.",
};

export function ChatGptSignIn({ connected }: { connected: boolean }) {
  const signIn = useChatGptSignIn();
  const [consent, setConsent] = useState(false);
  const [confirmDisconnect, setConfirmDisconnect] = useState(false);
  const snapshot = signIn.snapshot;
  const begin = () => {
    const accepted = consent;
    setConsent(false);
    void (connected ? signIn.reconnect(accepted) : signIn.start(accepted));
  };
  return (
    <div className="space-y-3" aria-label="ChatGPT sign-in">
      {signIn.error && <p role="alert">{signIn.error}</p>}
      {signIn.pending && <p role="status">Updating ChatGPT connection…</p>}
      {snapshot && (
        <p role="status">
          {messages[snapshot.state] ??
            "Refresh connection status before trying again."}
        </p>
      )}
      {snapshot?.state === "success" && (
        <Button variant="link" asChild className="h-auto p-0">
          <a href="/admin/normalized-descriptions?tab=catalog">
            Set up post-save ChatGPT suggestions
          </a>
        </Button>
      )}
      {snapshot?.state === "waiting" &&
        snapshot.userCode &&
        snapshot.verificationUrl && (
          <div className="space-y-2 rounded-md bg-muted p-3">
            <p>
              Enter this code:{" "}
              <strong className="break-all font-mono select-all">
                {snapshot.userCode}
              </strong>
            </p>
            <Button asChild>
              <a
                href={snapshot.verificationUrl}
                target="_blank"
                rel="noopener noreferrer"
                referrerPolicy="no-referrer"
              >
                Open OpenAI sign-in
              </a>
            </Button>
            {snapshot.localExpiresAt && (
              <p className="text-sm">
                Runner-reported sign-in deadline:{" "}
                {new Date(snapshot.localExpiresAt).toLocaleTimeString()}.
                Completion is confirmed by the server.
              </p>
            )}
          </div>
        )}
      {signIn.active ? (
        <div className="space-y-2">
          <Button
            variant="outline"
            onClick={() => void signIn.cancel()}
            disabled={signIn.pending}
          >
            Cancel sign-in
          </Button>
          <p className="text-sm text-muted-foreground">
            Cancel stops this attempt. Credentials may already have been saved;
            use Disconnect ChatGPT to remove them.
          </p>
        </div>
      ) : (
        <>
          <label className="flex items-start gap-2 text-sm">
            <input
              type="checkbox"
              className="mt-1"
              checked={consent}
              disabled={signIn.pending}
              onChange={(event) => setConsent(event.target.checked)}
            />
            I own this ChatGPT account and authorize Receipts to use my plan.
          </label>
          <div className="flex flex-wrap gap-2">
            <Button onClick={begin} disabled={!consent || signIn.pending}>
              {connected ? "Reconnect ChatGPT" : "Sign in with ChatGPT"}
            </Button>
            {connected && (
              <Button
                variant="outline"
                onClick={() => setConfirmDisconnect(true)}
                disabled={signIn.pending}
              >
                Disconnect ChatGPT
              </Button>
            )}
          </div>
          {confirmDisconnect && (
            <div className="space-y-2 rounded-md border p-3">
              <p>
                Remove this server’s ChatGPT credentials? Sign in again to
                reconnect.
              </p>
              <div className="flex flex-wrap gap-2">
                <Button
                  variant="destructive"
                  disabled={signIn.pending}
                  onClick={() => {
                    setConfirmDisconnect(false);
                    setConsent(false);
                    void signIn.disconnect();
                  }}
                >
                  Confirm disconnect
                </Button>
                <Button
                  variant="ghost"
                  onClick={() => setConfirmDisconnect(false)}
                >
                  Keep connection
                </Button>
              </div>
            </div>
          )}
        </>
      )}
    </div>
  );
}

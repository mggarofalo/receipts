import { useSearchParams } from "react-router";
import {
  useAiConnection,
  useAiRun,
  useAiRuns,
} from "@/hooks/useNormalizationAi";
import { useAuth } from "@/hooks/useAuth";
import { Button } from "@/components/ui/button";
import { RequestFailure } from "@/components/RequestFailure";
import { AiRunReview } from "./AiRunReview";
import { ChatGptSignIn } from "./ChatGptSignIn";

export function ChatGptTab() {
  const connection = useAiConnection();
  const { user } = useAuth();
  const [params, setParams] = useSearchParams();
  const runId = params.get("aiRun");
  const owner = connection.data?.configured === true && connection.data.isOwner;
  const runs = useAiRuns(owner);
  const run = useAiRun(runId, owner);
  const openCatalog = () =>
    setParams((previous) => {
      const next = new URLSearchParams(previous);
      next.set("tab", "catalog");
      next.delete("aiRun");
      return next;
    });
  const selectRun = (id: string | null) =>
    setParams((previous) => {
      const next = new URLSearchParams(previous);
      if (id) next.set("aiRun", id);
      else next.delete("aiRun");
      return next;
    });
  if (connection.isPending)
    return <p role="status">Checking ChatGPT connection...</p>;
  if (connection.isError)
    return (
      <RequestFailure
        message="ChatGPT status could not be loaded."
        retry={() => void connection.refetch()}
        isRetrying={connection.isFetching}
      />
    );
  return (
    <div className="space-y-6">
      <section
        className="space-y-2 rounded-lg border p-4"
        aria-label="ChatGPT connection"
      >
        <h2 className="card-title">ChatGPT normalization</h2>
        <p>
          Connect your personal ChatGPT account here. Request and review name
          suggestions for source groups in Catalog.
        </p>
        {!connection.data?.configured ? (
          <>
            <p>
              ChatGPT normalization is not configured. Enable it on the server
              and designate the administrator who owns the connection.
            </p>
            <p className="break-all text-sm">Your user ID: {user?.userId}</p>
          </>
        ) : !connection.data.isOwner ? (
          <p>
            This connection belongs to the designated administrator. Sign in as
            that administrator to use it.
          </p>
        ) : (
          <>
            <p role="status">
              {connection.data.busy
                ? "ChatGPT is busy. You can follow or cancel existing runs below."
                : connection.data.credentialsPresent
                  ? "Credentials are present. Model access is confirmed when a request succeeds."
                  : "Sign in with your ChatGPT account to connect."}
            </p>
            {user?.roles.includes("Admin") && (
              <ChatGptSignIn
                key={user.userId}
                connected={connection.data.credentialsPresent}
              />
            )}
            <details id="chatgpt-connection-settings">
              <summary className="cursor-pointer">
                Connection settings and reconnect help
              </summary>
              <p className="text-sm">
                On the server, run{" "}
                <code className="break-all">
                  docker compose run --rm --no-deps app --chatgpt-login
                </code>{" "}
                and complete the device login in your browser. Use{" "}
                <code>--chatgpt-logout</code> on the server to disconnect.
              </p>
            </details>
            <Button
              variant="outline"
              onClick={() => void connection.refetch()}
              disabled={connection.isFetching}
            >
              Refresh connection status
            </Button>
          </>
        )}
      </section>
      {owner && (
        <>
          {runId && (
            <>
              <Button variant="ghost" onClick={() => selectRun(null)}>
                Close run
              </Button>
              {run.isError ? (
                <RequestFailure
                  message="This run could not be loaded."
                  retry={() => void run.refetch()}
                  isRetrying={run.isFetching}
                />
              ) : run.isPending ? (
                <p role="status">Loading run...</p>
              ) : (
                run.data &&
                (run.data.proposals.some((proposal) => proposal.source) ? (
                  <Button
                    onClick={() =>
                      setParams((previous) => {
                        const next = new URLSearchParams(previous);
                        next.set("tab", "catalog");
                        return next;
                      })
                    }
                  >
                    Review these source suggestions in Catalog
                  </Button>
                ) : (
                  <AiRunReview run={run.data} />
                ))
              )}
            </>
          )}
          {!runId && (
            <Button onClick={openCatalog}>
              Request suggestions in Catalog
            </Button>
          )}
          <section className="space-y-2" aria-label="Recent ChatGPT runs">
            <h2 className="card-title">Recent runs</h2>
            {runs.isError ? (
              <RequestFailure
                message="Run history could not be loaded."
                retry={() => void runs.refetch()}
                isRetrying={runs.isFetching}
              />
            ) : runs.isPending ? (
              <p>Loading history...</p>
            ) : !runs.data?.length ? (
              <p>No requests yet.</p>
            ) : (
              <ul className="space-y-2">
                {runs.data.map((item) => (
                  <li key={item.id}>
                    <Button
                      variant="outline"
                      className="h-auto whitespace-normal text-left"
                      onClick={() => selectRun(item.id)}
                    >
                      {new Date(item.createdAt).toLocaleString()} / {item.model}{" "}
                      / {item.state}
                    </Button>
                  </li>
                ))}
              </ul>
            )}
          </section>
        </>
      )}
    </div>
  );
}

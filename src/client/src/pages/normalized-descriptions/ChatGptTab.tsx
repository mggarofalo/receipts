import { useSearchParams } from "react-router";
import {
  useAiConnection,
  useAiRun,
  useAiRuns,
} from "@/hooks/useNormalizationAi";
import { useAuth } from "@/hooks/useAuth";
import { Button } from "@/components/ui/button";
import { RequestFailure } from "@/components/RequestFailure";
import { AiCandidateSelection } from "./AiCandidateSelection";
import { AiRunReview } from "./AiRunReview";

export function ChatGptTab() {
  const connection = useAiConnection();
  const { user } = useAuth();
  const [params, setParams] = useSearchParams();
  const runId = params.get("aiRun");
  const owner = connection.data?.configured === true && connection.data.isOwner;
  const runs = useAiRuns(owner);
  const run = useAiRun(runId, owner);
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
          Use your personal ChatGPT connection to suggest item names, then
          review before applying.
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
                  : "Sign in to ChatGPT on the container host to connect."}
            </p>
            <p className="text-sm">
              On the server, run{" "}
              <code className="break-all">
                docker compose run --rm --no-deps app --chatgpt-login
              </code>{" "}
              and complete the device login in your browser. Use{" "}
              <code>--chatgpt-logout</code> on the server to disconnect.
            </p>
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
                run.data && (
                  <AiRunReview
                    key={`${run.data.id}:${run.data.revision}`}
                    run={run.data}
                    onRetried={selectRun}
                  />
                )
              )}
            </>
          )}
          {(connection.data?.credentialsPresent || connection.data?.busy) && (
            <AiCandidateSelection
              key={connection.data.generation}
              models={connection.data.models}
              busy={connection.data.busy}
              onStarted={selectRun}
            />
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

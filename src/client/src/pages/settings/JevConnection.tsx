import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useSessionMutation } from "@/hooks/useSessionMutation";
import client from "@/lib/api-client";
import { Card, CardHeader, CardTitle, CardContent } from "@/components/ui/card";

import { localErrorPolicy } from "@/lib/request-error-policy";

const queryKey = ["jev", "connection"];
const statusLabels = {
  missing: "Not configured",
  disabled: "Disabled",
  configuredUnverified: "Not checked",
  connected: "Connected",
  invalid: "Invalid credentials",
  unavailable: "Unavailable",
};
export function JevConnection() {
  const cache = useQueryClient();
  const status = useQuery({
    queryKey,
    ...localErrorPolicy.query,
    queryFn: async ({ signal }) => {
      const { data, error } = await client.GET("/api/jev/status", {
        signal,
        ...localErrorPolicy.request,
      });
      if (error || !data) throw new Error("Connection status unavailable");
      return data;
    },
  });
  const check = useSessionMutation({
    ...localErrorPolicy.mutation,
    mutationFn: async () => {
      const { data, error } = await client.POST(
        "/api/jev/check",
        localErrorPolicy.request,
      );
      if (error || !data) throw new Error("Connection check unavailable");
      return data;
    },
    onSuccess: (data) => {
      cache.setQueryData(queryKey, data);
    },
  });
  const data = status.isError || status.isFetching ? undefined : status.data;
  const configured = data && !["missing", "disabled"].includes(data.state);
  return (
    <Card className="mt-6">
      <CardHeader>
        <CardTitle>Jev connection</CardTitle>
      </CardHeader>
      <CardContent className="space-y-3">
        <p className="text-sm text-muted-foreground">
          Classification assistance is not enabled.
        </p>
        <details className="text-sm">
          <summary className="cursor-pointer font-medium">Setup</summary>
          <p className="mt-2 text-muted-foreground">
            Create a Jev API key and set TYPESAFE_API_KEY in the API server
            environment. Restart the API, then check the connection.
          </p>
        </details>
        {status.isPending && <p role="status">Loading connection status…</p>}
        {status.isError && <p role="alert">Connection status unavailable.</p>}
        {data && (
          <p role="status">
            {check.isPending
              ? "Checking connection…"
              : check.isError
                ? "Unavailable"
                : statusLabels[data.state]}
          </p>
        )}
        {configured && (
          <button
            className="btn"
            disabled={check.isPending}
            onClick={() => check.mutate()}
          >
            Check connection
          </button>
        )}
        {check.isError && (
          <p role="alert">Connection check unavailable. Try again.</p>
        )}
        {data?.state === "connected" && !check.isPending && !check.isError && (
          <section aria-label="Available Jev models">
            <h3>Available models</h3>
            <ul>
              {data.models.map((model) => (
                <li key={model}>{model}</li>
              ))}
            </ul>
          </section>
        )}
      </CardContent>
    </Card>
  );
}

import { useState } from "react";
import { Link } from "react-router";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import type { components } from "@/generated/api";
import { usePageTitle } from "@/hooks/usePageTitle";
import { localErrorPolicy } from "@/lib/request-error-policy";
import { extractErrorMessage } from "@/lib/problem-details";
import { PageHead } from "@/components/primitives";
import { Button } from "@/components/ui/button";

type Settings = components["schemas"]["CategoryAssistanceSettings"];
type Draft = Omit<Settings, "revision">;

function toDraft(settings: Settings): Draft {
  return {
    enabled: settings.enabled,
    provider: settings.provider,
    model: settings.model,
    dailyLimit: settings.dailyLimit,
    pendingLimit: settings.pendingLimit,
  };
}

export default function CategoryAssistanceSettings() {
  usePageTitle("Category Assistance Settings");
  const cache = useQueryClient();
  const [draft, setDraft] = useState<Draft>();
  const [revision, setRevision] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [conflict, setConflict] = useState(false);
  const [message, setMessage] = useState<string>();
  const [activityOpen, setActivityOpen] = useState(false);
  const status = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["category-assistance", "status"],
    queryFn: async ({ signal }) => {
      const result = await client.GET("/api/category-assistance/status", {
        ...localErrorPolicy.request,
        signal,
      });
      if (result.error || !result.data)
        throw result.error ?? new Error("Status unavailable");
      return result.data;
    },
  });
  const settings = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["category-assistance", "settings"],
    queryFn: async ({ signal }) => {
      const result = await client.GET("/api/category-assistance/settings", {
        ...localErrorPolicy.request,
        signal,
      });
      if (result.error || !result.data)
        throw result.error ?? new Error("Settings unavailable");
      return result.data;
    },
  });
  const activity = useQuery({
    ...localErrorPolicy.query,
    queryKey: ["category-assistance", "activity"],
    enabled: activityOpen,
    queryFn: async ({ signal }) => {
      const result = await client.GET("/api/category-assistance/activity", {
        ...localErrorPolicy.request,
        signal,
      });
      if (result.error || !result.data)
        throw result.error ?? new Error("Activity unavailable");
      return result.data;
    },
  });
  // The initial server value seeds the form. Later query refreshes never overwrite edits.
  const values = draft ?? (settings.data ? toDraft(settings.data) : undefined);
  const expectedRevision = revision ?? settings.data?.revision;
  const capability = status.data;
  const providerReady = capability?.capability === "ready";
  const provider = providerReady ? capability.provider : undefined;
  const models = providerReady ? capability.models : [];
  const valid =
    !!values &&
    Number.isInteger(values.dailyLimit) &&
    values.dailyLimit >= 1 &&
    values.dailyLimit <= 100 &&
    Number.isInteger(values.pendingLimit) &&
    values.pendingLimit >= 1 &&
    values.pendingLimit <= 20 &&
    (!values.enabled ||
      (providerReady &&
        values.provider === provider &&
        !!values.model &&
        models.includes(values.model)));

  function edit(patch: Partial<Draft>) {
    if (!values) return;
    setDraft({ ...values, ...patch });
    setMessage(undefined);
  }

  async function refreshAfterConflict() {
    setBusy(true);
    try {
      const latest = await settings.refetch();
      if (!latest.data) throw latest.error ?? new Error("Settings unavailable");
      setRevision(latest.data.revision);
      setConflict(false);
      setMessage("Current settings loaded. Review your edits before saving again.");
    } catch (error) {
      setMessage(extractErrorMessage(error) ?? "Could not refresh settings.");
    } finally {
      setBusy(false);
    }
  }

  async function save() {
    if (!values || !expectedRevision || !valid || conflict || busy) return;
    setBusy(true);
    setMessage(undefined);
    try {
      const result = await client.PUT("/api/category-assistance/settings", {
        ...localErrorPolicy.request,
        body: { ...values, expectedRevision },
      });
      if (result.response.status === 409) {
        setConflict(true);
        setMessage("Settings changed elsewhere. Refresh the revision, review your edits, then save again.");
        return;
      }
      if (result.error || !result.data)
        throw result.error ?? new Error("Settings could not be saved");
      setDraft(toDraft(result.data));
      setRevision(result.data.revision);
      setMessage("Assistance settings saved.");
      await Promise.all([
        cache.invalidateQueries({ queryKey: ["category-assistance", "status"] }),
        cache.invalidateQueries({ queryKey: ["category-assistance", "suggestions"] }),
        cache.invalidateQueries({ queryKey: ["category-assistance", "settings"] }),
      ]);
    } catch (error) {
      setMessage(extractErrorMessage(error) ?? "Settings could not be saved.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="space-y-6">
      <PageHead title="Category Assistance Settings" />
      <p>Control deployment-wide category suggestion consent and limits. Suggestions still require review before receipt items change.</p>
      <Link className="underline" to="/classification">Back to classification</Link>
      {status.isPending && <p role="status">Checking provider capability…</p>}
      {status.isError && (
        <div role="alert">Provider capability could not be loaded. <Button variant="outline" onClick={() => void status.refetch()}>Retry status</Button></div>
      )}
      {capability && !providerReady && (
        <div role="status" className="rounded-lg border p-4 space-y-2">
          <p>Category suggestion provider is {capability.capability}. Suggestions are unavailable.</p>
          {capability.capability === "unknown" ? <p>Provider status is unknown. <Button variant="outline" onClick={() => void status.refetch()}>Retry status</Button></p> : <p>Configure the API server with <code>TYPESAFE_API_KEY</code> using .NET User Secrets for local development. See <a className="underline" href="https://github.com/mggarofalo/Receipts/blob/main/docs/jev-setup.md">Jev setup</a>.</p>}
        </div>
      )}
      {capability && providerReady && <p>Provider available: {capability.provider}. This capability does not verify a model request or guarantee suggestions.</p>}
      {settings.isPending && <p role="status">Loading settings…</p>}
      {settings.isError && (
        <div role="alert">Settings could not be loaded. <Button variant="outline" onClick={() => void settings.refetch()}>Retry settings</Button></div>
      )}
      {values && (
        <section aria-label="Assistance settings" className="max-w-xl space-y-4 rounded-lg border p-4">
          <label className="flex items-start gap-2">
            <input type="checkbox" checked={values.enabled} disabled={busy || (!providerReady && !values.enabled)} onChange={(event) => edit({ enabled: event.target.checked })} />
            <span>Enable automatic category suggestion requests (explicit consent)</span>
          </label>
          {providerReady && (
            <>
              <label className="block space-y-1">Provider
                <select className="block w-full rounded border p-2" value={values.provider ?? ""} disabled={busy} onChange={(event) => edit({ provider: event.target.value || null, model: null })}>
                  <option value="">Select provider</option>
                  <option value={provider}>{provider}</option>
                </select>
              </label>
              <label className="block space-y-1">Model
                <select className="block w-full rounded border p-2" value={values.model ?? ""} disabled={busy || values.provider !== provider} onChange={(event) => edit({ model: event.target.value || null })}>
                  <option value="">Select model</option>
                  {models.map((model) => <option key={model} value={model}>{model}</option>)}
                </select>
              </label>
            </>
          )}
          <label className="block space-y-1">Daily request limit (1–100)
            <input className="block w-full rounded border p-2" type="number" min={1} max={100} step={1} value={values.dailyLimit} disabled={busy} onChange={(event) => edit({ dailyLimit: Number(event.target.value) })} />
          </label>
          <label className="block space-y-1">Pending suggestion limit (1–20)
            <input className="block w-full rounded border p-2" type="number" min={1} max={20} step={1} value={values.pendingLimit} disabled={busy} onChange={(event) => edit({ pendingLimit: Number(event.target.value) })} />
          </label>
          {!valid && <p role="alert">Choose an available provider and model when enabling assistance, and keep both limits in range.</p>}
          {conflict && <Button variant="outline" disabled={busy} onClick={() => void refreshAfterConflict()}>Refresh revision</Button>}
          <Button disabled={!valid || busy || conflict || !expectedRevision} onClick={() => void save()}>{busy ? "Saving…" : "Save settings"}</Button>
          {message && <p role={conflict || !message.endsWith("saved.") ? "alert" : "status"}>{message}</p>}
        </section>
      )}
      <section className="space-y-3">
        <Button variant="outline" aria-expanded={activityOpen} onClick={() => setActivityOpen(!activityOpen)}>{activityOpen ? "Hide technical activity" : "Show technical activity"}</Button>
        {activityOpen && (
          <div className="rounded-lg border p-4 space-y-2">
            <h2 className="font-semibold">Technical activity</h2>
            {activity.isPending && <p role="status">Loading activity…</p>}
            {activity.isError && <div role="alert">Activity unavailable. <Button variant="outline" onClick={() => void activity.refetch()}>Retry activity</Button></div>}
            {activity.data && <dl className="grid grid-cols-2 gap-2 text-sm">
              <dt>Today's attempts</dt><dd>{activity.data.todayAttempts}</dd>
              <dt>Queued</dt><dd>{activity.data.queued}</dd>
              <dt>Dispatching</dt><dd>{activity.data.dispatching}</dd>
              <dt>Suggested</dt><dd>{activity.data.suggested}</dd>
              <dt>Abstained (no suggestion)</dt><dd>{activity.data.abstained}</dd>
              <dt>Failed (request error)</dt><dd>{activity.data.failed}</dd>
              <dt>Stale</dt><dd>{activity.data.stale}</dd>
              <dt>Indeterminate</dt><dd>{activity.data.indeterminate}</dd>
            </dl>}
          </div>
        )}
      </section>
    </div>
  );
}

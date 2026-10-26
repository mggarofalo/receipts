import { useCallback, useEffect, useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import client from "@/lib/api-client";
import { addSessionChangeListener, getSessionVersion } from "@/lib/auth";
import { localErrorPolicy } from "@/lib/request-error-policy";
import type { components } from "@/generated/api";

type Snapshot = components["schemas"]["ChatGptSignInResponse"];
const emptyId = "00000000-0000-0000-0000-000000000000";
const active = (value: Snapshot) =>
  ["starting", "waiting"].includes(value.state) ||
  (value.state === "busy" && !!value.id && value.id !== emptyId);

// Ceremony data intentionally bypasses both query and mutation caches.
export function useChatGptSignIn() {
  const cache = useQueryClient();
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const operation = useRef(0);
  const locked = useRef(false);
  const controller = useRef<AbortController | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const deadline = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const stop = useCallback(() => {
    operation.current++;
    controller.current?.abort();
    clearTimeout(timer.current);
    clearTimeout(deadline.current);
    locked.current = false;
  }, []);
  useEffect(() => {
    const unsubscribe = addSessionChangeListener(() => {
      stop();
      setSnapshot(null);
      setPending(false);
      setError(null);
    });
    return () => {
      unsubscribe();
      stop();
    };
  }, [stop]);

  const refresh = useCallback(() => {
    void cache.invalidateQueries({ queryKey: ["normalization-ai"] });
    void cache.invalidateQueries({
      queryKey: ["normalization-catalog", "inbox", "preferences"],
    });
  }, [cache]);

  async function execute(
    action: "sign-in" | "reconnect" | "cancel" | "disconnect",
    ownerConsent = false,
  ) {
    if (action !== "cancel" && locked.current) return;
    if (["sign-in", "reconnect"].includes(action) && !ownerConsent) return;
    const id = snapshot?.id;
    if (action === "cancel" && !id) return;
    stop();
    locked.current = true;
    const epoch = operation.current;
    const version = getSessionVersion();
    const abort = new AbortController();
    controller.current = abort;
    const current = () =>
      operation.current === epoch &&
      version === getSessionVersion() &&
      !abort.signal.aborted;
    setSnapshot(null);
    setPending(true);
    setError(null);
    const options = {
      ...localErrorPolicy.request,
      signal: abort.signal,
      cache: "no-store" as const,
      referrerPolicy: "no-referrer" as const,
    };
    function fail(status?: number) {
      if (!current()) return;
      stop();
      setSnapshot(null);
      setPending(false);
      setError(
        status === 401 || status === 403
          ? "Only the configured account-owner administrator can manage this connection. Refresh your session and connection status."
          : status === 409
            ? "ChatGPT is busy. Finish or cancel the active operation, then try again."
            : "ChatGPT sign-in could not be completed. Refresh connection status before trying again.",
      );
    }
    function accept(value: Snapshot) {
      if (!current()) return;
      clearTimeout(deadline.current);
      const waiting = value.state === "waiting";
      const safeUrl =
        value.verificationUrl === "https://auth.openai.com/codex/device";
      const expires = Date.parse(value.localExpiresAt ?? "");
      const showCode =
        waiting && safeUrl && Number.isFinite(expires) && expires > Date.now();
      setSnapshot({
        id: value.id,
        state: value.state,
        ...(showCode
          ? {
              verificationUrl: value.verificationUrl,
              userCode: value.userCode,
              localExpiresAt: value.localExpiresAt,
            }
          : {}),
      });
      setPending(false);
      locked.current = false;
      if (showCode)
        deadline.current = setTimeout(
          () => {
            if (current()) setSnapshot({ id: value.id, state: value.state });
          },
          Math.max(0, expires - Date.now()),
        );
      if (active(value)) {
        timer.current = setTimeout(() => {
          void poll(value.id);
        }, 1500);
      } else {
        clearTimeout(timer.current);
        if (value.state === "success" || action === "cancel") refresh();
      }
    }
    async function poll(attemptId: string) {
      try {
        const result = await client.GET(
          "/api/normalization-ai/connection/sign-in/{id}",
          {
            ...options,
            params: { path: { id: attemptId } },
          },
        );
        if (!result.response.ok || !result.data) fail(result.response.status);
        else accept(result.data);
      } catch {
        fail();
      }
    }
    try {
      if (action === "disconnect") {
        const result = await client.POST(
          "/api/normalization-ai/connection/disconnect",
          options,
        );
        if (!current()) return;
        if (!result.response.ok) {
          fail(result.response.status);
          return;
        }
        locked.current = false;
        setPending(false);
        setSnapshot({ id: emptyId, state: "disconnected" });
        refresh();
        return;
      }
      const result =
        action === "cancel"
          ? await client.POST(
              "/api/normalization-ai/connection/sign-in/{id}/cancel",
              { ...options, params: { path: { id: id! } } },
            )
          : action === "reconnect"
            ? await client.POST("/api/normalization-ai/connection/reconnect", {
                ...options,
                body: { ownerConsent },
              })
            : await client.POST("/api/normalization-ai/connection/sign-in", {
                ...options,
                body: { ownerConsent },
              });
      if (!result.response.ok || !result.data) fail(result.response.status);
      else accept(result.data);
    } catch {
      fail();
    }
  }
  return {
    snapshot,
    pending,
    error,
    active: snapshot ? active(snapshot) : false,
    start: (consent: boolean) => execute("sign-in", consent),
    reconnect: (consent: boolean) => execute("reconnect", consent),
    cancel: () => execute("cancel"),
    disconnect: () => execute("disconnect"),
  };
}

import { useEffect, useMemo, useRef, useState } from "react";
import * as signalR from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { invalidateDomainChange, invalidateAfterReconnect, invalidateAfterBackupImport, isDomainChange } from "@/lib/query-invalidation";
import { getAccessToken, parseJwtPayload, getSessionVersion, addSessionChangeListener } from "@/lib/auth";
import { apiUrl } from "@/lib/api-config";
import { getConnectionAccessToken } from "@/lib/token-refresh";
import { bufferToast, clearBufferedToasts, type ToastOrigin } from "@/lib/signalr-toast-buffer";
import {
  setConnectionId,
  getConnectionId,
} from "@/lib/signalr-connection";

export type SignalRConnectionState =
  | "connected"
  | "disconnected"
  | "reconnecting";

interface EntityChangeNotification {
  entityType: string;
  changeType: string;
  id: string | null;
  count?: number;
  userId?: string | null;
  authMethod?: string | null;
  connectionId?: string | null;
}

const displayNameMap: Record<string, string> = {
  receipt: "receipt",
  "receipt-item": "receipt item",
  transaction: "transaction",
  adjustment: "adjustment",
  card: "card",
  account: "account",
  category: "category",
  subcategory: "subcategory",
  "item-template": "item template",
  "backup-import": "backup",
};

function classifyOrigin(
  notification: EntityChangeNotification,
  myConnectionId: string | null,
  myUserId: string | null,
): ToastOrigin | null {
  // Same session — suppress toast entirely
  if (notification.connectionId && notification.connectionId === myConnectionId) {
    return null;
  }

  if (notification.userId && notification.userId === myUserId) {
    if (notification.authMethod === "apikey") {
      return "api-key";
    }
    return "other-session";
  }

  return "other-user";
}

export function useSignalR(enabled: boolean) {
  const queryClient = useQueryClient();
  const connectionRef = useRef<signalR.HubConnection | null>(null);
  const [connectionState, setConnectionState] =
    useState<SignalRConnectionState>("disconnected");

  useEffect(() => {
    if (!enabled) {
      return;
    }

    const sessionVersion = getSessionVersion();
    const lifetime = new AbortController();
    let active = true;
    let retryTimer: ReturnType<typeof setTimeout> | undefined;
    let retryDelay = 1_000;
    let starting = false;
    const isCurrent = () => active && getSessionVersion() === sessionVersion;
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(apiUrl("/hubs/entities"), {
        accessTokenFactory: () => getConnectionAccessToken(sessionVersion, lifetime.signal),
      })
      .withAutomaticReconnect()
      .configureLogging(
        import.meta.env.DEV ? signalR.LogLevel.Debug : signalR.LogLevel.None,
      )
      .build();

    const cancelRetry = () => {
      if (retryTimer !== undefined) clearTimeout(retryTimer);
      retryTimer = undefined;
    };

    const connected = () => {
      if (!isCurrent() || connection.state !== signalR.HubConnectionState.Connected) return;
      cancelRetry();
      retryDelay = 1_000;
      setConnectionId(connection.connectionId ?? null);
      setConnectionState("connected");
      // Publish the ID before catch-up reads attach their origin headers.
      void invalidateAfterReconnect(
        queryClient,
        () => isCurrent() && connection.state === signalR.HubConnectionState.Connected,
      ).catch(() => {});
    };

    const scheduleRetry = () => {
      if (!isCurrent() || starting || retryTimer !== undefined ||
        connection.state !== signalR.HubConnectionState.Disconnected) return;
      retryTimer = setTimeout(() => {
        retryTimer = undefined;
        void start();
      }, retryDelay);
      retryDelay = Math.min(retryDelay * 2, 30_000);
    };

    const start = async () => {
      if (!isCurrent() || starting || connection.state !== signalR.HubConnectionState.Disconnected) return;
      starting = true;
      try {
        await connection.start();
        if (!isCurrent()) {
          void connection.stop().catch(() => {});
          return;
        }
        connected();
      } catch (error: unknown) {
        if (!isCurrent()) return;
        if (import.meta.env.DEV) console.debug("[SignalR] Connection error:", error);
        if (connection.state === signalR.HubConnectionState.Disconnected) {
          setConnectionState("disconnected");
          setConnectionId(null);
        }
      } finally {
        starting = false;
        // Also covers a close delivered before the successful start continuation.
        scheduleRetry();
      }
    };

    const stop = () => {
      if (!active) return;
      active = false;
      cancelRetry();
      lifetime.abort(new DOMException("The connection lifetime ended", "AbortError"));
      unsubscribe();
      connection.off("EntityChanged", onEntityChanged);
      if (connectionRef.current === connection) {
        connectionRef.current = null;
        setConnectionId(null);
        clearBufferedToasts();
        setConnectionState("disconnected");
      }
      void connection.stop().catch(() => {});
    };
    const unsubscribe = addSessionChangeListener(stop);

    connection.onreconnecting(() => {
      if (!isCurrent()) return;
      cancelRetry();
      setConnectionId(null);
      setConnectionState("reconnecting");
    });
    connection.onreconnected(connected);
    connection.onclose(() => {
      if (!isCurrent()) return;
      setConnectionState("disconnected");
      setConnectionId(null);
      scheduleRetry();
    });

    const onEntityChanged = (notification: EntityChangeNotification) => {
      if (!isCurrent()) return;
      if (import.meta.env.DEV) {
        console.debug("[SignalR] EntityChanged", notification);
      }

      if (notification.entityType === "backup-import") {
        void invalidateAfterBackupImport(queryClient, isCurrent).catch(() => {});
      } else if (isDomainChange(notification.entityType)) {
        invalidateDomainChange(
          queryClient,
          notification.entityType,
          notification.changeType === "created" ? "created" : "changed",
        );
      }

      const token = getAccessToken();
      const jwt = token ? parseJwtPayload(token) : null;
      const myUserId = jwt?.userId ?? null;
      const myConnectionId = getConnectionId();

      const origin = classifyOrigin(notification, myConnectionId, myUserId);
      if (origin === null) {
        // Same session — suppress toast, query invalidation already done
        return;
      }

      const displayName =
        displayNameMap[notification.entityType] ?? notification.entityType;
      bufferToast(displayName, notification.changeType, notification.count ?? 1, origin);
    };
    connection.on("EntityChanged", onEntityChanged);
    connectionRef.current = connection;
    void start();
    return stop;
  }, [enabled, queryClient]);

  return useMemo(() => ({ connectionState }), [connectionState]);
}

import { useEffect, useMemo, useRef, useState } from "react";
import * as signalR from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { getAccessToken, parseJwtPayload, getSessionVersion, addSessionChangeListener } from "@/lib/auth";
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

const queryKeyMap: Record<string, string[][]> = {
  receipt: [
    ["receipts"],
    ["receipt-items"],
    ["transactions"],
    ["adjustments"],
    ["receipts-with-items"],
    ["trips"],
    ["reports"],
    ["ynab", "split-comparison"],
    ["ynab", "receipt-sync-statuses"],
  ],
  "receipt-item": [["receipt-items"], ["receipts-with-items"], ["trips"]],
  transaction: [
    ["transactions"],
    ["receipts-with-items"],
    ["trips"],
    ["transaction-accounts"],
  ],
  adjustment: [
    ["adjustments"],
    ["receipts"],
    ["receipts-with-items"],
    ["trips"],
    ["reports"],
    ["ynab", "split-comparison"],
    ["ynab", "receipt-sync-statuses"],
  ],
  card: [["cards"], ["transaction-accounts"]],
  category: [["categories"]],
  subcategory: [["subcategories"]],
  "item-template": [["itemTemplates"]],
};

const displayNameMap: Record<string, string> = {
  receipt: "receipt",
  "receipt-item": "receipt item",
  transaction: "transaction",
  adjustment: "adjustment",
  card: "card",
  category: "category",
  subcategory: "subcategory",
  "item-template": "item template",
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
    let active = true;
    const isCurrent = () => active && getSessionVersion() === sessionVersion;
    const connection = new signalR.HubConnectionBuilder()
      .withUrl("/hubs/entities", {
        accessTokenFactory: () => isCurrent() ? getAccessToken() ?? "" : "",
      })
      .withAutomaticReconnect()
      .configureLogging(
        import.meta.env.DEV ? signalR.LogLevel.Debug : signalR.LogLevel.None,
      )
      .build();

    const stop = () => {
      if (!active) return;
      active = false;
      connectionRef.current = null;
      setConnectionId(null);
      clearBufferedToasts();
      setConnectionState("disconnected");
      void connection.stop().catch(() => {});
    };
    const unsubscribe = addSessionChangeListener(stop);

    connection.onreconnecting(() => {
      if (!isCurrent()) return;
      setConnectionState("reconnecting");
      if (import.meta.env.DEV) {
        console.debug("[SignalR] Reconnecting...");
      }
    });

    connection.onreconnected(() => {
      if (!isCurrent()) return;
      setConnectionState("connected");
      setConnectionId(connection.connectionId ?? null);
      if (import.meta.env.DEV) {
        console.debug("[SignalR] Reconnected.");
      }
    });

    connection.onclose(() => {
      if (!isCurrent()) return;
      setConnectionState("disconnected");
      setConnectionId(null);
      if (import.meta.env.DEV) {
        console.debug("[SignalR] Connection closed.");
      }
    });

    connection.on(
      "EntityChanged",
      (notification: EntityChangeNotification) => {
        if (!isCurrent()) return;
        if (import.meta.env.DEV) {
          console.debug("[SignalR] EntityChanged", notification);
        }

        const keys = queryKeyMap[notification.entityType];
        if (keys) {
          for (const queryKey of keys) {
            queryClient.invalidateQueries({ queryKey, refetchType: "active" });
          }
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
      },
    );

    connectionRef.current = connection;

    connection
      .start()
      .then(() => {
        if (!isCurrent()) {
          void connection.stop().catch(() => {});
          return;
        }
        setConnectionState("connected");
        setConnectionId(connection.connectionId ?? null);
        if (import.meta.env.DEV) {
          console.debug("[SignalR] Connected to /entities hub.");
        }
      })
      .catch((err: unknown) => {
        if (!isCurrent()) return;
        if (import.meta.env.DEV) {
          console.debug("[SignalR] Connection error:", err);
        }
        setConnectionState("disconnected");
      });

    return () => {
      unsubscribe();
      stop();
    };
  }, [enabled, queryClient]);

  return useMemo(() => ({ connectionState }), [connectionState]);
}

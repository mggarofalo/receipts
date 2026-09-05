import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { renderHook } from "@testing-library/react";

const mockConnection = {
  start: vi.fn().mockResolvedValue(undefined),
  stop: vi.fn().mockResolvedValue(undefined),
  on: vi.fn(),
  onreconnecting: vi.fn(),
  onreconnected: vi.fn(),
  onclose: vi.fn(),
  connectionId: "mock-conn-id",
};

const mockBuilder = {
  withUrl: vi.fn().mockReturnThis(),
  withAutomaticReconnect: vi.fn().mockReturnThis(),
  configureLogging: vi.fn().mockReturnThis(),
  build: vi.fn().mockReturnValue(mockConnection),
};

vi.mock("@microsoft/signalr", () => ({
  HubConnectionBuilder: vi.fn().mockImplementation(function () {
    return mockBuilder;
  }),
  LogLevel: { Debug: 1, Critical: 5, None: 6 },
}));

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: vi.fn().mockReturnValue({
    invalidateQueries: vi.fn(),
  }),
}));

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));

vi.mock("@/lib/signalr-toast-buffer", () => ({
  bufferToast: vi.fn(),
  clearBufferedToasts: vi.fn(),
}));

vi.mock("@/lib/auth", async (importOriginal) => ({
  ...await importOriginal<typeof import("@/lib/auth")>(),
  getAccessToken: vi.fn().mockReturnValue("mock-token"),
  parseJwtPayload: vi.fn().mockReturnValue({
    userId: "current-user-id",
    email: "user@example.com",
    roles: [],
    mustResetPassword: false,
  }),
}));

vi.mock("@/lib/signalr-connection", () => ({
  setConnectionId: vi.fn(),
  getConnectionId: vi.fn().mockReturnValue("mock-conn-id"),
}));

import { useSignalR } from "./useSignalR";
import { useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { bufferToast } from "@/lib/signalr-toast-buffer";
import { act } from "@testing-library/react";
import { setConnectionId, getConnectionId } from "@/lib/signalr-connection";
import { clearTokens, getAccessToken, parseJwtPayload, setTokens } from "@/lib/auth";

beforeEach(() => {
  vi.clearAllMocks();
  // Restore start to resolve by default (individual tests may override)
  mockConnection.start.mockResolvedValue(undefined);
  mockConnection.connectionId = "mock-conn-id";
  vi.mocked(getConnectionId).mockReturnValue("mock-conn-id");
});

/** Helper: render the hook, flush the start() promise, and return the result. */
async function renderEnabled() {
  const hookReturn = renderHook(() => useSignalR(true));
  // Flush the microtask so start().then() executes
  await act(async () => {});
  return hookReturn;
}

/** Helper: find the registered handler for a given hub event name. */
function getOnHandler(eventName: string): ((...args: unknown[]) => void) | undefined {
  const call = mockConnection.on.mock.calls.find(
    (c: unknown[]) => c[0] === eventName,
  );
  return call ? (call[1] as (...args: unknown[]) => void) : undefined;
}

describe("useSignalR", () => {
  it("ignores an old EntityChanged callback after its owner unmounts", async () => {
    const queryClient = vi.mocked(useQueryClient)();
    const { unmount } = await renderEnabled();
    const handler = getOnHandler("EntityChanged")!;
    unmount();
    vi.clearAllMocks();

    act(() => { handler({ entityType: "receipt", changeType: "created", id: "Alice-receipt" }); });

    expect(queryClient.invalidateQueries).not.toHaveBeenCalled();
    expect(bufferToast).not.toHaveBeenCalled();
  });

  it("does not restore a disposed connection ID when start finishes late", async () => {
    let finishStart!: () => void;
    mockConnection.start.mockImplementationOnce(() => new Promise<void>((resolve) => { finishStart = resolve; }));
    const { unmount } = renderHook(() => useSignalR(true));
    unmount();
    vi.mocked(setConnectionId).mockClear();

    await act(async () => { finishStart(); });

    expect(setConnectionId).not.toHaveBeenCalledWith("mock-conn-id");
  });

  it("stops the old session immediately and ignores its queued reconnect callback", async () => {
    setTokens("Alice-access", "Alice-refresh");
    await renderEnabled();
    const reconnected = mockConnection.onreconnected.mock.calls[0][0] as () => void;
    const handler = getOnHandler("EntityChanged")!;
    const queryClient = vi.mocked(useQueryClient)();
    vi.clearAllMocks();

    act(() => { clearTokens(); setTokens("Bob-access", "Bob-refresh"); });
    expect(mockConnection.stop).toHaveBeenCalled();
    vi.mocked(setConnectionId).mockClear();
    act(() => { reconnected(); handler({ entityType: "receipt", changeType: "created", id: "Alice-receipt" }); });

    expect(setConnectionId).not.toHaveBeenCalledWith("mock-conn-id");
    expect(queryClient.invalidateQueries).not.toHaveBeenCalled();
    expect(bufferToast).not.toHaveBeenCalled();
  });

  it("returns disconnected when not enabled", () => {
    const { result } = renderHook(() => useSignalR(false));

    expect(result.current.connectionState).toBe("disconnected");
    expect(mockConnection.start).not.toHaveBeenCalled();
  });

  it("starts connection when enabled", () => {
    renderHook(() => useSignalR(true));

    expect(mockBuilder.withUrl).toHaveBeenCalledWith(
      "/hubs/entities",
      expect.objectContaining({ accessTokenFactory: expect.any(Function) }),
    );
    expect(mockBuilder.withAutomaticReconnect).toHaveBeenCalled();
    expect(mockBuilder.configureLogging).toHaveBeenCalled();
    expect(mockBuilder.build).toHaveBeenCalled();
    expect(mockConnection.start).toHaveBeenCalled();
  });

  it("registers EntityChanged event handler", () => {
    renderHook(() => useSignalR(true));

    const registeredEvents = mockConnection.on.mock.calls.map(
      (call: unknown[]) => call[0],
    );
    expect(registeredEvents).toContain("EntityChanged");
  });

  it("registers reconnecting and close handlers", () => {
    renderHook(() => useSignalR(true));

    expect(mockConnection.onreconnecting).toHaveBeenCalled();
    expect(mockConnection.onreconnected).toHaveBeenCalled();
    expect(mockConnection.onclose).toHaveBeenCalled();
  });

  it("stops connection on cleanup", () => {
    const { unmount } = renderHook(() => useSignalR(true));

    unmount();

    expect(mockConnection.stop).toHaveBeenCalled();
  });

  it("sets connectionState to connected after start() resolves", async () => {
    const { result } = await renderEnabled();

    expect(result.current.connectionState).toBe("connected");
  });

  it("sets connectionId on connect", async () => {
    await renderEnabled();

    expect(setConnectionId).toHaveBeenCalledWith("mock-conn-id");
  });

  it("clears connectionRef on cleanup", () => {
    const { unmount } = renderHook(() => useSignalR(true));

    unmount();

    expect(mockConnection.stop).toHaveBeenCalled();
    expect(setConnectionId).toHaveBeenCalledWith(null);
  });

  describe("accessTokenFactory", () => {
    it("returns token when getAccessToken returns a value", () => {
      renderHook(() => useSignalR(true));

      const withUrlCall = mockBuilder.withUrl.mock.calls[0];
      const options = withUrlCall[1] as { accessTokenFactory: () => string };
      const result = options.accessTokenFactory();

      expect(result).toBe("mock-token");
    });

    it("returns empty string when getAccessToken returns null", () => {
      vi.mocked(getAccessToken).mockReturnValueOnce(null);

      renderHook(() => useSignalR(true));

      const withUrlCall = mockBuilder.withUrl.mock.calls[0];
      const options = withUrlCall[1] as { accessTokenFactory: () => string };
      const result = options.accessTokenFactory();

      expect(result).toBe("");
    });
  });

  describe("connectionId nullish coalescing", () => {
    it("sets connectionId to null when connection.connectionId is undefined on start", async () => {
      mockConnection.connectionId = undefined as unknown as string;

      await renderEnabled();

      expect(setConnectionId).toHaveBeenCalledWith(null);
    });

    it("sets connectionId to null when connection.connectionId is null on reconnect", async () => {
      await renderEnabled();

      // Clear previous calls
      vi.mocked(setConnectionId).mockClear();

      // Set connectionId to null for reconnect
      mockConnection.connectionId = null as unknown as string;

      const reconnectedCb = mockConnection.onreconnected.mock.calls[0][0] as () => void;
      act(() => {
        reconnectedCb();
      });

      expect(setConnectionId).toHaveBeenCalledWith(null);
    });
  });

  describe("EntityChanged handler", () => {
    it("invalidates receipt query keys and buffers toast for receipt created", async () => {
      const mockQueryClient = vi.mocked(useQueryClient)();

      await renderEnabled();

      const handler = getOnHandler("EntityChanged");
      expect(handler).toBeDefined();

      act(() => {
        handler!({ entityType: "receipt", changeType: "created", id: "abc-123", count: 1, userId: "other-user-id", authMethod: "jwt", connectionId: "other-conn" });
      });

      expect(mockQueryClient.invalidateQueries).toHaveBeenCalledWith({
        queryKey: ["receipts"],
        refetchType: "active",
      });
      expect(mockQueryClient.invalidateQueries).toHaveBeenCalledWith({
        queryKey: ["receipts-with-items"],
        refetchType: "active",
      });
      expect(mockQueryClient.invalidateQueries).toHaveBeenCalledWith({
        queryKey: ["trips"],
        refetchType: "active",
      });
      for (const queryKey of [
        ["receipt-items"],
        ["transactions"],
        ["adjustments"],
        ["reports"],
        ["ynab", "split-comparison"],
        ["ynab", "receipt-sync-statuses"],
      ]) {
        expect(mockQueryClient.invalidateQueries).toHaveBeenCalledWith({
          queryKey,
          refetchType: "active",
        });
      }
      expect(bufferToast).toHaveBeenCalledWith("receipt", "created", 1, "other-user");
      expect(toast.info).not.toHaveBeenCalled();
    });

    it("invalidates card query keys and buffers toast for card updated", async () => {
      const mockQueryClient = vi.mocked(useQueryClient)();

      await renderEnabled();

      const handler = getOnHandler("EntityChanged");
      expect(handler).toBeDefined();

      act(() => {
        handler!({ entityType: "card", changeType: "updated", id: "abc-123", count: 1, userId: "other-user-id", authMethod: "jwt", connectionId: "other-conn" });
      });

      expect(mockQueryClient.invalidateQueries).toHaveBeenCalledWith({
        queryKey: ["cards"],
        refetchType: "active",
      });
      expect(mockQueryClient.invalidateQueries).toHaveBeenCalledWith({
        queryKey: ["transaction-accounts"],
        refetchType: "active",
      });
      expect(bufferToast).toHaveBeenCalledWith("card", "updated", 1, "other-user");
      expect(toast.info).not.toHaveBeenCalled();
    });

    it("invalidates transaction query keys and buffers toast for transaction deleted", async () => {
      const mockQueryClient = vi.mocked(useQueryClient)();

      await renderEnabled();

      const handler = getOnHandler("EntityChanged");
      expect(handler).toBeDefined();

      act(() => {
        handler!({ entityType: "transaction", changeType: "deleted", id: "abc-123", count: 1, userId: "other-user-id", authMethod: "jwt", connectionId: "other-conn" });
      });

      expect(mockQueryClient.invalidateQueries).toHaveBeenCalledWith({
        queryKey: ["transactions"],
        refetchType: "active",
      });
      expect(mockQueryClient.invalidateQueries).toHaveBeenCalledWith({
        queryKey: ["receipts-with-items"],
        refetchType: "active",
      });
      expect(mockQueryClient.invalidateQueries).toHaveBeenCalledWith({
        queryKey: ["trips"],
        refetchType: "active",
      });
      expect(mockQueryClient.invalidateQueries).toHaveBeenCalledWith({
        queryKey: ["transaction-accounts"],
        refetchType: "active",
      });
      expect(bufferToast).toHaveBeenCalledWith("transaction", "deleted", 1, "other-user");
      expect(toast.info).not.toHaveBeenCalled();
    });

    it("invalidates adjustment and all receipt-derived query keys", async () => {
      const mockQueryClient = vi.mocked(useQueryClient)();
      await renderEnabled();
      const handler = getOnHandler("EntityChanged");

      act(() => {
        handler!({ entityType: "adjustment", changeType: "updated", id: "adj-1", count: 1, userId: "other-user-id", authMethod: "jwt", connectionId: "other-conn" });
      });

      for (const queryKey of [
        ["adjustments"],
        ["receipts"],
        ["receipts-with-items"],
        ["trips"],
        ["reports"],
        ["ynab", "split-comparison"],
        ["ynab", "receipt-sync-statuses"],
      ]) {
        expect(mockQueryClient.invalidateQueries).toHaveBeenCalledWith({
          queryKey,
          refetchType: "active",
        });
      }
    });

    it("buffers toast with display name for receipt-item", async () => {
      await renderEnabled();

      const handler = getOnHandler("EntityChanged");
      expect(handler).toBeDefined();

      act(() => {
        handler!({ entityType: "receipt-item", changeType: "created", id: "abc", count: 1, userId: "other-user-id", authMethod: "jwt", connectionId: "other-conn" });
      });

      expect(bufferToast).toHaveBeenCalledWith("receipt item", "created", 1, "other-user");
      expect(toast.info).not.toHaveBeenCalled();
    });

    it("buffers toast with display name for item-template", async () => {
      await renderEnabled();

      const handler = getOnHandler("EntityChanged");
      expect(handler).toBeDefined();

      act(() => {
        handler!({ entityType: "item-template", changeType: "updated", id: "abc", count: 1, userId: "other-user-id", authMethod: "jwt", connectionId: "other-conn" });
      });

      expect(bufferToast).toHaveBeenCalledWith("item template", "updated", 1, "other-user");
      expect(toast.info).not.toHaveBeenCalled();
    });

    it("suppresses toast when notification.connectionId matches own connectionId", async () => {
      const mockQueryClient = vi.mocked(useQueryClient)();

      await renderEnabled();

      const handler = getOnHandler("EntityChanged");
      expect(handler).toBeDefined();

      act(() => {
        handler!({ entityType: "receipt", changeType: "created", id: "abc", count: 1, userId: "current-user-id", authMethod: "jwt", connectionId: "mock-conn-id" });
      });

      // Queries still invalidated
      expect(mockQueryClient.invalidateQueries).toHaveBeenCalled();
      // But no toast
      expect(bufferToast).not.toHaveBeenCalled();
    });

    it("calls bufferToast with api-key origin when authMethod=apikey and userId matches", async () => {
      await renderEnabled();

      const handler = getOnHandler("EntityChanged");
      expect(handler).toBeDefined();

      act(() => {
        handler!({ entityType: "receipt", changeType: "updated", id: "abc", count: 1, userId: "current-user-id", authMethod: "apikey", connectionId: null });
      });

      expect(bufferToast).toHaveBeenCalledWith("receipt", "updated", 1, "api-key");
    });

    it("calls bufferToast with other-session origin when same userId, different connectionId", async () => {
      await renderEnabled();

      const handler = getOnHandler("EntityChanged");
      expect(handler).toBeDefined();

      act(() => {
        handler!({ entityType: "receipt", changeType: "created", id: "abc", count: 1, userId: "current-user-id", authMethod: "jwt", connectionId: "different-conn" });
      });

      expect(bufferToast).toHaveBeenCalledWith("receipt", "created", 1, "other-session");
    });

    it("calls bufferToast with other-user origin when different userId", async () => {
      await renderEnabled();

      const handler = getOnHandler("EntityChanged");
      expect(handler).toBeDefined();

      act(() => {
        handler!({ entityType: "receipt", changeType: "deleted", id: "abc", count: 1, userId: "someone-else", authMethod: "jwt", connectionId: "other-conn" });
      });

      expect(bufferToast).toHaveBeenCalledWith("receipt", "deleted", 1, "other-user");
    });

    it("skips query invalidation for unknown entity type", async () => {
      const mockQueryClient = vi.mocked(useQueryClient)();

      await renderEnabled();

      const handler = getOnHandler("EntityChanged");
      expect(handler).toBeDefined();

      act(() => {
        handler!({ entityType: "unknown-entity", changeType: "created", id: "abc", count: 1, userId: "other-user-id", authMethod: "jwt", connectionId: "other-conn" });
      });

      // No query invalidation since entity type is not in queryKeyMap
      expect(mockQueryClient.invalidateQueries).not.toHaveBeenCalled();
      // But toast still fires with entityType as display name (fallback)
      expect(bufferToast).toHaveBeenCalledWith("unknown-entity", "created", 1, "other-user");
    });

    it("uses entityType as display name for unmapped entity type", async () => {
      await renderEnabled();

      const handler = getOnHandler("EntityChanged");
      expect(handler).toBeDefined();

      act(() => {
        handler!({ entityType: "custom-widget", changeType: "deleted", id: "abc", count: 1, userId: "other-user-id", authMethod: "jwt", connectionId: "other-conn" });
      });

      expect(bufferToast).toHaveBeenCalledWith("custom-widget", "deleted", 1, "other-user");
    });

    it("defaults count to 1 when notification.count is undefined", async () => {
      await renderEnabled();

      const handler = getOnHandler("EntityChanged");
      expect(handler).toBeDefined();

      act(() => {
        handler!({ entityType: "receipt", changeType: "created", id: "abc", userId: "other-user-id", authMethod: "jwt", connectionId: "other-conn" });
      });

      expect(bufferToast).toHaveBeenCalledWith("receipt", "created", 1, "other-user");
    });

    it("classifies as other-user when getAccessToken returns null", async () => {
      await renderEnabled();

      const handler = getOnHandler("EntityChanged");
      expect(handler).toBeDefined();

      vi.mocked(getAccessToken).mockReturnValueOnce(null);

      act(() => {
        handler!({ entityType: "receipt", changeType: "created", id: "abc", count: 1, userId: "current-user-id", authMethod: "jwt", connectionId: "different-conn" });
      });

      // When token is null, parseJwtPayload is not called, myUserId is null → other-user
      expect(bufferToast).toHaveBeenCalledWith("receipt", "created", 1, "other-user");
    });

    it("classifies as other-user when notification has null userId and null connectionId", async () => {
      await renderEnabled();

      const handler = getOnHandler("EntityChanged");
      expect(handler).toBeDefined();

      act(() => {
        handler!({ entityType: "receipt", changeType: "created", id: "abc", count: 1, userId: null, authMethod: null, connectionId: null });
      });

      expect(bufferToast).toHaveBeenCalledWith("receipt", "created", 1, "other-user");
    });

    it("classifies as other-user when parseJwtPayload returns null", async () => {
      vi.mocked(parseJwtPayload).mockReturnValueOnce(null);

      await renderEnabled();

      const handler = getOnHandler("EntityChanged");
      expect(handler).toBeDefined();

      act(() => {
        handler!({ entityType: "receipt", changeType: "created", id: "abc", count: 1, userId: "current-user-id", authMethod: "jwt", connectionId: "different-conn" });
      });

      expect(bufferToast).toHaveBeenCalledWith("receipt", "created", 1, "other-user");
    });
  });

  describe("connection state transitions", () => {
    it("sets state to reconnecting when onreconnecting fires", async () => {
      const { result } = await renderEnabled();
      expect(result.current.connectionState).toBe("connected");

      const reconnectingCb = mockConnection.onreconnecting.mock.calls[0][0] as () => void;

      act(() => {
        reconnectingCb();
      });

      expect(result.current.connectionState).toBe("reconnecting");
    });

    it("sets state to connected when onreconnected fires", async () => {
      const { result } = await renderEnabled();

      // Simulate reconnecting first
      const reconnectingCb = mockConnection.onreconnecting.mock.calls[0][0] as () => void;
      act(() => {
        reconnectingCb();
      });
      expect(result.current.connectionState).toBe("reconnecting");

      // Now reconnected
      const reconnectedCb = mockConnection.onreconnected.mock.calls[0][0] as () => void;
      act(() => {
        reconnectedCb();
      });

      expect(result.current.connectionState).toBe("connected");
    });

    it("sets state to disconnected when onclose fires", async () => {
      const { result } = await renderEnabled();
      expect(result.current.connectionState).toBe("connected");

      const closeCb = mockConnection.onclose.mock.calls[0][0] as () => void;

      act(() => {
        closeCb();
      });

      expect(result.current.connectionState).toBe("disconnected");
    });

    it("clears connectionId when onclose fires", async () => {
      await renderEnabled();

      vi.mocked(setConnectionId).mockClear();

      const closeCb = mockConnection.onclose.mock.calls[0][0] as () => void;
      act(() => {
        closeCb();
      });

      expect(setConnectionId).toHaveBeenCalledWith(null);
    });

    it("updates connectionId on reconnect", async () => {
      await renderEnabled();

      vi.mocked(setConnectionId).mockClear();
      mockConnection.connectionId = "new-conn-id";

      const reconnectedCb = mockConnection.onreconnected.mock.calls[0][0] as () => void;
      act(() => {
        reconnectedCb();
      });

      expect(setConnectionId).toHaveBeenCalledWith("new-conn-id");
    });
  });

  describe("error handling", () => {
    it("sets state to disconnected when start() rejects", async () => {
      mockConnection.start.mockRejectedValueOnce(new Error("Connection failed"));

      const { result } = renderHook(() => useSignalR(true));

      // Flush the rejected promise + catch handler
      await act(async () => {});

      expect(result.current.connectionState).toBe("disconnected");
    });
  });

  describe("production mode (DEV=false)", () => {
    const originalDev = import.meta.env.DEV;

    beforeEach(() => {
      import.meta.env.DEV = false;
    });

    afterEach(() => {
      import.meta.env.DEV = originalDev;
    });

    it("uses LogLevel.None and suppresses console.debug on start", async () => {
      const debugSpy = vi.spyOn(console, "debug").mockImplementation(() => {});

      await renderEnabled();

      // configureLogging should still be called (with LogLevel.None via ternary false branch)
      expect(mockBuilder.configureLogging).toHaveBeenCalledWith(6); // LogLevel.None
      // No debug output in production
      expect(debugSpy).not.toHaveBeenCalled();

      debugSpy.mockRestore();
    });

    it("suppresses console.debug on reconnecting", async () => {
      const debugSpy = vi.spyOn(console, "debug").mockImplementation(() => {});

      await renderEnabled();

      const reconnectingCb = mockConnection.onreconnecting.mock.calls[0][0] as () => void;
      act(() => {
        reconnectingCb();
      });

      expect(debugSpy).not.toHaveBeenCalled();
      debugSpy.mockRestore();
    });

    it("suppresses console.debug on reconnected", async () => {
      const debugSpy = vi.spyOn(console, "debug").mockImplementation(() => {});

      await renderEnabled();

      const reconnectedCb = mockConnection.onreconnected.mock.calls[0][0] as () => void;
      act(() => {
        reconnectedCb();
      });

      expect(debugSpy).not.toHaveBeenCalled();
      debugSpy.mockRestore();
    });

    it("suppresses console.debug on close", async () => {
      const debugSpy = vi.spyOn(console, "debug").mockImplementation(() => {});

      await renderEnabled();

      const closeCb = mockConnection.onclose.mock.calls[0][0] as () => void;
      act(() => {
        closeCb();
      });

      expect(debugSpy).not.toHaveBeenCalled();
      debugSpy.mockRestore();
    });

    it("suppresses console.debug on EntityChanged", async () => {
      const debugSpy = vi.spyOn(console, "debug").mockImplementation(() => {});

      await renderEnabled();

      const handler = getOnHandler("EntityChanged");
      expect(handler).toBeDefined();

      act(() => {
        handler!({ entityType: "receipt", changeType: "created", id: "abc", count: 1, userId: "other-user-id", authMethod: "jwt", connectionId: "other-conn" });
      });

      expect(debugSpy).not.toHaveBeenCalled();
      debugSpy.mockRestore();
    });

    it("suppresses console.debug on start() error", async () => {
      const debugSpy = vi.spyOn(console, "debug").mockImplementation(() => {});
      mockConnection.start.mockRejectedValueOnce(new Error("Connection failed"));

      renderHook(() => useSignalR(true));
      await act(async () => {});

      expect(debugSpy).not.toHaveBeenCalled();
      debugSpy.mockRestore();
    });
  });
});

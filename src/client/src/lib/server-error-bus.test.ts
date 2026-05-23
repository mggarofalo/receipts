import { describe, it, expect, beforeEach, vi } from "vitest";
import {
  addServerErrorListener,
  notifyServerError,
  hasShownServerErrorPage,
  markServerErrorPageShown,
  clearServerErrorPageFlag,
  setLoginFlash,
  consumeLoginFlash,
} from "./server-error-bus";

describe("server-error-bus", () => {
  beforeEach(() => {
    window.sessionStorage.clear();
  });

  describe("listener pub/sub", () => {
    it("delivers the status code to subscribers", () => {
      const listener = vi.fn();
      const unsubscribe = addServerErrorListener(listener);
      notifyServerError(503);
      expect(listener).toHaveBeenCalledWith(503);
      unsubscribe();
    });

    it("does not notify after unsubscribe", () => {
      const listener = vi.fn();
      const unsubscribe = addServerErrorListener(listener);
      unsubscribe();
      notifyServerError(500);
      expect(listener).not.toHaveBeenCalled();
    });

    it("supports multiple subscribers independently", () => {
      const a = vi.fn();
      const b = vi.fn();
      const offA = addServerErrorListener(a);
      const offB = addServerErrorListener(b);
      notifyServerError(502);
      expect(a).toHaveBeenCalledOnce();
      expect(b).toHaveBeenCalledOnce();
      offA();
      notifyServerError(500);
      expect(a).toHaveBeenCalledOnce();
      expect(b).toHaveBeenCalledTimes(2);
      offB();
    });
  });

  describe("session flag", () => {
    it("returns false when nothing has been marked", () => {
      expect(hasShownServerErrorPage()).toBe(false);
    });

    it("returns true after markServerErrorPageShown", () => {
      markServerErrorPageShown();
      expect(hasShownServerErrorPage()).toBe(true);
    });

    it("clears the flag on demand", () => {
      markServerErrorPageShown();
      clearServerErrorPageFlag();
      expect(hasShownServerErrorPage()).toBe(false);
    });
  });

  describe("login flash", () => {
    it("returns null when nothing is set", () => {
      expect(consumeLoginFlash()).toBeNull();
    });

    it("reads back what was set", () => {
      setLoginFlash("session-expired");
      expect(consumeLoginFlash()).toBe("session-expired");
    });

    it("consume-on-read clears the value", () => {
      setLoginFlash("once");
      consumeLoginFlash();
      expect(consumeLoginFlash()).toBeNull();
    });
  });

  describe("storage failures", () => {
    it("tolerates getItem throwing", () => {
      const spy = vi
        .spyOn(window.sessionStorage.__proto__, "getItem")
        .mockImplementation(() => {
          throw new Error("denied");
        });
      expect(hasShownServerErrorPage()).toBe(false);
      expect(consumeLoginFlash()).toBeNull();
      spy.mockRestore();
    });

    it("tolerates setItem throwing", () => {
      const spy = vi
        .spyOn(window.sessionStorage.__proto__, "setItem")
        .mockImplementation(() => {
          throw new Error("denied");
        });
      expect(() => markServerErrorPageShown()).not.toThrow();
      expect(() => setLoginFlash("x")).not.toThrow();
      spy.mockRestore();
    });
  });
});

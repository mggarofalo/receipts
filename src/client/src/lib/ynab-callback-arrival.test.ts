import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  captureYnabCallbackArrival,
  clearYnabCallbackArrival,
  parseYnabCallbackArrival,
  peekYnabCallbackArrival,
} from "./ynab-callback-arrival";

function fakeWindow(pathname: string, search = "", hash = "") {
  return {
    location: { pathname, search, hash } as Location,
    history: {
      state: { key: "existing" },
      replaceState: vi.fn(),
    } as unknown as History,
  };
}

beforeEach(() => {
  clearYnabCallbackArrival();
});

describe("parseYnabCallbackArrival", () => {
  it("reads the code and state", () => {
    expect(parseYnabCallbackArrival("?code=c-1&state=s-1")).toEqual({
      code: "c-1",
      state: "s-1",
      declined: false,
    });
  });

  it("reports a declined request without exposing the error value", () => {
    expect(parseYnabCallbackArrival("?error=access_denied&state=s-1")).toEqual({
      code: null,
      state: "s-1",
      declined: true,
    });
  });

  it("reports missing values as null", () => {
    expect(parseYnabCallbackArrival("")).toEqual({
      code: null,
      state: null,
      declined: false,
    });
  });
});

describe("captureYnabCallbackArrival", () => {
  it("remembers the values and rewrites the address without its query and fragment", () => {
    const win = fakeWindow(
      "/settings/ynab/callback",
      "?code=c-1&state=s-1",
      "#frag",
    );

    captureYnabCallbackArrival(win);

    expect(peekYnabCallbackArrival()).toEqual({
      code: "c-1",
      state: "s-1",
      declined: false,
    });
    expect(win.history.replaceState).toHaveBeenCalledTimes(1);
    expect(win.history.replaceState).toHaveBeenCalledWith(
      { key: "existing" },
      "",
      "/settings/ynab/callback",
    );
  });

  it("also captures a declined arrival and one with a trailing slash", () => {
    const win = fakeWindow(
      "/settings/ynab/callback/",
      "?error=access_denied&state=s-1",
    );

    captureYnabCallbackArrival(win);

    expect(peekYnabCallbackArrival()).toEqual({
      code: null,
      state: "s-1",
      declined: true,
    });
    expect(win.history.replaceState).toHaveBeenCalledWith(
      expect.anything(),
      "",
      "/settings/ynab/callback",
    );
  });

  it.each([
    ["/receipts", "?code=c-1&state=s-1"],
    ["/settings/ynab", "?code=c-1&state=s-1"],
    ["/settings/ynab/callback", ""],
  ])("does nothing for %s%s", (pathname, search) => {
    const win = fakeWindow(pathname, search);

    captureYnabCallbackArrival(win);

    expect(peekYnabCallbackArrival()).toBeNull();
    expect(win.history.replaceState).not.toHaveBeenCalled();
  });

  it("forgets the values when cleared", () => {
    captureYnabCallbackArrival(
      fakeWindow("/settings/ynab/callback", "?code=c-1&state=s-1"),
    );

    clearYnabCallbackArrival();

    expect(peekYnabCallbackArrival()).toBeNull();
  });
});

import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { MemoryRouter, Routes, Route, useLocation } from "react-router";
import {
  useLastRoutePersistence,
  isTrackableRoute,
} from "./useLastRoutePersistence";

const STORAGE_KEY = "receipts:last-route";

function Probe() {
  useLastRoutePersistence();
  const location = useLocation();
  return <div data-testid="path">{location.pathname + (location.search ?? "")}</div>;
}

function harness(initialEntry: string) {
  return render(
    <MemoryRouter initialEntries={[initialEntry]}>
      <Routes>
        <Route path="/" element={<Probe />} />
        <Route path="/receipts" element={<Probe />} />
        <Route path="/receipts/abc-123" element={<Probe />} />
        <Route path="/cards" element={<Probe />} />
        <Route path="/login" element={<Probe />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe("isTrackableRoute", () => {
  it.each([
    ["/", false],
    ["/login", false],
    ["/login/forgot", false],
    ["/change-password", false],
    ["/onboarding", false],
    ["/onboarding/step-2", false],
    ["/receipts", true],
    ["/receipts/abc-123", true],
    ["/cards", true],
    ["/reports", true],
  ])("returns %s → %s", (path, expected) => {
    expect(isTrackableRoute(path)).toBe(expected);
  });
});

describe("useLastRoutePersistence", () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  it("writes the current pathname to localStorage when on a trackable route", () => {
    harness("/cards");
    expect(window.localStorage.getItem(STORAGE_KEY)).toBe("/cards");
  });

  it("preserves the search string", () => {
    harness("/receipts?filter=open");
    expect(window.localStorage.getItem(STORAGE_KEY)).toBe("/receipts?filter=open");
  });

  it("does not write when on an untrackable route", () => {
    harness("/login");
    expect(window.localStorage.getItem(STORAGE_KEY)).toBeNull();
  });

  it("does not write when on root", () => {
    harness("/");
    expect(window.localStorage.getItem(STORAGE_KEY)).toBeNull();
  });

  it("redirects from / to the stored route on first mount", () => {
    window.localStorage.setItem(STORAGE_KEY, "/cards");
    harness("/");
    expect(screen.getByTestId("path")).toHaveTextContent("/cards");
  });

  it("does not redirect when stored route is untrackable", () => {
    window.localStorage.setItem(STORAGE_KEY, "/login");
    harness("/");
    expect(screen.getByTestId("path")).toHaveTextContent("/");
  });

  it("does not redirect when stored value looks like an external URL", () => {
    window.localStorage.setItem(STORAGE_KEY, "//evil.example.com/path");
    harness("/");
    expect(screen.getByTestId("path")).toHaveTextContent("/");
  });

  it("does not redirect when no stored route exists", () => {
    harness("/");
    expect(screen.getByTestId("path")).toHaveTextContent("/");
  });

  it("does not redirect when the current path is not exactly /", () => {
    window.localStorage.setItem(STORAGE_KEY, "/cards");
    harness("/receipts");
    expect(screen.getByTestId("path")).toHaveTextContent("/receipts");
  });

  it("ignores localStorage read errors", () => {
    const spy = vi
      .spyOn(window.localStorage.__proto__, "getItem")
      .mockImplementation(() => {
        throw new Error("denied");
      });
    expect(() => harness("/")).not.toThrow();
    spy.mockRestore();
  });
});

import { describe, it, expect } from "vitest";
import { render, screen, renderHook, act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, useSearchParams, useNavigationType } from "react-router";
import type { ReactNode } from "react";
import { useReportSearchParams } from "./useReportSearchParams";

interface TestValues {
  foo: string;
  count: number;
}

function parseTestValues(params: URLSearchParams): TestValues {
  return {
    foo: params.get("foo") ?? "default-foo",
    count: Number(params.get("count") ?? "0"),
  };
}

function createWrapper(route: string = "/") {
  return function Wrapper({ children }: { children: ReactNode }) {
    return <MemoryRouter initialEntries={[route]}>{children}</MemoryRouter>;
  };
}

describe("useReportSearchParams", () => {
  it("parses defaults when no search params are present", () => {
    const { result } = renderHook(
      () => useReportSearchParams(parseTestValues),
      { wrapper: createWrapper() },
    );
    expect(result.current[0]).toEqual({ foo: "default-foo", count: 0 });
  });

  it("parses values from the URL", () => {
    const { result } = renderHook(
      () => useReportSearchParams(parseTestValues),
      { wrapper: createWrapper("/?foo=bar&count=3") },
    );
    expect(result.current[0]).toEqual({ foo: "bar", count: 3 });
  });

  it("update() writes a new value", () => {
    const { result } = renderHook(
      () => useReportSearchParams(parseTestValues),
      { wrapper: createWrapper("/?foo=bar") },
    );

    act(() => {
      result.current[1]({ foo: "baz" });
    });

    expect(result.current[0].foo).toBe("baz");
  });

  it("update() merges a patch without clobbering other tracked fields", () => {
    const { result } = renderHook(
      () => useReportSearchParams(parseTestValues),
      { wrapper: createWrapper("/?foo=bar&count=5") },
    );

    act(() => {
      result.current[1]({ foo: "baz" });
    });

    expect(result.current[0]).toEqual({ foo: "baz", count: 5 });
  });

  it("update() with undefined removes the key, reverting to the parsed default", () => {
    const { result } = renderHook(
      () => useReportSearchParams(parseTestValues),
      { wrapper: createWrapper("/?foo=bar") },
    );

    act(() => {
      result.current[1]({ foo: undefined });
    });

    expect(result.current[0].foo).toBe("default-foo");
  });

  it("stringifies numeric and boolean patch values", async () => {
    const user = userEvent.setup();

    function Harness() {
      const [, update] = useReportSearchParams(parseTestValues);
      const [searchParams] = useSearchParams();
      return (
        <div>
          <span data-testid="raw-count">{searchParams.get("count")}</span>
          <span data-testid="raw-flag">{searchParams.get("flag")}</span>
          <button
            onClick={() => update({ count: 7, flag: true })}
          >
            update
          </button>
        </div>
      );
    }

    render(<Harness />, { wrapper: createWrapper() });
    expect(screen.getByTestId("raw-count")).toBeEmptyDOMElement();

    await user.click(screen.getByRole("button", { name: "update" }));

    expect(screen.getByTestId("raw-count")).toHaveTextContent("7");
    expect(screen.getByTestId("raw-flag")).toHaveTextContent("true");
  });

  it("preserves unrelated search params (e.g. the report slug) across an update", async () => {
    const user = userEvent.setup();

    function Harness() {
      const [, update] = useReportSearchParams(parseTestValues);
      const [searchParams] = useSearchParams();
      return (
        <div>
          <span data-testid="report">{searchParams.get("report")}</span>
          <button onClick={() => update({ foo: "baz" })}>update</button>
        </div>
      );
    }

    render(<Harness />, {
      wrapper: createWrapper("/?report=out-of-balance&foo=bar"),
    });

    await user.click(screen.getByRole("button", { name: "update" }));

    expect(screen.getByTestId("report")).toHaveTextContent("out-of-balance");
  });

  it("uses replace navigation so filter updates do not push a new history entry", async () => {
    const user = userEvent.setup();

    function Harness() {
      const [, update] = useReportSearchParams(parseTestValues);
      const navType = useNavigationType();
      return (
        <div>
          <span data-testid="nav-type">{navType}</span>
          <button onClick={() => update({ foo: "baz" })}>update</button>
        </div>
      );
    }

    render(<Harness />, { wrapper: createWrapper("/?foo=bar") });

    // Initial render of a MemoryRouter reports POP, not REPLACE.
    expect(screen.getByTestId("nav-type")).toHaveTextContent("POP");

    await user.click(screen.getByRole("button", { name: "update" }));

    expect(screen.getByTestId("nav-type")).toHaveTextContent("REPLACE");
  });

  it("returns a referentially stable update function across renders with no URL change", () => {
    const { result, rerender } = renderHook(
      () => useReportSearchParams(parseTestValues),
      { wrapper: createWrapper("/?foo=bar") },
    );
    const firstUpdate = result.current[1];
    rerender();
    expect(result.current[1]).toBe(firstUpdate);
  });

  it("update's identity changes after a call that changes the URL (react-router's setSearchParams contract)", () => {
    // useReportSearchParams wraps setSearchParams in useCallback, but
    // react-router's own setSearchParams re-derives its identity from the
    // current searchParams — so `update` is only stable while the URL is
    // unchanged, not across an update() call that changes it. No consumer
    // in this codebase depends on `update` staying stable across a URL
    // change (no Effect lists it as a dependency), but this test pins the
    // actual contract so a future caller doesn't assume more than it gets.
    const { result } = renderHook(
      () => useReportSearchParams(parseTestValues),
      { wrapper: createWrapper("/?foo=bar") },
    );
    const firstUpdate = result.current[1];

    act(() => {
      result.current[1]({ foo: "baz" });
    });

    expect(result.current[1]).not.toBe(firstUpdate);
  });
});

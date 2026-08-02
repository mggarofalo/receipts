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

  it("stringifies numeric and boolean patch values", () => {
    function Harness() {
      const [, update] = useReportSearchParams(parseTestValues);
      const [searchParams] = useSearchParams();
      return (
        <div>
          <span data-testid="raw-count">{searchParams.get("count")}</span>
          <button onClick={() => update({ count: 7 })}>update</button>
        </div>
      );
    }

    render(<Harness />, { wrapper: createWrapper() });
    expect(screen.getByTestId("raw-count")).toBeEmptyDOMElement();
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

  it("returns a referentially stable update function across renders", () => {
    const { result, rerender } = renderHook(
      () => useReportSearchParams(parseTestValues),
      { wrapper: createWrapper("/?foo=bar") },
    );
    const firstUpdate = result.current[1];
    rerender();
    expect(result.current[1]).toBe(firstUpdate);
  });
});

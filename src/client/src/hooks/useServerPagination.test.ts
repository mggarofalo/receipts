import { describe, it, expect, beforeEach } from "vitest";
import { renderHook, act } from "@testing-library/react";
import { useServerPagination } from "./useServerPagination";

describe("useServerPagination", () => {
  it("persists each table independently across remounts", () => {
    localStorage.clear();
    const receipts = renderHook(() =>
      useServerPagination({ tableId: "receipts" }),
    );
    const purchases = renderHook(() =>
      useServerPagination({ tableId: "purchases" }),
    );
    act(() => receipts.result.current.setPageSize(10));
    act(() => purchases.result.current.setPageSize(100));
    receipts.unmount();
    purchases.unmount();
    expect(
      renderHook(() => useServerPagination({ tableId: "receipts" })).result
        .current.pageSize,
    ).toBe(10);
    expect(
      renderHook(() => useServerPagination({ tableId: "purchases" })).result
        .current.pageSize,
    ).toBe(100);
  });

  it.each([0, -1, 15, NaN, Infinity])(
    "ignores unsupported page size %s without resetting the current page",
    (size) => {
      const { result } = renderHook(() =>
        useServerPagination({ tableId: "receipts", defaultPageSize: 10 }),
      );
      act(() => result.current.setPage(3, 100));
      act(() => result.current.setPageSize(size));
      expect(result.current.pageSize).toBe(10);
      expect(result.current.currentPage).toBe(3);
    },
  );
  beforeEach(() => {
    localStorage.clear();
  });

  it("returns offset 0 and default page size on mount", () => {
    const { result } = renderHook(() =>
      useServerPagination({ tableId: "receipts" }),
    );
    expect(result.current.offset).toBe(0);
    expect(result.current.limit).toBe(25);
    expect(result.current.pageSize).toBe(25);
    expect(result.current.currentPage).toBe(1);
  });

  it("respects custom defaultPageSize", () => {
    const { result } = renderHook(() =>
      useServerPagination({ tableId: "receipts", defaultPageSize: 50 }),
    );
    expect(result.current.limit).toBe(50);
  });

  it("navigates to a specific page", () => {
    const { result } = renderHook(() =>
      useServerPagination({ tableId: "receipts", defaultPageSize: 10 }),
    );
    act(() => result.current.setPage(3, 100));
    expect(result.current.currentPage).toBe(3);
    expect(result.current.offset).toBe(20);
  });

  it("clamps page to minimum of 1", () => {
    const { result } = renderHook(() =>
      useServerPagination({ tableId: "receipts", defaultPageSize: 10 }),
    );
    act(() => result.current.setPage(0, 100));
    expect(result.current.currentPage).toBe(1);
    expect(result.current.offset).toBe(0);
  });

  it("clamps page to maximum", () => {
    const { result } = renderHook(() =>
      useServerPagination({ tableId: "receipts", defaultPageSize: 10 }),
    );
    act(() => result.current.setPage(999, 50));
    expect(result.current.currentPage).toBe(5);
    expect(result.current.offset).toBe(40);
  });

  it("calculates totalPages correctly", () => {
    const { result } = renderHook(() =>
      useServerPagination({ tableId: "receipts", defaultPageSize: 10 }),
    );
    expect(result.current.totalPages(50)).toBe(5);
    expect(result.current.totalPages(51)).toBe(6);
    expect(result.current.totalPages(0)).toBe(1);
  });

  it("resets to offset 0 when page size changes", () => {
    const { result } = renderHook(() =>
      useServerPagination({ tableId: "receipts", defaultPageSize: 10 }),
    );
    act(() => result.current.setPage(3, 100));
    expect(result.current.offset).toBe(20);

    act(() => result.current.setPageSize(25));
    expect(result.current.offset).toBe(0);
    expect(result.current.currentPage).toBe(1);
    expect(result.current.limit).toBe(25);
  });

  it("uses persisted page size from localStorage when available", () => {
    localStorage.setItem("table-page-size", "50");
    const { result } = renderHook(() =>
      useServerPagination({ tableId: "receipts", defaultPageSize: 10 }),
    );
    expect(result.current.limit).toBe(50);
    expect(result.current.pageSize).toBe(50);
  });

  it("falls back to defaultPageSize when nothing is persisted", () => {
    const { result } = renderHook(() =>
      useServerPagination({ tableId: "receipts", defaultPageSize: 10 }),
    );
    expect(result.current.limit).toBe(10);
  });

  it("persists new page size to localStorage via setPageSize", () => {
    const { result } = renderHook(() =>
      useServerPagination({ tableId: "receipts", defaultPageSize: 10 }),
    );
    act(() => result.current.setPageSize(50));
    expect(localStorage.getItem("table-page-size:receipts")).toBe("50");
    expect(result.current.limit).toBe(50);
  });

  it("resets offset when sortBy changes", () => {
    const { result, rerender } = renderHook(
      ({ sortBy }) =>
        useServerPagination({
          tableId: "receipts",
          defaultPageSize: 10,
          sortBy,
          sortDirection: "asc",
        }),
      { initialProps: { sortBy: "name" } },
    );
    act(() => result.current.setPage(3, 100));
    expect(result.current.offset).toBe(20);

    rerender({ sortBy: "date" });
    expect(result.current.offset).toBe(0);
    expect(result.current.currentPage).toBe(1);
  });

  it("resets offset when sortDirection changes", () => {
    const { result, rerender } = renderHook(
      ({ sortDirection }: { sortDirection: "asc" | "desc" }) =>
        useServerPagination({
          tableId: "receipts",
          defaultPageSize: 10,
          sortBy: "name",
          sortDirection,
        }),
      { initialProps: { sortDirection: "asc" as "asc" | "desc" } },
    );
    act(() => result.current.setPage(3, 100));
    expect(result.current.offset).toBe(20);

    rerender({ sortDirection: "desc" });
    expect(result.current.offset).toBe(0);
    expect(result.current.currentPage).toBe(1);
  });

  it("does not reset offset on initial mount with sort params", () => {
    const { result } = renderHook(() =>
      useServerPagination({
        tableId: "receipts",
        defaultPageSize: 10,
        sortBy: "name",
        sortDirection: "asc",
      }),
    );
    expect(result.current.offset).toBe(0);
    expect(result.current.currentPage).toBe(1);
  });

  it("does not reset offset when sort params stay the same", () => {
    const { result, rerender } = renderHook(
      ({ sortBy }) =>
        useServerPagination({
          tableId: "receipts",
          defaultPageSize: 10,
          sortBy,
          sortDirection: "asc",
        }),
      { initialProps: { sortBy: "name" } },
    );
    act(() => result.current.setPage(3, 100));
    expect(result.current.offset).toBe(20);

    rerender({ sortBy: "name" });
    expect(result.current.offset).toBe(20);
  });

  it("works without sort params (backward compatible)", () => {
    const { result } = renderHook(() =>
      useServerPagination({ tableId: "receipts", defaultPageSize: 10 }),
    );
    act(() => result.current.setPage(3, 100));
    expect(result.current.offset).toBe(20);
    expect(result.current.currentPage).toBe(3);
  });
});

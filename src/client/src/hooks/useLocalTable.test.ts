import { act, renderHook } from "@testing-library/react";
import { useLocalTable } from "./useLocalTable";

beforeEach(() => localStorage.clear());
it("filters the complete cache then sorts before pagination without mutating input", () => {
  const rows = Object.freeze(
    Array.from({ length: 30 }, (_, index) =>
      Object.freeze({ id: index, price: 30 - index, active: index % 2 === 0 }),
    ),
  );
  const filter = vi.fn((row: (typeof rows)[number]) => row.active);
  const compare = vi.fn(
    (left: (typeof rows)[number], right: (typeof rows)[number]) =>
      left.price - right.price,
  );
  const { result } = renderHook(() =>
    useLocalTable({
      tableId: "keys",
      rows,
      filter,
      compare,
      resetKey: "active:price:asc",
      defaultPageSize: 10,
    }),
  );
  expect(result.current.total).toBe(15);
  expect(result.current.items.map((row) => row.id)).toEqual([
    28, 26, 24, 22, 20, 18, 16, 14, 12, 10,
  ]);
  const comparisons = compare.mock.calls.length;
  act(() => result.current.setPage(2));
  expect(result.current.items.map((row) => row.id)).toEqual([8, 6, 4, 2, 0]);
  expect(filter).toHaveBeenCalledTimes(30);
  expect(compare).toHaveBeenCalledTimes(comparisons);
  expect(rows.map((row) => row.id)).toEqual(
    Array.from({ length: 30 }, (_, index) => index),
  );
});
it("keeps equal-sort rows stable and immediately resets paging when the projection changes", () => {
  const rows = Array.from({ length: 22 }, (_, id) => ({ id, rank: 1 }));
  const compare = () => 0;
  const { result, rerender } = renderHook(
    ({ resetKey }) =>
      useLocalTable({
        tableId: "keys",
        rows,
        compare,
        resetKey,
        defaultPageSize: 10,
      }),
    { initialProps: { resetKey: "all" } },
  );
  act(() => result.current.setPage(3));
  expect(result.current.items.map((row) => row.id)).toEqual([20, 21]);
  rerender({ resetKey: "new-sort" });
  expect(result.current.currentPage).toBe(1);
  expect(result.current.items.map((row) => row.id)).toEqual([
    0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
  ]);
  rerender({ resetKey: "all" });
  expect(result.current.currentPage).toBe(1);
  expect(result.current.items[0].id).toBe(0);
});
it("clamps after cache deletion and handles an empty collection", () => {
  const { result, rerender } = renderHook(
    ({ rows }) =>
      useLocalTable({
        tableId: "keys",
        rows,
        resetKey: "all",
        defaultPageSize: 10,
      }),
    { initialProps: { rows: Array.from({ length: 22 }, (_, id) => id) } },
  );
  act(() => result.current.setPage(3));
  rerender({ rows: [1, 2] });
  expect(result.current.currentPage).toBe(1);
  expect(result.current.items).toEqual([1, 2]);
  rerender({ rows: [] });
  expect(result.current.total).toBe(0);
  expect(result.current.totalPages).toBe(1);
  expect(result.current.items).toEqual([]);
});
it("validates and remembers local page size without changing another table", () => {
  const rows = Array.from({ length: 35 }, (_, id) => id);
  const first = renderHook(() =>
    useLocalTable({
      tableId: "keys",
      rows,
      resetKey: "all",
      defaultPageSize: 10,
    }),
  );
  act(() => first.result.current.setPage(3));
  act(() => first.result.current.setPageSize(0));
  expect(first.result.current.currentPage).toBe(3);
  act(() => first.result.current.setPageSize(25));
  expect(first.result.current.currentPage).toBe(1);
  first.unmount();
  expect(
    renderHook(() =>
      useLocalTable({
        tableId: "keys",
        rows,
        resetKey: "all",
        defaultPageSize: 10,
      }),
    ).result.current.pageSize,
  ).toBe(25);
  expect(
    renderHook(() =>
      useLocalTable({
        tableId: "other",
        rows,
        resetKey: "all",
        defaultPageSize: 10,
      }),
    ).result.current.pageSize,
  ).toBe(10);
});

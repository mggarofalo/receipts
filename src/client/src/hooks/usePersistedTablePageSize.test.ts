import { act, renderHook } from "@testing-library/react";
import { findStorageMethodOwner } from "@/test/storage-spy";
import { usePersistedTablePageSize } from "./usePersistedTablePageSize";
beforeEach(() => localStorage.clear());
it("hydrates a table-specific choice ahead of the legacy fallback", () => {
  localStorage.setItem("table-page-size", "50");
  localStorage.setItem("table-page-size:keys", "10");
  expect(
    renderHook(() => usePersistedTablePageSize("keys")).result.current.pageSize,
  ).toBe(10);
  expect(
    renderHook(() => usePersistedTablePageSize("other")).result.current
      .pageSize,
  ).toBe(50);
});
it("preserves independent choices after remount", () => {
  const keys = renderHook(() => usePersistedTablePageSize("keys"));
  act(() => keys.result.current.setPageSize(100));
  keys.unmount();
  expect(
    renderHook(() => usePersistedTablePageSize("keys")).result.current.pageSize,
  ).toBe(100);
  expect(
    renderHook(() => usePersistedTablePageSize("other")).result.current
      .pageSize,
  ).toBe(25);
});
it("rejects invalid sizes and remains usable if persistence fails", () => {
  const { result } = renderHook(() => usePersistedTablePageSize("keys"));
  act(() => result.current.setPageSize(NaN));
  expect(result.current.pageSize).toBe(25);
  const spy = vi
    .spyOn(findStorageMethodOwner(localStorage, "setItem"), "setItem")
    .mockImplementation(() => {
      throw new Error("denied");
    });
  try {
    act(() => result.current.setPageSize(50));
    expect(result.current.pageSize).toBe(50);
  } finally {
    spy.mockRestore();
  }
});

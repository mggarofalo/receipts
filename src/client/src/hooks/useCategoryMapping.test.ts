import type { Mock } from "vitest";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { act, renderHook, waitFor } from "@testing-library/react";
import { createQueryWrapper } from "@/test/test-utils";
import client from "@/lib/api-client";
import { useCategoryMapping } from "./useCategoryMapping";
vi.mock("@/lib/api-client", () => ({
  default: { POST: vi.fn(), GET: vi.fn() },
}));
const post = client.POST as unknown as Mock<
  (path: string, options?: { body?: unknown }) => Promise<unknown>
>;
const source = { location: " Shop ", description: "Milk", itemCode: null };
beforeEach(() => vi.clearAllMocks());
describe("useCategoryMapping", () => {
  it("inspects exact source and loads immutable approval taxonomy without providers", async () => {
    post.mockResolvedValue({
      data: { source, mapping: { taxonomyRevisionId: "old" } },
    } as never);
    vi.mocked(client.GET).mockResolvedValue({
      data: { id: "old", nodes: [] },
    } as never);
    const { result } = renderHook(() => useCategoryMapping(source), {
      wrapper: createQueryWrapper(),
    });
    await waitFor(() =>
      expect(result.current.approvedTaxonomy.isSuccess).toBe(true),
    );
    expect(client.POST).toHaveBeenCalledWith(
      "/api/category-mappings/inspect",
      expect.objectContaining({ body: { source } }),
    );
    expect(client.GET).toHaveBeenCalledWith(
      "/api/taxonomy/revisions/{id}",
      expect.objectContaining({ params: { path: { id: "old" } } }),
    );
    expect(client.GET).toHaveBeenCalledTimes(1);
  });
  it("keeps a failed inspection local and supports retry", async () => {
    post
      .mockResolvedValueOnce({ error: { status: 503 } } as never)
      .mockResolvedValue({ data: { source, mapping: null } } as never);
    const { result } = renderHook(() => useCategoryMapping(source), {
      wrapper: createQueryWrapper(),
    });
    await waitFor(() => expect(result.current.inspection.isError).toBe(true));
    await act(() => result.current.inspection.refetch());
    await waitFor(() =>
      expect(result.current.inspection.data?.mapping).toBeNull(),
    );
  });
  it("writes the complete nullable category contract and refreshes inspection only", async () => {
    post.mockImplementation(
      async (path) =>
        ({
          data: path.endsWith("changes")
            ? { id: "new" }
            : { source, mapping: null },
        }) as never,
    );
    const { result } = renderHook(() => useCategoryMapping(source), {
      wrapper: createQueryWrapper(),
    });
    await waitFor(() => expect(result.current.inspection.isSuccess).toBe(true));
    const body = {
      source,
      sourceFingerprint: "hash",
      expectedMappingRevisionId: null,
      expectedTaxonomyRevisionId: "revision",
      category: null,
      operationId: "operation",
      reason: "Revoke",
    };
    await act(() => result.current.change.mutateAsync(body));
    expect(client.POST).toHaveBeenCalledWith(
      "/api/category-mappings/changes",
      expect.objectContaining({ body }),
    );
    expect(client.GET).not.toHaveBeenCalled();
  });
});

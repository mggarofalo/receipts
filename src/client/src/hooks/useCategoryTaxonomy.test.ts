import { beforeEach, describe, expect, it, vi } from "vitest";
import { renderHook, waitFor } from "@testing-library/react";
import { createQueryWrapper } from "@/test/test-utils";
import client from "@/lib/api-client";
import { useCategoryTaxonomy } from "./useCategoryTaxonomy";

vi.mock("@/lib/api-client", () => ({ default: { GET: vi.fn() } }));

beforeEach(() => vi.clearAllMocks());

describe("useCategoryTaxonomy", () => {
  it("fetches the complete revision without a provider or pagination", async () => {
    const revision = {
      id: "revision",
      nodes: [{ id: "archive", name: "Archive", archived: true }],
    };
    vi.mocked(client.GET).mockResolvedValue({ data: revision } as never);
    const { result } = renderHook(() => useCategoryTaxonomy(), {
      wrapper: createQueryWrapper(),
    });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(revision);
    expect(client.GET).toHaveBeenCalledWith(
      "/api/taxonomy",
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    );
    expect(client.GET).toHaveBeenCalledTimes(1);
  });

  it("surfaces failures locally and supports an explicit retry", async () => {
    const error = { detail: "Taxonomy unavailable" };
    vi.mocked(client.GET)
      .mockResolvedValueOnce({ error } as never)
      .mockResolvedValueOnce({ data: { id: "recovered", nodes: [] } } as never);
    const { result } = renderHook(() => useCategoryTaxonomy(), {
      wrapper: createQueryWrapper(),
    });
    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toEqual(error);
    await result.current.refetch();
    await waitFor(() => expect(result.current.data?.id).toBe("recovered"));
  });

  it("does not fetch while disabled", () => {
    renderHook(() => useCategoryTaxonomy(false), {
      wrapper: createQueryWrapper(),
    });
    expect(client.GET).not.toHaveBeenCalled();
  });
});

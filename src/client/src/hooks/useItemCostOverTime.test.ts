import { renderHook, waitFor } from "@testing-library/react";
import { createQueryWrapper } from "@/test/test-utils";
import {
  useItemDescriptions,
  useItemCostOverTime,
} from "./useItemCostOverTime";

vi.mock("@/lib/api-client", () => ({
  default: {
    GET: vi.fn(),
  },
}));

import client from "@/lib/api-client";
const mockClient = vi.mocked(client);

describe("useItemDescriptions", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("fetches descriptions when search is at least 2 characters", async () => {
    const mockData = {
      items: [
        { description: "Milk", category: "Dairy", occurrences: 10 },
        { description: "Milk Chocolate", category: "Candy", occurrences: 3 },
      ],
    };
    mockClient.GET.mockResolvedValue({
      data: mockData,
      error: undefined,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(
      () => useItemDescriptions({ search: "mi" }),
      { wrapper: createQueryWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(mockData);
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/reports/item-descriptions",
      {
        params: {
          query: {
            search: "mi",
            categoryOnly: undefined,
            limit: undefined,
          },
        },
      },
    );
  });

  it("does not fetch when search is less than 2 characters", () => {
    renderHook(() => useItemDescriptions({ search: "m" }), {
      wrapper: createQueryWrapper(),
    });

    expect(mockClient.GET).not.toHaveBeenCalled();
  });

  it("passes categoryOnly and limit parameters", async () => {
    const mockData = { items: [] };
    mockClient.GET.mockResolvedValue({
      data: mockData,
      error: undefined,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(
      () =>
        useItemDescriptions({
          search: "da",
          categoryOnly: true,
          limit: 10,
        }),
      { wrapper: createQueryWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/reports/item-descriptions",
      {
        params: {
          query: {
            search: "da",
            categoryOnly: true,
            limit: 10,
          },
        },
      },
    );
  });

  it("throws when API returns an error", async () => {
    const apiError = { message: "Server error" };
    mockClient.GET.mockResolvedValue({
      data: undefined,
      error: apiError,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(
      () => useItemDescriptions({ search: "milk" }),
      { wrapper: createQueryWrapper() },
    );

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toEqual(apiError);
  });
});

describe("useItemCostOverTime", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("fetches cost data when description is provided", async () => {
    const mockData = {
      buckets: [
        { period: "2025-01-15", amount: 3.99 },
        { period: "2025-02-20", amount: 4.29 },
      ],
    };
    mockClient.GET.mockResolvedValue({
      data: mockData,
      error: undefined,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(
      () => useItemCostOverTime({ description: "Milk" }),
      { wrapper: createQueryWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(mockData);
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/reports/item-cost-over-time",
      {
        params: {
          query: {
            description: "Milk",
            category: undefined,
            startDate: undefined,
            endDate: undefined,
            granularity: undefined,
          },
        },
      },
    );
  });

  it("fetches cost data when category is provided", async () => {
    const mockData = { buckets: [] };
    mockClient.GET.mockResolvedValue({
      data: mockData,
      error: undefined,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(
      () => useItemCostOverTime({ category: "Dairy" }),
      { wrapper: createQueryWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/reports/item-cost-over-time",
      {
        params: {
          query: {
            description: undefined,
            category: "Dairy",
            startDate: undefined,
            endDate: undefined,
            granularity: undefined,
          },
        },
      },
    );
  });

  it("does not fetch when neither description nor category is provided", () => {
    renderHook(() => useItemCostOverTime({}), {
      wrapper: createQueryWrapper(),
    });

    expect(mockClient.GET).not.toHaveBeenCalled();
  });

  it("passes all parameters including date range and granularity", async () => {
    const mockData = { buckets: [{ period: "2025-01", amount: 4.15 }] };
    mockClient.GET.mockResolvedValue({
      data: mockData,
      error: undefined,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(
      () =>
        useItemCostOverTime({
          description: "Milk",
          startDate: "2025-01-01",
          endDate: "2025-12-31",
          granularity: "monthly",
        }),
      { wrapper: createQueryWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/reports/item-cost-over-time",
      {
        params: {
          query: {
            description: "Milk",
            category: undefined,
            startDate: "2025-01-01",
            endDate: "2025-12-31",
            granularity: "monthly",
          },
        },
      },
    );
  });

  it("throws when API returns an error", async () => {
    const apiError = { message: "Server error" };
    mockClient.GET.mockResolvedValue({
      data: undefined,
      error: apiError,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(
      () => useItemCostOverTime({ description: "Milk" }),
      { wrapper: createQueryWrapper() },
    );

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toEqual(apiError);
  });

  it("fetches cost data when only normalizedDescription is provided", async () => {
    const mockData = { buckets: [{ period: "2025-01", amount: 2.5 }] };
    mockClient.GET.mockResolvedValue({
      data: mockData,
      error: undefined,
      response: {} as Response,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
    } as any);

    const { result } = renderHook(
      () => useItemCostOverTime({ normalizedDescription: "Organic Milk" }),
      { wrapper: createQueryWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual(mockData);
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/reports/item-cost-over-time",
      {
        params: {
          query: {
            description: undefined,
            category: undefined,
            normalizedDescription: "Organic Milk",
            startDate: undefined,
            endDate: undefined,
            granularity: undefined,
          },
        },
      },
    );
  });

  it("does not fetch when description, category, and normalizedDescription are all absent", () => {
    renderHook(() => useItemCostOverTime({ normalizedDescription: undefined }), {
      wrapper: createQueryWrapper(),
    });

    expect(mockClient.GET).not.toHaveBeenCalled();
  });
});

import { renderHook, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { server } from "@/test/msw/server";
import { usePromoteToTemplate } from "./usePromoteToTemplate";
import { createQueryWrapper } from "@/test/test-utils";

describe("usePromoteToTemplate (integration)", () => {
  it("creates a template via POST when no duplicate exists", async () => {
    let postedBody: unknown = null;
    server.use(
      http.get("*/api/item-templates/similar", () =>
        HttpResponse.json([
          {
            name: "Milk (gallon)",
            similarity: 1,
            semanticSimilarity: null,
            combinedScore: 1,
            source: "history",
            defaultCategory: "Food",
            defaultSubcategory: "Dairy",
            defaultUnitPrice: 3.5,
            defaultItemCode: "MILK-GAL",
          },
        ]),
      ),
      http.post("*/api/item-templates", async ({ request }) => {
        postedBody = await request.json();
        return HttpResponse.json(
          {
            id: "11111111-1111-1111-1111-111111111111",
            name: "Milk (gallon)",
          },
          { status: 201 },
        );
      }),
    );

    const { result } = renderHook(() => usePromoteToTemplate(), {
      wrapper: createQueryWrapper(),
    });

    result.current.mutate({
      name: "Milk (gallon)",
      defaultCategory: "Food",
      defaultSubcategory: "Dairy",
      defaultUnitPrice: 3.5,
      defaultItemCode: "MILK-GAL",
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual({
      created: true,
      name: "Milk (gallon)",
    });
    expect(postedBody).toEqual({
      name: "Milk (gallon)",
      defaultCategory: "Food",
      defaultSubcategory: "Dairy",
      defaultUnitPrice: 3.5,
      defaultItemCode: "MILK-GAL",
    });
  });

  it("skips the POST when a template with the same name already exists", async () => {
    let postCalled = false;
    server.use(
      http.get("*/api/item-templates/similar", () =>
        HttpResponse.json([
          {
            name: "MILK (GALLON)",
            similarity: 1,
            semanticSimilarity: null,
            combinedScore: 1,
            source: "template",
            defaultCategory: "Food",
            defaultSubcategory: null,
            defaultUnitPrice: null,
            defaultItemCode: null,
          },
        ]),
      ),
      http.post("*/api/item-templates", () => {
        postCalled = true;
        return HttpResponse.json(
          { id: "11111111-1111-1111-1111-111111111111" },
          { status: 201 },
        );
      }),
    );

    const { result } = renderHook(() => usePromoteToTemplate(), {
      wrapper: createQueryWrapper(),
    });

    result.current.mutate({ name: "Milk (gallon)" });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toEqual({
      created: false,
      name: "Milk (gallon)",
    });
    expect(postCalled).toBe(false);
  });

  it("surfaces API errors from the create request", async () => {
    server.use(
      http.get("*/api/item-templates/similar", () => HttpResponse.json([])),
      http.post("*/api/item-templates", () =>
        HttpResponse.json({ message: "Internal Server Error" }, { status: 500 }),
      ),
    );

    const { result } = renderHook(() => usePromoteToTemplate(), {
      wrapper: createQueryWrapper(),
    });

    result.current.mutate({ name: "Milk (gallon)" });

    await waitFor(() => expect(result.current.isError).toBe(true));
  });
});

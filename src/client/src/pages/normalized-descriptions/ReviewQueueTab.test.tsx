import { Link, useSearchParams } from "react-router";
import { CatalogSourceList } from "./CatalogSourceList";
import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithQueryClient } from "@/test/test-utils";
import { ReviewQueueTab } from "./ReviewQueueTab";
import client from "@/lib/api-client";
import { pendingReviewHref } from "./catalog-review-navigation";
vi.mock("@/lib/api-client", () => ({
  default: { GET: vi.fn(), PUT: vi.fn(), POST: vi.fn(), PATCH: vi.fn() },
}));
const target = "10000000-0000-0000-0000-000000000001";
const source = {
  location: "Store A",
  description: "RAW SOURCE A",
  itemCode: "001",
};
const row = {
  id: target,
  canonicalName: "Different Product B",
  displayName: "Different Product B",
  status: "pendingReview",
  linkedItemCount: 2,
  createdAt: "2026-01-01T00:00:00Z",
  sampleRawDescriptions: [],
};
beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  vi.mocked(client.GET).mockImplementation(
    async (path: string) =>
      ({
        response: new Response(),
        data: path.endsWith("/{id}") ? row : { items: [], totalCount: 0 },
      }) as never,
  );
});
it("loads an exact deep-linked identity without requesting a list page and does not mutate on navigation", async () => {
  renderWithQueryClient(<ReviewQueueTab />, {
    route: pendingReviewHref(target, source),
  });
  expect(await screen.findByText("Different Product B")).toBeInTheDocument();
  expect(client.GET).toHaveBeenCalledWith(
    "/api/normalized-descriptions/{id}",
    expect.objectContaining({ params: { path: { id: target } } }),
  );
  expect(client.GET).not.toHaveBeenCalledWith(
    "/api/normalized-descriptions",
    expect.anything(),
  );
  expect(client.PATCH).not.toHaveBeenCalled();
  expect(client.PUT).not.toHaveBeenCalled();
  expect(client.POST).not.toHaveBeenCalled();
  const href = screen
    .getByRole("link", { name: "Return to source catalog" })
    .getAttribute("href")!;
  const params = new URL(href, "http://test").searchParams;
  expect(JSON.parse(params.get("catalogSource")!)).toEqual(source);
  expect(params.get("tab")).toBe("catalog");
  expect(params.has("reviewTarget")).toBe(false);
});
it.each([404, 503])(
  "shows a retryable error for exact-target %i instead of an empty queue",
  async (status) => {
    vi.mocked(client.GET).mockResolvedValue({
      response: new Response(null, { status }),
    } as never);
    renderWithQueryClient(<ReviewQueueTab />, {
      route: pendingReviewHref(target),
    });
    expect(
      await screen.findByText("Failed to load review queue."),
    ).toBeInTheDocument();
    expect(screen.queryByText("Review Queue Empty")).not.toBeInTheDocument();
    await userEvent
      .setup()
      .click(screen.getByRole("button", { name: /retry/i }));
    await waitFor(() => expect(client.GET).toHaveBeenCalledTimes(2));
  },
);
it("shows loading without actions until the exact target resolves", () => {
  vi.mocked(client.GET).mockReturnValue(new Promise(() => {}));
  renderWithQueryClient(<ReviewQueueTab />, {
    route: pendingReviewHref(target),
  });
  expect(
    screen.queryByRole("button", { name: "Approve" }),
  ).not.toBeInTheDocument();
  expect(screen.queryByText("Review Queue Empty")).not.toBeInTheDocument();
});
it.each(["active", "rejected"])(
  "does not offer approval for an already %s target",
  async (status) => {
    vi.mocked(client.GET).mockResolvedValue({
      response: new Response(),
      data: { ...row, status },
    } as never);
    renderWithQueryClient(<ReviewQueueTab />, {
      route: pendingReviewHref(target, source),
    });
    expect(await screen.findByRole("status")).toHaveTextContent(
      status === "active" ? "Active" : "Rejected",
    );
    expect(
      screen.queryByRole("button", { name: "Approve" }),
    ).not.toBeInTheDocument();
    expect(client.PATCH).not.toHaveBeenCalled();
    expect(client.PUT).not.toHaveBeenCalled();
  },
);
it("sends a submitted search to the server at page zero and reset removes it", async () => {
  const user = userEvent.setup();
  renderWithQueryClient(<ReviewQueueTab />, {
    route: "/admin/normalized-descriptions?tab=review",
  });
  await user.type(
    screen.getByRole("textbox", { name: "Search pending identities" }),
    "  Different Product  ",
  );
  await user.click(screen.getByRole("button", { name: "Search review queue" }));
  await waitFor(() =>
    expect(client.GET).toHaveBeenCalledWith(
      "/api/normalized-descriptions",
      expect.objectContaining({
        params: {
          query: {
            status: ["PendingReview"],
            q: "Different Product",
            offset: 0,
            limit: 50,
          },
        },
      }),
    ),
  );
  expect(
    await screen.findByText("No pending identities match this search."),
  ).toBeInTheDocument();
  await user.click(
    screen.getByRole("button", { name: "Show all pending identities" }),
  );
  expect(
    screen.getByRole("textbox", { name: "Search pending identities" }),
  ).toHaveValue("");
});

it("only explicitly approving mutates the exact target and refetches its authoritative state", async () => {
  let approved = false;
  vi.mocked(client.GET).mockImplementation(
    async () =>
      ({
        response: new Response(),
        data: { ...row, status: approved ? "active" : "pendingReview" },
      }) as never,
  );
  vi.mocked(client.PATCH).mockImplementation(async () => {
    approved = true;
    return { response: new Response(null, { status: 204 }) } as never;
  });
  renderWithQueryClient(<ReviewQueueTab />, {
    route: pendingReviewHref(target, source),
  });
  await userEvent
    .setup()
    .click(await screen.findByRole("button", { name: "Approve" }));
  expect(client.PATCH).toHaveBeenCalledWith(
    "/api/normalized-descriptions/{id}/status",
    { params: { path: { id: target } }, body: { status: "active" } },
  );
  expect(await screen.findByRole("status")).toHaveTextContent("Active");
  expect(client.GET).toHaveBeenCalledTimes(2);
  expect(
    screen.getByRole("link", { name: "Return to source catalog" }),
  ).toBeInTheDocument();
});

it("ignores a late target A response after navigating to target B", async () => {
  let resolveA!: (value: never) => void;
  vi.mocked(client.GET).mockImplementation((_, options) => {
    const id = (options as { params: { path: { id: string } } }).params.path.id;
    if (id === "target-a")
      return new Promise((resolve) => {
        resolveA = resolve;
      });
    return Promise.resolve({ response: new Response(), data: row } as never);
  });
  function Navigation() {
    return (
      <>
        <Link to={pendingReviewHref(target)}>Go to B</Link>
        <ReviewQueueTab />
      </>
    );
  }
  renderWithQueryClient(<Navigation />, {
    route: pendingReviewHref("target-a"),
  });
  await userEvent.setup().click(screen.getByRole("link", { name: "Go to B" }));
  expect(await screen.findByText("Different Product B")).toBeInTheDocument();
  await act(async () =>
    resolveA({
      response: new Response(),
      data: { ...row, id: "target-a", displayName: "Stale Product A" },
    } as never),
  );
  expect(screen.queryByText("Stale Product A")).not.toBeInTheDocument();
  vi.mocked(client.PATCH).mockResolvedValue({
    response: new Response(null, { status: 503 }),
  } as never);
  await userEvent
    .setup()
    .click(screen.getByRole("button", { name: "Approve" }));
  expect(client.PATCH).toHaveBeenCalledWith(
    "/api/normalized-descriptions/{id}/status",
    { params: { path: { id: target } }, body: { status: "active" } },
  );
});
it("recovers from a pending source through explicit approval and returns with fresh Active label eligibility", async () => {
  const user = userEvent.setup();
  let approved = false;
  vi.mocked(client.GET).mockImplementation(async (path: string) => {
    let data: unknown;
    if (path === "/api/normalization-ai/connection")
      data = {
        configured: true,
        isOwner: true,
        credentialsPresent: true,
        busy: false,
        generation: "g",
        models: ["explicit-model"],
      };
    else if (path === "/api/normalization-catalog/sources")
      data = {
        items: [
          {
            source,
            itemCount: 2,
            unassignedCount: 0,
            automaticCount: 2,
            legacyUnknownCount: 0,
            protectedCount: 0,
            mappedCount: 0,
            excludedCount: 0,
            revisableCount: 0,
            linkedTargetCount: 1,
            linkedTargets: [
              {
                id: target,
                name: row.canonicalName,
                status: approved ? "active" : "pendingReview",
              },
            ],
          },
        ],
        total: 1,
      };
    else if (path === "/api/normalized-descriptions/{id}")
      data = { ...row, status: approved ? "active" : "pendingReview" };
    else throw new Error(`Unexpected request ${path}`);
    return { response: new Response(), data } as never;
  });
  vi.mocked(client.PATCH).mockImplementation(async () => {
    approved = true;
    return { response: new Response(null, { status: 204 }) } as never;
  });
  function Workflow() {
    const [params] = useSearchParams();
    return params.get("tab") === "review" ? (
      <ReviewQueueTab />
    ) : (
      <CatalogSourceList onReview={vi.fn()} onStarted={vi.fn()} />
    );
  }
  renderWithQueryClient(<Workflow />, {
    route: "/admin/normalized-descriptions?tab=catalog",
  });
  await user.click(
    await screen.findByRole("checkbox", { name: /Improve display names only/ }),
  );
  expect(
    await screen.findByRole("checkbox", {
      name: "Select RAW SOURCE A at Store A",
    }),
  ).toBeDisabled();
  await user.click(
    screen.getByRole("link", { name: "Review pending identity" }),
  );
  expect(await screen.findByText(row.displayName)).toBeInTheDocument();
  expect(client.PATCH).not.toHaveBeenCalled();
  expect(client.POST).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Approve" }));
  expect(await screen.findByRole("status")).toHaveTextContent("Active");
  await user.click(
    screen.getByRole("link", { name: "Return to source catalog" }),
  );
  await user.click(
    await screen.findByRole("checkbox", { name: /Improve display names only/ }),
  );
  const choice = await screen.findByRole("checkbox", {
    name: "Select RAW SOURCE A at Store A",
  });
  await waitFor(() => expect(choice).toBeEnabled());
  await user.click(choice);
  await user.selectOptions(
    screen.getByRole("combobox", { name: "Catalog suggestion model" }),
    "explicit-model",
  );
  expect(
    screen.getByRole("button", { name: "Suggest readable display names" }),
  ).toBeEnabled();
  expect(
    screen.queryByRole("link", { name: "Review pending identity" }),
  ).not.toBeInTheDocument();
  expect(client.PATCH).toHaveBeenCalledTimes(1);
  expect(client.POST).not.toHaveBeenCalled();
  expect(client.GET).toHaveBeenCalledWith(
    "/api/normalization-catalog/sources",
    expect.objectContaining({
      params: {
        query: expect.objectContaining({
          q: source.description,
          disposition: "all",
          offset: 0,
        }),
      },
    }),
  );
});

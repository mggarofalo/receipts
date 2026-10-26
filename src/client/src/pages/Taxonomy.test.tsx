import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useQueryClient, type QueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
import { renderWithQueryClient } from "@/test/test-utils";
import mockClient, { resetMockClient } from "@/test/mock-api-client";
import type { components } from "@/generated/api";
import Taxonomy from "./Taxonomy";
vi.mock("@/lib/api-client", () => ({ default: mockClient }));
const permissions = vi.hoisted(() => ({ admin: true }));
vi.mock("@/hooks/usePermission", () => ({
  usePermission: () => ({ isAdmin: () => permissions.admin }),
}));
type Revision = components["schemas"]["TaxonomyRevisionResponse"];
const revision: Revision = {
  id: "rev-1",
  sequence: 2,
  previousRevisionId: "rev-0",
  recordedAt: "2026-10-25T12:00:00Z",
  actor: "admin",
  reason: "Set up categories",
  operationId: "op-1",
  affectedCount: 5,
  nodes: [
    { id: "food", name: "Food", archived: false },
    { id: "dairy", name: "Dairy", parentId: "food", archived: false },
    { id: "milk", name: "Milk", parentId: "dairy", archived: false },
    { id: "plant", name: "Plant", archived: false },
    { id: "plant-milk", name: "Milk", parentId: "plant", archived: false },
    { id: "old", name: "Old", parentId: "food", archived: true },
  ],
};
const success = (data: Revision) => ({
  data,
  error: undefined,
  response: new Response(null, { status: 200 }),
});
const failure = (status: number, detail: string) => ({
  data: undefined,
  error: { status, detail },
  response: new Response(null, { status }),
});
async function renderTree(route = "/categories") {
  renderWithQueryClient(<Taxonomy />, { route });
  await screen.findByRole("tree");
  await userEvent.click(screen.getByRole("button", { name: "Expand all" }));
}
async function open(action: string, path: string) {
  await userEvent.click(
    screen.getByRole("button", { name: `${action} category: ${path}` }),
  );
  return within(screen.getByRole("dialog"));
}
beforeEach(() => {
  resetMockClient();
  permissions.admin = true;
  mockClient.GET.mockResolvedValue(success(revision));
  mockClient.POST.mockResolvedValue(
    success({ ...revision, id: "rev-2", sequence: 3 }),
  );
});
describe("Category tree", () => {
  it("shows loading and retries a failed read", async () => {
    mockClient.GET.mockResolvedValueOnce(failure(503, "Tree unavailable"));
    renderWithQueryClient(<Taxonomy />);
    expect(screen.getByRole("status")).toHaveTextContent("Loading");
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Tree unavailable",
    );
    await userEvent.click(screen.getByRole("button", { name: "Retry" }));
    expect(
      await screen.findByRole("treeitem", { name: "Food" }),
    ).toBeInTheDocument();
  });
  it("searches complete paths and distinguishes duplicate labels", async () => {
    await renderTree();
    await userEvent.type(
      screen.getByRole("textbox", { name: "Search category paths" }),
      "milk",
    );
    const list = within(screen.getByRole("tree", { name: "Categories" }));
    expect(
      list.getByRole("treeitem", { name: "Food / Dairy / Milk" }),
    ).toBeInTheDocument();
    expect(
      list.getByRole("treeitem", { name: "Plant / Milk" }),
    ).toBeInTheDocument();
    expect(list.getByRole("treeitem", { name: "Food" })).toBeInTheDocument();
  });
  it("supports deep paths without truncation", async () => {
    let parentId: string | undefined;
    const nodes = Array.from({ length: 80 }, (_, i) => {
      const node = {
        id: `node-${i}`,
        name: `Level${i}`,
        parentId,
        archived: false,
      };
      parentId = node.id;
      return node;
    });
    mockClient.GET.mockResolvedValue(success({ ...revision, nodes }));
    renderWithQueryClient(<Taxonomy />);
    await screen.findByRole("tree");
    await userEvent.type(
      screen.getByRole("textbox", { name: "Search category paths" }),
      "Level79",
    );
    expect(
      screen.getByRole("treeitem", {
        name: nodes.map((n) => n.name).join(" / "),
      }),
    ).toHaveAttribute("aria-level", "80");
  });
  it("presents one Categories page even when the tree is empty", async () => {
    mockClient.GET.mockResolvedValue(success({ ...revision, nodes: [] }));
    renderWithQueryClient(<Taxonomy />);
    expect(
      await screen.findByText("No categories yet."),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("heading", { name: "Categories" }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("link", { name: "Subcategories" }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByText(/legacy data is migrated/),
    ).not.toBeInTheDocument();
  });
  it("hides every editing action for readers", async () => {
    permissions.admin = false;
    await renderTree();
    expect(
      screen.queryByRole("button", { name: "New category" }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: /Rename category/ }),
    ).not.toBeInTheDocument();
    expect(mockClient.POST).not.toHaveBeenCalled();
  });
  it("loads history as read-only with revision details on demand", async () => {
    await renderTree("/categories?revision=rev-1");
    expect(mockClient.GET).toHaveBeenCalledWith(
      "/api/taxonomy/revisions/{id}",
      expect.objectContaining({
        params: { path: { id: "rev-1" } },
        signal: expect.any(AbortSignal),
      }),
    );
    expect(
      screen.getByRole("link", { name: "Back to categories" }),
    ).toHaveAttribute("href", "/categories");
    await userEvent.click(screen.getByText("Revision details · 2"));
    expect(screen.getByText("Set up categories")).toBeVisible();
    expect(
      screen.getByRole("link", { name: "Previous revision" }),
    ).toHaveAttribute("href", "/categories?revision=rev-0");
    expect(
      screen.queryByRole("button", { name: "New category" }),
    ).not.toBeInTheDocument();
  });
  it("creates under a full path against the expected revision", async () => {
    await renderTree();
    await userEvent.click(screen.getByRole("button", { name: "New category" }));
    const d = within(screen.getByRole("dialog"));
    await userEvent.type(d.getByLabelText("Name"), "Cream");
    await userEvent.selectOptions(d.getByLabelText("Parent"), "dairy");
    await userEvent.type(d.getByLabelText("Reason"), "Missing");
    await userEvent.click(d.getByRole("button", { name: "New category" }));
    await waitFor(() =>
      expect(mockClient.POST).toHaveBeenCalledWith(
        "/api/taxonomy/changes",
        expect.objectContaining({
          body: {
            expectedRevisionId: "rev-1",
            operationId: expect.any(String),
            reason: "Missing",
            changes: [
              {
                kind: "create",
                categoryId: expect.any(String),
                parentId: "dairy",
                name: "Cream",
              },
            ],
          },
        }),
      ),
    );
  });
  it.each(["before submit", "while saving"])(
    "keeps the saved revision after a late background read started %s",
    async (readTiming) => {
      let cache!: QueryClient;
      function CacheProbe() {
        const queryClient = useQueryClient();
        useEffect(() => {
          cache = queryClient;
        }, [queryClient]);
        return null;
      }
      let finishRead!: (value: ReturnType<typeof success>) => void;
      const oldRead = new Promise<ReturnType<typeof success>>((resolve) => {
        finishRead = resolve;
      });
      let finishSave!: (value: ReturnType<typeof success>) => void;
      mockClient.POST.mockReturnValueOnce(
        new Promise<ReturnType<typeof success>>((resolve) => {
          finishSave = resolve;
        }),
      );
      renderWithQueryClient(
        <>
          <CacheProbe />
          <Taxonomy />
        </>,
      );
      await screen.findByRole("tree");
      const d = await open("Rename", "Food");
      await userEvent.clear(d.getByLabelText("Name"));
      await userEvent.type(d.getByLabelText("Name"), "Groceries");
      await userEvent.type(d.getByLabelText("Reason"), "Clarify");
      mockClient.GET.mockReturnValueOnce(oldRead);
      let pendingRead!: Promise<void>;
      function startRead() {
        act(() => {
          pendingRead = cache.refetchQueries({
            queryKey: ["taxonomy", "current"],
            exact: true,
          });
        });
      }
      if (readTiming === "before submit") startRead();
      await userEvent.click(d.getByRole("button", { name: "Rename category" }));
      await waitFor(() => expect(mockClient.POST).toHaveBeenCalledOnce());
      if (readTiming === "while saving") startRead();
      await waitFor(() => expect(mockClient.GET).toHaveBeenCalledTimes(2));
      const readSignal = mockClient.GET.mock.calls[1][1]!.signal!;
      const saved = {
        ...revision,
        id: "rev-2",
        sequence: 3,
        nodes: revision.nodes.map((node) =>
          node.id === "food" ? { ...node, name: "Groceries" } : node,
        ),
      };
      await act(async () => finishSave(success(saved)));
      await waitFor(() =>
        expect(screen.queryByRole("dialog")).not.toBeInTheDocument(),
      );
      expect(
        screen.getByRole("treeitem", { name: "Groceries" }),
      ).toBeInTheDocument();
      // Resolve even if cancelled: a transport completing late must not replace
      // the authoritative revision returned by the successful change.
      await act(async () => {
        finishRead(success(revision));
        await pendingRead;
      });
      expect(cache.getQueryData(["taxonomy", "current"])).toEqual(saved);
      expect(
        screen.getByRole("treeitem", { name: "Groceries" }),
      ).toBeInTheDocument();
      expect(screen.getByText("Revision details · 3")).toBeInTheDocument();
      expect(readSignal.aborted).toBe(true);
    },
  );
  it("discloses rename scope and saves the name", async () => {
    await renderTree();
    const d = await open("Rename", "Food / Dairy");
    expect(d.getByText(/2 categories affected/)).toBeInTheDocument();
    await userEvent.clear(d.getByLabelText("Name"));
    await userEvent.type(d.getByLabelText("Name"), "Dairy foods");
    await userEvent.type(d.getByLabelText("Reason"), "Clarify");
    await userEvent.click(d.getByRole("button", { name: "Rename category" }));
    await waitFor(() =>
      expect(mockClient.POST).toHaveBeenCalledWith(
        "/api/taxonomy/changes",
        expect.objectContaining({
          body: expect.objectContaining({
            changes: [
              { kind: "rename", categoryId: "dairy", name: "Dairy foods" },
            ],
          }),
        }),
      ),
    );
  });
  it("excludes descendants from move targets and surfaces cycle errors", async () => {
    mockClient.POST.mockResolvedValueOnce(
      failure(400, "The change would create a cycle."),
    );
    await renderTree();
    const d = await open("Move", "Food");
    expect(
      d.queryByRole("option", { name: "Food / Dairy" }),
    ).not.toBeInTheDocument();
    await userEvent.selectOptions(d.getByLabelText("Parent"), "plant");
    await userEvent.type(d.getByLabelText("Reason"), "Reorganize");
    await userEvent.click(d.getByRole("button", { name: "Move category" }));
    expect(await d.findByRole("alert")).toHaveTextContent(
      "The change would create a cycle.",
    );
  });
  it("discloses subtree archive count and single-node restore", async () => {
    await renderTree();
    const d = await open("Archive", "Food");
    expect(d.getByText(/3 categories affected/)).toHaveTextContent(
      "entire subtree",
    );
    await userEvent.type(d.getByLabelText("Reason"), "Retire");
    await userEvent.click(d.getByRole("button", { name: "Archive category" }));
    await waitFor(() =>
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument(),
    );
    await userEvent.click(screen.getByLabelText("Show archived"));
    const r = await open("Restore", "Food / Old");
    expect(r.getByText(/1 category affected/)).toHaveTextContent(
      "Only this category",
    );
    await userEvent.type(r.getByLabelText("Reason"), "Needed");
    await userEvent.click(r.getByRole("button", { name: "Restore category" }));
    await waitFor(() =>
      expect(mockClient.POST).toHaveBeenLastCalledWith(
        "/api/taxonomy/changes",
        expect.objectContaining({
          body: expect.objectContaining({
            changes: [{ kind: "restore", categoryId: "old" }],
          }),
        }),
      ),
    );
  });
  it("blocks stale retries and refreshes before reviewing the new scope", async () => {
    mockClient.POST.mockResolvedValueOnce(
      failure(409, "The expected revision is stale."),
    );
    await renderTree();
    const d = await open("Rename", "Food");
    await userEvent.type(d.getByLabelText("Reason"), "Update");
    await userEvent.click(d.getByRole("button", { name: "Rename category" }));
    expect(await d.findByRole("alert")).toHaveTextContent(
      "The expected revision is stale.",
    );
    expect(d.getByRole("button", { name: "Retry change" })).toBeDisabled();
    mockClient.GET.mockResolvedValue(
      success({ ...revision, id: "rev-new", sequence: 3 }),
    );
    await userEvent.click(d.getByRole("button", { name: "Refresh tree" }));
    await d.findByText(/Your draft is preserved/);
    expect(d.getByLabelText("Reason")).toHaveValue("Update");
    await userEvent.click(d.getByRole("button", { name: "Rename category" }));
    await waitFor(() =>
      expect(mockClient.POST).toHaveBeenLastCalledWith(
        "/api/taxonomy/changes",
        expect.objectContaining({
          body: expect.objectContaining({ expectedRevisionId: "rev-new" }),
        }),
      ),
    );
  });
  it("retries uncertain failures with the exact same operation body", async () => {
    mockClient.POST.mockRejectedValueOnce(new Error("Network interrupted"));
    await renderTree();
    const d = await open("Archive", "Food");
    await userEvent.type(d.getByLabelText("Reason"), "Retire");
    await userEvent.click(d.getByRole("button", { name: "Archive category" }));
    await d.findByRole("alert");
    await userEvent.click(d.getByRole("button", { name: "Retry change" }));
    await waitFor(() => expect(mockClient.POST).toHaveBeenCalledTimes(2));
    expect(mockClient.POST.mock.calls[1][1].body).toEqual(
      mockClient.POST.mock.calls[0][1].body,
    );
  });
  it("supports opening and cancelling with the keyboard", async () => {
    await renderTree();
    const button = screen.getByRole("button", {
      name: "Rename category: Food",
    });
    button.focus();
    await userEvent.keyboard("{Enter}");
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    await userEvent.keyboard("{Escape}");
    await waitFor(() =>
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument(),
    );
    await waitFor(() => expect(button).toHaveFocus());
  });
  it.each(["Cancel", "Close"])(
    "returns focus to the opener after %s",
    async (dismiss) => {
      await renderTree();
      const button = screen.getByRole("button", {
        name: "Move category: Food / Dairy",
      });
      await userEvent.click(button);
      await userEvent.click(
        within(screen.getByRole("dialog")).getByRole("button", {
          name: dismiss,
        }),
      );
      await waitFor(() => expect(button).toHaveFocus());
    },
  );
  it("returns focus to the renamed row after saving", async () => {
    mockClient.POST.mockResolvedValueOnce(
      success({
        ...revision,
        id: "rev-2",
        nodes: revision.nodes.map((node) =>
          node.id === "food" ? { ...node, name: "Groceries" } : node,
        ),
      }),
    );
    await renderTree();
    const d = await open("Rename", "Food");
    await userEvent.clear(d.getByLabelText("Name"));
    await userEvent.type(d.getByLabelText("Name"), "Groceries");
    await userEvent.type(d.getByLabelText("Reason"), "Clarify");
    await userEvent.click(d.getByRole("button", { name: "Rename category" }));
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Rename category: Groceries" }),
      ).toHaveFocus(),
    );
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });
  it("focuses New category when an archived subtree removes the opener", async () => {
    mockClient.POST.mockResolvedValueOnce(
      success({
        ...revision,
        id: "rev-2",
        nodes: revision.nodes.map((node) =>
          ["food", "dairy", "milk"].includes(node.id)
            ? { ...node, archived: true }
            : node,
        ),
      }),
    );
    await renderTree();
    const d = await open("Archive", "Food");
    await userEvent.type(d.getByLabelText("Reason"), "Retire");
    await userEvent.click(d.getByRole("button", { name: "Archive category" }));
    await waitFor(() =>
      expect(
        screen.queryByRole("button", { name: "Archive category: Food" }),
      ).not.toBeInTheDocument(),
    );
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "New category" }),
      ).toHaveFocus(),
    );
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });
});

it("invalidates current dashboard hierarchy after a committed tree edit and retains recorded buckets", async () => {
  let cache: QueryClient;
  function Seed() {
    const queryClient = useQueryClient();
    useEffect(() => {
      cache = queryClient;
      queryClient.setQueryDefaults(["dashboard", "spending-by-category"], {
        gcTime: Infinity,
      });
      queryClient.setQueryData(
        ["dashboard", "spending-by-category", "currentHierarchy"],
        { items: [] },
      );
      queryClient.setQueryData(
        ["dashboard", "spending-by-category", "asClassified"],
        { items: [] },
      );
    }, [queryClient]);
    return null;
  }
  renderWithQueryClient(
    <>
      <Seed />
      <Taxonomy />
    </>,
  );
  await screen.findByRole("tree");
  await userEvent.click(screen.getByRole("button", { name: "New category" }));
  const dialog = within(screen.getByRole("dialog"));
  await userEvent.type(dialog.getByLabelText("Name"), "New root");
  await userEvent.type(dialog.getByLabelText("Reason"), "Testing invalidation");
  await userEvent.click(dialog.getByRole("button", { name: "New category" }));
  await waitFor(() =>
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument(),
  );
  expect(
    cache!.getQueryState([
      "dashboard",
      "spending-by-category",
      "currentHierarchy",
    ])?.isInvalidated,
  ).toBe(true);
  expect(
    cache!.getQueryState(["dashboard", "spending-by-category", "asClassified"])
      ?.isInvalidated,
  ).toBe(false);
});

import "@/test/setup-combobox-polyfills";
import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createRef } from "react";
import type { CategoryRevision } from "@/lib/category-paths";
import { renderWithQueryClient } from "@/test/test-utils";
import mockClient, { resetMockClient } from "@/test/mock-api-client";
import { CategoryPathPicker } from "./CategoryPathPicker";

vi.mock("@/lib/api-client", () => ({ default: mockClient }));

const revision: CategoryRevision = {
  id: "revision",
  sequence: 1,
  recordedAt: "2026-10-26T00:00:00Z",
  actor: "admin",
  reason: "Initial",
  operationId: "operation",
  affectedCount: 4,
  nodes: [
    { id: "food", name: "Food", archived: false },
    { id: "fruit", parentId: "food", name: "Fruit", archived: false },
    { id: "apple", parentId: "fruit", name: "Apple", archived: false },
    { id: "archived", name: "Old", archived: true },
  ],
};

describe("CategoryPathPicker", () => {
  beforeEach(() => resetMockClient());
  it("groups duplicate leaf names by root and selects the searched path with the keyboard", async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    const otherRoot = { id: "home", name: "Home", archived: false };
    const otherApple = {
      id: "home-apple",
      parentId: "home",
      name: "Apple",
      archived: false,
    };
    render(
      <CategoryPathPicker
        revision={{
          ...revision,
          nodes: [...revision.nodes, otherRoot, otherApple],
        }}
        onValueChange={onValueChange}
      />,
    );
    await user.click(screen.getByRole("combobox"));
    expect(
      screen.getByRole("option", { name: "Food / Fruit / Apple" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("option", { name: "Home / Apple" }),
    ).toBeInTheDocument();
    await user.type(
      screen.getByPlaceholderText("Search category paths…"),
      "Home Apple",
    );
    expect(
      screen.queryByRole("option", { name: "Food / Fruit / Apple" }),
    ).not.toBeInTheDocument();
    await user.keyboard("{ArrowDown}{Enter}");
    expect(onValueChange).toHaveBeenCalledWith({
      categoryId: "home-apple",
      taxonomyRevisionId: revision.id,
      path: [otherRoot, otherApple],
    });
    expect(screen.queryByRole("option")).not.toBeInTheDocument();
  });

  it("searches full paths and returns stable category and revision IDs", async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    render(
      <CategoryPathPicker
        revision={revision}
        onValueChange={onValueChange}
        aria-label="Category"
      />,
    );
    await user.click(screen.getByRole("combobox", { name: "Category" }));
    await user.type(
      screen.getByPlaceholderText("Search category paths…"),
      "Food Fruit Apple",
    );
    await user.click(
      screen.getByRole("option", { name: "Food / Fruit / Apple" }),
    );
    expect(onValueChange).toHaveBeenCalledWith({
      categoryId: "apple",
      taxonomyRevisionId: "revision",
      path: revision.nodes.slice(0, 3),
    });
  });

  it("allows parent and explicit unclassified choices without a second review", async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    render(
      <CategoryPathPicker revision={revision} onValueChange={onValueChange} />,
    );
    await user.click(screen.getByRole("combobox"));
    expect(
      screen.queryByRole("option", { name: "Old" }),
    ).not.toBeInTheDocument();
    await user.click(screen.getByRole("option", { name: /^Food$/ }));
    expect(onValueChange).toHaveBeenLastCalledWith({
      categoryId: "food",
      taxonomyRevisionId: "revision",
      path: [revision.nodes[0]],
    });
    await user.click(screen.getByRole("combobox"));
    await user.click(screen.getByRole("option", { name: "Unclassified" }));
    expect(onValueChange).toHaveBeenLastCalledWith({
      categoryId: null,
      taxonomyRevisionId: "revision",
      path: [],
    });
  });

  it("can hide Unclassified for consumers that require a stable category ID", async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    render(
      <CategoryPathPicker
        revision={revision}
        onValueChange={onValueChange}
        allowUnclassified={false}
      />,
    );
    await user.click(screen.getByRole("combobox"));
    expect(
      screen.queryByRole("option", { name: "Unclassified" }),
    ).not.toBeInTheDocument();
    await user.click(
      screen.getByRole("option", { name: "Food / Fruit / Apple" }),
    );
    expect(onValueChange).toHaveBeenCalledWith(
      expect.objectContaining({ categoryId: "apple" }),
    );
  });

  it("preserves the selected historical path through refresh and lookup failures", () => {
    const onValueChange = vi.fn();
    const value = {
      categoryId: "old",
      taxonomyRevisionId: "old-revision",
      path: [{ id: "old", name: "Historical name", archived: false }],
    };
    const ref = createRef<HTMLButtonElement>();
    const { rerender } = render(
      <CategoryPathPicker
        ref={ref}
        revision={revision}
        value={value}
        onValueChange={onValueChange}
        aria-describedby="category-help"
      />,
    );
    expect(screen.getByRole("combobox")).toHaveTextContent("Historical name");
    expect(ref.current).toHaveAttribute("aria-describedby", "category-help");
    rerender(
      <CategoryPathPicker value={value} onValueChange={onValueChange} error />,
    );
    expect(screen.getByRole("combobox")).toHaveTextContent("Historical name");
    expect(screen.getByRole("combobox")).toBeDisabled();
    expect(onValueChange).not.toHaveBeenCalled();
  });

  it("exposes loading and initial error states without inventing a choice", () => {
    const onValueChange = vi.fn();
    const { rerender } = render(
      <CategoryPathPicker onValueChange={onValueChange} loading />,
    );
    expect(screen.getByRole("combobox")).toHaveTextContent(
      "Loading categories…",
    );
    expect(screen.getByRole("combobox")).toHaveAttribute("aria-busy", "true");
    rerender(<CategoryPathPicker onValueChange={onValueChange} error />);
    expect(screen.getByRole("combobox")).toHaveTextContent(
      "Categories unavailable",
    );
    expect(screen.getByRole("combobox")).toBeDisabled();
  });

  it("creates beneath the chosen stable parent, selects the returned path, and keeps draft fields", async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    mockClient.POST.mockImplementation(async (_path, { body }) => ({
      data: {
        ...revision,
        id: "revision-2",
        nodes: [
          ...revision.nodes,
          {
            id: body.changes[0].categoryId,
            parentId: "fruit",
            name: "Berries",
            archived: false,
          },
        ],
      },
      error: undefined,
      response: new Response(null, { status: 200 }),
    }));
    renderWithQueryClient(
      <div>
        <input aria-label="Item description" defaultValue="Fresh berries" />
        <CategoryPathPicker
          revision={revision}
          value={{
            categoryId: "fruit",
            taxonomyRevisionId: revision.id,
            path: revision.nodes.slice(0, 2),
          }}
          onValueChange={onValueChange}
          canCreate
        />
      </div>,
    );
    await user.click(screen.getByRole("combobox"));
    await user.click(screen.getByRole("button", { name: "Create category" }));
    expect(screen.getByRole("combobox", { name: "Parent" })).toHaveValue(
      "fruit",
    );
    await user.type(
      screen.getByRole("textbox", { name: "New category" }),
      "Berries",
    );
    await user.click(screen.getByRole("button", { name: "Create and select" }));
    const createdId =
      mockClient.POST.mock.calls[0][1].body.changes[0].categoryId;
    await waitFor(() =>
      expect(onValueChange).toHaveBeenCalledWith({
        categoryId: createdId,
        taxonomyRevisionId: "revision-2",
        path: [
          revision.nodes[0],
          revision.nodes[1],
          {
            id: createdId,
            parentId: "fruit",
            name: "Berries",
            archived: false,
          },
        ],
      }),
    );
    expect(mockClient.POST.mock.calls[0][1].body.changes[0]).toMatchObject({
      kind: "create",
      parentId: "fruit",
      name: "Berries",
    });
    expect(
      screen.getByRole("textbox", { name: "Item description" }),
    ).toHaveValue("Fresh berries");
  });

  it("refreshes after a revision conflict and requires review before a fresh operation", async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    const refreshed = {
      ...revision,
      id: "revision-2",
      nodes: revision.nodes.map((node) =>
        node.id === "fruit" ? { ...node, archived: true } : node,
      ),
    };
    mockClient.POST.mockResolvedValueOnce({
      data: undefined,
      error: { status: 409, detail: "Revision changed" },
      response: new Response(null, { status: 409 }),
    }).mockImplementationOnce(async (_path, { body }) => ({
      data: {
        ...refreshed,
        id: "revision-3",
        nodes: [
          ...revision.nodes,
          {
            id: body.changes[0].categoryId,
            name: "Household",
            archived: false,
          },
        ],
      },
      error: undefined,
      response: new Response(null, { status: 200 }),
    }));
    mockClient.GET.mockResolvedValue({
      data: refreshed,
      error: undefined,
      response: new Response(null, { status: 200 }),
    });
    renderWithQueryClient(
      <CategoryPathPicker
        revision={revision}
        value={{
          categoryId: "fruit",
          taxonomyRevisionId: revision.id,
          path: revision.nodes.slice(0, 2),
        }}
        canCreate
        onValueChange={onValueChange}
      />,
    );
    await user.click(screen.getByRole("combobox"));
    await user.click(screen.getByRole("button", { name: "Create category" }));
    await user.type(
      screen.getByRole("textbox", { name: "New category" }),
      "Household",
    );
    await user.click(screen.getByRole("button", { name: "Create and select" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Revision changed",
    );
    expect(
      screen.getByRole("button", { name: "Retry creation" }),
    ).toBeDisabled();
    await user.click(screen.getByRole("button", { name: "Refresh tree" }));
    expect(await screen.findByRole("status")).toHaveTextContent(
      "Review the parent",
    );
    expect(screen.getByRole("textbox", { name: "New category" })).toHaveValue(
      "Household",
    );
    expect(screen.getByText("Choose an active parent.")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Create and select" }),
    ).toBeDisabled();
    await user.selectOptions(
      screen.getByRole("combobox", { name: "Parent" }),
      "",
    );
    await user.click(screen.getByRole("button", { name: "Create and select" }));
    await waitFor(() =>
      expect(onValueChange).toHaveBeenCalledWith(
        expect.objectContaining({
          categoryId:
            mockClient.POST.mock.calls[1][1].body.changes[0].categoryId,
          taxonomyRevisionId: "revision-3",
        }),
      ),
    );
    const first = mockClient.POST.mock.calls[0][1].body;
    const second = mockClient.POST.mock.calls[1][1].body;
    expect(second.expectedRevisionId).toBe("revision-2");
    expect(second.operationId).not.toBe(first.operationId);
    expect(second.changes[0].parentId).toBeNull();
  });

  it("shows create errors inline and retries with the same operation", async () => {
    const user = userEvent.setup();
    const onValueChange = vi.fn();
    mockClient.POST.mockResolvedValueOnce({
      data: undefined,
      error: { status: 503, detail: "Temporarily unavailable" },
      response: new Response(null, { status: 503 }),
    }).mockImplementationOnce(async (_path, { body }) => ({
      data: {
        ...revision,
        id: "revision-2",
        nodes: [
          ...revision.nodes,
          { id: body.changes[0].categoryId, name: "Other", archived: false },
        ],
      },
      error: undefined,
      response: new Response(null, { status: 200 }),
    }));
    renderWithQueryClient(
      <CategoryPathPicker
        revision={revision}
        canCreate
        onValueChange={onValueChange}
      />,
    );
    await user.click(screen.getByRole("combobox"));
    await user.click(screen.getByRole("button", { name: "Create category" }));
    await user.type(
      screen.getByRole("textbox", { name: "New category" }),
      "Other",
    );
    await user.click(screen.getByRole("button", { name: "Create and select" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Temporarily unavailable",
    );
    await user.click(screen.getByRole("button", { name: "Retry creation" }));
    await waitFor(() => expect(onValueChange).toHaveBeenCalled());
    expect(mockClient.POST.mock.calls[1][1].body).toEqual(
      mockClient.POST.mock.calls[0][1].body,
    );
  });

  it("keeps creation hidden when the caller has no Admin permission", async () => {
    const user = userEvent.setup();
    render(
      <CategoryPathPicker
        revision={revision}
        onValueChange={vi.fn()}
        canCreate={false}
      />,
    );
    await user.click(screen.getByRole("combobox"));
    expect(
      screen.queryByRole("button", { name: "Create category" }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole("option", { name: "Unclassified" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("option", { name: "Food / Fruit" }),
    ).toBeInTheDocument();
  });
});

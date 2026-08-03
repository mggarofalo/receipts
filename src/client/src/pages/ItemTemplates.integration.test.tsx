import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import { server } from "@/test/msw/server";
import { itemTemplates } from "@/test/msw/fixtures/item-templates";
import { itemTemplateHistoryCandidates } from "@/test/msw/fixtures/item-template-history-candidates";
import { categories } from "@/test/msw/fixtures/categories";
import { subcategories } from "@/test/msw/fixtures/subcategories";
import { createListHandlers, createEnumMetadataHandler } from "@/test/msw/handler-factories";
import { renderWithQueryClient } from "@/test/test-utils";
import ItemTemplates from "./ItemTemplates";

beforeEach(() => {
  localStorage.clear();
  server.use(
    ...createListHandlers("item-templates", itemTemplates),
    ...createListHandlers("categories", categories),
    ...createListHandlers("subcategories", subcategories),
    createEnumMetadataHandler(),
  );
});

describe("ItemTemplates (integration)", () => {
  it("fetches and renders item templates from the API", async () => {
    renderWithQueryClient(<ItemTemplates />);

    await waitFor(() => {
      expect(screen.getByText("Whole Milk")).toBeInTheDocument();
    });
    expect(screen.getByText("Sourdough Bread")).toBeInTheDocument();
    expect(screen.getByText("Drill Bit Set")).toBeInTheDocument();
  });

  it("shows loading skeleton then resolves to data", async () => {
    const { container } = renderWithQueryClient(<ItemTemplates />);

    expect(container.querySelector("[data-slot='skeleton']")).toBeInTheDocument();

    await waitFor(() => {
      expect(screen.getByText("Whole Milk")).toBeInTheDocument();
    });
    expect(container.querySelector("[data-slot='skeleton']")).not.toBeInTheDocument();
  });

  it("renders empty state when API returns no data", async () => {
    server.use(
      http.get("*/api/item-templates", () => {
        return HttpResponse.json({ data: [], total: 0, offset: 0, limit: 50 });
      }),
    );

    renderWithQueryClient(<ItemTemplates />);

    await waitFor(() => {
      expect(screen.getByText(/no item templates yet/i)).toBeInTheDocument();
    });
  });

  it("renders category columns", async () => {
    renderWithQueryClient(<ItemTemplates />);

    await waitFor(() => {
      expect(screen.getByText("Whole Milk")).toBeInTheDocument();
    });

    // Category columns
    expect(screen.getAllByText("Groceries").length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText("Tools")).toBeInTheDocument();
  });

  it("renders placeholder for null optional fields", async () => {
    // Add an item with null visible columns
    const itemsWithNulls = [
      ...itemTemplates,
      {
        id: "ffff4444-4444-4444-4444-444444444444",
        name: "Miscellaneous",
        description: null,
        defaultCategory: null,
        defaultSubcategory: null,
        defaultUnitPrice: null,
        defaultUnitPriceCurrency: null,
        defaultItemCode: null,
      },
    ];

    server.use(...createListHandlers("item-templates", itemsWithNulls));

    renderWithQueryClient(<ItemTemplates />);

    await waitFor(() => {
      expect(screen.getByText("Miscellaneous")).toBeInTheDocument();
    });

    // The "Miscellaneous" row has null category, subcategory, and unitPrice
    // Each renders a "--" placeholder
    const placeholders = screen.getAllByText("--");
    expect(placeholders.length).toBeGreaterThanOrEqual(3);
  });

  it("toggles checkbox selection and shows delete button", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<ItemTemplates />);

    await waitFor(() => {
      expect(screen.getByText("Whole Milk")).toBeInTheDocument();
    });

    // No delete button initially
    expect(screen.queryByRole("button", { name: /delete/i })).not.toBeInTheDocument();

    // Select an item
    await user.click(screen.getByLabelText("Select Whole Milk"));

    // Delete button appears
    expect(screen.getByRole("button", { name: /delete/i })).toBeInTheDocument();
  });

  it("select all checkbox selects all items", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<ItemTemplates />);

    await waitFor(() => {
      expect(screen.getByText("Whole Milk")).toBeInTheDocument();
    });

    await user.click(screen.getByLabelText("Select all rows"));

    // All individual checkboxes should be checked
    expect(screen.getByLabelText("Select Whole Milk")).toBeChecked();
    expect(screen.getByLabelText("Select Sourdough Bread")).toBeChecked();
    expect(screen.getByLabelText("Select Drill Bit Set")).toBeChecked();

    // Delete button shows count
    expect(screen.getByRole("button", { name: /delete \(3\)/i })).toBeInTheDocument();
  });

  it("opens delete dialog and confirms deletion via API", async () => {
    const user = userEvent.setup();
    let capturedBody: string[] | null = null;

    server.use(
      http.delete("*/api/item-templates", async ({ request }) => {
        capturedBody = (await request.json()) as string[];
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderWithQueryClient(<ItemTemplates />);

    await waitFor(() => {
      expect(screen.getByText("Whole Milk")).toBeInTheDocument();
    });

    await user.click(screen.getByLabelText("Select Whole Milk"));
    await user.click(screen.getByRole("button", { name: /delete/i }));

    // Confirmation dialog
    expect(screen.getByRole("heading", { name: /delete item templates/i })).toBeInTheDocument();
    expect(screen.getByText(/1 item template/i)).toBeInTheDocument();

    // Confirm deletion
    const dialogDeleteBtn = screen
      .getAllByRole("button", { name: /delete/i })
      .find((btn) => btn.closest("[role='dialog']") !== null);
    expect(dialogDeleteBtn).toBeDefined();
    await user.click(dialogDeleteBtn!);

    await waitFor(() => {
      expect(capturedBody).not.toBeNull();
    });
    expect(capturedBody).toContain(itemTemplates[0].id);
  });

  it("opens create dialog and submits a new template via API", async () => {
    const user = userEvent.setup();
    let capturedBody: Record<string, unknown> | null = null;

    server.use(
      http.post("*/api/item-templates", async ({ request }) => {
        capturedBody = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json(
          { id: "new-id", ...capturedBody },
          { status: 201 },
        );
      }),
    );

    renderWithQueryClient(<ItemTemplates />);

    await waitFor(() => {
      expect(screen.getByText("Whole Milk")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /new template/i }));
    expect(screen.getByRole("heading", { name: /create item template/i })).toBeInTheDocument();

    await user.type(screen.getByLabelText(/^name$/i), "Orange Juice");
    await user.click(screen.getByRole("button", { name: /create template/i }));

    await waitFor(() => {
      expect(capturedBody).not.toBeNull();
    });
    expect(capturedBody).toMatchObject({ name: "Orange Juice" });
  });

  it("opens edit dialog and submits updates via API", async () => {
    const user = userEvent.setup();
    let capturedBody: Record<string, unknown> | null = null;

    server.use(
      http.put("*/api/item-templates/:id", async ({ request }) => {
        capturedBody = (await request.json()) as Record<string, unknown>;
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderWithQueryClient(<ItemTemplates />);

    await waitFor(() => {
      expect(screen.getByText("Whole Milk")).toBeInTheDocument();
    });

    const editButtons = screen.getAllByRole("button", { name: /edit/i });
    await user.click(editButtons[0]);

    expect(screen.getByRole("heading", { name: /edit item template/i })).toBeInTheDocument();

    const nameInput = screen.getByLabelText(/^name$/i);
    await user.clear(nameInput);
    await user.type(nameInput, "Organic Milk");
    await user.click(screen.getByRole("button", { name: /update template/i }));

    await waitFor(() => {
      expect(capturedBody).not.toBeNull();
    });
    expect(capturedBody).toMatchObject({ name: "Organic Milk" });
  });

  it("closes create dialog when Cancel is clicked", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<ItemTemplates />);

    await waitFor(() => {
      expect(screen.getByRole("button", { name: /new template/i })).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /new template/i }));
    expect(screen.getByRole("heading", { name: /create item template/i })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /cancel/i }));
    await waitFor(() => {
      expect(screen.queryByRole("heading", { name: /create item template/i })).not.toBeInTheDocument();
    });
  });

  it("opens create dialog on shortcut:new-item event", async () => {
    renderWithQueryClient(<ItemTemplates />);

    await waitFor(() => {
      expect(screen.getByRole("button", { name: /new template/i })).toBeInTheDocument();
    });

    window.dispatchEvent(new Event("shortcut:new-item"));

    await waitFor(() => {
      expect(screen.getByRole("heading", { name: /create item template/i })).toBeInTheDocument();
    });
  });

  it("opens create dialog when navigated with openNew state", async () => {
    renderWithQueryClient(<ItemTemplates />, {
      route: { pathname: "/item-templates", state: { openNew: true } },
    });

    await waitFor(() => {
      expect(
        screen.getByRole("heading", { name: /create item template/i }),
      ).toBeInTheDocument();
    });
  });

  describe("suggested from your history", () => {
    it("fetches and renders history candidates from the API", async () => {
      renderWithQueryClient(<ItemTemplates />);

      await waitFor(() => {
        expect(screen.getByText("Orange Juice")).toBeInTheDocument();
      });
      expect(
        screen.getByRole("heading", { name: /suggested from your history/i }),
      ).toBeInTheDocument();
      expect(screen.getByText("seen 6 times")).toBeInTheDocument();
      expect(screen.getByText("Paper Towels")).toBeInTheDocument();
    });

    it("hides the section when the API returns no candidates", async () => {
      server.use(
        http.get("*/api/item-templates/history-candidates", () =>
          HttpResponse.json({ data: [], total: 0, offset: 0, limit: 10 }),
        ),
      );

      renderWithQueryClient(<ItemTemplates />);

      await waitFor(() => {
        expect(screen.getByText("Whole Milk")).toBeInTheDocument();
      });
      expect(
        screen.queryByRole("heading", { name: /suggested from your history/i }),
      ).not.toBeInTheDocument();
    });

    it("creates a template from a candidate and drops the row on refetch", async () => {
      const user = userEvent.setup();
      let capturedBody: Record<string, unknown> | null = null;
      let remaining = [...itemTemplateHistoryCandidates];

      server.use(
        http.get("*/api/item-templates/history-candidates", () =>
          HttpResponse.json({
            data: remaining,
            total: remaining.length,
            offset: 0,
            limit: 10,
          }),
        ),
        http.post("*/api/item-templates", async ({ request }) => {
          capturedBody = (await request.json()) as Record<string, unknown>;
          // The server would no longer report a description that now has a template.
          remaining = remaining.filter((c) => c.name !== capturedBody!.name);
          return HttpResponse.json(
            { id: "created-id", ...capturedBody },
            { status: 201 },
          );
        }),
      );

      renderWithQueryClient(<ItemTemplates />);

      await waitFor(() => {
        expect(screen.getByText("Orange Juice")).toBeInTheDocument();
      });

      await user.click(
        screen.getByRole("button", { name: "Create template for Orange Juice" }),
      );

      await waitFor(() => {
        expect(capturedBody).not.toBeNull();
      });
      expect(capturedBody).toMatchObject({
        name: "Orange Juice",
        defaultCategory: "Groceries",
        defaultSubcategory: "Beverages",
        defaultUnitPrice: 5.29,
        defaultItemCode: "OJ-100",
      });

      // The create mutation invalidates the itemTemplates key, which the candidates
      // query lives under, so the row disappears once the refetch lands.
      await waitFor(() => {
        expect(screen.queryByText("Orange Juice")).not.toBeInTheDocument();
      });
      expect(screen.getByText("Paper Towels")).toBeInTheDocument();
    });

    it("remembers the collapsed state across mounts", async () => {
      const user = userEvent.setup();
      const { unmount } = renderWithQueryClient(<ItemTemplates />);

      await waitFor(() => {
        expect(screen.getByText("Orange Juice")).toBeInTheDocument();
      });

      await user.click(
        screen.getByRole("button", { name: /suggested from your history/i }),
      );
      await waitFor(() => {
        expect(screen.queryByText("Orange Juice")).not.toBeInTheDocument();
      });

      unmount();
      renderWithQueryClient(<ItemTemplates />);

      await waitFor(() => {
        expect(
          screen.getByRole("heading", { name: /suggested from your history/i }),
        ).toBeInTheDocument();
      });
      expect(screen.queryByText("Orange Juice")).not.toBeInTheDocument();
    });
  });
});

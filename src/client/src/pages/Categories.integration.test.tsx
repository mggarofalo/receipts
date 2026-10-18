import type { components } from "@/generated/api";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import { server } from "@/test/msw/server";
import { renderWithQueryClient } from "@/test/test-utils";
import Categories from "./Categories";

beforeEach(() => {
  localStorage.clear();
});

describe("Categories (integration)", () => {
  it("fetches and renders categories from the API", async () => {
    renderWithQueryClient(<Categories />);

    await waitFor(() => {
      expect(screen.getByText("Groceries")).toBeInTheDocument();
    });
    expect(screen.getByText("Tools")).toBeInTheDocument();
    expect(screen.getByText("Electronics")).toBeInTheDocument();
    expect(screen.getByText("Food and household items")).toBeInTheDocument();
  });

  it("shows loading skeleton then resolves to data", async () => {
    const { container } = renderWithQueryClient(<Categories />);

    expect(
      container.querySelector("[data-slot='skeleton']"),
    ).toBeInTheDocument();

    await waitFor(() => {
      expect(screen.getByText("Groceries")).toBeInTheDocument();
    });
    expect(
      container.querySelector("[data-slot='skeleton']"),
    ).not.toBeInTheDocument();
  });

  it("renders empty state when API returns no data", async () => {
    server.use(
      http.get("*/api/categories", () => {
        return HttpResponse.json({ data: [], total: 0, offset: 0, limit: 50 });
      }),
    );

    renderWithQueryClient(<Categories />);

    await waitFor(() => {
      expect(screen.getByText(/no categories yet/i)).toBeInTheDocument();
    });
  });

  it("renders placeholder for null descriptions", async () => {
    renderWithQueryClient(<Categories />);

    await waitFor(() => {
      expect(screen.getByText("Electronics")).toBeInTheDocument();
    });

    // Electronics has null description, should show "--"
    expect(screen.getByText("--")).toBeInTheDocument();
  });

  it("defaults to showing only active categories", async () => {
    renderWithQueryClient(<Categories />);

    await waitFor(() => {
      expect(screen.getByText("Groceries")).toBeInTheDocument();
    });

    // Active categories visible
    expect(screen.getByText("Tools")).toBeInTheDocument();
    expect(screen.getByText("Electronics")).toBeInTheDocument();
    // Inactive category filtered out
    expect(screen.queryByText("Clothing")).not.toBeInTheDocument();

    // Active tab selected
    const activeTab = screen.getByRole("tab", { name: "Active" });
    expect(activeTab).toHaveAttribute("data-state", "active");
  });

  it("shows inactive categories when Inactive tab is clicked", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<Categories />);

    await waitFor(() => {
      expect(screen.getByText("Groceries")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("tab", { name: "Inactive" }));

    await waitFor(() => {
      expect(screen.getByText("Clothing")).toBeInTheDocument();
    });
    expect(screen.queryByText("Groceries")).not.toBeInTheDocument();
  });

  it("shows all categories when All tab is clicked", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<Categories />);

    await waitFor(() => {
      expect(screen.getByText("Groceries")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("tab", { name: "All" }));

    await waitFor(() => {
      expect(screen.getByText("Clothing")).toBeInTheDocument();
    });
    expect(screen.getByText("Groceries")).toBeInTheDocument();
    expect(screen.getByText("Tools")).toBeInTheDocument();
    expect(screen.getByText("Electronics")).toBeInTheDocument();
  });

  it("persists status filter in localStorage", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<Categories />);

    await waitFor(() => {
      expect(screen.getByText("Groceries")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("tab", { name: "All" }));

    expect(localStorage.getItem("categories-status-filter")).toBe("all");
  });

  it("opens create dialog and submits a new category via API", async () => {
    const user = userEvent.setup();
    let capturedBody: Record<string, unknown> | null = null;

    server.use(
      http.post("*/api/categories", async ({ request }) => {
        const body =
          (await request.json()) as components["schemas"]["CreateCategoryRequest"];
        capturedBody = body;
        return HttpResponse.json(
          {
            ...body,
            isActive: body.isActive ?? false,
            id: "00000000-0000-0000-0000-000000000099",
          } satisfies components["schemas"]["CategoryResponse"],
          { status: 200 },
        );
      }),
    );

    renderWithQueryClient(<Categories />);

    await waitFor(() => {
      expect(screen.getByText("Groceries")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /new category/i }));
    expect(
      screen.getByRole("heading", { name: /create category/i }),
    ).toBeInTheDocument();

    await user.type(
      screen.getByRole("textbox", { name: /^name\s*\*$/i }),
      "Automotive",
    );
    await user.click(screen.getByRole("button", { name: /create category/i }));

    await waitFor(() => {
      expect(capturedBody).not.toBeNull();
    });
    expect(capturedBody).toMatchObject({ name: "Automotive" });
  });

  it("opens edit dialog and submits updates via API", async () => {
    const user = userEvent.setup();
    let capturedBody: Record<string, unknown> | null = null;

    server.use(
      http.put("*/api/categories/:id", async ({ request }) => {
        capturedBody = (await request.json()) as Record<string, unknown>;
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderWithQueryClient(<Categories />);

    await waitFor(() => {
      expect(screen.getByText("Groceries")).toBeInTheDocument();
    });

    const editButtons = screen.getAllByRole("button", { name: /edit/i });
    await user.click(editButtons[0]);

    expect(
      screen.getByRole("heading", { name: /edit category/i }),
    ).toBeInTheDocument();

    const nameInput = screen.getByRole("textbox", { name: /^name\s*\*$/i });
    await user.clear(nameInput);
    await user.type(nameInput, "Updated Groceries");
    await user.click(screen.getByRole("button", { name: /update category/i }));

    await waitFor(() => {
      expect(capturedBody).not.toBeNull();
    });
    expect(capturedBody).toMatchObject({ name: "Updated Groceries" });
  });

  it("closes create dialog when Cancel is clicked", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(<Categories />);

    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: /new category/i }),
      ).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /new category/i }));
    expect(
      screen.getByRole("heading", { name: /create category/i }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /cancel/i }));
    await waitFor(() => {
      expect(
        screen.queryByRole("heading", { name: /create category/i }),
      ).not.toBeInTheDocument();
    });
  });

  it("opens create dialog on shortcut:new-item event", async () => {
    renderWithQueryClient(<Categories />);

    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: /new category/i }),
      ).toBeInTheDocument();
    });

    window.dispatchEvent(new Event("shortcut:new-item"));

    await waitFor(() => {
      expect(
        screen.getByRole("heading", { name: /create category/i }),
      ).toBeInTheDocument();
    });
  });

  it("opens create dialog when navigated with openNew state", async () => {
    renderWithQueryClient(<Categories />, {
      route: { pathname: "/categories", state: { openNew: true } },
    });

    await waitFor(() => {
      expect(
        screen.getByRole("heading", { name: /create category/i }),
      ).toBeInTheDocument();
    });
  });
});

import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { SortableTableHead } from "./SortableTableHead";
import {
  Table,
  TableHeader,
  TableRow,
} from "@/components/ui/table";

function renderSortableHead(props: {
  column?: string;
  label?: string;
  currentSortBy?: string | null;
  currentSortDirection?: "asc" | "desc";
  onToggleSort?: (column: string) => void;
  className?: string;
  align?: "left" | "right";
}) {
  const defaults = {
    column: "name",
    label: "Name",
    currentSortBy: null,
    currentSortDirection: "asc" as const,
    onToggleSort: vi.fn(),
  };
  const merged = { ...defaults, ...props };
  return render(
    <Table>
      <TableHeader>
        <TableRow>
          <SortableTableHead {...merged} />
        </TableRow>
      </TableHeader>
    </Table>,
  );
}

describe("SortableTableHead", () => {
  describe("rendering", () => {
    it("renders a button with the column label", () => {
      renderSortableHead({ label: "Name" });
      const button = screen.getByRole("button", { name: /name/i });
      expect(button).toBeInTheDocument();
    });

    it("renders the button inside a th element", () => {
      const { container } = renderSortableHead({ label: "Amount" });
      const th = container.querySelector("th");
      expect(th).toBeInTheDocument();
      expect(th!.querySelector("button")).toBeInTheDocument();
    });
  });

  describe("aria-sort", () => {
    it("sets aria-sort to 'none' when column is not the active sort", () => {
      const { container } = renderSortableHead({
        column: "name",
        currentSortBy: "date",
        currentSortDirection: "asc",
      });
      const th = container.querySelector("th");
      expect(th).toHaveAttribute("aria-sort", "none");
    });

    it("sets aria-sort to 'ascending' when column is active and direction is asc", () => {
      const { container } = renderSortableHead({
        column: "name",
        currentSortBy: "name",
        currentSortDirection: "asc",
      });
      const th = container.querySelector("th");
      expect(th).toHaveAttribute("aria-sort", "ascending");
    });

    it("sets aria-sort to 'descending' when column is active and direction is desc", () => {
      const { container } = renderSortableHead({
        column: "name",
        currentSortBy: "name",
        currentSortDirection: "desc",
      });
      const th = container.querySelector("th");
      expect(th).toHaveAttribute("aria-sort", "descending");
    });

    it("sets aria-sort to 'none' when currentSortBy is null", () => {
      const { container } = renderSortableHead({
        column: "name",
        currentSortBy: null,
      });
      const th = container.querySelector("th");
      expect(th).toHaveAttribute("aria-sort", "none");
    });
  });

  describe("keyboard activation", () => {
    it("calls onToggleSort when button is activated with Enter key", async () => {
      const user = userEvent.setup();
      const onToggleSort = vi.fn();
      renderSortableHead({ column: "name", label: "Name", onToggleSort });

      const button = screen.getByRole("button", { name: /name/i });
      button.focus();
      await user.keyboard("{Enter}");

      expect(onToggleSort).toHaveBeenCalledWith("name");
    });

    it("calls onToggleSort when button is activated with Space key", async () => {
      const user = userEvent.setup();
      const onToggleSort = vi.fn();
      renderSortableHead({ column: "amount", label: "Amount", onToggleSort });

      const button = screen.getByRole("button", { name: /amount/i });
      button.focus();
      await user.keyboard(" ");

      expect(onToggleSort).toHaveBeenCalledWith("amount");
    });

    it("calls onToggleSort when button is clicked", async () => {
      const user = userEvent.setup();
      const onToggleSort = vi.fn();
      renderSortableHead({ column: "date", label: "Date", onToggleSort });

      await user.click(screen.getByRole("button", { name: /date/i }));

      expect(onToggleSort).toHaveBeenCalledWith("date");
    });

    it("button is focusable via tab", async () => {
      const user = userEvent.setup();
      renderSortableHead({ label: "Name" });

      await user.tab();
      const button = screen.getByRole("button", { name: /name/i });
      expect(button).toHaveFocus();
    });
  });

  describe("alignment", () => {
    it("left-aligns the button content by default", () => {
      renderSortableHead({ label: "Name" });
      const button = screen.getByRole("button", { name: /name/i });
      expect(button.className).not.toContain("justify-end");
    });

    it("right-aligns the button content when align is 'right'", () => {
      renderSortableHead({ label: "Amount", align: "right" });
      const button = screen.getByRole("button", { name: /amount/i });
      expect(button.className).toContain("justify-end");
    });
  });

  describe("arrow icon accessibility", () => {
    it("marks arrow icons as aria-hidden", () => {
      const { container } = renderSortableHead({
        column: "name",
        currentSortBy: "name",
        currentSortDirection: "asc",
      });
      const svgs = container.querySelectorAll("svg");
      svgs.forEach((svg) => {
        expect(svg).toHaveAttribute("aria-hidden", "true");
      });
    });

    it("marks the neutral sort icon as aria-hidden when column is inactive", () => {
      const { container } = renderSortableHead({
        column: "name",
        currentSortBy: null,
      });
      const svgs = container.querySelectorAll("svg");
      svgs.forEach((svg) => {
        expect(svg).toHaveAttribute("aria-hidden", "true");
      });
    });
  });
});

import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Pagination } from "./Pagination";

describe("Pagination", () => {
  const defaultProps = {
    currentPage: 1,
    totalItems: 50,
    pageSize: 10,
    totalPages: 5,
    onPageChange: vi.fn(),
  };

  it("disables paging and size changes while the owning query is pending", () => {
    render(
      <Pagination
        {...defaultProps}
        currentPage={2}
        disabled
        onPageSizeChange={vi.fn()}
      />,
    );
    expect(
      screen.getByRole("combobox", { name: "Rows per page" }),
    ).toBeDisabled();
    expect(screen.getByLabelText("Previous page")).toBeDisabled();
    expect(screen.getByLabelText("Next page")).toBeDisabled();
  });

  it("does not display an inverted range after data shrinks beyond the current page", () => {
    render(
      <Pagination
        {...defaultProps}
        currentPage={5}
        totalItems={3}
        totalPages={1}
      />,
    );
    expect(screen.getByText("Showing 0-0 of 3")).toBeInTheDocument();
    expect(screen.getByLabelText("Previous page")).toBeEnabled();
    expect(screen.getByLabelText("Next page")).toBeDisabled();
  });

  it("keeps the page-size control available with an honest empty range", () => {
    render(
      <Pagination
        {...defaultProps}
        totalItems={0}
        totalPages={1}
        onPageSizeChange={vi.fn()}
      />,
    );
    expect(screen.getByText("Showing 0-0 of 0")).toBeInTheDocument();
    expect(
      screen.getByRole("combobox", { name: "Rows per page" }),
    ).toBeInTheDocument();
    expect(screen.getByLabelText("Previous page")).toBeDisabled();
    expect(screen.getByLabelText("Next page")).toBeDisabled();
  });

  it("offers the supported sizes through a named keyboard-accessible control", async () => {
    const onPageSizeChange = vi.fn();
    render(
      <Pagination {...defaultProps} onPageSizeChange={onPageSizeChange} />,
    );
    const user = userEvent.setup();
    screen.getByRole("combobox", { name: "Rows per page" }).focus();
    await user.keyboard("{Enter}");
    for (const size of [10, 25, 50, 100])
      expect(
        screen.getByRole("option", { name: String(size) }),
      ).toBeInTheDocument();
    await user.keyboard("{Home}{ArrowDown}{ArrowDown}{Enter}");
    expect(onPageSizeChange).toHaveBeenCalledWith(50);
  });

  it("displays the correct range text", () => {
    render(<Pagination {...defaultProps} />);
    expect(screen.getByText("Showing 1-10 of 50")).toBeInTheDocument();
  });

  it("displays correct range for a middle page", () => {
    render(<Pagination {...defaultProps} currentPage={3} />);
    expect(screen.getByText("Showing 21-30 of 50")).toBeInTheDocument();
  });

  it("displays current page and total pages", () => {
    render(<Pagination {...defaultProps} currentPage={2} />);
    expect(screen.getByText("2/5")).toBeInTheDocument();
  });

  it("disables previous button on first page", () => {
    render(<Pagination {...defaultProps} currentPage={1} />);
    expect(screen.getByLabelText("Previous page")).toBeDisabled();
  });

  it("disables next button on last page", () => {
    render(<Pagination {...defaultProps} currentPage={5} />);
    expect(screen.getByLabelText("Next page")).toBeDisabled();
  });

  it("calls onPageChange with previous page when clicking previous", async () => {
    const user = userEvent.setup();
    const onPageChange = vi.fn();
    render(
      <Pagination
        {...defaultProps}
        currentPage={3}
        onPageChange={onPageChange}
      />,
    );

    await user.click(screen.getByLabelText("Previous page"));
    expect(onPageChange).toHaveBeenCalledWith(2);
  });

  it("calls onPageChange with next page when clicking next", async () => {
    const user = userEvent.setup();
    const onPageChange = vi.fn();
    render(
      <Pagination
        {...defaultProps}
        currentPage={3}
        onPageChange={onPageChange}
      />,
    );

    await user.click(screen.getByLabelText("Next page"));
    expect(onPageChange).toHaveBeenCalledWith(4);
  });
});

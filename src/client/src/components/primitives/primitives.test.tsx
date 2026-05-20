import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createRef } from "react";
import { Chip } from "./Chip";
import { Checkbox } from "./Checkbox";
import { EmptyState } from "./EmptyState";
import { Icon } from "./icons";
import { Kbd } from "./Kbd";
import { PageHead } from "./PageHead";
import { Tag } from "./Tag";
import { YnabChip } from "./YnabChip";

describe("Kbd", () => {
  it("renders a <kbd> with the kbd class and children", () => {
    render(<Kbd>K</Kbd>);
    const el = screen.getByText("K");
    expect(el.tagName).toBe("KBD");
    expect(el).toHaveClass("kbd");
  });

  it("forwards a ref and merges className", () => {
    const ref = createRef<HTMLElement>();
    render(
      <Kbd ref={ref} className="extra">
        ⌘
      </Kbd>,
    );
    expect(ref.current).toBeInstanceOf(HTMLElement);
    expect(ref.current).toHaveClass("kbd", "extra");
  });
});

describe("Chip", () => {
  it("renders children and defaults to the default variant", () => {
    render(<Chip>Reconciled</Chip>);
    expect(screen.getByText("Reconciled")).toBeInTheDocument();
  });

  it("applies variant styling without throwing", () => {
    render(<Chip variant="pos">Synced</Chip>);
    expect(screen.getByText("Synced")).toBeInTheDocument();
  });
});

describe("Tag", () => {
  it("renders children", () => {
    render(<Tag>Groceries</Tag>);
    expect(screen.getByText("Groceries")).toBeInTheDocument();
  });
});

describe("YnabChip", () => {
  it("renders the label for each status", () => {
    const { rerender } = render(<YnabChip status="synced" />);
    expect(screen.getByText("YNAB")).toBeInTheDocument();
    rerender(<YnabChip status="pending" />);
    expect(screen.getByText("Pending")).toBeInTheDocument();
    rerender(<YnabChip status="error" />);
    expect(screen.getByText("Error")).toBeInTheDocument();
    rerender(<YnabChip status="none" />);
    expect(screen.getByText("—")).toBeInTheDocument();
  });
});

describe("Checkbox", () => {
  it("exposes its checked state via aria-checked", () => {
    const { rerender } = render(<Checkbox on={false} />);
    expect(screen.getByRole("checkbox")).toHaveAttribute(
      "aria-checked",
      "false",
    );
    rerender(<Checkbox on={true} />);
    expect(screen.getByRole("checkbox")).toHaveAttribute(
      "aria-checked",
      "true",
    );
  });

  it("fires onClick when activated", async () => {
    const onClick = vi.fn();
    const user = userEvent.setup();
    render(<Checkbox on={false} onClick={onClick} />);
    await user.click(screen.getByRole("checkbox"));
    expect(onClick).toHaveBeenCalledTimes(1);
  });
});

describe("EmptyState", () => {
  it("renders the icon, title, body, and actions", () => {
    render(
      <EmptyState
        icon={Icon.Inbox}
        title="No receipts yet"
        body="Add your first receipt."
        actions={<button>New receipt</button>}
      />,
    );
    expect(screen.getByText("No receipts yet")).toBeInTheDocument();
    expect(screen.getByText("Add your first receipt.")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "New receipt" }),
    ).toBeInTheDocument();
  });
});

describe("PageHead", () => {
  it("renders the title and optional eyebrow", () => {
    render(<PageHead title="Receipts" sub="42 total" />);
    expect(
      screen.getByRole("heading", { name: "Receipts" }),
    ).toBeInTheDocument();
    expect(screen.getByText("42 total")).toBeInTheDocument();
  });
});

describe("Icon", () => {
  it("renders an svg with the design-system stroke width", () => {
    const { container } = render(<Icon.Dashboard data-testid="dash" />);
    const svg = container.querySelector("svg");
    expect(svg).not.toBeNull();
    expect(svg).toHaveAttribute("stroke-width", "1.6");
  });

  it("allows the stroke width to be overridden", () => {
    const { container } = render(<Icon.Search strokeWidth={2} />);
    expect(container.querySelector("svg")).toHaveAttribute("stroke-width", "2");
  });
});

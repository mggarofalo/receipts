import { render, screen } from "@testing-library/react";
import { YnabSyncBadge } from "./YnabSyncBadge";

describe("YnabSyncBadge", () => {
  it("renders nothing when status is undefined", () => {
    const { container } = render(<YnabSyncBadge status={undefined} />);
    expect(container.innerHTML).toBe("");
  });

  it("renders Synced badge", () => {
    render(<YnabSyncBadge status="Synced" />);
    expect(screen.getByText("Synced")).toBeInTheDocument();
    expect(
      screen.getByLabelText("YNAB sync status: Synced"),
    ).toBeInTheDocument();
  });

  it("renders Pending badge", () => {
    render(<YnabSyncBadge status="Pending" />);
    expect(screen.getByText("Pending")).toBeInTheDocument();
  });

  it("renders Failed badge", () => {
    render(<YnabSyncBadge status="Failed" />);
    expect(screen.getByText("Failed")).toBeInTheDocument();
  });

  it("renders Not Synced badge", () => {
    render(<YnabSyncBadge status="NotSynced" />);
    expect(screen.getByText("Not Synced")).toBeInTheDocument();
  });
});

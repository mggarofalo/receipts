import { render, screen } from "@testing-library/react";
import { YnabSyncBadge } from "./YnabSyncBadge";

describe("YnabSyncBadge", () => {
  it("renders nothing when status is undefined", () => {
    const { container } = render(<YnabSyncBadge status={undefined} />);
    expect(container.innerHTML).toBe("");
  });

  it("renders Synced badge", () => {
    render(<YnabSyncBadge status="synced" />);
    expect(screen.getByText("Synced")).toBeInTheDocument();
    expect(
      screen.getByLabelText("YNAB sync status: Synced"),
    ).toBeInTheDocument();
  });

  it("renders Pending badge", () => {
    render(<YnabSyncBadge status="pending" />);
    expect(screen.getByText("Pending")).toBeInTheDocument();
  });

  it("renders Failed badge", () => {
    render(<YnabSyncBadge status="failed" />);
    expect(screen.getByText("Failed")).toBeInTheDocument();
  });

  it("renders an ambiguous outcome as needing review", () => {
    render(<YnabSyncBadge status="unknown" />);
    expect(screen.getByText("Needs Review")).toBeInTheDocument();
    expect(
      screen.getByLabelText("YNAB sync status: Needs Review"),
    ).toBeInTheDocument();
  });

  it("renders Not Synced badge", () => {
    render(<YnabSyncBadge status="notSynced" />);
    expect(screen.getByText("Not Synced")).toBeInTheDocument();
  });
});

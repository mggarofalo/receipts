import { render, screen } from "@testing-library/react";
import { ReceiptNormalizationError } from "./ReceiptNormalizationError";

it("keeps pending-name collision recovery in a separate tab with the exact review target", () => {
  render(
    <ReceiptNormalizationError
      error={{
        status: 409,
        detail: "A product with this name is awaiting review.",
        normalizedDescriptionId: "target-a",
        normalizedDescriptionStatus: "pendingReview",
      }}
    />,
  );
  expect(screen.getByRole("alert")).toHaveTextContent(
    "A product with this name is awaiting review.",
  );
  const link = screen.getByRole("link", {
    name: "Review the existing pending product in a new tab",
  });
  expect(link).toHaveAttribute(
    "href",
    "/admin/normalized-descriptions?tab=review&reviewTarget=target-a",
  );
  expect(link).toHaveAttribute("target", "_blank");
  expect(link).toHaveAttribute("rel", "noopener noreferrer");
  expect(screen.getByRole("alert")).toHaveTextContent(
    "your receipt draft stays open",
  );
});

it("does not invent a pending identity recovery link for a generic conflict", () => {
  render(
    <ReceiptNormalizationError
      error={{ status: 409, detail: "The source changed." }}
    />,
  );
  expect(screen.getByRole("alert")).toHaveTextContent(
    "Refresh product choices",
  );
  expect(screen.queryByRole("link")).not.toBeInTheDocument();
});

it("renders nothing before any rejected save", () => {
  const { container } = render(<ReceiptNormalizationError error={null} />);
  expect(container).toBeEmptyDOMElement();
});

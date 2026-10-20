import { screen } from "@testing-library/react";
import { renderWithProviders } from "@/test/test-utils";
import { PendingIdentityRecovery } from "./PendingIdentityRecovery";
it.each([
  null,
  "failure",
  {},
  { normalizedDescriptionStatus: "active", normalizedDescriptionId: "a" },
  { normalizedDescriptionStatus: "pendingReview" },
])("does not invent a recovery target from %j", (error) => {
  renderWithProviders(<PendingIdentityRecovery error={error} />);
  expect(screen.queryByRole("link")).not.toBeInTheDocument();
});
it("links the explicit pending target and explains that navigation does not approve", () => {
  renderWithProviders(
    <PendingIdentityRecovery
      error={{
        normalizedDescriptionStatus: "pendingReview",
        normalizedDescriptionId: "b",
      }}
    />,
  );
  expect(screen.getByRole("link")).toHaveAttribute(
    "href",
    "/admin/normalized-descriptions?tab=review&reviewTarget=b",
  );
  expect(
    screen.getByText(/Approval is a separate, explicit action/),
  ).toBeInTheDocument();
});

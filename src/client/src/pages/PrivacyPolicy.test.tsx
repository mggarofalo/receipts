import { screen, within } from "@testing-library/react";
import { Route, Routes } from "react-router";
import { PublicLayout } from "@/components/PublicLayout";
import { renderWithProviders } from "@/test/test-utils";
import PrivacyPolicy from "./PrivacyPolicy";

vi.mock("@/hooks/usePageTitle", () => ({
  usePageTitle: vi.fn(),
}));

import { usePageTitle } from "@/hooks/usePageTitle";

const REQUIRED_SECTIONS = [
  "Who is responsible",
  "What is obtained from YNAB and why",
  "How it is stored and secured",
  "How long it is kept",
  "Third parties",
  "Deleting your data",
];

function section(name: string) {
  return within(screen.getByRole("region", { name }));
}

describe("PrivacyPolicy", () => {
  it("renders the whole policy for a visitor who is not signed in", () => {
    // renderWithProviders supplies a signed-out session (user: null).
    renderWithProviders(<PrivacyPolicy />, { route: "/privacy" });

    expect(
      screen.getByRole("heading", { level: 1, name: "Privacy policy" }),
    ).toBeInTheDocument();
    expect(usePageTitle).toHaveBeenCalledWith("Privacy policy");
    for (const name of REQUIRED_SECTIONS) {
      expect(
        screen.getByRole("heading", { level: 2, name }),
      ).toBeInTheDocument();
    }
  });

  it("renders the same policy for a signed-in user", () => {
    renderWithProviders(<PrivacyPolicy />, {
      route: "/privacy",
      auth: {
        user: {
          userId: "user-1",
          email: "member@example.com",
          roles: ["User"],
          mustResetPassword: false,
        },
      },
    });

    for (const name of REQUIRED_SECTIONS) {
      expect(
        screen.getByRole("heading", { level: 2, name }),
      ).toBeInTheDocument();
    }
  });

  it.each([
    ["signed out", null],
    [
      "signed in",
      {
        userId: "user-1",
        email: "member@example.com",
        roles: ["User"],
        mustResetPassword: false,
      },
    ],
  ])("is shown by the real public layout while %s, without redirecting away", (_name, user) => {
    renderWithProviders(
      <Routes>
        <Route element={<PublicLayout />}>
          <Route path="/privacy" element={<PrivacyPolicy />} />
        </Route>
        <Route path="*" element={<div data-testid="elsewhere" />} />
      </Routes>,
      { route: "/privacy", auth: { user } },
    );

    expect(
      screen.getByRole("heading", { level: 1, name: "Privacy policy" }),
    ).toBeInTheDocument();
    expect(screen.queryByTestId("elsewhere")).not.toBeInTheDocument();
  });

  it("shows the date it was last updated", () => {
    renderWithProviders(<PrivacyPolicy />);

    expect(
      screen.getByText("Last updated: 19 November 2026"),
    ).toBeInTheDocument();
  });

  it("names the operator as responsible and says the authors do not receive the data", () => {
    renderWithProviders(<PrivacyPolicy />);

    const responsible = section("Who is responsible");
    expect(
      responsible.getByText(/its operator\) is responsible for the data/i),
    ).toBeInTheDocument();
    expect(
      responsible.getByText(/authors of the software do not receive/i),
    ).toBeInTheDocument();
  });

  it("states what is read from and written to YNAB", () => {
    renderWithProviders(<PrivacyPolicy />);

    const ynab = section("What is obtained from YNAB and why");
    expect(
      ynab.getByText(/unless an administrator connects YNAB/i),
    ).toBeInTheDocument();
    expect(ynab.getByText(/budget names and ids/i)).toBeInTheDocument();
    expect(
      ynab.getByText(/account ids, names, types and balances/i),
    ).toBeInTheDocument();
    expect(
      ynab.getByText(/category ids, names and group names/i),
    ).toBeInTheDocument();
    expect(
      ynab.getByText(/transactions on the dates being matched/i),
    ).toBeInTheDocument();
    expect(ynab.getByText(/new transactions for receipts/i)).toBeInTheDocument();
    expect(
      ynab.getByText(/memo updates that add a link to the receipt/i),
    ).toBeInTheDocument();
  });

  it("states how tokens are protected and who may connect", () => {
    renderWithProviders(<PrivacyPolicy />);

    const storage = section("How it is stored and secured");
    expect(
      storage.getByText(
        /encrypted before they are stored, are never shown in the app, and are not included in backups/i,
      ),
    ).toBeInTheDocument();
    expect(
      storage.getByText(/only the id of the matched transaction is kept/i),
    ).toBeInTheDocument();
    expect(
      storage.getByText(
        /connecting or disconnecting YNAB requires an administrator/i,
      ),
    ).toBeInTheDocument();
  });

  it("states the retention periods", () => {
    renderWithProviders(<PrivacyPolicy />);

    const retention = section("How long it is kept");
    expect(retention.getByText(/kept until it is deleted/i)).toBeInTheDocument();
    expect(retention.getByText(/removed after 180 days/i)).toBeInTheDocument();
    expect(
      retention.getByText(/removed after 30 days by default/i),
    ).toBeInTheDocument();
    expect(
      retention.getByText(/receipts with classification history are retained/i),
    ).toBeInTheDocument();
  });

  it("names each external provider, what it receives, and that none receives YNAB data", () => {
    renderWithProviders(<PrivacyPolicy />);

    const thirdParties = section("Third parties");
    expect(
      thirdParties.getByText(
        "Data obtained from YNAB is not sent to any third party.",
      ),
    ).toBeInTheDocument();
    expect(thirdParties.getByText(/Jev \(typesafe\.ai\)/)).toHaveTextContent(
      "No prices, dates, accounts or YNAB data are sent.",
    );
    expect(thirdParties.getByText(/ChatGPT \(OpenAI\)/)).toHaveTextContent(
      "No store names, prices, dates, accounts or YNAB data are sent.",
    );
    expect(thirdParties.getByText(/\(Sentry\)/)).toHaveTextContent(
      "include the user id and email address",
    );
    expect(
      thirdParties.getByText(/only if the operator configures one/i),
    ).toBeInTheDocument();
    expect(thirdParties.getByText(/Google Fonts/)).toHaveTextContent(
      "IP address and browser details",
    );
    expect(thirdParties.getByText(/Hugging Face/)).toHaveTextContent(
      "without sending any user data",
    );
  });

  it("explains how data is deleted", () => {
    renderWithProviders(<PrivacyPolicy />);

    const deletion = section("Deleting your data");
    expect(
      deletion.getByText(
        /asks YNAB to revoke the app's access and removes the stored tokens/i,
      ),
    ).toBeInTheDocument();
    expect(
      deletion.getByText(/removed in YNAB's account settings at any time/i),
    ).toBeInTheDocument();
    expect(
      deletion.getByText(/ask the deployment's operator/i),
    ).toBeInTheDocument();
  });
});

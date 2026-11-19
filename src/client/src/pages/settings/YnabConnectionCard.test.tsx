import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import { mockMutationResult } from "@/test/mock-hooks";
import type { components } from "@/generated/api";
import {
  YnabConnectionCard,
  type YnabConnectionCardProps,
} from "./YnabConnectionCard";

vi.mock("@/hooks/useYnabConnection", () => ({
  useStartYnabAuthorization: vi.fn(),
  useDisconnectYnab: vi.fn(),
}));

vi.mock("@/lib/external-navigation", () => ({
  navigateToExternal: vi.fn(),
}));

import {
  useDisconnectYnab,
  useStartYnabAuthorization,
} from "@/hooks/useYnabConnection";
import { navigateToExternal } from "@/lib/external-navigation";

type ConnectionStatus = components["schemas"]["YnabConnectionStatusResponse"];
type RemoteRevocation = components["schemas"]["YnabRemoteRevocation"];

// Each fixture is a status the server can actually report.
const statuses = {
  notConfigured: {
    isConfigured: false,
    isConnected: false,
    credentialSource: "none",
    oauthAvailable: false,
    reconnectRequired: false,
    oauthConnectedAtUtc: null,
    lastSuccessfulSyncUtc: null,
  },
  oauthAvailable: {
    isConfigured: false,
    isConnected: false,
    credentialSource: "none",
    oauthAvailable: true,
    reconnectRequired: false,
    oauthConnectedAtUtc: null,
    lastSuccessfulSyncUtc: null,
  },
  reconnectRequired: {
    isConfigured: false,
    isConnected: false,
    credentialSource: "none",
    oauthAvailable: true,
    reconnectRequired: true,
    oauthConnectedAtUtc: null,
    lastSuccessfulSyncUtc: null,
  },
  oauth: {
    isConfigured: true,
    isConnected: true,
    credentialSource: "oauth",
    oauthAvailable: true,
    reconnectRequired: false,
    oauthConnectedAtUtc: "2026-11-19T12:00:00Z",
    lastSuccessfulSyncUtc: null,
  },
  personalAccessToken: {
    isConfigured: true,
    isConnected: true,
    credentialSource: "personalAccessToken",
    oauthAvailable: false,
    reconnectRequired: false,
    oauthConnectedAtUtc: null,
    lastSuccessfulSyncUtc: null,
  },
  personalAccessTokenWithOAuthAvailable: {
    isConfigured: true,
    isConnected: true,
    credentialSource: "personalAccessToken",
    oauthAvailable: true,
    reconnectRequired: false,
    oauthConnectedAtUtc: null,
    lastSuccessfulSyncUtc: null,
  },
} satisfies Record<string, ConnectionStatus>;

const ACTION_NAMES = ["Connect to YNAB", "Reconnect", "Disconnect"];

const adminAuth = {
  user: {
    userId: "admin-1",
    email: "admin@example.com",
    roles: ["Admin"],
    mustResetPassword: false,
  },
};
const memberAuth = {
  user: {
    userId: "user-1",
    email: "member@example.com",
    roles: ["User"],
    mustResetPassword: false,
  },
};

function renderCard(
  status: ConnectionStatus,
  { admin = true, ...overrides }: { admin?: boolean } & Partial<
    YnabConnectionCardProps
  > = {},
) {
  const props: YnabConnectionCardProps = {
    isLoading: false,
    isError: false,
    isFetching: false,
    retry: vi.fn(),
    isConfigured: status.isConfigured,
    isConnected: status.isConnected,
    credentialSource: status.credentialSource,
    oauthAvailable: status.oauthAvailable,
    reconnectRequired: status.reconnectRequired,
    oauthConnectedAtUtc: status.oauthConnectedAtUtc ?? null,
    lastSuccessfulSyncUtc: status.lastSuccessfulSyncUtc ?? null,
    ...overrides,
  };
  return renderWithProviders(<YnabConnectionCard {...props} />, {
    auth: admin ? adminAuth : memberAuth,
  });
}

function actionButtons() {
  return ACTION_NAMES.filter((name) =>
    screen.queryByRole("button", { name }),
  );
}

let startMutate: ReturnType<typeof vi.fn>;
let disconnectMutate: ReturnType<typeof vi.fn>;

beforeEach(() => {
  vi.clearAllMocks();
  startMutate = vi.fn();
  disconnectMutate = vi.fn();
  vi.mocked(useStartYnabAuthorization).mockReturnValue(
    mockMutationResult({ mutate: startMutate }),
  );
  vi.mocked(useDisconnectYnab).mockReturnValue(
    mockMutationResult({ mutate: disconnectMutate }),
  );
});

describe("YnabConnectionCard for an Admin", () => {
  it("offers Connect when OAuth is available and nothing is connected, and sends the browser to YNAB", async () => {
    startMutate.mockImplementation((_variables, callbacks) =>
      callbacks.onSuccess({
        authorizationUrl: "https://app.ynab.com/oauth/authorize?state=abc",
      }),
    );
    renderCard(statuses.oauthAvailable);

    expect(screen.getByText("Not connected")).toBeInTheDocument();
    expect(actionButtons()).toEqual(["Connect to YNAB"]);

    await userEvent.click(
      screen.getByRole("button", { name: "Connect to YNAB" }),
    );

    expect(startMutate).toHaveBeenCalledTimes(1);
    expect(navigateToExternal).toHaveBeenCalledTimes(1);
    expect(navigateToExternal).toHaveBeenCalledWith(
      "https://app.ynab.com/oauth/authorize?state=abc",
    );
  });

  it("shows a visible Reconnect required state with Reconnect and Disconnect", async () => {
    renderCard(statuses.reconnectRequired);

    expect(screen.getByText("Reconnect required")).toBeInTheDocument();
    expect(
      screen.getByText(/ynab no longer accepts the stored connection/i),
    ).toBeInTheDocument();
    expect(actionButtons()).toEqual(["Reconnect", "Disconnect"]);

    await userEvent.click(screen.getByRole("button", { name: "Reconnect" }));

    // Reconnecting is the same authorization flow as connecting.
    expect(startMutate).toHaveBeenCalledTimes(1);
    expect(disconnectMutate).not.toHaveBeenCalled();
  });

  it("shows an OAuth connection as connected, with when it was made, and offers Disconnect", () => {
    renderCard(statuses.oauth);

    expect(screen.getByText("Connected")).toBeInTheDocument();
    expect(
      screen.getByText("Connected with OAuth on Nov 19, 2026"),
    ).toBeInTheDocument();
    expect(actionButtons()).toEqual(["Disconnect"]);
  });

  it("shows a personal access token connection and recommends OAuth when it is available", () => {
    renderCard(statuses.personalAccessTokenWithOAuthAvailable);

    expect(screen.getByText("Connected")).toBeInTheDocument();
    expect(
      screen.getByText(/connected with a personal access token/i),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/connecting with oauth is recommended/i),
    ).toBeInTheDocument();
    expect(actionButtons()).toEqual(["Connect to YNAB"]);
  });

  it("offers no action for a personal access token when OAuth is not available", () => {
    renderCard(statuses.personalAccessToken);

    expect(
      screen.getByText(/connected with a personal access token/i),
    ).toBeInTheDocument();
    expect(actionButtons()).toEqual([]);
  });

  it("explains both ways the operator can enable the integration when neither is configured", () => {
    renderCard(statuses.notConfigured);

    const explanation = screen.getByText(/operator of this deployment/i);
    expect(explanation).toHaveTextContent(
      "The operator of this deployment enables the integration by adding the YNAB OAuth settings (recommended) or by setting the YNAB_PAT environment variable.",
    );
    expect(screen.getByText("Not Configured")).toBeInTheDocument();
    expect(actionButtons()).toEqual([]);
  });

  it("shows the server's detail when starting is refused because OAuth is not configured", () => {
    vi.mocked(useStartYnabAuthorization).mockReturnValue(
      mockMutationResult({
        mutate: startMutate,
        isError: true,
        status: "error",
        error: {
          status: 409,
          title: "Conflict",
          detail: "YNAB OAuth is not configured on this deployment",
        },
      }),
    );

    renderCard(statuses.oauthAvailable);

    expect(
      screen.getByText("YNAB OAuth is not configured on this deployment"),
    ).toBeInTheDocument();
    expect(navigateToExternal).not.toHaveBeenCalled();
  });

  it("asks for confirmation before disconnecting and does nothing when cancelled", async () => {
    renderCard(statuses.oauth);

    await userEvent.click(screen.getByRole("button", { name: "Disconnect" }));

    const dialog = screen.getByRole("alertdialog");
    expect(
      within(dialog).getByText("Disconnect YNAB?"),
    ).toBeInTheDocument();
    expect(disconnectMutate).not.toHaveBeenCalled();

    await userEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(disconnectMutate).not.toHaveBeenCalled();
  });

  it("disconnects once after the confirmation is accepted", async () => {
    renderCard(statuses.oauth);

    await userEvent.click(screen.getByRole("button", { name: "Disconnect" }));
    await userEvent.click(
      within(screen.getByRole("alertdialog")).getByRole("button", {
        name: "Disconnect",
      }),
    );

    expect(disconnectMutate).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
  });

  it.each<{ remoteRevocation: RemoteRevocation; expected: RegExp }>([
    {
      remoteRevocation: "succeeded",
      expected:
        /stored connection was removed and YNAB accepted the request to revoke/i,
    },
    {
      remoteRevocation: "failed",
      expected:
        /stored connection was removed, but YNAB could not be asked to revoke access\. Remove this application's access in YNAB's account settings/i,
    },
    {
      remoteRevocation: "notAttempted",
      expected: /YNAB was not asked to revoke access/i,
    },
  ])("reports the outcome when remote revocation $remoteRevocation", ({ remoteRevocation, expected }) => {
    vi.mocked(useDisconnectYnab).mockReturnValue(
      mockMutationResult({
        mutate: disconnectMutate,
        isSuccess: true,
        isIdle: false,
        status: "success",
        data: { remoteRevocation },
      }),
    );

    // The status shown is the one the server reports after the disconnect.
    renderCard(statuses.oauthAvailable);

    expect(screen.getByText(expected)).toBeInTheDocument();
    if (remoteRevocation !== "failed") {
      expect(
        screen.queryByText(/could not be asked to revoke access/i),
      ).not.toBeInTheDocument();
    }
  });

  it("says so when the disconnect request itself fails", () => {
    vi.mocked(useDisconnectYnab).mockReturnValue(
      mockMutationResult({
        mutate: disconnectMutate,
        isError: true,
        status: "error",
        error: { status: 503 },
      }),
    );

    renderCard(statuses.oauth);

    expect(
      screen.getByText("YNAB could not be disconnected. Try again."),
    ).toBeInTheDocument();
  });

  it("links to the privacy policy beside the connection controls", () => {
    renderCard(statuses.oauthAvailable);

    const link = screen.getByRole("link", { name: "Privacy policy" });
    expect(link).toHaveAttribute("href", "/privacy");
    expect(link.parentElement).toContainElement(
      screen.getByRole("button", { name: "Connect to YNAB" }),
    );
  });

  it("offers no action while the status is unavailable", () => {
    renderCard(statuses.oauth, { isError: true });

    expect(
      screen.getByText("YNAB connection status is unavailable."),
    ).toBeInTheDocument();
    expect(actionButtons()).toEqual([]);
  });
});

describe("YnabConnectionCard for a signed-in user who is not an Admin", () => {
  it.each([
    ["oauthAvailable", "Not connected"],
    ["reconnectRequired", "Reconnect required"],
    ["oauth", "Connected"],
    ["personalAccessTokenWithOAuthAvailable", "Connected"],
    ["notConfigured", "Not Configured"],
  ] as const)("shows the %s state without connect, reconnect or disconnect", (name, badge) => {
    renderCard(statuses[name], { admin: false });

    expect(screen.getByText(badge)).toBeInTheDocument();
    expect(screen.queryAllByRole("button")).toEqual([]);
    expect(
      screen.getByRole("link", { name: "Privacy policy" }),
    ).toHaveAttribute("href", "/privacy");
  });
});

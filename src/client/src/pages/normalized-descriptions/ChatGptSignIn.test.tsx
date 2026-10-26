import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useChatGptSignIn } from "@/hooks/useChatGptSignIn";
import { ChatGptSignIn } from "./ChatGptSignIn";

vi.mock("@/hooks/useChatGptSignIn", () => ({ useChatGptSignIn: vi.fn() }));
const start = vi.fn();
const cancel = vi.fn();
const disconnect = vi.fn();
const reconnect = vi.fn();
function state(overrides: Partial<ReturnType<typeof useChatGptSignIn>> = {}) {
  vi.mocked(useChatGptSignIn).mockReturnValue({
    snapshot: null,
    pending: false,
    error: null,
    active: false,
    start,
    cancel,
    disconnect,
    reconnect,
    ...overrides,
  });
}
beforeEach(() => {
  vi.clearAllMocks();
  state();
});

it("requires owner attestation for each fresh sign-in", async () => {
  const user = userEvent.setup();
  render(<ChatGptSignIn connected={false} />);
  const button = screen.getByRole("button", { name: "Sign in with ChatGPT" });
  expect(button).toBeDisabled();
  await user.click(
    screen.getByRole("checkbox", { name: /I own this ChatGPT account/ }),
  );
  await user.click(button);
  expect(start).toHaveBeenCalledWith(true);
  expect(screen.getByRole("checkbox")).not.toBeChecked();
  expect(button).toBeDisabled();
});

it("shows loading and authorization errors without enabling new requests", () => {
  state({
    pending: true,
    error:
      "Only the configured account-owner administrator can manage this connection.",
  });
  render(<ChatGptSignIn connected={false} />);
  expect(screen.getByRole("status")).toHaveTextContent(
    "Updating ChatGPT connection",
  );
  expect(screen.getByRole("alert")).toHaveTextContent(
    "account-owner administrator",
  );
  expect(screen.getByRole("checkbox")).toBeDisabled();
});

it("offers safe provider link and cancel with explicit saved-credential warning", async () => {
  state({
    active: true,
    snapshot: {
      id: "test-id",
      state: "waiting",
      userCode: "FAKE-CODE",
      verificationUrl: "https://auth.openai.com/codex/device",
      localExpiresAt: "2026-10-26T15:00:00Z",
    },
  });
  const user = userEvent.setup();
  render(<ChatGptSignIn connected={false} />);
  const link = screen.getByRole("link", { name: "Open OpenAI sign-in" });
  expect(link).toHaveAttribute("rel", "noopener noreferrer");
  expect(link).toHaveAttribute("referrerpolicy", "no-referrer");
  expect(
    screen.getByText(/Credentials may already have been saved/),
  ).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Cancel sign-in" }));
  expect(cancel).toHaveBeenCalledOnce();
  expect(disconnect).not.toHaveBeenCalled();
});

it("requires explicit disconnect confirmation and supports reconnect consent", async () => {
  const user = userEvent.setup();
  render(<ChatGptSignIn connected />);
  await user.click(screen.getByRole("button", { name: "Disconnect ChatGPT" }));
  expect(disconnect).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Confirm disconnect" }));
  expect(disconnect).toHaveBeenCalledOnce();
  await user.click(screen.getByRole("checkbox"));
  await user.click(screen.getByRole("button", { name: "Reconnect ChatGPT" }));
  expect(reconnect).toHaveBeenCalledWith(true);
});

it.each([
  "success",
  "cancelled",
  "expired",
  "rejected",
  "restart",
  "reconnect",
  "busy",
])("has terminal guidance with no code or URL for %s", (terminal) => {
  state({ snapshot: { id: "test-id", state: terminal } });
  render(<ChatGptSignIn connected={false} />);
  expect(screen.getByRole("status")).toBeVisible();
  expect(
    screen.queryByRole("link", { name: "Open OpenAI sign-in" }),
  ).not.toBeInTheDocument();
  if (terminal === "success")
    expect(
      screen.getByRole("link", {
        name: "Set up post-save ChatGPT suggestions",
      }),
    ).toHaveAttribute("href", "/admin/normalized-descriptions?tab=catalog");
  else expect(screen.queryByRole("link")).not.toBeInTheDocument();
  expect(screen.queryByText(/Enter this code/)).not.toBeInTheDocument();
});

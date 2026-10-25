import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createQueryWrapper } from "@/test/test-utils";
import client from "@/lib/api-client";
import { JevConnection } from "./JevConnection";
vi.mock("@/lib/api-client", () => ({
  default: { GET: vi.fn(), POST: vi.fn() },
}));
describe("JevConnection", () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });
  it.each(["missing", "disabled"] as const)(
    "hides dependent controls when %s",
    async (state) => {
      vi.mocked(client.GET).mockResolvedValue({
        data: { state, reason: state, models: [] },
      } as never);
      render(<JevConnection />, { wrapper: createQueryWrapper() });
      expect(
        await screen.findByText(
          state === "missing" ? "Not configured" : "Disabled",
        ),
      ).toBeInTheDocument();
      expect(
        screen.queryByRole("button", { name: "Check connection" }),
      ).not.toBeInTheDocument();
      expect(client.POST).not.toHaveBeenCalled();
    },
  );
  it("performs only passive status until explicit check and displays returned models", async () => {
    vi.mocked(client.GET).mockResolvedValue({
      data: { state: "configuredUnverified", reason: "notChecked", models: [] },
    } as never);
    vi.mocked(client.POST).mockResolvedValue({
      data: {
        state: "connected",
        reason: "authenticationVerified",
        models: ["jev-latest"],
      },
    } as never);
    render(<JevConnection />, { wrapper: createQueryWrapper() });
    const button = await screen.findByRole("button", {
      name: "Check connection",
    });
    expect(client.POST).not.toHaveBeenCalled();
    expect(screen.getByText("Not checked")).toBeInTheDocument();
    await userEvent.click(button);
    expect(await screen.findByText("jev-latest")).toBeInTheDocument();
    expect(screen.getByText("Connected", { exact: true })).toBeInTheDocument();
    expect(
      screen.queryByText("authenticationVerified"),
    ).not.toBeInTheDocument();
    vi.mocked(client.POST).mockResolvedValue({ error: {} } as never);
    await userEvent.click(button);
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Connection check unavailable",
    );
    expect(
      screen.queryByText("Connected", { exact: true }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole("region", { name: "Available Jev models" }),
    ).not.toBeInTheDocument();
    expect(client.POST).toHaveBeenCalledWith(
      "/api/jev/check",
      expect.objectContaining({ middleware: expect.any(Array) }),
    );
  });
  it("hides controls while loading", () => {
    vi.mocked(client.GET).mockReturnValue(new Promise(() => {}));
    render(<JevConnection />, { wrapper: createQueryWrapper() });
    expect(screen.getByText(/Loading connection/)).toBeInTheDocument();
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
  });
  it("hides controls on status failure", async () => {
    vi.mocked(client.GET).mockResolvedValue({ error: {} } as never);
    render(<JevConnection />, { wrapper: createQueryWrapper() });
    await waitFor(() =>
      expect(screen.getByRole("alert")).toHaveTextContent(
        "Connection status unavailable",
      ),
    );
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
  });
});

import { describe, it, expect, beforeEach } from "vitest";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderWithProviders } from "@/test/test-utils";
import { ThemeToggle } from "./ThemeToggle";

describe("ThemeToggle", () => {
  beforeEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute("data-palette");
    document.documentElement.removeAttribute("data-density");
  });

  it("renders the toggle button with an sr-only label", () => {
    renderWithProviders(<ThemeToggle />);
    expect(screen.getByText("Appearance settings")).toBeInTheDocument();
  });

  it("opens the appearance menu with palette and density groups", async () => {
    const user = userEvent.setup();
    renderWithProviders(<ThemeToggle />);

    await user.click(screen.getByRole("button"));

    expect(screen.getByText("Palette")).toBeInTheDocument();
    expect(screen.getByText("Density")).toBeInTheDocument();
    // Paper intensity and motion were removed — the design ships at
    // "soft" / "subtle" and there is nothing left to configure.
    expect(screen.queryByText("Paper intensity")).not.toBeInTheDocument();
    expect(screen.queryByText("Motion")).not.toBeInTheDocument();
  });

  it("applies the palette to <html> and localStorage when selected", async () => {
    const user = userEvent.setup();
    renderWithProviders(<ThemeToggle />);

    await user.click(screen.getByRole("button"));
    await user.click(screen.getByRole("menuitemradio", { name: "Paper" }));

    expect(document.documentElement.getAttribute("data-palette")).toBe("paper");
    expect(localStorage.getItem("appearance.palette")).toBe("paper");
  });

  it("applies the density to <html> when selected", async () => {
    const user = userEvent.setup();
    renderWithProviders(<ThemeToggle />);

    await user.click(screen.getByRole("button"));
    await user.click(screen.getByRole("menuitemradio", { name: "Compact" }));

    expect(document.documentElement.getAttribute("data-density")).toBe(
      "compact",
    );
  });
});

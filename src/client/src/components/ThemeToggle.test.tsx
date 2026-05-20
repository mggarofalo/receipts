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

  it("opens the appearance menu with all preference groups", async () => {
    const user = userEvent.setup();
    renderWithProviders(<ThemeToggle />);

    await user.click(screen.getByRole("button"));

    expect(screen.getByText("Palette")).toBeInTheDocument();
    expect(screen.getByText("Density")).toBeInTheDocument();
    expect(screen.getByText("Paper intensity")).toBeInTheDocument();
    expect(screen.getByText("Motion")).toBeInTheDocument();
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

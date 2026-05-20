import { describe, it, expect } from "vitest";
import { screen } from "@testing-library/react";
import { renderWithProviders } from "@/test/test-utils";
import { PublicLayout } from "./PublicLayout";

describe("PublicLayout", () => {
  it("renders the Receipts brand link", () => {
    renderWithProviders(<PublicLayout />);
    const brandLink = screen.getByText("Receipts");
    expect(brandLink).toBeInTheDocument();
    expect(brandLink.closest("a")).toHaveAttribute("href", "/");
  });

  it("renders the appearance settings button", () => {
    renderWithProviders(<PublicLayout />);
    expect(screen.getByText("Appearance settings")).toBeInTheDocument();
  });

  it("renders a header element", () => {
    renderWithProviders(<PublicLayout />);
    const header = document.querySelector("header");
    expect(header).toBeInTheDocument();
  });

  it("renders a main element for content", () => {
    renderWithProviders(<PublicLayout />);
    const main = document.querySelector("main");
    expect(main).toBeInTheDocument();
  });
});

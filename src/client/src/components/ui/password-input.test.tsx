import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { PasswordInput } from "./password-input";

describe("PasswordInput", () => {
  it("renders as a password input by default", () => {
    render(<PasswordInput placeholder="Enter password" />);
    const input = screen.getByPlaceholderText("Enter password");
    expect(input).toHaveAttribute("type", "password");
  });

  it("shows 'Show password' toggle button", () => {
    render(<PasswordInput />);
    expect(screen.getByLabelText("Show password")).toBeInTheDocument();
  });

  it("toggles to text input when visibility button is clicked", async () => {
    const user = userEvent.setup();
    render(<PasswordInput placeholder="Enter password" />);

    const input = screen.getByPlaceholderText("Enter password");
    const toggleButton = screen.getByLabelText("Show password");

    await user.click(toggleButton);

    expect(input).toHaveAttribute("type", "text");
    expect(screen.getByLabelText("Hide password")).toBeInTheDocument();
  });

  it("toggles back to password input on second click", async () => {
    const user = userEvent.setup();
    render(<PasswordInput placeholder="Enter password" />);

    const input = screen.getByPlaceholderText("Enter password");
    const toggleButton = screen.getByLabelText("Show password");

    await user.click(toggleButton);
    expect(input).toHaveAttribute("type", "text");

    await user.click(screen.getByLabelText("Hide password"));
    expect(input).toHaveAttribute("type", "password");
  });

  it("forwards props to the input element", () => {
    render(
      <PasswordInput
        placeholder="test placeholder"
        disabled
        aria-invalid="true"
      />,
    );
    const input = screen.getByPlaceholderText("test placeholder");
    expect(input).toBeDisabled();
    expect(input).toHaveAttribute("aria-invalid", "true");
  });

  it("applies custom className", () => {
    render(<PasswordInput className="custom-class" placeholder="test" />);
    const input = screen.getByPlaceholderText("test");
    expect(input.className).toContain("custom-class");
  });

  it("toggle button is in natural tab order so keyboard users can reach it", () => {
    render(<PasswordInput />);
    const toggleButton = screen.getByLabelText("Show password");
    expect(toggleButton).not.toHaveAttribute("tabindex", "-1");
  });

  it("toggle button is reachable and activatable via keyboard", async () => {
    const user = userEvent.setup();
    render(<PasswordInput placeholder="Enter password" />);

    const input = screen.getByPlaceholderText("Enter password");

    await user.tab(); // password input
    expect(input).toHaveFocus();
    await user.tab(); // toggle button
    expect(screen.getByLabelText("Show password")).toHaveFocus();

    await user.keyboard("{Enter}");
    expect(input).toHaveAttribute("type", "text");
    expect(screen.getByLabelText("Hide password")).toHaveFocus();
  });
});

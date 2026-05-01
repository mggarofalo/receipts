import "@/test/setup-combobox-polyfills";
import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { TooltipProvider } from "@/components/ui/tooltip";
import { ConfidenceIndicator } from "./ConfidenceIndicator";
import type { ConfidenceLevel } from "./types";

function renderIndicator(confidence: ConfidenceLevel | undefined) {
  return render(
    <TooltipProvider delayDuration={0}>
      <ConfidenceIndicator confidence={confidence} />
    </TooltipProvider>,
  );
}

describe("ConfidenceIndicator", () => {
  describe("render-nothing behaviour", () => {
    it("renders nothing when confidence is undefined", () => {
      const { container } = renderIndicator(undefined);
      expect(container).toBeEmptyDOMElement();
    });

    it("renders nothing when confidence is high", () => {
      const { container } = renderIndicator("high");
      expect(container).toBeEmptyDOMElement();
    });

    it("renders nothing when confidence is none", () => {
      const { container } = renderIndicator("none");
      expect(container).toBeEmptyDOMElement();
    });
  });

  describe("low confidence", () => {
    it("renders neutral rating copy 'AI: low' (no verb)", () => {
      renderIndicator("low");
      expect(screen.getByText("AI: low")).toBeInTheDocument();
      // Sanity-check: old verb copy must be gone.
      expect(screen.queryByText("Low confidence")).not.toBeInTheDocument();
      expect(screen.queryByText("Review")).not.toBeInTheDocument();
    });

    it("exposes a rating-shaped aria-label, not a button label", () => {
      renderIndicator("low");
      const chip = screen.getByLabelText("AI confidence rating: low");
      expect(chip).toBeInTheDocument();
    });

    it("is not focusable and has no button role", () => {
      renderIndicator("low");
      const chip = screen.getByLabelText("AI confidence rating: low");
      expect(chip.getAttribute("tabindex")).toBe("-1");
      expect(chip.getAttribute("role")).not.toBe("button");
      expect(screen.queryByRole("button")).not.toBeInTheDocument();
    });

    it("uses cursor-default so it does not signal interactivity", () => {
      renderIndicator("low");
      const chip = screen.getByLabelText("AI confidence rating: low");
      expect(chip.className).toContain("cursor-default");
    });

    it("shows a tooltip with the verify-before-saving guidance on hover", async () => {
      const user = userEvent.setup();
      renderIndicator("low");
      const chip = screen.getByLabelText("AI confidence rating: low");

      await user.hover(chip);

      // Radix renders the tooltip in a portal; the role="tooltip" element
      // appears asynchronously after hover.
      const tooltip = await screen.findByRole("tooltip");
      expect(tooltip).toHaveTextContent(/low confidence/i);
      expect(tooltip).toHaveTextContent(/verify before saving/i);
    });
  });

  describe("medium confidence", () => {
    it("renders neutral rating copy 'AI: medium' (no 'Review' verb)", () => {
      renderIndicator("medium");
      expect(screen.getByText("AI: medium")).toBeInTheDocument();
      expect(screen.queryByText("Review")).not.toBeInTheDocument();
    });

    it("exposes a rating-shaped aria-label, not a button label", () => {
      renderIndicator("medium");
      const chip = screen.getByLabelText("AI confidence rating: medium");
      expect(chip).toBeInTheDocument();
    });

    it("is not focusable and has no button role", () => {
      renderIndicator("medium");
      const chip = screen.getByLabelText("AI confidence rating: medium");
      expect(chip.getAttribute("tabindex")).toBe("-1");
      expect(chip.getAttribute("role")).not.toBe("button");
      expect(screen.queryByRole("button")).not.toBeInTheDocument();
    });

    it("uses cursor-default so it does not signal interactivity", () => {
      renderIndicator("medium");
      const chip = screen.getByLabelText("AI confidence rating: medium");
      expect(chip.className).toContain("cursor-default");
    });

    it("shows a tooltip with the quick-glance guidance on hover", async () => {
      const user = userEvent.setup();
      renderIndicator("medium");
      const chip = screen.getByLabelText("AI confidence rating: medium");

      await user.hover(chip);

      const tooltip = await screen.findByRole("tooltip");
      expect(tooltip).toHaveTextContent(/medium confidence/i);
      expect(tooltip).toHaveTextContent(/quick glance/i);
    });
  });
});

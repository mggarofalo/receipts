import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("@/lib/toast", () => ({
  showApiError: vi.fn(),
  showNetworkError: vi.fn(),
}));

vi.mock("sonner", () => ({
  toast: { error: vi.fn() },
}));

import { handleGlobalError } from "./global-error-handler";
import { showApiError, showNetworkError } from "@/lib/toast";
import { toast } from "sonner";

beforeEach(() => {
  vi.clearAllMocks();
});

describe("handleGlobalError", () => {
  it("passes the ProblemDetails detail to showApiError for a 400", () => {
    handleGlobalError({
      status: 400,
      title: "Bad Request",
      detail: "Date cannot be in the future",
    });
    expect(showApiError).toHaveBeenCalledWith(
      400,
      "Date cannot be in the future",
    );
  });

  it("falls back to the first field error when detail is absent", () => {
    handleGlobalError({
      status: 400,
      title: "One or more validation errors occurred.",
      errors: { Date: ["Date cannot be in the future"] },
    });
    expect(showApiError).toHaveBeenCalledWith(
      400,
      "Date cannot be in the future",
    );
  });

  it("passes the 409 duplicate-name detail", () => {
    handleGlobalError({
      status: 409,
      detail: "A card named 'Visa' already exists.",
    });
    expect(showApiError).toHaveBeenCalledWith(
      409,
      "A card named 'Visa' already exists.",
    );
  });

  it("ignores the generic ASP.NET validation title (no useful message)", () => {
    handleGlobalError({
      status: 400,
      title: "One or more validation errors occurred.",
    });
    expect(showApiError).toHaveBeenCalledWith(400, undefined);
  });

  it("passes undefined when the error carries no ProblemDetails text", () => {
    handleGlobalError({ status: 500 });
    expect(showApiError).toHaveBeenCalledWith(500, undefined);
  });

  it("shows a network toast for Chrome's 'Failed to fetch' TypeError", () => {
    handleGlobalError(new TypeError("Failed to fetch"));
    expect(showNetworkError).toHaveBeenCalledTimes(1);
    expect(showApiError).not.toHaveBeenCalled();
  });

  it("shows a network toast for Firefox's network TypeError", () => {
    handleGlobalError(
      new TypeError("NetworkError when attempting to fetch resource."),
    );
    expect(showNetworkError).toHaveBeenCalledTimes(1);
  });

  it("shows a network toast for Safari's 'Load failed' TypeError", () => {
    handleGlobalError(new TypeError("Load failed"));
    expect(showNetworkError).toHaveBeenCalledTimes(1);
  });

  it("shows a timeout toast for a TimeoutError DOMException", () => {
    handleGlobalError(new DOMException("timed out", "TimeoutError"));
    expect(toast.error).toHaveBeenCalledWith(
      "Request timed out. Please try again.",
    );
    expect(showApiError).not.toHaveBeenCalled();
    expect(showNetworkError).not.toHaveBeenCalled();
  });
});

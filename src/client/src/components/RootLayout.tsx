import { useEffect } from "react";
import { Outlet, useLocation, useNavigate } from "react-router";
import { toast } from "sonner";
import { Toaster } from "@/components/ui/sonner";
import { ErrorBoundary } from "@/components/ErrorBoundary";
import {
  addServerErrorListener,
  hasShownServerErrorPage,
  markServerErrorPageShown,
} from "@/lib/server-error-bus";

/**
 * On the first 5xx in a tab session we navigate to /error/500 for the full
 * editorial treatment; subsequent 5xx land as toasts so we don't yank the
 * user out of whatever they were doing repeatedly (RECEIPTS-740).
 *
 * Listener registered here so it survives across page transitions — placing
 * it lower in the tree would unmount whenever the user navigates.
 */
function ServerErrorBridge() {
  const navigate = useNavigate();
  const location = useLocation();

  useEffect(() => {
    const unsubscribe = addServerErrorListener(() => {
      // Already on the error page — don't bounce back to it.
      if (location.pathname === "/error/500") return;
      if (hasShownServerErrorPage()) {
        toast.error("A server error occurred. Please try again.");
        return;
      }
      markServerErrorPageShown();
      navigate("/error/500");
    });
    return unsubscribe;
  }, [navigate, location.pathname]);

  return null;
}

export function RootLayout() {
  return (
    <ErrorBoundary>
      <ServerErrorBridge />
      <Outlet />
      <Toaster />
    </ErrorBoundary>
  );
}

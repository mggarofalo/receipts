import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { RouterProvider } from "react-router";
import {
  MutationCache,
  QueryCache,
  QueryClient,
  QueryClientProvider,
} from "@tanstack/react-query";
import { toast } from "sonner";
import { showApiError, showNetworkError } from "@/lib/toast";
import { isTimeoutError } from "@/lib/api-client";
import { sentryCreateBrowserRouter } from "@/lib/sentry";
import { AppearanceProvider } from "@/contexts/AppearanceContext";
import { TooltipProvider } from "@/components/ui/tooltip";
import { AuthProvider } from "@/contexts/AuthContext";
import { ShortcutsProvider } from "@/contexts/ShortcutsContext";
import { routeConfig } from "./App.tsx";
import "./index.css";

const router = sentryCreateBrowserRouter(routeConfig);

function handleGlobalError(error: unknown) {
  if (isTimeoutError(error)) {
    toast.error("Request timed out. Please try again.");
    return;
  }

  if (
    error &&
    typeof error === "object" &&
    "status" in error &&
    typeof (error as Record<string, unknown>).status === "number"
  ) {
    showApiError((error as Record<string, unknown>).status as number);
    return;
  }

  if (error instanceof TypeError && error.message === "Failed to fetch") {
    showNetworkError();
    return;
  }
}

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 5 * 60 * 1000,
      retry: 1,
    },
  },
  queryCache: new QueryCache({
    onError: handleGlobalError,
  }),
  mutationCache: new MutationCache({
    onError: handleGlobalError,
  }),
});

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <AppearanceProvider>
      <QueryClientProvider client={queryClient}>
        <AuthProvider>
          <ShortcutsProvider>
            <TooltipProvider>
              <RouterProvider router={router} />
            </TooltipProvider>
          </ShortcutsProvider>
        </AuthProvider>
      </QueryClientProvider>
    </AppearanceProvider>
  </StrictMode>,
);

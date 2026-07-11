import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { RouterProvider } from "react-router";
import {
  MutationCache,
  QueryCache,
  QueryClient,
  QueryClientProvider,
} from "@tanstack/react-query";
import { sentryCreateBrowserRouter } from "@/lib/sentry";
import { handleGlobalError } from "@/lib/global-error-handler";
import { AppearanceProvider } from "@/contexts/AppearanceContext";
import { TooltipProvider } from "@/components/ui/tooltip";
import { AuthProvider } from "@/contexts/AuthContext";
import { ShortcutsProvider } from "@/contexts/ShortcutsContext";
import { routeConfig } from "./App.tsx";
import "./index.css";

const router = sentryCreateBrowserRouter(routeConfig);

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

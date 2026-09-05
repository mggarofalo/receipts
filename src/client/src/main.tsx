import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { RouterProvider } from "react-router";
import { sentryCreateBrowserRouter } from "@/lib/sentry";
import { AppearanceProvider } from "@/contexts/AppearanceContext";
import { TooltipProvider } from "@/components/ui/tooltip";
import { AuthProvider } from "@/contexts/AuthContext";
import { ShortcutsProvider } from "@/contexts/ShortcutsContext";
import { routeConfig } from "./App.tsx";
import "./index.css";

const router = sentryCreateBrowserRouter(routeConfig);

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <AppearanceProvider>
        <AuthProvider>
          <ShortcutsProvider>
            <TooltipProvider>
              <RouterProvider router={router} />
            </TooltipProvider>
          </ShortcutsProvider>
        </AuthProvider>
    </AppearanceProvider>
  </StrictMode>,
);

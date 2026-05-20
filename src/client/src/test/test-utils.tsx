import { render, type RenderOptions } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import type { ReactNode } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { AuthContext, type AuthContextValue } from "@/contexts/auth-context";
import { AppearanceProvider } from "@/contexts/AppearanceContext";
import { TooltipProvider } from "@/components/ui/tooltip";

const defaultAuth: AuthContextValue = {
  user: null,
  isLoading: false,
  mustResetPassword: false,
  login: async () => {},
  logout: async () => {},
  changePassword: async () => {},
};

type RouteEntry =
  | string
  | { pathname: string; search?: string; state?: unknown };

interface WrapperOptions {
  auth?: Partial<AuthContextValue>;
  route?: RouteEntry;
}

export function createWrapper({ auth, route = "/" }: WrapperOptions = {}) {
  const authValue = { ...defaultAuth, ...auth };
  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <MemoryRouter initialEntries={[route]}>
        <AppearanceProvider>
          <TooltipProvider>
            <AuthContext.Provider value={authValue}>
              {children}
            </AuthContext.Provider>
          </TooltipProvider>
        </AppearanceProvider>
      </MemoryRouter>
    );
  };
}

export function renderWithProviders(
  ui: React.ReactElement,
  options?: WrapperOptions & Omit<RenderOptions, "wrapper">,
) {
  const { auth, route, ...renderOptions } = options ?? {};
  return render(ui, {
    wrapper: createWrapper({ auth, route }),
    ...renderOptions,
  });
}

export function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
}

export function createQueryWrapper(wrapperOptions?: WrapperOptions) {
  const queryClient = createQueryClient();
  const authValue = { ...defaultAuth, ...wrapperOptions?.auth };
  return function QueryWrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={[wrapperOptions?.route ?? "/"]}>
          <AppearanceProvider>
            <TooltipProvider>
              <AuthContext.Provider value={authValue}>
                {children}
              </AuthContext.Provider>
            </TooltipProvider>
          </AppearanceProvider>
        </MemoryRouter>
      </QueryClientProvider>
    );
  };
}

export function renderWithQueryClient(
  ui: React.ReactElement,
  options?: WrapperOptions & Omit<RenderOptions, "wrapper">,
) {
  const { auth, route, ...renderOptions } = options ?? {};
  return render(ui, {
    wrapper: createQueryWrapper({ auth, route }),
    ...renderOptions,
  });
}

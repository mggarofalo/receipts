vi.hoisted(() => vi.stubEnv("VITE_API_URL", "http://local-errors.test"));
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { AuthProvider } from "@/contexts/AuthContext";
import { RootLayout } from "@/components/RootLayout";
import { AppearanceProvider } from "@/contexts/AppearanceContext";
import { createAppQueryClient } from "@/lib/query-client";
import { clearTokens } from "@/lib/auth";
import { clearServerErrorPageFlag } from "@/lib/server-error-bus";
import Login from "./Login";

let status = 429;
const server = setupServer(
  http.post("*/api/auth/login", () =>
    HttpResponse.json(
      {
        status,
        detail:
          status === 429 ? "Too many login attempts" : "Invalid credentials",
      },
      { status },
    ),
  ),
);
const clients: ReturnType<typeof createAppQueryClient>[] = [];
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterAll(() => server.close());
beforeEach(() => {
  clearTokens();
  clearServerErrorPageFlag();
});
afterEach(() => {
  cleanup();
  clients.splice(0).forEach((c) => c.clear());
  server.resetHandlers();
});

async function submitLogin() {
  const client = createAppQueryClient();
  clients.push(client);
  render(
    <MemoryRouter initialEntries={["/login"]}>
      <AppearanceProvider>
        <AuthProvider queryClientFactory={() => client}>
          <Routes>
            <Route element={<RootLayout />}>
              <Route path="/login" element={<Login />} />
              <Route
                path="/error/500"
                element={<h1>Global server error route</h1>}
              />
            </Route>
          </Routes>
        </AuthProvider>
      </AppearanceProvider>
    </MemoryRouter>,
  );
  const user = userEvent.setup();
  await user.type(screen.getByLabelText(/email/i), "alice@example.test");
  await user.type(screen.getByLabelText(/^password/i), "Password123!");
  await user.click(screen.getByRole("button", { name: /sign in/i }));
  return screen.findByRole("alert");
}

it("explains actual login429 as temporary rate limiting instead of incorrect credentials", async () => {
  status = 429;
  const alert = await submitLogin();
  expect(alert).not.toHaveTextContent("Invalid email or password");
  expect(alert).toHaveTextContent(/too many|rate limit/i);
  expect(alert).toHaveTextContent(/try again|retry|wait/i);
});

it("retains generic invalid-credentials guidance for actual login401", async () => {
  status = 401;
  expect(await submitLogin()).toHaveTextContent("Invalid email or password");
});

it("keeps server failure on the login form with retry guidance instead of invalid credentials", async () => {
  status = 503;
  const alert = await submitLogin();
  expect(alert).toHaveTextContent(/unavailable|could not sign in/i);
  expect(alert).toHaveTextContent(/try again|retry/i);
  expect(alert).not.toHaveTextContent("Invalid email or password");
  expect(screen.getByLabelText(/email/i)).toHaveValue("alice@example.test");
  expect(
    screen.queryByRole("heading", { name: "Global server error route" }),
  ).not.toBeInTheDocument();
});

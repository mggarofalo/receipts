/// <reference types="vitest/globals" />
// Share the browser environment with unit tests; HTTP integration adds only MSW.
import "./setup";
import { server } from "./msw/server";

beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

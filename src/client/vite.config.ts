import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { sentryVitePlugin } from "@sentry/vite-plugin";
import path from "path";

export default defineConfig({
  build: {
    sourcemap: true,
  },
  plugins: [
    react(),
    tailwindcss(),
    // Upload source maps to Sentry during production builds (CI only).
    // Requires SENTRY_AUTH_TOKEN, SENTRY_ORG, and SENTRY_PROJECT env vars.
    process.env.SENTRY_AUTH_TOKEN
      ? sentryVitePlugin({
          org: process.env.SENTRY_ORG,
          project: process.env.SENTRY_PROJECT,
          authToken: process.env.SENTRY_AUTH_TOKEN,
          release: {
            name: `receipts-frontend@${process.env.VITE_APP_VERSION || "dev"}`,
          },
          sourcemaps: {
            filesToDeleteAfterUpload: ["./dist/**/*.map"],
          },
        })
      : null,
  ].filter(Boolean),
  define: {
    __APP_VERSION__: JSON.stringify(process.env.VITE_APP_VERSION || "dev"),
    __COMMIT_HASH__: JSON.stringify(process.env.VITE_COMMIT_HASH || "local"),
  },
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "./src"),
    },
  },
  test: {
    globals: true,
    environment: "jsdom",
    pool: "threads",
    setupFiles: ["./src/test/setup.ts"],
    // eslint-rules/ lives outside src (it's plain JS consumed by the flat
    // config, not app code) but its RuleTester specs still run with the suite.
    include: ["src/**/*.test.{ts,tsx}", "eslint-rules/**/*.test.js"],
    exclude: ["src/**/*.integration.test.{ts,tsx}"],
    coverage: {
      provider: "v8",
      reporter: ["text", "cobertura", "html"],
      reportsDirectory: "./coverage",
      include: ["src/**/*.{ts,tsx}"],
      exclude: [
        "src/generated/**",
        "**/*.d.ts",
        "**/*.test.{ts,tsx}",
        "src/test/**",
        "src/main.tsx",
        "src/components/ui/!(combobox|currency-input).{ts,tsx}",
        "src/lib/api-types.ts",
      ],
      thresholds: process.env.VITEST_SHARD
        ? undefined
        : {
            statements: 75,
            branches: 65,
            functions: 70,
            lines: 78,
          },
    },
  },
  server: {
    // Bind the IPv4 loopback literal rather than Vite's default `localhost`. On
    // Windows that hostname resolves to ::1 only, so Aspire's DCP proxy — which
    // dials the target over IPv4 — connects to nothing and holds the request open
    // forever. Without this, http://localhost:5173 hangs and the app is reachable
    // only on the dynamic port Aspire passes to Vite via --port (RECEIPTS-882).
    //
    // Deliberately NOT `true`/`0.0.0.0`: those also fix the mismatch but publish the
    // dev server — and the /api and /hubs proxies below, which forward to the backend
    // with TLS validation off — to every host on the LAN.
    host: "127.0.0.1",
    proxy: {
      "/api": {
        target: process.env.services__api__https__0 ?? process.env.services__api__http__0 ?? "https://localhost:5001",
        changeOrigin: true,
        secure: false,
      },
      "/hubs": {
        target: process.env.services__api__https__0 ?? process.env.services__api__http__0 ?? "https://localhost:5001",
        changeOrigin: true,
        secure: false,
        ws: true,
      },
    },
  },
});

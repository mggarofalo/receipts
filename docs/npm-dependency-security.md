# npm dependency security review

The 10 September 2026 review found no npm advisories after compatible updates. Both
workspaces also report zero production advisories with development packages omitted.

## Audit results

All commands ran with the repository's pinned Node 24.21.0 and npm 11.19.0.

| Workspace | Before full audit | Before production audit | After full audit | After production audit |
|---|---:|---:|---:|---:|
| Root tooling | 13 | 0 | 0 | 0 |
| React client | 29 | 2 | 0 | 0 |

Run the same checks from each workspace:

```bash
npm audit
npm audit --omit=dev
```

An audit count describes affected dependency entries. It does not prove that the application
exposes the reported code path.

## Patched packages

The update stayed within the existing major versions. It included these direct tools and their
libraries:

- Vite 7.3.6, Vitest 4.1.11, and `@vitest/coverage-v8` 4.1.11.
- React Router 7.18.3 and SignalR 10.0.11.
- MSW 2.15.0, the Sentry Vite plugin 5.4.0, and the React Vite plugin 5.2.0.
- Spectral 6.16.3, js-yaml 4.3.2, and Commitlint 20.5.3.

Targeted transitive updates removed the remaining advisories. The client temporarily overrides
`brace-expansion` 1.x to 1.1.18 because `eslint-plugin-jsx-a11y` still permits an older vulnerable
release. Remove the override when that dependency tree resolves 1.1.18 or later on its own.

Vite's [WebSocket file-read advisory](https://github.com/vitejs/vite/security/advisories/GHSA-p9ff-h696-f583)
and [Windows path advisory](https://github.com/vitejs/vite/security/advisories/GHSA-fx2h-pf6j-xcff)
applied to the development server, so the Vite patch was required. The repository's
`windowsHide` patch remains applied to Vite 7.3.6.

The Vitest critical advisory concerned its optional browser user interface and API server. This
project uses command-line run, watch, coverage, and jsdom modes. React Router's reported server,
RSC, and manifest paths are also outside this browser-only SPA. SignalR uses the browser's native
WebSocket implementation rather than its Node `ws` adapter. We updated all three dependency trees
despite those exposure limits, and no advisory is deferred.

## Update policy

Use targeted compatible updates. Do not use `npm audit fix --force`, because it can cross major
versions and change runtime behavior without review.

After any dependency update:

1. Run full and production-only audits in both workspaces.
2. Review `allowScripts` with `npm install-scripts ls`.
3. Pin any approved installer to its reviewed version.
4. Run generated-type, lint, build, unit, integration, and Aspire startup checks.

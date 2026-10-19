# ChatGPT normalization provider

This package uses the pinned Pi `ModelRuntime` provider capability. It never
constructs an agent session, loads extensions, or provides model tools. Receipts
owns payload selection, strict output validation, durable work and reviewed writes.
The provider owns OAuth, token refresh and model transport.

Install with `npm ci --ignore-scripts`, then `npm run repair`. Pi publishes an npm
shrinkwrap with an older nested `brace-expansion`; the repair copies the exact
integrity-locked 5.0.12 package already installed at the root. It validates versions
and paths and never downloads code. Keep the repair with SDK upgrades until its
upstream shrinkwrap is fixed. Run `npm test` for the network-free protocol tests.

The .NET adapter invokes `bridge.mjs` with one command (`login`, `logout`, `status`,
`models`, or `infer`). Requests arrive on stdin; newline-delimited JSON results
arrive on stdout. Error text is converted to fixed diagnostic categories. Provider
stderr is discarded. The caller kills the process tree on cancellation or timeout.
Do not run this bridge directly against a live credential volume: the .NET adapter
holds the cross-process file lock, including during CLI login and logout.

`RECEIPTS_CHATGPT_HOME` is supplied only by the adapter. No application database,
JWT, API-key, personal HOME, proxy or Node injection settings are inherited.
Credentials and SDK model cache live in this private directory; temporary HOME and
working directories are removed after each command. A model listing is cached
catalog information, not proof that the signed-in account can use that model.

The model receives text and a JSON schema in its instructions, without receipt
images or application credentials. This is not a claim of provider-enforced JSON
schema or a hard token-spend ceiling. Application validation remains authoritative.

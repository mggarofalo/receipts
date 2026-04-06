# Agentic Testing Guide

This document defines how AI agents should approach test authoring in this codebase. It complements [testing.md](testing.md), which covers frameworks, conventions, and tooling. This document covers *principles* — the judgment calls that determine whether a test suite is genuinely useful or just inflating a coverage number.

## Test-First Workflow

Write tests before implementation. This is not optional.

1. **Red.** Write a failing test that describes the behavior you're about to implement. Run it. Confirm it fails. If it passes without any implementation, the test is wrong — it's asserting something trivially true or testing the wrong thing.
2. **Green.** Write the minimal implementation that makes the test pass. One behavior per cycle. Resist the urge to implement three things and then write tests for all three — that's test-after, and it biases the tests toward confirming what you just wrote rather than specifying what should be true.
3. **Refactor.** Clean up the implementation while keeping the tests green. This is high-value, low-risk work. Mechanical cleanup with a safety net.

Work in tight loops: one test, one behavior, confirm, move on. Large batches of tests written after large batches of code produce tests that mirror the implementation rather than specify the behavior.

### When test-first isn't practical

Some code resists test-first — visual components with no branching logic, configuration wiring, or exploratory prototypes. In those cases, write tests immediately after, but be honest about it: the tests you write after seeing the implementation are inherently less rigorous. Compensate by testing from the user's perspective (what does the component render? what happens when the user clicks?) rather than the implementation's perspective (did this internal function get called?).

## What Makes a Good Test

A good test has three properties:

1. **It tests behavior, not implementation.** Assert on what the system does, not how it does it. `expect(result.total).toBe(42)` is good. `expect(internalCalculator.computeSubtotal).toHaveBeenCalled()` is brittle — it breaks on refactors that don't change behavior.

2. **It would fail if the behavior broke.** This sounds obvious, but agent-authored tests often pass vacuously. A test that renders a component and asserts `expect(container).toBeTruthy()` tells you nothing — a `<div />` would pass. Assert on the specific content, state, or side effect that matters.

3. **It has a descriptive name that reads as a specification.** `should reject expired tokens` tells you what the system guarantees. `test auth function` tells you nothing. Good test names are documentation; when a test fails, the name should tell you what broke without reading the test body.

### Tests that are actively harmful

Avoid these patterns — they increase maintenance cost without catching bugs:

- **Assertion-free tests.** Calling a function and not asserting on the result. These exist only to increase line coverage.
- **Implementation-mirroring tests.** Tests that reconstruct the implementation logic in the assertion. If the test and the code have the same bug, the test passes and catches nothing.
- **Over-mocked tests.** When you mock everything except the function under test, you're testing that your mocks work, not that your code works. Mock at boundaries (API clients, external services), not internal collaborators.
- **Snapshot tests of dynamic content.** Snapshots of rendered HTML break on every styling change and train developers to blindly update them. They are banned in this codebase (see [testing.md](testing.md)).

## Coverage as an Observed Outcome

**Never target a coverage number.** Coverage is a useful signal when it emerges from behavior-driven tests. It becomes actively misleading when it's the goal.

When you're told to implement a feature, your job is to test every meaningful behavior of that feature. If you do that well, coverage follows naturally. If you find yourself writing a test because a line is uncovered rather than because a behavior is unspecified, stop — that's Goodhart's Law in action.

### When low coverage is acceptable

Some code categories resist unit testing and that's fine:

- **Visualization components** (charts, graphs). The meaningful bugs are visual regressions, not logic errors. A unit test asserting that an SVG element exists doesn't catch a chart rendering the wrong data shape. If chart components have branching logic (conditional formatting, data transforms), test that logic in isolation.
- **Configuration wiring** (DI setup, route definitions, middleware registration). These are validated by the application starting and integration tests passing.
- **Generated code.** Already excluded from coverage by convention.

### When low coverage is a problem

- **Hooks and services** with business logic. These should be thoroughly tested.
- **Components with conditional rendering, form validation, or user interaction flows.** These have real behavior that can break.
- **Data transformation functions.** Pure functions are the easiest code to test and the least excusable to skip.

## Frontend-Specific Guidance

### What to test in React components

Test from the user's perspective using React Testing Library:

- **Rendered content.** Does the component show the right data? Use `screen.getByRole`, `screen.getByText`, not implementation details like `wrapper.find(InternalComponent)`.
- **User interactions.** Click a button — does the right thing happen? Type in an input — does it validate? Use `userEvent`, not `fireEvent`.
- **Loading, error, and empty states.** These are the states users actually encounter. Mock the hook to return `isPending: true` and verify the loading indicator appears.
- **Conditional rendering.** If a section shows/hides based on state, test both branches.

### What NOT to test in React components

- Internal state management (test via the rendered output instead)
- Styling and CSS classes (not behavior)
- Third-party library internals (test your integration with them, not their behavior)
- Exact DOM structure (couples tests to implementation)

### New hooks must have tests

Every custom hook (`useX`) must have a corresponding `useX.test.ts` that:
- Tests the success path (data returned correctly)
- Tests the error path (API error handled, toast shown)
- Tests mutations (correct API call made, cache invalidated)
- Uses the mock patterns documented in [testing.md](testing.md#mock-fidelity-rules)

### New pages must have tests

Every page component must have a corresponding test that:
- Renders the page with mocked hook data and verifies key content appears
- Tests loading and error states
- Tests primary user interactions (create, edit, delete flows) at least for the happy path

## Backend-Specific Guidance

### What to test

- **MediatR handlers** (commands and queries). These are the core business logic. Test each handler with realistic inputs, edge cases, and error conditions.
- **Mappers.** Use concrete Mapperly mapper instances (never mock them). Verify all fields map correctly, especially fields with transformations.
- **Validators.** Test both valid and invalid inputs. Test boundary conditions.
- **Services** with business logic. Test the logic, mock the repository.
- **Controllers** only if they contain logic beyond delegation. Thin controllers that just call MediatR don't need direct tests.

### What NOT to test

- Auto-generated code (migrations, NSwag DTOs)
- Trivial property getters/setters
- Framework behavior (does EF Core save correctly? does MediatR dispatch?)

## The Agent Contract

When implementing a feature, the agent commits to:

1. **Tests and implementation in the same PR.** Never "I'll add tests in a follow-up."
2. **Test-first when possible.** Write the failing test, then the implementation.
3. **Meaningful assertions in every test.** No assertion-free tests. No `Assert.True(true)`.
4. **Coverage is an outcome, not a target.** If coverage drops, it's because a behavior is untested — find and test that behavior, don't pad with line-covering tests.
5. **Frontend and backend parity.** Both stacks require tests. Adding a new API endpoint and a new page component means tests for both.
6. **Never modify coverage thresholds or CI configuration to make tests pass.** Coverage configs and CI gates are not part of the implementation task.

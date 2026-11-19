/**
 * Leaves the app for an external address with a full page navigation.
 * A module boundary because jsdom does not allow `window.location.assign` to
 * be replaced, so callers are tested by mocking this function.
 */
export function navigateToExternal(url: string): void {
  window.location.assign(url);
}

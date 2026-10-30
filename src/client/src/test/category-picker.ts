import { screen } from "@testing-library/react";
import type { UserEvent } from "@testing-library/user-event";

/**
 * Finds a category path in an open CategoryPathPicker the way a person would:
 * by searching the whole tree for it. Browsing shows one level at a time, so a
 * path is not on screen just because the picker is open. Results are named by
 * their full path ("Food / Dairy"), roots by their name, and "Unclassified"
 * appears when the query spells it.
 */
export async function findCategoryOption(user: UserEvent, path: string) {
  const search = await screen.findByRole("combobox", {
    name: "Search category paths",
  });
  await user.clear(search);
  await user.type(search, path);
  return screen.findByRole("option", { name: path });
}

/** Searches for `path` in the open picker and chooses it. */
export async function chooseCategoryOption(user: UserEvent, path: string) {
  await user.click(await findCategoryOption(user, path));
}

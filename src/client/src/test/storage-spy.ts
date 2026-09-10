type StorageMethod = "getItem" | "setItem";

export function findStorageMethodOwner(
  storage: Storage,
  method: StorageMethod,
): Storage {
  let owner: object | null = storage;

  while (owner && !Object.hasOwn(owner, method)) {
    owner = Object.getPrototypeOf(owner);
  }

  if (!owner) {
    throw new Error(`Storage implementation does not define ${method}`);
  }

  return owner as Storage;
}

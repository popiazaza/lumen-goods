// Indexing helper shared by specs. Not imported by app code, so never bundled.

/**
 * The item at `index`. Throws when there is none, so a test that expects the item fails at the
 * lookup with the index and the length, not later on an `undefined`.
 */
export const itemAt = <T>(items: ArrayLike<T>, index: number): T => {
  const item = items[index];
  if (item === undefined) {
    throw new Error(`Expected an item at index ${index}, but there are ${items.length} items.`);
  }
  return item;
};

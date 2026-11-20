import { ProductCategory } from '../../core/api/products.api';

const CATEGORY_LABELS: Readonly<Record<ProductCategory, string>> = {
  apparel: 'Apparel',
  footwear: 'Footwear',
  bags: 'Bags',
  home: 'Home',
  outdoor: 'Outdoor',
  accessories: 'Accessories',
};

export const categoryLabel = (category: ProductCategory): string =>
  CATEGORY_LABELS[category] ?? category;

/**
 * Turns an attribute key into a label: `fillPower` -> "Fill power", `sizeEU` -> "Size EU",
 * `color` -> "Color". Keys are lower camelCase (brief, page 5); snake and kebab case also work.
 */
export const humaniseAttributeKey = (key: string): string => {
  const words = key
    .replaceAll(/[_-]+/gu, ' ')
    .replaceAll(/(?<=[a-z\d])(?=[A-Z])/gu, ' ')
    .replaceAll(/(?<=[A-Z])(?=[A-Z][a-z])/gu, ' ')
    .trim()
    .split(/\s+/u)
    .filter(Boolean)
    // Keep acronyms (EU, UV) as they are; lower-case every other word.
    .map((word) => (word.length > 1 && word === word.toUpperCase() ? word : word.toLowerCase()));
  if (words.length === 0) {
    return key;
  }
  const sentence = words.join(' ');
  return sentence.charAt(0).toUpperCase() + sentence.slice(1);
};

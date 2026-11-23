import { formatCurrency } from '@angular/common';

/**
 * An amount the API priced, written as templates show it with `currency: 'USD'` ("$1,234.50"), for
 * text built in TypeScript (announcements, notices, filter chips). Display only: the amount is never
 * rounded, added up or converted here.
 */
export const formatUsd = (amount: number): string =>
  formatCurrency(amount, 'en-US', '$', 'USD', '1.2-2');

const NAME_LIST = /* @__PURE__ */ new Intl.ListFormat('en-US', { type: 'conjunction' });

/**
"A", "A and B", "A, B, and C": seller and product names in a sentence.
*/
export const joinNames = (names: readonly string[]): string => NAME_LIST.format(names);

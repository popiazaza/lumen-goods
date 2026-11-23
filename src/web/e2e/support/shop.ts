import { expect } from '@playwright/test';
import type { Locator, Page } from '@playwright/test';

/**
 * The three sellers as the storefront names them (`GET /api/v1/sellers`).
 */
export const SHOPS = {
  lumen: 'Lumen Goods',
  harbor: 'Harbor Supply Co.',
  kestrel: 'Kestrel Outfitters',
} as const;

export type Shop = keyof typeof SHOPS;

/**
 * Test cards from the README.
 */
export const CARDS = {
  approved: '4111 1111 1111 1111',
  stolen: '4242 0000 0000 0026',
  oneTimeCode: '4242 0000 0000 0034',
} as const;

/**
 * Order numbers look like `LG-7K3M9QX2`.
 */
const ORDER_NUMBER = /LG-[A-Z0-9]+/u;
const PRODUCT_URL = /\/products\/(?<id>[^/?#]+)$/u;
const EXPIRY_YEARS_AHEAD = 2;

const removeAll = async (removeButtons: Locator, count: number): Promise<void> => {
  if (count === 0) {
    return;
  }
  // Each removal re-renders the list, so always the first of what is left.
  // eslint-disable-next-line playwright/no-nth-methods -- the lines are removed one by one
  await removeButtons.first().click();
  await expect(removeButtons).toHaveCount(count - 1);
  await removeAll(removeButtons, count - 1);
};

/**
 * Removes every line from the signed-in customer's cart. Paying an order empties it; this covers a test that stopped before paying.
 */
export const emptyCart = async (page: Page): Promise<void> => {
  await page.goto('/cart');
  const empty = page.getByRole('heading', { name: 'Your cart is empty' });
  const removeButtons = page.getByRole('button', { name: /^Remove /u });
  // eslint-disable-next-line playwright/no-nth-methods -- any line shows the cart has loaded
  await expect(empty.or(removeButtons.first())).toBeVisible();
  await removeAll(removeButtons, await removeButtons.count());
  await expect(empty).toBeVisible();
};

/**
 * Opens the cheapest (or dearest) product of a shop in the catalogue that says "In stock", and adds one to the cart, all from the UI. Returns the product's name and id.
 */
export const addToCart = async (
  page: Page,
  shop: Shop,
  sort: 'price_asc' | 'price_desc' = 'price_asc'
): Promise<{ name: string; id: string }> => {
  await page.goto(`/?seller=${shop}&sort=${sort}`);
  // "In stock" rather than "Only 3 left": every run buys a few, and a partner's stock is checked live at Place order, so a nearly sold-out item would end a later run early.
  const wellStocked = page
    .getByRole('main')
    .getByRole('listitem')
    .filter({ has: page.getByRole('heading', { level: 3 }) })
    .filter({ hasText: /In stock/u });
  // eslint-disable-next-line playwright/no-nth-methods -- the first in price order
  await wellStocked.first().getByRole('link').click();
  await expect(page).toHaveURL(PRODUCT_URL);
  await expect(page.getByText(`Sold by ${SHOPS[shop]}`)).toBeVisible();
  const heading = await page.getByRole('heading', { level: 1 }).textContent();
  const name = heading?.trim() ?? '';

  await page.getByRole('button', { name: 'Add to cart' }).click();
  await expect(page.getByRole('link', { name: 'View cart' })).toBeVisible();
  return { name, id: PRODUCT_URL.exec(page.url())?.groups?.['id'] ?? '' };
};

/**
 * From the cart through the address and shipping steps to the review.
 */
export const checkoutToReview = async (
  page: Page,
  { express = false }: { express?: boolean } = {}
): Promise<void> => {
  await page.goto('/cart');
  await page.getByRole('button', { name: 'Check out' }).click();

  await expect(page.getByRole('heading', { level: 1, name: 'Shipping address' })).toBeVisible();
  await page.getByLabel('Full name').fill('Ada Lovelace');
  await page.getByLabel('Address line 1').fill('1200 NW Naito Pkwy');
  await page.getByLabel('City').fill('Portland');
  await page.getByLabel('State').selectOption('OR');
  await page.getByLabel('ZIP code').fill('97209');
  await page.getByRole('button', { name: 'Continue to shipping' }).click();

  await expect(page.getByRole('heading', { level: 1, name: 'Shipping method' })).toBeVisible();
  if (express) {
    await page.getByRole('radio', { name: /Express/u }).check();
  }
  await page.getByRole('button', { name: 'Continue to review' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Review your order' })).toBeVisible();
};

/**
 * The order number on the payment page, once it has loaded.
 */
export const orderNumberOnPaymentPage = async (page: Page): Promise<string> => {
  await expect(page.getByRole('heading', { level: 1, name: 'Payment' })).toBeVisible();
  // Not anchored: text matching sees the paragraph's raw whitespace.
  const orderLine = page.getByText(/Order LG-[A-Z0-9]+/u);
  await expect(orderLine).toBeVisible();
  return ORDER_NUMBER.exec((await orderLine.textContent()) ?? '')?.[0] ?? '';
};

/**
 * Place order on the review step; returns the new order's number.
 */
export const placeOrder = async (page: Page): Promise<string> => {
  await page.getByRole('button', { name: 'Place order' }).click();
  return await orderNumberOnPaymentPage(page);
};

/**
 * Fills the card form (any name, a future expiry, any CVC) and pays.
 */
export const payByCard = async (page: Page, number: string): Promise<void> => {
  const today = new Date();
  const year = (today.getFullYear() + EXPIRY_YEARS_AHEAD) % 100;
  await page.getByLabel('Card number').fill(number);
  await page.getByLabel('Name on card').fill('Ada Lovelace');
  await page.getByLabel('Expiry date (MM/YY)').fill(`12/${year}`);
  await page.getByLabel('Security code (CVC)').fill('123');
  await page.getByRole('button', { name: /^Pay \$/u }).click();
};

export const payFromWallet = async (page: Page): Promise<void> => {
  await page.getByRole('radio', { name: 'Lumen Goods wallet' }).check();
  await page.getByRole('button', { name: /^Pay \$/u }).click();
};

/**
 * The confirmation page of a paid order.
 */
export const expectConfirmed = async (page: Page, orderNumber: string): Promise<void> => {
  await expect(
    page.getByRole('heading', { level: 1, name: `Order ${orderNumber} confirmed` })
  ).toBeVisible();
  await expect(page.getByText(`Your order number is ${orderNumber}.`)).toBeVisible();
};

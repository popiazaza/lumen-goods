import { ADMIN_STATE, CUSTOMER_STATE } from './support/accounts';
import {
  SHOPS,
  addToCart,
  checkoutToReview,
  emptyCart,
  expectConfirmed,
  payFromWallet,
  placeOrder,
} from './support/shop';
import { expect, test } from './support/test';

// The partners page polls a requested refresh for up to a minute.
const REFRESH_TIMEOUT = 30_000;

test.describe('admin', () => {
  test.use({ storageState: ADMIN_STATE });

  test(
    'the admin sections lead to products, an order with its payment events, and partner status',
    { tag: ['@BR-ADM-01'] },
    async ({ page, openAs }) => {
      // The run's customer pays one order first, so there is one to open on any stack, even a fresh one.
      const orderNumber = await test.step('the customer pays an order', async () => {
        const shopper = await openAs(CUSTOMER_STATE);
        await emptyCart(shopper);
        await addToCart(shopper, 'lumen');
        await checkoutToReview(shopper);
        const placed = await placeOrder(shopper);
        await payFromWallet(shopper);
        await expectConfirmed(shopper, placed);
        return placed;
      });

      const sections = page.getByRole('navigation', { name: 'Admin sections' });

      await page.goto('/admin');
      await expect(page.getByRole('heading', { level: 1, name: 'Products' })).toBeVisible();

      await sections.getByRole('link', { name: 'Orders' }).click();
      await expect(page.getByRole('heading', { level: 1, name: 'Orders' })).toBeVisible();
      await page
        .getByRole('region', { name: 'Orders' })
        .getByRole('link', { name: orderNumber })
        .click();
      await expect(
        page.getByRole('heading', { level: 1, name: `Order ${orderNumber}` })
      ).toBeVisible();
      await expect(page.getByRole('heading', { level: 2, name: 'Payment events' })).toBeVisible();
      await expect(page.getByRole('listitem').filter({ hasText: 'Approved' })).toBeVisible();

      await sections.getByRole('link', { name: 'Partners' }).click();
      await expect(page.getByRole('heading', { level: 1, name: 'Partner status' })).toBeVisible();
      await expect(page.getByRole('heading', { level: 2, name: SHOPS.harbor })).toBeVisible();
      await expect(page.getByRole('heading', { level: 2, name: SHOPS.kestrel })).toBeVisible();
      // One Refresh now per partner, both named only "Refresh now". A refresh is harmless: sync runs on its own anyway.
      const refresh = page.getByRole('button', { name: 'Refresh now' });
      await expect(refresh).toHaveCount(2);
      // eslint-disable-next-line playwright/no-nth-methods -- the two buttons share one name; the first card is Harbor's
      await refresh.first().click();
      await expect(
        page.getByRole('main').getByText(/^Harbor Supply Co\. refreshed: \d+ live products\.$/u)
      ).toBeVisible({ timeout: REFRESH_TIMEOUT });
    }
  );
});

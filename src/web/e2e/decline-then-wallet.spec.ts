import { ADMIN_STATE, CUSTOMER_STATE } from './support/accounts';
import {
  CARDS,
  addToCart,
  checkoutToReview,
  emptyCart,
  expectConfirmed,
  payByCard,
  payFromWallet,
  placeOrder,
} from './support/shop';
import { expect, test } from './support/test';

test.describe('declined card', () => {
  test.use({ storageState: CUSTOMER_STATE });

  test.beforeEach(async ({ page }) => {
    await emptyCart(page);
  });

  test(
    'a stolen card shows only a generic decline, and the same order is then paid from the wallet',
    { tag: ['@BR-PAY-07', '@BR-PAY-06'] },
    async ({ page, openAs }) => {
      await addToCart(page, 'lumen');
      await checkoutToReview(page);
      const orderNumber = await placeOrder(page);

      await payByCard(page, CARDS.stolen);
      const outcome = page.getByRole('alert').filter({ hasText: 'Your card was declined' });
      await expect(outcome).toBeVisible();
      await expect(outcome).not.toContainText(/stolen/iu);

      // Only the admin sees the real reason, in the order's payment events.
      const adminPage = await openAs(ADMIN_STATE);
      await adminPage.goto(`/admin/orders/${orderNumber}`);
      await expect(adminPage.getByText('Card reported stolen')).toBeVisible();

      await payFromWallet(page);
      await expectConfirmed(page, orderNumber);
      await expect(page.getByText('Paid from your Lumen Goods wallet')).toBeVisible();
    }
  );
});

import { CUSTOMER_STATE } from './support/accounts';
import {
  CARDS,
  SHOPS,
  addToCart,
  checkoutToReview,
  emptyCart,
  expectConfirmed,
  payByCard,
  placeOrder,
} from './support/shop';
import { expect, test } from './support/test';

test.describe('checkout', () => {
  test.use({ storageState: CUSTOMER_STATE });

  test.beforeEach(async ({ page }) => {
    await emptyCart(page);
  });

  test(
    'one cart from three shops is paid once and confirmed with a shipment per shop',
    { tag: ['@BR-CART-01', '@BR-SHIP-01', '@BR-PAY-01', '@BR-PAY-02', '@phone'] },
    async ({ page }) => {
      const shipment = (shop: string) => page.getByRole('region', { name: `Sold by ${shop}` });
      const lumen = await addToCart(page, 'lumen');
      const harbor = await addToCart(page, 'harbor');
      const kestrel = await addToCart(page, 'kestrel');

      await page.goto('/cart');
      await expect(page.getByRole('region', { name: /^Sold by /u })).toHaveCount(3);

      await checkoutToReview(page, { express: true });
      await expect(page.getByRole('region', { name: /^Sold by /u })).toHaveCount(3);
      await expect(shipment(SHOPS.lumen)).toContainText('Express shipping');
      await expect(shipment(SHOPS.harbor)).toContainText('Standard shipping');
      await expect(shipment(SHOPS.kestrel)).toContainText('Standard shipping');

      const orderNumber = await placeOrder(page);
      await payByCard(page, CARDS.approved);

      await expectConfirmed(page, orderNumber);
      await expect(page.getByText('Paid by Visa ending 1111')).toBeVisible();
      await expect(shipment(SHOPS.lumen)).toContainText(`1 × ${lumen.name}`);
      await expect(shipment(SHOPS.harbor)).toContainText(`1 × ${harbor.name}`);
      await expect(shipment(SHOPS.kestrel)).toContainText(`1 × ${kestrel.name}`);
    }
  );
});

import { CUSTOMER_STATE } from './support/accounts';
import {
  CARDS,
  SHOPS,
  addToCart,
  checkoutToReview,
  emptyCart,
  expectConfirmed,
  orderNumberOnPaymentPage,
  payByCard,
} from './support/shop';
import { expect, test } from './support/test';

const usd = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' });
const CENTS = 100;
const REVIEWED_UNIT_PRICE = /1 × \$(?<amount>[\d,]+\.\d{2})/u;

test.describe('price changed at Place order', () => {
  // Changing a Harbor price needs Docker access to the stack (`docker compose exec`); CI sets E2E_COMPOSE=1.
  test.skip(
    process.env['E2E_COMPOSE'] !== '1',
    'Changes a Harbor price with docker compose exec: set E2E_COMPOSE=1 to run it against the compose stack.'
  );
  test.use({ storageState: CUSTOMER_STATE });

  test.beforeEach(async ({ page }) => {
    await emptyCart(page);
  });

  test(
    'a Harbor price change after the review stops Place order until the new total is accepted',
    { tag: ['@BR-CHK-01'] },
    async ({ page, setHarborPrice }) => {
      // The dearest Harbor item: the other specs buy the cheapest.
      const product = await addToCart(page, 'harbor', 'price_desc');
      const response = await page.request.get(`/api/v1/products/${product.id}`);
      const { sourceProductId: itemCode } = (await response.json()) as { sourceProductId: string };

      await checkoutToReview(page);
      const line =
        (await page.getByRole('region', { name: `Sold by ${SHOPS.harbor}` }).textContent()) ?? '';
      const reviewed = REVIEWED_UNIT_PRICE.exec(line)?.groups?.['amount'] ?? '';
      const reviewedCents = Math.round(Number(reviewed.replaceAll(',', '')) * CENTS);
      expect(reviewedCents).toBeGreaterThan(0);

      // A dollar above the reviewed price, whatever Lumen last synced from Harbor.
      const changedCents = reviewedCents + CENTS;
      setHarborPrice(itemCode, changedCents);
      await page.getByRole('button', { name: 'Place order' }).click();

      const changed = page.getByRole('alert').filter({ hasText: 'Your order changed' });
      await expect(changed).toContainText(product.name);
      await expect(changed.getByRole('deletion')).toHaveText(
        `was ${usd.format(reviewedCents / CENTS)}`
      );
      await expect(changed).toContainText(`to ${usd.format(changedCents / CENTS)}`);

      await changed.getByRole('button', { name: 'Accept new total and place order' }).click();
      const orderNumber = await orderNumberOnPaymentPage(page);
      await payByCard(page, CARDS.approved);
      await expectConfirmed(page, orderNumber);
    }
  );
});

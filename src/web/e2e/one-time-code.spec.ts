import { CUSTOMER_STATE } from './support/accounts';
import {
  CARDS,
  addToCart,
  checkoutToReview,
  emptyCart,
  expectConfirmed,
  payByCard,
  placeOrder,
} from './support/shop';
import { expect, test } from './support/test';

test.describe('one-time code', () => {
  test.use({ storageState: CUSTOMER_STATE });

  test.beforeEach(async ({ page }) => {
    await emptyCart(page);
  });

  test(
    'card 0034 asks for a one-time code: a wrong code is refused, then 123456 approves',
    { tag: ['@BR-PAY-03'] },
    async ({ page }) => {
      const securityCheck = page.getByRole('heading', { name: 'Extra security check' });
      const code = page.getByLabel('Verification code');
      await addToCart(page, 'lumen');
      await checkoutToReview(page);
      const orderNumber = await placeOrder(page);

      await payByCard(page, CARDS.oneTimeCode);
      await expect(securityCheck).toBeVisible();
      await expect(page.getByText('Test code: 123456')).toBeVisible();
      await code.fill('654321');
      await page.getByRole('button', { name: 'Verify' }).click();

      // The wrong code declines this attempt; Try again keeps the card details except the security
      // code, which is cleared after every decline, and the next Pay sends a new code.
      const outcome = page.getByRole('alert').filter({ hasText: 'The code is not correct' });
      await expect(outcome).toBeVisible();
      await outcome.getByRole('button', { name: 'Try again' }).click();
      const cvc = page.getByLabel('Security code (CVC)');
      await expect(cvc).toHaveValue('');
      await cvc.fill('123');
      await page.getByRole('button', { name: /^Pay \$/u }).click();
      await expect(securityCheck).toBeVisible();
      await code.fill('123456');
      await page.getByRole('button', { name: 'Verify' }).click();

      await expectConfirmed(page, orderNumber);
    }
  );
});

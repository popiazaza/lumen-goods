import { randomUUID } from 'node:crypto';
import { expect, test as setup } from '@playwright/test';
import { ADMIN_STATE, CUSTOMER_STATE, SEEDED_ADMIN } from './support/accounts';
import { SHOPS } from './support/shop';

const CATALOGUE_READY_TIMEOUT = 90_000;

// The API allows 5 sign-ins a minute and 5 registrations per 10 minutes per IP, so each run signs each account in once here and the specs reuse the saved states. The seeded customer is never used: a reviewer may be shopping with it.
setup.describe('setup', () => {
  setup('the catalogue sells products from all three shops', async ({ request }) => {
    // Partner sync runs at start-up; a Kestrel item is in stock only once its stock file is read.
    const inStockCount = async (shop: string): Promise<number> => {
      const response = await request.get(`/api/v1/products?seller=${shop}&inStock=true&pageSize=1`);
      const body = (await response.json()) as { totalCount?: number };
      return body.totalCount ?? 0;
    };
    await Promise.all(
      Object.keys(SHOPS).map(async (shop) => {
        await expect
          .poll(async () => await inStockCount(shop), { timeout: CATALOGUE_READY_TIMEOUT })
          .toBeGreaterThan(0);
      })
    );
  });

  setup('register a fresh customer', async ({ page }) => {
    // A new account starts with a $500.00 wallet (BR-PAY-06). Identity wants upper and lower case, a digit and a symbol.
    const id = randomUUID();
    await page.goto('/register');
    await page.getByLabel('Email address').fill(`e2e-${id}@lumengoods.test`);
    await page.getByLabel('Password', { exact: true }).fill(`E2e-${id}`);
    await page.getByRole('button', { name: 'Create account' }).click();
    await expect(page.getByRole('button', { name: 'Account', exact: true })).toBeVisible();
    await page.context().storageState({ path: CUSTOMER_STATE });
  });

  setup('sign in the seeded admin', async ({ page }) => {
    await page.goto('/sign-in');
    await page.getByLabel('Email address').fill(SEEDED_ADMIN.email);
    await page.getByLabel('Password', { exact: true }).fill(SEEDED_ADMIN.password);
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByRole('button', { name: 'Account', exact: true })).toBeVisible();
    await page.context().storageState({ path: ADMIN_STATE });
  });
});

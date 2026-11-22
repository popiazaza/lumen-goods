import { SHOPS } from './support/shop';
import { expect, test } from './support/test';
import type { Locator, Page } from '@playwright/test';

/**
 * Runs `change` on the filters: the sidebar on a desktop, a bottom sheet behind "Filters" on a phone (closed again with its "Show N products" button).
 */
const inFilters = async (
  page: Page,
  isMobile: boolean,
  change: (panel: Locator) => Promise<void>
): Promise<void> => {
  if (!isMobile) {
    await change(page.getByRole('complementary', { name: 'Filters' }));
    return;
  }
  await page.getByRole('button', { name: /^Filters/u }).click();
  const sheet = page.getByRole('dialog', { name: 'Filters' });
  await change(sheet);
  await sheet.getByRole('button', { name: /^Show \d+ products$/u }).click();
  await expect(sheet).toBeHidden();
};

test.describe('catalogue', () => {
  test(
    'search, seller filters, sort and page are kept in the URL and survive a reload',
    { tag: ['@BR-CAT-03', '@phone'] },
    async ({ page, isMobile }) => {
      const main = page.getByRole('main');
      const productNames = main.getByRole('heading', { level: 3 });
      const partnerShopNames = main.getByText(
        /^Sold by (?:Harbor Supply Co\.|Kestrel Outfitters)$/u
      );
      const linkToPage2 = page
        .getByRole('navigation', { name: 'Pages' })
        .getByRole('link', { name: 'Page 2' });
      const sort = page.getByRole('combobox', { name: 'Sort by' });
      const search = page.getByRole('searchbox', { name: 'Search products' });
      const resultCount = page.getByRole('heading', { level: 2, name: /^\d+ products$/u });

      await page.goto('/');
      await expect(resultCount).toBeVisible();
      // "with" matches about 40 partner products, so the filtered results have a second page.
      await search.fill('with');
      await search.press('Enter');
      await expect(page).toHaveURL('/?q=with');

      await inFilters(page, isMobile, async (panel) => {
        await panel.getByRole('checkbox', { name: SHOPS.harbor }).check();
        await expect(page).toHaveURL('/?q=with&seller=harbor');
        await panel.getByRole('checkbox', { name: SHOPS.kestrel }).check();
        await expect(page).toHaveURL('/?q=with&seller=harbor&seller=kestrel');
      });

      await sort.click();
      await page.getByRole('option', { name: 'Price: low to high' }).click();
      await expect(page).toHaveURL('/?q=with&seller=harbor&seller=kestrel&sort=price_asc');

      await linkToPage2.click();
      await expect(page).toHaveURL('/?q=with&seller=harbor&seller=kestrel&sort=price_asc&page=2');
      await expect(main.getByText(/^Showing 25–/u)).toBeVisible();
      const names = await productNames.allTextContents();
      // Only partner products are left, and every card names its shop.
      await expect(partnerShopNames).toHaveCount(names.length);

      await page.reload();
      await expect(productNames).toHaveText(names);
      await expect(partnerShopNames).toHaveCount(names.length);
      await expect(sort).toHaveText(/Price: low to high/u);
      await expect(linkToPage2).toHaveAttribute('aria-current', 'page');
    }
  );
});

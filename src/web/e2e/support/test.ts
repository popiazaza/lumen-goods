import path from 'node:path';
import { test as base } from '@playwright/test';
import { harborPriceCents, setHarborPriceCents } from './harbor';
import type { BrowserContext, Page } from '@playwright/test';

export { expect } from '@playwright/test';

/**
 * Product images are not under test. On a fresh stack Lumen fetches partner images through its image proxy within Harbor's budget of 10 a minute, and a page of slow images holds all six HTTP/1.1 connections the browser allows per host, so the next API call waits behind them.
 */
const skipProductImages = async (context: BrowserContext): Promise<void> => {
  await context.route(
    (url) => url.pathname.startsWith('/media/'),
    async (route) => {
      await route.abort();
    }
  );
};

interface Fixtures {
  /**
   * Opens a page in a new browser context signed in with a saved state, for a second person in the same test (closed afterwards).
   */
  openAs: (storageState: string) => Promise<Page>;
  /**
   * Sets an item's price at Harbor in whole cents; every changed price is put back after the test, pass or fail.
   */
  setHarborPrice: (itemCode: string, cents: number) => void;
}

export const test = base.extend<Fixtures>({
  context: async ({ context }, use) => {
    await skipProductImages(context);
    await use(context);
  },
  openAs: async ({ browser }, use) => {
    const contexts: BrowserContext[] = [];
    await use(async (storageState) => {
      const context = await browser.newContext({ storageState });
      contexts.push(context);
      await skipProductImages(context);
      return await context.newPage();
    });
    await Promise.all(
      contexts.map(async (context) => {
        await context.close();
      })
    );
  },
  setHarborPrice: async ({}, use, testInfo) => {
    const repoRoot = path.resolve(testInfo.project.testDir, '..', '..', '..');
    const originals = new Map<string, number>();
    await use((itemCode, cents) => {
      if (!originals.has(itemCode)) {
        originals.set(itemCode, harborPriceCents(repoRoot, itemCode));
      }
      setHarborPriceCents(repoRoot, itemCode, cents);
    });
    for (const [itemCode, cents] of originals) {
      setHarborPriceCents(repoRoot, itemCode, cents);
    }
  },
});

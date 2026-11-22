/**
 * Signed-in browser states the setup project writes once per run (`auth.setup.ts`). Paths are relative to `src/web`, where `npx playwright test` runs.
 */
export const CUSTOMER_STATE = 'e2e/.auth/customer.json';
export const ADMIN_STATE = 'e2e/.auth/admin.json';

/**
 * The admin the stack seeds (README, "Seeded accounts"). The tests only look, and ask for one partner refresh.
 */
export const SEEDED_ADMIN = { email: 'admin@lumengoods.test', password: 'Admin!2026' } as const;

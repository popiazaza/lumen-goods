import { Routes } from '@angular/router';

/**
 * The admin pages, under `/admin`. `app.routes.ts` matches them for admins only (`adminGuard`);
 * the pages still show "Sign in as an admin" when the API answers 401 or 403, as a session can
 * end or lose the role while a page is open.
 */
export const adminRoutes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'products' },
  {
    path: 'products',
    title: 'Products — Admin',
    loadComponent: () => import('./admin-products-page').then((m) => m.AdminProductsPage),
  },
  {
    path: 'products/new',
    title: 'New product — Admin',
    loadComponent: () => import('./admin-product-edit-page').then((m) => m.AdminProductEditPage),
  },
  {
    path: 'products/:id',
    title: 'Edit product — Admin',
    loadComponent: () => import('./admin-product-edit-page').then((m) => m.AdminProductEditPage),
  },
  {
    path: 'orders',
    title: 'Orders — Admin',
    loadComponent: () => import('./admin-orders-page').then((m) => m.AdminOrdersPage),
  },
  {
    path: 'orders/:id',
    title: 'Order details — Admin',
    loadComponent: () => import('./admin-order-detail-page').then((m) => m.AdminOrderDetailPage),
  },
  {
    path: 'partners',
    title: 'Partner status — Admin',
    loadComponent: () => import('./admin-partners-page').then((m) => m.AdminPartnersPage),
  },
];

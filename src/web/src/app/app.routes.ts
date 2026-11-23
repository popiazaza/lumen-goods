import { Routes } from '@angular/router';
import { adminGuard } from './core/routing/admin.guard';
import { adminRoutes } from './features/admin/admin.routes';
import { checkoutRoutes } from './features/checkout/checkout.routes';
import { ordersRoutes } from './features/orders/orders.routes';

// Every page is lazy (`loadComponent`); the child route tables are plain arrays so guards can be
// added on the parents (admin role for /admin here; checkout.routes.ts adds signed-in for checkout).
// The order pages have no guard: they read the API and show their own sign-in prompt on a 401, which
// also covers a session that ends while an order page is open.
export const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    title: 'Shop',
    loadComponent: () => import('./features/catalogue/catalogue-page').then((m) => m.CataloguePage),
  },
  {
    path: 'products/:id',
    title: 'Product details',
    loadComponent: () =>
      import('./features/products/product-detail-page').then((m) => m.ProductDetailPage),
  },
  {
    path: 'cart',
    title: 'Your cart',
    loadComponent: () => import('./features/cart/cart-page').then((m) => m.CartPage),
  },
  {
    path: 'sign-in',
    title: 'Sign in',
    loadComponent: () => import('./features/account/sign-in-page').then((m) => m.SignInPage),
  },
  {
    path: 'register',
    title: 'Create an account',
    loadComponent: () => import('./features/account/register-page').then((m) => m.RegisterPage),
  },
  { path: 'checkout', children: checkoutRoutes },
  { path: 'orders', children: ordersRoutes },
  { path: 'admin', canMatch: [adminGuard], children: adminRoutes },
  {
    path: '**',
    title: 'Page not found',
    loadComponent: () => import('./features/not-found/not-found-page').then((m) => m.NotFoundPage),
  },
];

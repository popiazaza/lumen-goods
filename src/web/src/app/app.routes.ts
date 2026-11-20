import { Routes } from '@angular/router';
import { adminRoutes } from './features/admin/admin.routes';
import { checkoutRoutes } from './features/checkout/checkout.routes';
import { ordersRoutes } from './features/orders/orders.routes';

// Every page is lazy (`loadComponent`); the child route tables are plain arrays so guards
// (signed-in for checkout and orders, admin role for /admin) can be added on the parents.
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
  { path: 'admin', children: adminRoutes },
  {
    path: '**',
    title: 'Page not found',
    loadComponent: () => import('./features/not-found/not-found-page').then((m) => m.NotFoundPage),
  },
];

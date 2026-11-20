import { Routes } from '@angular/router';

export const ordersRoutes: Routes = [
  {
    path: '',
    title: 'Your orders',
    loadComponent: () => import('./order-list-page').then((m) => m.OrderListPage),
  },
  {
    path: ':id',
    title: 'Order details',
    loadComponent: () => import('./order-detail-page').then((m) => m.OrderDetailPage),
  },
  {
    path: ':id/confirmation',
    title: 'Order confirmed',
    loadComponent: () => import('./order-confirmation-page').then((m) => m.OrderConfirmationPage),
  },
];

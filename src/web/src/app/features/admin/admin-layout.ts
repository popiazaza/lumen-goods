import { Component } from '@angular/core';
import { MatTabLink, MatTabNav, MatTabNavPanel } from '@angular/material/tabs';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

/**
One admin section: a tab that stays active on its child pages (`/admin/orders/LG-1` is Orders).
*/
interface AdminSection {
  path: string;
  label: string;
}

export const ADMIN_SECTIONS: readonly AdminSection[] = [
  { path: '/admin/products', label: 'Products' },
  { path: '/admin/orders', label: 'Orders' },
  { path: '/admin/partners', label: 'Partner status' },
];

/**
 * Frame of every admin page: tabs to move between the admin sections, then the routed page. The
 * account menu links to `/admin` only, so without these Orders and Partner status were reachable
 * by typing their URL. Material's routed tab bar needs its panel, which makes the links ARIA tabs
 * (`aria-selected`) rather than plain links with `aria-current`.
 */
@Component({
  selector: 'app-admin-layout',
  imports: [MatTabNav, MatTabLink, MatTabNavPanel, RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './admin-layout.html',
  styleUrl: './admin-layout.scss',
})
export class AdminLayout {
  protected readonly sections = ADMIN_SECTIONS;
}

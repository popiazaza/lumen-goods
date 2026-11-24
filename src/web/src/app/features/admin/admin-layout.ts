import { Component } from '@angular/core';
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
  // Short, so the three tabs fit a 390 px phone without paging arrows.
  { path: '/admin/partners', label: 'Partners' },
];

/**
 * Frame of every admin page: tabs to move between the admin sections, then the routed page. The
 * account menu links to `/admin` only, so without these Orders and Partner status were reachable
 * by typing their URL. The sections are links with `aria-current="page"` on the current one, not
 * ARIA tabs: each loads its own page, so the browser's link behaviour (open in a new tab, Back) holds.
 */
@Component({
  selector: 'app-admin-layout',
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './admin-layout.html',
  styleUrl: './admin-layout.scss',
})
export class AdminLayout {
  protected readonly sections = ADMIN_SECTIONS;
}

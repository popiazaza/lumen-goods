import { Component, input } from '@angular/core';
import { MatButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { RouterLink } from '@angular/router';

/** What an admin page shows for 401 (signed out) and 403 (signed in without the admin role). */
@Component({
  selector: 'app-admin-access-denied',
  imports: [MatButton, MatIcon, RouterLink],
  template: `
    <mat-icon svgIcon="lock" class="icon" />
    <p><strong>Sign in as an admin to see this page.</strong></p>
    @if (status() === 403) {
      <p>You are signed in with an account that does not have the admin role.</p>
    }
    <a matButton="filled" routerLink="/sign-in" [queryParams]="{ returnUrl: returnUrl() }"
      >Sign in</a
    >
  `,
  styles: `
    :host {
      display: grid;
      justify-items: start;
      gap: var(--lg-space-3);
    }

    p {
      margin: 0;
    }

    .icon {
      width: 40px;
      height: 40px;
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class AdminAccessDenied {
  readonly status = input.required<number>();
  /** Where sign-in should come back to. */
  readonly returnUrl = input<string | null>(null);
}

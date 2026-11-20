import { Component, input } from '@angular/core';
import { MatButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { RouterLink } from '@angular/router';

/**
What an admin page shows for 401 (signed out) and 403 (signed in without the admin role).
*/
@Component({
  selector: 'app-admin-access-denied',
  imports: [MatButton, MatIcon, RouterLink],
  templateUrl: './admin-access-denied.html',
  styleUrl: './admin-access-denied.scss',
})
export class AdminAccessDenied {
  readonly status = input.required<number>();
  /**
  Where sign-in should come back to.
  */
  readonly returnUrl = input<string | null>(null);
}

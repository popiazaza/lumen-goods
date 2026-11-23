import { Component } from '@angular/core';
import { MatButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-not-found-page',
  imports: [RouterLink, MatButton, MatIcon],
  template: `
    <div class="state">
      <mat-icon svgIcon="link_off" aria-hidden="true" />
      <h1>Page not found</h1>
      <p>The page you were looking for does not exist or has moved.</p>
      <a matButton="filled" routerLink="/">Go to the shop</a>
    </div>
  `,
  styleUrl: './not-found-page.scss',
})
export class NotFoundPage {}

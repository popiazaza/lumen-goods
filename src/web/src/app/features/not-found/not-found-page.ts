import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-not-found-page',
  imports: [RouterLink],
  template: `
    <h1>Page not found</h1>
    <p>The page you were looking for does not exist or has moved.</p>
    <p><a routerLink="/">Go to the shop</a></p>
  `,
})
export class NotFoundPage {}

import { Component, input } from '@angular/core';

@Component({
  selector: 'app-order-confirmation-page',
  template: `<h1>Order {{ id() }} confirmed</h1>`,
})
export class OrderConfirmationPage {
  /** Bound from the `:id` route parameter. */
  readonly id = input.required<string>();
}

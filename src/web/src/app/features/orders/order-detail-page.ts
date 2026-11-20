import { Component, input } from '@angular/core';

@Component({
  selector: 'app-order-detail-page',
  template: `<h1>Order {{ id() }}</h1>`,
})
export class OrderDetailPage {
  /** Bound from the `:id` route parameter. */
  readonly id = input.required<string>();
}

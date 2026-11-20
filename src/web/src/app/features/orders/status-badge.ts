import { Component, input } from '@angular/core';
import { MatIcon } from '@angular/material/icon';
import { StatusView } from './order-labels';

/**
A status as icon, text and tone together: never colour alone (WCAG 1.4.1).
*/
@Component({
  selector: 'app-status-badge',
  imports: [MatIcon],
  template: `<mat-icon [svgIcon]="status().icon" /><span class="text">{{ status().text }}</span>`,
  styleUrl: './status-badge.scss',
  host: {
    class: 'status-badge',
    '[class]': "'tone-' + status().tone",
  },
})
export class StatusBadge {
  readonly status = input.required<StatusView>();
}

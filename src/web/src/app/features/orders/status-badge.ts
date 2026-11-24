import { Component, input } from '@angular/core';
import { NgIcon } from '@ng-icons/core';
import { provideOrderIcons } from './order-icons';
import { StatusView } from './order-labels';

/**
A status as icon, text and tone together: never colour alone (WCAG 1.4.1).
*/
@Component({
  selector: 'app-status-badge',
  imports: [NgIcon],
  template: `<ng-icon [name]="status().icon" /><span class="text">{{ status().text }}</span>`,
  styleUrl: './status-badge.scss',
  providers: [provideOrderIcons()],
  host: {
    class: 'status-badge',
    '[class]': "'tone-' + status().tone",
    '[attr.data-icon]': 'status().icon',
  },
})
export class StatusBadge {
  readonly status = input.required<StatusView>();
}

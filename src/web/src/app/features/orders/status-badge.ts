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
  styles: `
    :host {
      display: inline-flex;
      align-items: center;
      gap: var(--lg-space-1);
      padding: 2px var(--lg-space-2) 2px var(--lg-space-1);
      border: 1px solid currentColor;
      border-radius: 999px;
      background: var(--mat-sys-surface);
      font: var(--mat-sys-label-large);
      line-height: 1.25;
      color: var(--mat-sys-on-surface-variant);
      /* never stretched by a column flex or grid parent: a badge is as wide as its text */
      width: fit-content;
      max-width: 100%;
    }

    mat-icon {
      flex: none;
      width: 18px;
      height: 18px;
    }

    .text {
      overflow-wrap: anywhere;
    }

    :host(.tone-success) {
      color: var(--lg-status-success);
    }

    :host(.tone-info) {
      color: var(--mat-sys-primary);
    }

    :host(.tone-warning) {
      color: var(--lg-status-warning);
    }

    :host(.tone-error) {
      color: var(--mat-sys-error);
    }
  `,
  host: {
    class: 'status-badge',
    '[class]': "'tone-' + status().tone",
  },
})
export class StatusBadge {
  readonly status = input.required<StatusView>();
}

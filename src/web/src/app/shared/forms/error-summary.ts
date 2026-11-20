import { Component, ElementRef, inject, input } from '@angular/core';
import { MatIcon } from '@angular/material/icon';

/**
One problem in a form, pointing at the field that has it.
*/
export interface FormProblem {
  /**
  The `id` of the control to focus.
  */
  fieldId: string;
  message: string;
}

/**
 * GOV.UK-style error summary shown above a form after a failed submit (WCAG 3.3.1): a heading,
 * then one link per problem that moves focus to its field. The page calls `focus()` after
 * submit so screen readers read it at once.
 */
@Component({
  selector: 'app-error-summary',
  imports: [MatIcon],
  template: `
    <div class="summary" role="alert" tabindex="-1">
      <h2 class="title">
        <mat-icon svgIcon="error" aria-hidden="true" />
        {{ heading() }}
      </h2>
      <ul>
        @for (problem of problems(); track problem.fieldId) {
          <li>
            <a [attr.href]="'#' + problem.fieldId" (click)="focusField($event, problem.fieldId)">{{
              problem.message
            }}</a>
          </li>
        }
      </ul>
    </div>
  `,
  styles: `
    .summary {
      margin: 0 0 var(--lg-space-6);
      padding: var(--lg-space-4);
      border: 2px solid var(--mat-sys-error);
      border-radius: var(--lg-radius-card);
      background: var(--mat-sys-surface);

      &:focus-visible {
        outline-offset: 3px;
      }
    }

    .title {
      display: flex;
      align-items: center;
      gap: var(--lg-space-2);
      margin: 0 0 var(--lg-space-2);
      color: var(--mat-sys-error);
      font: var(--mat-sys-title-medium);
    }

    ul {
      margin: 0;
      padding-left: var(--lg-space-6);
    }

    a {
      color: var(--mat-sys-error);
      font-weight: 500;
    }
  `,
})
export class ErrorSummary {
  readonly problems = input.required<readonly FormProblem[]>();
  readonly heading = input('There is a problem');

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  /**
  Moves focus to the summary (call after a failed submit, once it has rendered).
  */
  focus(): void {
    this.host.nativeElement.querySelector<HTMLElement>('.summary')?.focus();
  }

  // A plain "#id" href would resolve against <base href="/"> and leave the page.
  protected focusField(event: Event, fieldId: string): void {
    event.preventDefault();
    const field = this.host.nativeElement.ownerDocument.getElementById(fieldId);
    field?.focus();
    field?.scrollIntoView?.({ block: 'center' });
  }
}

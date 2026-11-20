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
  templateUrl: './error-summary.html',
  styleUrl: './error-summary.scss',
})
export class ErrorSummary {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly problems = input.required<readonly FormProblem[]>();

  readonly heading = input('There is a problem');

  /**
  Moves focus to the summary (call after a failed submit, once it has rendered).
  */
  focus(): void {
    this.host.nativeElement.querySelector<HTMLElement>('.summary')?.focus();
  }

  // A plain "#id" href would resolve against <base href="/"> and leave the page.
  protected focusField(event: Event, fieldId: string): void {
    event.preventDefault();
    const field = this.host.nativeElement.ownerDocument.querySelector<HTMLElement>(
      `#${CSS.escape(fieldId)}`
    );
    field?.focus();
    field?.scrollIntoView?.({ block: 'center' });
  }
}

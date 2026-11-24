import { Component, computed, input, output } from '@angular/core';
import { NgIcon } from '@ng-icons/core';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmLabel } from '@spartan-ng/helm/label';
import { HlmNativeSelectImports } from '@spartan-ng/helm/native-select';

/**
The page an admin list asks for: a 0-based index and the page size.
*/
export interface AdminPageChange {
  pageIndex: number;
  pageSize: number;
}

/**
 * The admin lists' pager: items per page, the range shown ("25 – 48 of 60") and previous/next. The
 * page keeps the state in its URL; this only says what was asked for (`pageChange`).
 */
@Component({
  selector: 'app-admin-paginator',
  imports: [HlmButton, HlmLabel, HlmNativeSelectImports, NgIcon],
  templateUrl: './admin-paginator.html',
  styleUrl: './admin-paginator.scss',
  host: { role: 'group' },
})
export class AdminPaginator {
  private readonly pageCount = computed(() =>
    Math.max(1, Math.ceil(this.length() / this.pageSize()))
  );

  /**
  How many items there are in all.
  */
  readonly length = input.required<number>();

  readonly pageIndex = input.required<number>();

  readonly pageSize = input.required<number>();

  readonly pageSizeOptions = input.required<readonly number[]>();

  readonly pageChange = output<AdminPageChange>();

  protected readonly hasPrevious = computed(() => this.pageIndex() > 0);

  protected readonly hasNext = computed(() => this.pageIndex() < this.pageCount() - 1);

  /**
  "25 – 48 of 60", or "0 of 0" for an empty list.
  */
  protected readonly rangeLabel = computed(() => {
    const length = this.length();
    if (length === 0) {
      return `0 of ${length}`;
    }
    const start = this.pageIndex() * this.pageSize();
    const end = Math.min(start + this.pageSize(), length);
    return `${start + 1} – ${end} of ${length}`;
  });

  protected go(pageIndex: number): void {
    this.pageChange.emit({ pageIndex, pageSize: this.pageSize() });
  }

  protected changeSize(value: string | null | undefined): void {
    const pageSize = Number(value);
    if (!Number.isSafeInteger(pageSize) || pageSize === this.pageSize()) {
      return;
    }
    // A new page size starts again at the first page.
    this.pageChange.emit({ pageIndex: 0, pageSize });
  }
}

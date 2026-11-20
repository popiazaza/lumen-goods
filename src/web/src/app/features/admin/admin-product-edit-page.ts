import { LiveAnnouncer } from '@angular/cdk/a11y';
import { DatePipe } from '@angular/common';
import {
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { rxResource, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ReactiveFormsModule } from '@angular/forms';
import { MatButton, MatIconButton } from '@angular/material/button';
import { MatCard } from '@angular/material/card';
import { MatOption } from '@angular/material/core';
import {
  MatError,
  MatFormField,
  MatHint,
  MatLabel,
  MatPrefix,
  MatSuffix,
} from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatSelect } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router, RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { AdminProduct, AdminProductsApi } from '../../core/api/admin-products.api';
import {
  httpStatusOf,
  isAccessDenied,
  problemMessage,
  problemOf,
} from '../../core/api/problem-details';
import { PRODUCT_CATEGORIES } from '../../core/api/products.api';
import { categoryLabel } from '../../shared/products/product-labels';
import { registerOrderIcons } from '../orders/order-icons';
import { AdminAccessDenied } from './admin-access-denied';
import {
  MAX_ATTRIBUTES,
  MAX_IMAGES,
  ProductField,
  ProductFormValue,
  attributeRow,
  createProductForm,
  fieldError,
  fieldOfErrorKey,
  fillForm,
  productFormValue,
  toProductInput,
} from './product-form';

/**
 * Admin: create (`/admin/products/new`) or edit (`/admin/products/:id`) one of Lumen Goods' own
 * products. Edits send the version they started from; if someone else saved in between, the API
 * answers 409 and overwrites nothing (BR-ADM-01). The page then loads the saved version, keeps the
 * admin's edits in the form and shows the saved value under each field that differs, so a second
 * Save applies the edits knowingly.
 */
@Component({
  selector: 'app-admin-product-edit-page',
  imports: [
    DatePipe,
    ReactiveFormsModule,
    RouterLink,
    MatButton,
    MatCard,
    MatError,
    MatFormField,
    MatHint,
    MatIcon,
    MatIconButton,
    MatInput,
    MatLabel,
    MatOption,
    MatPrefix,
    MatProgressBar,
    MatSelect,
    MatSuffix,
    AdminAccessDenied,
  ],
  templateUrl: './admin-product-edit-page.html',
  styleUrl: './admin-product-edit-page.scss',
})
export class AdminProductEditPage {
  /**
  The product id, bound from the `:id` route parameter; absent when creating.
  */
  readonly id = input<string>();

  private readonly api = inject(AdminProductsApi);
  private readonly router = inject(Router);
  private readonly snackBar = inject(MatSnackBar);
  private readonly announcer = inject(LiveAnnouncer);
  private readonly destroyRef = inject(DestroyRef);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  protected readonly isNew = computed(() => !this.id());
  protected readonly loaded = rxResource({
    params: () => this.id(),
    stream: ({ params: id }) => this.api.get(id),
  });
  protected readonly media = rxResource({ stream: () => this.api.media() });

  /**
  The saved product this edit is based on: its version is what Save sends.
  */
  protected readonly base = signal<AdminProduct | undefined>(undefined);
  /**
  Set after a 409: the version saved by someone else, loaded so the admin can compare.
  */
  protected readonly conflict = signal<AdminProduct | undefined>(undefined);
  protected readonly saving = signal(false);
  protected readonly formError = signal('');
  private readonly deniedStatus = signal<number | undefined>(undefined);

  protected readonly form = createProductForm(() => this.base()?.reserved ?? 0);
  protected readonly attributes = this.form.controls.attributes;

  protected readonly accessStatus = computed(() => {
    const error = this.loaded.error() ?? this.media.error();
    return isAccessDenied(error) ? httpStatusOf(error) : this.deniedStatus();
  });
  protected readonly loadError = computed(() =>
    this.loaded.status() === 'error' ? (httpStatusOf(this.loaded.error()) ?? -1) : undefined
  );
  protected readonly ready = computed(() => this.isNew() || this.base() !== undefined);

  /**
  File name to URL, from the library and the product's own images.
  */
  protected readonly imageUrls = computed(() => {
    const urls = new Map<string, string>();
    for (const image of this.base()?.images ?? []) {
      urls.set(image.file, image.url);
    }
    for (const file of this.media.hasValue() ? this.media.value() : []) {
      urls.set(file.file, file.url);
    }
    return urls;
  });

  protected readonly categories = PRODUCT_CATEGORIES.map((value) => ({
    value,
    label: categoryLabel(value),
  }));
  protected readonly maxAttributes = MAX_ATTRIBUTES;
  protected readonly maxImages = MAX_IMAGES;

  constructor() {
    registerOrderIcons();
    // Fill the form once, from the first load. A later reload after a conflict must not wipe
    // the admin's edits, so it goes through `conflict` instead.
    effect(() => {
      if (!this.loaded.hasValue()) {
        return;
      }
      const product = this.loaded.value();
      untracked(() => {
        if (this.base()?.id === product.id) {
          return;
        }

        this.base.set(product);
        fillForm(this.form, productFormValue(product));
      });
    });
  }

  protected error(field: ProductField): string {
    return fieldError(field, this.form.controls[field].errors);
  }

  /**
  The value saved by someone else, when it differs from the form; '' otherwise.
  */
  protected savedNow(field: Exclude<ProductField, 'sku'>): string {
    const latest = this.conflict();
    if (!latest) {
      return '';
    }
    const saved = productFormValue(latest);
    const current = this.form.getRawValue() as ProductFormValue;
    const text = (value: ProductFormValue) => describe(field, value);
    return text(saved) === text(current) ? '' : text(saved) || '(empty)';
  }

  protected addAttribute(): void {
    if (this.attributes.length >= MAX_ATTRIBUTES) {
      return;
    }
    this.attributes.push(attributeRow());
    this.focusAfterRender(`#attribute-key-${this.attributes.length - 1}`);
  }

  protected removeAttribute(index: number): void {
    this.attributes.removeAt(index);
    this.attributes.markAsDirty();
    this.focusAfterRender('#add-attribute');
  }

  protected isSelected(file: string): boolean {
    return this.form.controls.images.value.includes(file);
  }

  /**
  Adds an image at the end, or removes it. The first image is the main one.
  */
  protected toggleImage(file: string): void {
    const { images } = this.form.controls;
    images.setValue(
      images.value.includes(file)
        ? images.value.filter((name) => name !== file)
        : [...images.value, file]
    );
    images.markAsDirty();
    images.markAsTouched();
  }

  protected makeMain(file: string): void {
    const { images } = this.form.controls;
    images.setValue([file, ...images.value.filter((name) => name !== file)]);
    images.markAsDirty();
  }

  /**
  Back to the saved version: the admin drops their edits after a conflict.
  */
  protected useSavedVersion(): void {
    const latest = this.conflict();
    if (!latest) {
      return;
    }

    fillForm(this.form, productFormValue(latest));
    this.conflict.set(undefined);
    void this.announcer.announce('The form now shows the saved version.', 'polite');
  }

  protected save(): void {
    if (this.saving()) {
      return;
    }
    this.form.markAllAsTouched();
    this.formError.set('');
    if (this.form.invalid) {
      this.focusFirstInvalid();
      return;
    }
    const id = this.id();
    const base = this.base();
    const input = toProductInput(this.form);
    const request: Observable<AdminProduct> =
      id && base
        ? this.api.update(id, { ...input, version: base.version })
        : this.api.create({ ...input, sku: this.form.controls.sku.value.trim() || null });

    this.saving.set(true);
    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.form.markAsPristine();
        this.snackBar.open(`${saved.name} saved.`, undefined, { duration: 5000 });
        void this.router.navigate(['/admin/products']);
      },
      error: (error: unknown) => this.saveFailed(error),
    });
  }

  private saveFailed(error: unknown): void {
    const status = httpStatusOf(error);
    const problem = problemOf(error);
    if (isAccessDenied(error)) {
      this.saving.set(false);
      this.deniedStatus.set(status);
      return;
    }
    if (status === 400 && problem?.errors) {
      this.saving.set(false);
      this.showServerErrors(problem.errors);
      return;
    }
    if (status === 409) {
      const reserved = (problem as { reserved?: unknown } | undefined)?.reserved;
      if (typeof reserved === 'number') {
        // On hand went below what orders now hold: keep everything else and say how much.
        this.base.update((product) => product && { ...product, reserved });
        this.saving.set(false);
        this.showServerErrors({ onHand: [problemMessage(error, 'On hand is below reserved.')] });
        return;
      }
      if (this.isNew()) {
        this.saving.set(false);
        this.showServerErrors({ sku: [problemMessage(error, 'That SKU is already in use.')] });
        return;
      }
      this.reloadAfterConflict();
      return;
    }
    this.saving.set(false);
    this.formError.set(
      status === 404
        ? 'This product no longer exists.'
        : problemMessage(
            error,
            'We couldn’t save the product. Check your connection and try again.'
          )
    );
    this.focusAfterRender('#form-error');
  }

  /**
  Loads what is saved now, keeps the admin's edits, and bases the next Save on it.
  */
  private reloadAfterConflict(): void {
    const id = this.id();
    if (!id) {
      return;
    }
    this.api
      .get(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (latest) => {
          this.base.set(latest);
          this.conflict.set(latest);
          this.form.controls.onHand.updateValueAndValidity();
          this.saving.set(false);
          this.focusAfterRender('#conflict');
        },
        error: (error: unknown) => {
          this.saving.set(false);
          this.formError.set(
            `This product was changed by someone else, and we couldn’t load their version: ${problemMessage(error, 'try again.')}`
          );
          this.focusAfterRender('#form-error');
        },
      });
  }

  /**
  The API's 400/409 messages, shown under their fields; others above the buttons.
  */
  private showServerErrors(errors: Record<string, string[]>): void {
    const general: string[] = [];
    for (const [key, messages] of Object.entries(errors)) {
      const field = fieldOfErrorKey(key);
      const message = messages.join(' ');
      if (field) {
        const control = this.form.controls[field];
        control.setErrors({ ...control.errors, server: message });
        control.markAsTouched();
      } else {
        general.push(message);
      }
    }
    this.formError.set(general.join(' '));
    this.focusFirstInvalid();
  }

  private focusFirstInvalid(): void {
    this.focusAfterRender(
      'input.ng-invalid, textarea.ng-invalid, mat-select.ng-invalid, .group-error'
    );
  }

  private focusAfterRender(selector: string): void {
    afterNextRender(
      {
        write: () => this.host.nativeElement.querySelector<HTMLElement>(selector)?.focus(),
      },
      { injector: this.injector }
    );
  }
}

/**
One field of a form value as text, for comparing it with the saved version.
*/
function describe(field: Exclude<ProductField, 'sku'>, value: ProductFormValue): string {
  switch (field) {
    case 'attributes': {
      return value.attributes.map((row) => `${row.key.trim()}: ${row.value.trim()}`).join(', ');
    }
    case 'images': {
      return value.images.join(', ');
    }
    case 'category': {
      return value.category ? categoryLabel(value.category) : '';
    }
    case 'price': {
      return value.price.trim() === '' ? '' : `$${Number(value.price).toFixed(2)}`;
    }
    case 'onHand': {
      return value.onHand === null ? '' : String(value.onHand);
    }
    default: {
      return value[field].trim();
    }
  }
}

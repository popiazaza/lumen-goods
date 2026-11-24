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
import { Router, RouterLink } from '@angular/router';
import { NgIcon } from '@ng-icons/core';
import { toast } from '@spartan-ng/brain/sonner';
import { Observable } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmInput } from '@spartan-ng/helm/input';
import { HlmLabel } from '@spartan-ng/helm/label';
import { HlmNativeSelectImports } from '@spartan-ng/helm/native-select';
import { HlmProgressImports } from '@spartan-ng/helm/progress';
import { HlmTextarea } from '@spartan-ng/helm/textarea';
import { AdminProduct, AdminProductsApi } from '../../core/api/admin-products.api';
import {
  httpStatusOf,
  isAccessDenied,
  problemMessage,
  problemOf,
} from '../../core/api/problem-details';
import { PRODUCT_CATEGORIES, ProductCategory } from '../../core/api/products.api';
import { SessionApi } from '../../core/api/session.api';
import { categoryLabel } from '../../shared/products/product-labels';
import { AdminAccessDenied } from './admin-access-denied';
import { ChosenImages } from './chosen-images';
import { HasUnsavedChanges } from './unsaved-changes.guard';
import { ImageLibrary } from './image-library';
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

const categoryOption = (value: ProductCategory) => ({ value, label: categoryLabel(value) });

/**
The line under a form field: its error, or a hint (the saved value after a conflict is one).
*/
interface FieldNote {
  /**
  The id suffix: `product-name-error` or `product-name-hint`.
  */
  kind: 'error' | 'hint';
  className: string;
  text: string;
}

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
    NgIcon,
    HlmButton,
    HlmInput,
    HlmLabel,
    HlmNativeSelectImports,
    HlmProgressImports,
    HlmTextarea,
    AdminAccessDenied,
    ChosenImages,
    ImageLibrary,
  ],
  templateUrl: './admin-product-edit-page.html',
  styleUrl: './admin-product-edit-page.scss',
})
export class AdminProductEditPage implements HasUnsavedChanges {
  private readonly api = inject(AdminProductsApi);

  private readonly router = inject(Router);

  private readonly announcer = inject(LiveAnnouncer);

  private readonly session = inject(SessionApi);

  private readonly destroyRef = inject(DestroyRef);

  private readonly injector = inject(Injector);

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  private readonly deniedStatus = signal<number | undefined>(undefined);

  /**
  The product id, bound from the `:id` route parameter; absent when creating.
  */
  readonly id = input<string>();

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
    const productImages = this.base()?.images;
    if (productImages) {
      for (const image of productImages) {
        urls.set(image.file, image.url);
      }
    }
    if (this.media.hasValue()) {
      const library = this.media.value();
      for (const image of library) {
        urls.set(image.file, image.url);
      }
    }
    return urls;
  });

  protected readonly categories = PRODUCT_CATEGORIES.map(categoryOption);

  protected readonly maxAttributes = MAX_ATTRIBUTES;

  protected readonly maxImages = MAX_IMAGES;

  constructor() {
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
      'input.ng-invalid, textarea.ng-invalid, hlm-native-select.ng-invalid select, .group-error'
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

  protected error(field: ProductField): string {
    return fieldError(field, this.form.controls[field].errors);
  }

  /**
  A field's error shows once it was touched (Save touches every field), as on the app's other forms.
  */
  protected shows(field: ProductField): boolean {
    const control = this.form.controls[field];
    return control.invalid && control.touched;
  }

  /**
   * What is under a field, for its `aria-describedby` too: its error once touched (Save touches
   * every field), else the value someone else saved after a conflict, else its hint, if any.
   */
  protected note(field: ProductField, hint: string): FieldNote;
  protected note(field: ProductField): FieldNote | null;
  protected note(field: ProductField, hint = ''): FieldNote | null {
    if (this.shows(field)) {
      return { kind: 'error', className: 'field-error', text: this.error(field) };
    }
    // The SKU cannot change after creation, so no one else can have saved a different one.
    const saved = field === 'sku' ? '' : this.savedNow(field);
    if (saved) {
      return { kind: 'hint', className: 'hint saved-now', text: `Saved now: ${saved}` };
    }
    return hint ? { kind: 'hint', className: 'hint', text: hint } : null;
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
   * Edits not saved yet: leaving asks first (`unsavedChangesGuard`). Not once the form is gone: after a
   * 401 or 403 the page shows access denied and the sign-in redirect must go through, and after the
   * admin signed out there is nothing left to save.
   */
  hasUnsavedChanges(): boolean {
    return this.form.dirty && !this.accessStatus() && this.session.isAuthenticated();
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
    const product = toProductInput(this.form);
    const request: Observable<AdminProduct> =
      id && base
        ? this.api.update(id, { ...product, version: base.version })
        : this.api.create({ ...product, sku: this.form.controls.sku.value.trim() || null });

    this.saving.set(true);
    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.form.markAsPristine();
        toast(`${saved.name} saved.`, { duration: 5000 });
        void this.router.navigate(['/admin/products']);
      },
      error: (error: unknown) => this.saveFailed(error),
    });
  }
}

/**
One field of a form value as text, for comparing it with the saved version.
*/
const describe = (field: Exclude<ProductField, 'sku'>, value: ProductFormValue): string => {
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
};

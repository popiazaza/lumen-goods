import {
  AbstractControl,
  FormArray,
  FormControl,
  FormGroup,
  ValidationErrors,
  ValidatorFn,
  Validators,
} from '@angular/forms';
import { AdminProduct, OwnProductInput } from '../../core/api/admin-products.api';
import { ProductCategory } from '../../core/api/products.api';

// The own-product form. These checks mirror OwnProductValidation on the API for quick feedback
// only; the API checks again and its answer (400 errors keyed by field) wins.

export const MAX_NAME_LENGTH = 300;
export const MAX_DESCRIPTION_LENGTH = 5000;
export const MAX_ATTRIBUTES = 20;
export const MAX_IMAGES = 8;
export const MAX_ON_HAND = 1_000_000;
const SKU_PATTERN = /^[A-Za-z0-9][A-Za-z0-9-]{1,31}$/u;
/**
Dollars with at most two decimals, e.g. "18", "18.5", "18.50".
*/
const PRICE_PATTERN = /^\d{1,10}(?:\.\d{1,2})?$/u;

export type AttributeRow = FormGroup<{
  key: FormControl<string>;
  value: FormControl<string>;
}>;

export type ProductForm = FormGroup<{
  sku: FormControl<string>;
  name: FormControl<string>;
  description: FormControl<string>;
  category: FormControl<ProductCategory | ''>;
  price: FormControl<string>;
  onHand: FormControl<number | null>;
  attributes: FormArray<AttributeRow>;
  images: FormControl<string[]>;
}>;

/**
The form's fields; also the keys of the API's validation errors.
*/
export type ProductField = keyof ProductForm['controls'];
export const PRODUCT_FIELDS: readonly ProductField[] = [
  'sku',
  'name',
  'description',
  'category',
  'price',
  'onHand',
  'attributes',
  'images',
];

export const FIELD_LABELS: Readonly<Record<ProductField, string>> = {
  sku: 'SKU',
  name: 'Name',
  description: 'Description',
  category: 'Category',
  price: 'Price',
  onHand: 'On hand',
  attributes: 'Attributes',
  images: 'Images',
};

/**
The form's plain value, as `fillForm` takes it and `formValueOf` gives it.
*/
export interface ProductFormValue {
  sku: string;
  name: string;
  description: string;
  category: ProductCategory | '';
  price: string;
  onHand: number | null;
  attributes: { key: string; value: string }[];
  images: string[];
}

/**
 * @param reserved Units the saved product has reserved; on hand may not go below them (BR-ADM-01).
 */
export const createProductForm = (reserved: () => number): ProductForm =>
  new FormGroup({
    sku: new FormControl('', { nonNullable: true, validators: [Validators.pattern(SKU_PATTERN)] }),
    name: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, notBlank, Validators.maxLength(MAX_NAME_LENGTH)],
    }),
    description: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(MAX_DESCRIPTION_LENGTH)],
    }),
    category: new FormControl<ProductCategory | ''>('', {
      nonNullable: true,
      validators: [Validators.required],
    }),
    price: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.pattern(PRICE_PATTERN), positive],
    }),
    onHand: new FormControl<number | null>(0, {
      validators: [
        Validators.required,
        wholeNumber,
        Validators.min(0),
        Validators.max(MAX_ON_HAND),
        atLeastReserved(reserved),
      ],
    }),
    attributes: new FormArray<AttributeRow>([], {
      validators: [Validators.maxLength(MAX_ATTRIBUTES), uniqueAttributeNames],
    }),
    images: new FormControl<string[]>([], {
      nonNullable: true,
      validators: [Validators.maxLength(MAX_IMAGES)],
    }),
  });

export const attributeRow = (key = '', value = ''): AttributeRow =>
  new FormGroup({
    key: new FormControl(key, { nonNullable: true, validators: [Validators.required, notBlank] }),
    value: new FormControl(value, {
      nonNullable: true,
      validators: [Validators.required, notBlank, Validators.maxLength(200)],
    }),
  });

/**
The form values for a saved product.
*/
export const productFormValue = (product: AdminProduct): ProductFormValue => ({
  sku: product.sku,
  name: product.name,
  description: product.description,
  category: product.category,
  // The API sends two decimals; this only writes them as text for the field.
  price: product.price === null ? '' : product.price.toFixed(2),
  onHand: product.onHand,
  attributes: Object.entries(product.attributes).map(([key, value]) => ({ key, value })),
  images: product.images.map((image) => image.file),
});

/**
Replaces every value, rebuilding the attribute rows, and marks the form pristine.
*/
export const fillForm = (form: ProductForm, value: ProductFormValue): void => {
  const rows = form.controls.attributes;
  rows.clear({ emitEvent: false });
  for (const attribute of value.attributes) {
    rows.push(attributeRow(attribute.key, attribute.value), { emitEvent: false });
  }
  form.reset(value);
};

/**
What create and update send: trimmed text, the price as a number, attributes as an object.
*/
export const toProductInput = (form: ProductForm): OwnProductInput => {
  const value = form.getRawValue();
  return {
    name: value.name.trim(),
    description: value.description.trim(),
    category: value.category as ProductCategory,
    price: Number(value.price),
    attributes: Object.fromEntries(
      value.attributes.map((row) => [row.key.trim(), row.value.trim()])
    ),
    images: value.images,
    onHand: value.onHand ?? 0,
  };
};

/**
Text for a field's first error, or '' when it has none.
*/
export const fieldError = (field: ProductField, errors: ValidationErrors | null): string => {
  if (!errors) {
    return '';
  }
  if (errors['server']) {
    return String(errors['server']);
  }
  const label = FIELD_LABELS[field];
  if (errors['required'] || errors['blank']) {
    return `${label} is required.`;
  }
  if (errors['maxlength']) {
    const max = (errors['maxlength'] as { requiredLength: number }).requiredLength;
    return field === 'images' || field === 'attributes'
      ? `At most ${max} ${field}.`
      : `${label} can be at most ${max} characters.`;
  }
  if (errors['pattern']) {
    return field === 'price'
      ? 'Enter dollars and cents, e.g. 18.50.'
      : 'Use 2 to 32 letters, digits or dashes, starting with a letter or digit.';
  }
  if (errors['positive']) {
    return 'Price must be more than $0.00.';
  }
  if (errors['wholeNumber']) {
    return 'Enter a whole number.';
  }
  if (errors['min']) {
    return `${label} cannot be negative.`;
  }
  if (errors['max']) {
    return `${label} can be at most ${MAX_ON_HAND.toLocaleString('en-US')}.`;
  }
  if (errors['belowReserved']) {
    const { reserved } = errors['belowReserved'] as { reserved: number };
    return `${reserved} reserved by orders awaiting payment: on hand must be at least ${reserved}.`;
  }
  return errors['duplicateNames']
    ? `Two attributes are both named "${String(errors['duplicateNames'])}".`
    : `${label} is not valid.`;
};

/**
The API's error keys (`onHand`, `attributes.color`, `images[0]`) mapped to form fields.
*/
export const fieldOfErrorKey = (key: string): ProductField | undefined => {
  const [head = ''] = key.split(/[.[]/u, 1);
  return PRODUCT_FIELDS.find((field) => field.toLowerCase() === head.toLowerCase());
};

const notBlank = (control: AbstractControl<string>): ValidationErrors | null =>
  control.value && control.value.trim() === '' ? { blank: true } : null;

const positive = (control: AbstractControl<string>): ValidationErrors | null => {
  const { value } = control;
  return value && PRICE_PATTERN.test(value) && Number(value) <= 0 ? { positive: true } : null;
};

const wholeNumber = (control: AbstractControl<number | null>): ValidationErrors | null => {
  const { value } = control;
  return value === null || Number.isSafeInteger(value) ? null : { wholeNumber: true };
};

const atLeastReserved =
  (reserved: () => number): ValidatorFn =>
  (control) => {
    const value = control.value as number | null;
    const held = reserved();
    return value !== null && held > 0 && value < held
      ? { belowReserved: { reserved: held } }
      : null;
  };

const uniqueAttributeNames = (control: AbstractControl): ValidationErrors | null => {
  const seen = new Set<string>();
  for (const row of (control as FormArray<AttributeRow>).controls) {
    const name = row.controls.key.value.trim().toLowerCase();
    if (name && seen.has(name)) {
      return { duplicateNames: row.controls.key.value.trim() };
    }
    seen.add(name);
  }
  return null;
};

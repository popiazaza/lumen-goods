import { Provider } from '@angular/core';
import { provideIcons } from '@ng-icons/core';
import { lucidePencil, lucideReceipt, lucideStar, lucideUndo2 } from '@ng-icons/lucide';
import { AppIconName } from '../../core/icons/app-icons';

/**
 * Lucide icons that only the order and admin pages use. They are provided by those lazy components
 * (`providers: [provideOrderIcons()]`) instead of app-wide in core/icons/app-icons.ts, so the
 * shop's start-up bundle does not carry them. Add an icon here only if it is not in that set.
 */
const ORDER_ICONS = { lucidePencil, lucideReceipt, lucideStar, lucideUndo2 } as const;

/**
An icon the order and admin pages can show: the app-wide set plus the ones above.
*/
export type OrderIconName = AppIconName | keyof typeof ORDER_ICONS;

/**
Registers the icons above for a component and its children.
*/
export const provideOrderIcons = (): Provider => provideIcons(ORDER_ICONS);

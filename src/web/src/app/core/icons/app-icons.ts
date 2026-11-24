import { EnvironmentProviders, makeEnvironmentProviders } from '@angular/core';
import { provideIcons } from '@ng-icons/core';
import {
  lucideBan,
  lucideCheck,
  lucideChevronDown,
  lucideChevronLeft,
  lucideChevronRight,
  lucideCircleAlert,
  lucideCircleCheck,
  lucideCircleUser,
  lucideCircleX,
  lucideClock,
  lucideCreditCard,
  lucideEye,
  lucideEyeOff,
  lucideInfo,
  lucideLock,
  lucideMenu,
  lucideMinus,
  lucidePlus,
  lucideRefreshCw,
  lucideSearch,
  lucideShoppingCart,
  lucideSlidersHorizontal,
  lucideTrash2,
  lucideTriangleAlert,
  lucideTruck,
  lucideUnlink,
  lucideWallet,
  lucideX,
} from '@ng-icons/lucide';

/**
 * The app's icons: Lucide (ISC licence) through ng-icons, which bundles each SVG as a string, so
 * icons make no third-party request and render offline. Use them as
 * `<ng-icon name="lucideSearch" />`; ng-icon hides itself from assistive technology unless given
 * an `aria-hidden` of its own, so the text or `aria-label` beside it carries the meaning. Add new
 * icons here.
 */
const LUCIDE_ICONS = {
  lucideBan,
  lucideCheck,
  lucideChevronDown,
  lucideChevronLeft,
  lucideChevronRight,
  lucideCircleAlert,
  lucideCircleCheck,
  lucideCircleUser,
  lucideCircleX,
  lucideClock,
  lucideCreditCard,
  lucideEye,
  lucideEyeOff,
  lucideInfo,
  lucideLock,
  lucideMenu,
  lucideMinus,
  lucidePlus,
  lucideRefreshCw,
  lucideSearch,
  lucideShoppingCart,
  lucideSlidersHorizontal,
  lucideTrash2,
  lucideTriangleAlert,
  lucideTruck,
  lucideUnlink,
  lucideWallet,
  lucideX,
} as const;

/**
A Lucide icon this app registers.
*/
export type AppIconName = keyof typeof LUCIDE_ICONS;

/**
Registers the app's icons with ng-icons, for every component.
*/
export const provideAppIcons = (): EnvironmentProviders =>
  makeEnvironmentProviders([provideIcons(LUCIDE_ICONS)]);

import { inject } from '@angular/core';
import { MatIconRegistry } from '@angular/material/icon';
import { DomSanitizer } from '@angular/platform-browser';

// Material Icons paths (Apache-2.0) that only the order and admin pages use and the app-wide set
// in core/icons/app-icons.ts lacks. Those lazy pages register them (`registerOrderIcons()`), so the
// shop's start-up bundle does not carry them. Add an icon here only if it is not in that set.
const ORDER_ICON_PATHS: Readonly<Record<string, string>> = {
  undo: 'M12.5 8c-2.65 0-5.05.99-6.9 2.6L2 7v9h9l-3.62-3.62c1.39-1.16 3.16-1.88 5.12-1.88 3.54 0 6.55 2.31 7.6 5.5l2.37-.78C21.08 11.03 17.15 8 12.5 8z',
  edit: 'M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25zM20.71 7.04c.39-.39.39-1.02 0-1.41l-2.34-2.34c-.39-.39-1.02-.39-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83z',
  star: 'M12 17.27L18.18 21l-1.64-7.03L22 9.24l-7.19-.61L12 2 9.19 8.63 2 9.24l5.46 4.73L5.82 21z',
};

const registries = /* @__PURE__ */ new WeakSet<MatIconRegistry>();

/**
 * Registers the order and admin icons with Angular Material's icon registry, once per registry.
 * Call it from an injection context (a page's constructor or field initialiser).
 */
export const registerOrderIcons = (): void => {
  const registry = inject(MatIconRegistry);
  if (registries.has(registry)) {
    return;
  }
  registries.add(registry);
  const sanitizer = inject(DomSanitizer);
  for (const [name, path] of Object.entries(ORDER_ICON_PATHS)) {
    const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="${path}"/></svg>`;
    // eslint-disable-next-line sonarjs/no-angular-bypass-sanitization -- addSvgIconLiteral takes SafeHtml; these SVGs are the constants above, never user or server input
    registry.addSvgIconLiteral(name, sanitizer.bypassSecurityTrustHtml(svg));
  }
};

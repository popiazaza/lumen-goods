import { Component } from '@angular/core';

@Component({
  selector: 'app-footer',
  template: `
    <footer class="footer">
      <p>
        Lumen Goods: our own products plus picks from Harbor Supply Co. and Kestrel Outfitters, in
        one cart. All prices in USD.
      </p>
    </footer>
  `,
  styles: `
    .footer {
      margin-top: var(--lg-space-8);
      padding: var(--lg-space-6) var(--lg-gutter);
      border-top: 1px solid var(--mat-sys-outline-variant);
      background: var(--mat-sys-surface-container-low);
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-medium);
    }

    p {
      max-width: var(--lg-content-max);
      margin: 0 auto;
    }
  `,
})
export class Footer {}

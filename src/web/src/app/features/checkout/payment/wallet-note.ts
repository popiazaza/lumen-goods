import { CurrencyPipe } from '@angular/common';
import { Component, input } from '@angular/core';

/**
 * The text under the wallet option: its balance, or why it can't pay this order. Inline, so the
 * page keeps the paragraph (`#wallet-note`) that the wallet option is described by.
 */
@Component({
  selector: 'app-wallet-note',
  imports: [CurrencyPipe],
  templateUrl: './wallet-note.html',
})
export class WalletNote {
  /**
  Only the same card payment may be tried again (BR-PAY-05), so the wallet is out.
  */
  readonly samePaymentOnly = input.required<boolean>();

  /**
  The wallet balance; null until it is loaded.
  */
  readonly balance = input.required<number | null>();

  /**
  The balance covers the whole order total (BR-PAY-06).
  */
  readonly covers = input.required<boolean>();

  readonly total = input.required<number | null>();

  readonly loadFailed = input.required<boolean>();
}

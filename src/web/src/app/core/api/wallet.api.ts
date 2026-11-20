import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from './api-base-url';

export type WalletTransactionKind = 'seed' | 'payment' | 'refund';

export interface WalletTransactionDto {
  id: string;
  createdUtc: string;
  kind: WalletTransactionKind;
  /** Signed: payments are negative, the opening balance and refunds positive. */
  amount: number;
  balanceAfter: number;
  orderNumber: string | null;
}

/** The signed-in customer's mock USD wallet (BR-PAY-06). */
export interface WalletDto {
  balance: number;
  currency: 'USD';
  recentTransactions: WalletTransactionDto[];
}

/** `GET /api/v1/wallet` (signed in; 401 otherwise). */
@Service()
export class WalletApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(API_BASE_URL);

  get(): Observable<WalletDto> {
    return this.http.get<WalletDto>(`${this.baseUrl}/wallet`);
  }
}

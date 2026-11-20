import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from './api-base-url';

/**
A shop whose products the catalogue lists, e.g. `{ source: 'harbor', name: 'Harbor Supply Co.' }`.
*/
export interface Seller {
  source: string;
  name: string;
}

/**
`GET /api/v1/sellers`.
*/
@Service()
export class SellersApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(API_BASE_URL);

  list(): Observable<Seller[]> {
    return this.http.get<Seller[]>(`${this.baseUrl}/sellers`);
  }
}

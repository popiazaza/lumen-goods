import { DatePipe } from '@angular/common';
import { Component, computed, input } from '@angular/core';
import { NgIcon } from '@ng-icons/core';
import { PartnerOrder } from '../../core/api/admin-orders.api';
import { StatusBadge } from '../orders/status-badge';
import { partnerOrderView } from './admin-order-labels';

/**
 * One partner order on the admin's order page: its status, Lumen's reference and the partner's,
 * the attempts and when each step happened, and why an admin may need to step in (BR-ORD-04).
 */
@Component({
  selector: 'app-partner-order-details',
  imports: [DatePipe, NgIcon, StatusBadge],
  templateUrl: './partner-order-details.html',
  styleUrl: './partner-order-details.scss',
})
export class PartnerOrderDetails {
  readonly partnerOrder = input.required<PartnerOrder>();

  protected readonly status = computed(() => partnerOrderView(this.partnerOrder()));
}

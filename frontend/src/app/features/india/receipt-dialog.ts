import { Component, ViewEncapsulation, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MAT_DIALOG_DATA, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { Observable } from 'rxjs';
import { toProblem } from '../../core/http';
import { PropertyContext } from '../../core/property-context';
import { OrderBill, PaperWidth, readPaper, savePaper } from './india.api';
import { KotTicket, RECEIPT_CSS, billHtml, kotHtml, printReceipt } from './receipt';

/** What to show and print: a bill fetched when the dialog opens, or a kitchen ticket already in hand. */
export interface ReceiptData {
  title: string;
  bill?: Observable<OrderBill>;
  kot?: KotTicket;
}

/**
 * Preview and print a restaurant bill or a kitchen ticket (Sprint 30). The preview is the receipt exactly as it prints.
 * The paper width (80 mm or 58 mm) is remembered on this device, because the printer belongs to the device. A bill that
 * is still open shows the UPI QR; the missing FSSAI number or UPI ID is pointed out here, with where to add it.
 */
@Component({
  selector: 'app-receipt-dialog',
  encapsulation: ViewEncapsulation.None,
  imports: [MatDialogModule, MatButtonModule, MatButtonToggleModule, MatIconModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <div mat-dialog-content class="rcpt-dialog">
      <div class="rcpt-bar">
        <span id="paper-lbl">Paper</span>
        <mat-button-toggle-group [value]="paper()" (change)="setPaper($event.value)" aria-labelledby="paper-lbl" hideSingleSelectionIndicator>
          <mat-button-toggle [value]="80">80 mm</mat-button-toggle>
          <mat-button-toggle [value]="58">58 mm</mat-button-toggle>
        </mat-button-toggle-group>
      </div>
      @if (html(); as h) {
        <div class="rcpt-paper"><div [class]="'receipt w' + paper()" data-testid="receipt" [innerHTML]="h"></div></div>
        @for (t of tips(); track t) { <p class="rcpt-tip" data-testid="receipt-tip"><mat-icon>info</mat-icon>{{ t }}</p> }
      } @else if (error()) { <p class="rcpt-err" role="alert">{{ error() }}</p> }
      @else { <p class="rcpt-mute">Getting the bill…</p> }
      @if (printError()) { <p class="rcpt-err" role="alert">{{ printError() }}</p> }
    </div>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Close</button>
      <button mat-flat-button type="button" (click)="print()" [disabled]="!html()" data-testid="print-receipt"><mat-icon>print</mat-icon>Print</button>
    </div>
  `,
  styles: `
    .rcpt-dialog { min-width: min(380px, 86vw); }
    .rcpt-bar { display: flex; align-items: center; gap: var(--space-3); margin-bottom: var(--space-3); color: var(--ro-mute); font-size: .9rem; }
    .rcpt-paper { display: flex; justify-content: center; padding: var(--space-3); background: var(--ro-sunk); border-radius: var(--ro-radius-lg); overflow-x: auto; }
    .rcpt-paper .receipt { box-shadow: 0 2px 10px rgba(0, 0, 0, .25); flex: 0 0 auto; }
    .rcpt-tip { display: flex; gap: var(--space-2); align-items: flex-start; margin: var(--space-3) 0 0; font-size: .88rem; color: var(--ro-warn-strong); }
    .rcpt-tip mat-icon { flex: 0 0 auto; font-size: 18px; width: 18px; height: 18px; }
    .rcpt-err { color: var(--ro-error); } .rcpt-mute { color: var(--ro-mute); }
    ${RECEIPT_CSS}
  `,
})
export class ReceiptDialog {
  data = inject<ReceiptData>(MAT_DIALOG_DATA);
  private ctx = inject(PropertyContext);
  private tz = this.ctx.current()?.timezone ?? 'Asia/Kolkata';

  paper = signal<PaperWidth>(readPaper());
  private bill = signal<OrderBill | null>(null);
  error = signal('');
  printError = signal('');

  html = computed(() => this.data.kot ? kotHtml(this.data.kot, this.tz) : this.bill() ? billHtml(this.bill()!, this.tz) : '');
  /** What is missing from the bill that Indian rules or habit expect, and where to add it. */
  tips = computed(() => {
    const b = this.bill();
    if (!b) return [];
    const out: string[] = [];
    if (!b.property.fssaiLicenceNo) out.push('No FSSAI licence number on this bill. Food bills must show it: add it under Settings, Properties, Food licence and UPI.');
    if (b.status === 'OPEN' && !b.upi) out.push('No UPI QR on this bill, because the property has no UPI ID yet. Add it under Settings, Properties, Food licence and UPI.');
    return out;
  });

  constructor() {
    this.data.bill?.subscribe({ next: b => this.bill.set(b), error: e => this.error.set(toProblem(e).detail) });
  }

  setPaper(w: PaperWidth) { this.paper.set(w); savePaper(w); }

  print() {
    this.printError.set('');
    if (!printReceipt(this.html(), this.paper(), this.data.title))
      this.printError.set('This browser did not open a print window. Allow printing for this site, or use the browser menu, Print.');
  }
}

import { MatDatepickerModule } from '@angular/material/datepicker';
import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { AbstractControl, FormArray, FormBuilder, FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { catchError, concatMap, from, map, of, toArray } from 'rxjs';
import { toProblem } from '../../core/http';
import { Budget, BudgetSummary, CostCentre, Expense, ExpenseCategory, ExpenseSetup } from '../../core/models';
import { MAX_PHOTO_BYTES, compressPhoto } from '../housekeeping/housekeeping.api';
import { showError } from '../leave/leave.api';
import { ExpensesApi, MAX_RECEIPTS, PAYMENT_METHOD, gstSplit, inr, monthShort, spread, yearLabel } from './expenses.api';

const server = (c: AbstractControl | null) => (c?.errors?.['server'] as string | undefined) ?? '';
const text = (v: string | null | undefined) => (v ?? '').trim() || null;

interface PickedReceipt { name: string; blob: Blob; url: string | null; pdf: boolean; }

// ------------------------------------------------------------------ recording or changing an expense

export interface ExpenseData { propertyId: string; setup: ExpenseSetup; today: string; expense: Expense | null; }
/** What the dialog closes with: the expense, and any receipt that failed to upload (the expense itself was saved). */
export interface ExpenseSaved { expense: Expense; failed: string[]; }

/**
 * Records money spent, from a phone: what for, which department, who was paid and how, the amount, and for a GST bill the
 * vendor's GSTIN, the bill number and the GST on it (the split into CGST + SGST or IGST is shown as it will be saved).
 * Receipt photos are shrunk on the phone before they are sent; a PDF bill goes as it is (at most 2 MB). Receipts go up one
 * by one after the expense is saved, so one that fails doesn't lose the expense.
 */
@Component({
  selector: 'app-expense-dialog',
  imports: [MatDatepickerModule, ReactiveFormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>{{ e ? 'Change ' + e.code : 'Record an expense' }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="expense-form">
      <mat-form-field appearance="outline"><mat-label>What was it for</mat-label>
        <input matInput formControlName="description" maxlength="200" placeholder="e.g. Diesel for the generator" autocomplete="off" />
        <mat-error>{{ server(form.get('description')) || 'Say what was bought or paid for, 3–200 characters.' }}</mat-error></mat-form-field>
      <div class="two">
        <mat-form-field appearance="outline"><mat-label>Amount paid (with GST)</mat-label>
          <span matTextPrefix>₹&nbsp;</span><input matInput type="number" inputmode="decimal" formControlName="amount" min="0.01" step="0.01" />
          <mat-error>{{ server(form.get('amount')) || 'Enter the amount paid, more than ₹0.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Day spent</mat-label>
          <input matInput [matDatepicker]="dp4" formControlName="expenseDate" [max]="data.today" /><mat-datepicker-toggle matIconSuffix [for]="dp4" /><mat-datepicker #dp4 />
          <mat-error>{{ server(form.get('expenseDate')) || 'Enter the day the money was spent.' }}</mat-error></mat-form-field>
      </div>
      <div class="two">
        <mat-form-field appearance="outline"><mat-label>Category</mat-label>
          <mat-select formControlName="categoryId" [aria-label]="'Category'">
            @for (c of categories; track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }
          </mat-select>
          <mat-error>{{ server(form.get('categoryId')) || 'Choose what the money was for.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Department</mat-label>
          <mat-select formControlName="costCentreId" [aria-label]="'Department'">
            @for (c of centres; track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }
          </mat-select>
          <mat-error>{{ server(form.get('costCentreId')) || 'Choose the department that spent it.' }}</mat-error></mat-form-field>
      </div>
      <div class="two">
        <mat-form-field appearance="outline"><mat-label>Paid to</mat-label>
          <input matInput formControlName="payee" maxlength="100" placeholder="e.g. Malvan Fuels" autocomplete="off" />
          <mat-error>{{ server(form.get('payee')) || 'Say who was paid, 2–100 characters.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Paid by</mat-label>
          <mat-select formControlName="paymentMethod" [aria-label]="'Paid by'">
            @for (m of data.setup.paymentMethods; track m) { <mat-option [value]="m">{{ method[m] }}</mat-option> }
          </mat-select>
          <mat-error>{{ server(form.get('paymentMethod')) }}</mat-error></mat-form-field>
      </div>
      @if (form.value.paymentMethod !== 'CASH') {
        <mat-form-field appearance="outline"><mat-label>Payment reference (optional)</mat-label>
          <input matInput formControlName="paymentReference" maxlength="60" placeholder="e.g. UPI ref 4021…, cheque 001234" autocomplete="off" />
          <mat-error>{{ server(form.get('paymentReference')) }}</mat-error></mat-form-field>
      }

      <mat-checkbox formControlName="gstBill">It is a GST bill (it shows the vendor's GSTIN)</mat-checkbox>
      @if (form.value.gstBill) {
        <div class="gst">
          <div class="two">
            <mat-form-field appearance="outline"><mat-label>Vendor's GSTIN</mat-label>
              <input matInput formControlName="vendorGstin" maxlength="20" placeholder="e.g. 30ABCDE1234F1ZD" autocomplete="off" class="caps" />
              <mat-error>{{ server(form.get('vendorGstin')) || 'Enter the GSTIN printed on the bill (15 characters).' }}</mat-error></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Bill number</mat-label>
              <input matInput formControlName="billNumber" maxlength="30" placeholder="e.g. MF/2627/0412" autocomplete="off" />
              <mat-error>{{ server(form.get('billNumber')) || 'Enter the bill number printed on the bill.' }}</mat-error></mat-form-field>
          </div>
          <mat-form-field appearance="outline"><mat-label>GST on the bill</mat-label>
            <span matTextPrefix>₹&nbsp;</span><input matInput type="number" inputmode="decimal" formControlName="gstAmount" min="0" step="0.01" />
            <mat-hint>{{ gstHint() }}</mat-hint>
            <mat-error>{{ server(form.get('gstAmount')) || 'Enter the total GST shown on the bill (CGST + SGST, or IGST).' }}</mat-error></mat-form-field>
        </div>
      }
      <mat-form-field appearance="outline"><mat-label>Note (optional)</mat-label>
        <textarea matInput formControlName="note" rows="2" maxlength="500"></textarea>
        <mat-error>{{ server(form.get('note')) }}</mat-error></mat-form-field>

      @if (!e) {
        <h3>Receipt</h3>
        <div class="receipts">
          @for (r of receipts(); track $index; let i = $index) {
            <figure>
              @if (r.pdf) { <span class="pdf"><mat-icon>picture_as_pdf</mat-icon><small>{{ r.name }}</small></span> }
              @else { <img [src]="r.url" [alt]="'Receipt ' + (i + 1)" /> }
              <button type="button" mat-icon-button (click)="remove(i)" [attr.aria-label]="'Remove receipt ' + (i + 1)"><mat-icon>close</mat-icon></button>
            </figure>
          }
          @if (receipts().length < maxReceipts) {
            <label class="add">
              <mat-icon>photo_camera</mat-icon><span>{{ shrinking() ? 'Shrinking…' : 'Add receipt' }}</span>
              <input type="file" accept="image/*,application/pdf" capture="environment" multiple (change)="pick($event)" data-testid="receipt-input" />
            </label>
          }
        </div>
        <p class="hint">{{ receiptHint() }}</p>
        @if (receiptError()) { <p class="err" role="alert">{{ receiptError() }}</p> }
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="expense-form" [disabled]="busy() || shrinking()">{{ busy() ? 'Saving…' : e ? 'Save' : 'Record' }}</button>
    </div>
  `,
  styles: `
    form { display: flex; flex-direction: column; gap: 2px; width: min(560px, 86vw); }
    .two { display: grid; grid-template-columns: 1fr 1fr; gap: var(--space-2); }
    .gst { background: var(--ro-wash); border-radius: 10px; padding: 10px 10px 0; margin: var(--space-1) 0 10px; }
    .caps input, input.caps { text-transform: uppercase; }
    h3 { font-size: 1rem; font-weight: 600; margin: 6px 0; }
    .receipts { display: flex; flex-wrap: wrap; gap: var(--space-2); }
    figure { position: relative; margin: 0; width: 84px; height: 84px; }
    figure img { width: 100%; height: 100%; object-fit: cover; border-radius: 8px; }
    figure button { position: absolute; top: -6px; right: -6px; background: var(--ro-paper); transform: scale(.8); }
    .pdf { display: flex; flex-direction: column; align-items: center; justify-content: center; height: 100%; border: 1px solid var(--ro-line); border-radius: 8px;
           color: var(--ro-error-strong); overflow: hidden; } .pdf small { color: var(--ro-ink-soft); font-size: .7rem; max-width: 76px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .add { position: relative; width: 84px; height: 84px; border: 2px dashed var(--ro-outline); border-radius: 8px; display: flex; flex-direction: column;
           align-items: center; justify-content: center; color: var(--ro-ink-soft); font-size: .8rem; cursor: pointer; text-align: center; }
    .add input { position: absolute; width: 1px; height: 1px; opacity: 0; }
    .hint { color: var(--ro-mute); font-size: .85rem; margin: var(--space-1) 0; } .err { color: var(--ro-error); }
    mat-checkbox { margin: 0 0 6px; }
    @media (max-width: 520px) { .two { grid-template-columns: 1fr; gap: 0; } }
  `,
})
export class ExpenseDialog {
  data = inject<ExpenseData>(MAT_DIALOG_DATA);
  e = this.data.expense;
  private ref = inject(MatDialogRef<ExpenseDialog, ExpenseSaved>);
  private api = inject(ExpensesApi);
  server = server; method = PAYMENT_METHOD; maxReceipts = MAX_RECEIPTS;
  categories = this.data.setup.categories.filter(c => c.isActive || c.id === this.e?.categoryId);
  centres = this.data.setup.costCentres.filter(c => c.isActive || c.id === this.e?.costCentreId);
  busy = signal(false);
  shrinking = signal(false);
  error = signal('');
  receiptError = signal('');
  receipts = signal<PickedReceipt[]>([]);
  /** One key per recording attempt: a retry after a lost answer replays the first one. */
  private key = crypto.randomUUID();

  form = inject(FormBuilder).group({
    description: [this.e?.description ?? '', [Validators.required, Validators.minLength(3), Validators.maxLength(200)]],
    amount: [this.e?.amount ?? (null as number | null), [Validators.required, Validators.min(0.01)]],
    expenseDate: [this.e?.expenseDate ?? this.data.today, Validators.required],
    categoryId: [this.e?.categoryId ?? '', Validators.required],
    costCentreId: [this.e?.costCentreId ?? '', Validators.required],
    payee: [this.e?.payee ?? '', [Validators.required, Validators.minLength(2), Validators.maxLength(100)]],
    paymentMethod: [this.e?.paymentMethod ?? 'CASH'],
    paymentReference: [this.e?.paymentReference ?? ''],
    gstBill: [!!this.e?.vendorGstin],
    vendorGstin: [this.e?.vendorGstin ?? ''],
    billNumber: [this.e?.billNumber ?? ''],
    gstAmount: [this.e?.gstAmount ?? (0 as number | null)],
    note: [this.e?.note ?? ''],
  });
  private value = toSignal(this.form.valueChanges, { initialValue: this.form.value });

  gstHint = computed(() => {
    const v = this.value();
    const gst = Number(v.gstAmount) || 0;
    if (!gst) return 'From the bill: the CGST + SGST, or the IGST.';
    const s = gstSplit(gst, text(v.vendorGstin)?.toUpperCase() ?? null, this.data.setup.stateCode);
    const split = s.igst ? `IGST ${inr(s.igst)} (another state)` : `CGST ${inr(s.cgst)} + SGST ${inr(s.sgst)}`;
    const amount = Number(v.amount) || 0;
    return `${split}; taxable value ${inr(Math.max(0, Math.round((amount - gst) * 100) / 100))}.` +
      (this.data.setup.gstRegistered ? '' : ' The property isn\'t GST registered, so the tax can\'t be claimed back.');
  });
  receiptHint = computed(() => {
    const amount = Number(this.value().amount) || 0;
    const limit = this.data.setup.receiptRequiredOver;
    return amount > limit
      ? `Over ${inr(limit)}: add a photo of the receipt, or it can't be approved. Photos are shrunk before sending.`
      : `Add a photo of the receipt if you have one (needed over ${inr(limit)}).`;
  });

  constructor() {
    this.form.valueChanges.subscribe(() => { this.key = crypto.randomUUID(); });
  }

  async pick(ev: Event) {
    const input = ev.target as HTMLInputElement;
    const files = [...(input.files ?? [])];
    input.value = '';
    this.receiptError.set('');
    const room = MAX_RECEIPTS - this.receipts().length;
    if (files.length > room) this.receiptError.set(`At most ${MAX_RECEIPTS} receipts per expense; only the first ${room} were added.`);
    this.shrinking.set(true);
    for (const f of files.slice(0, room)) {
      const pdf = f.type === 'application/pdf';
      if (!pdf && !f.type.startsWith('image/')) { this.receiptError.set(`${f.name} isn't a photo or a PDF. Choose a JPEG, PNG or WebP photo, or a PDF bill.`); continue; }
      const blob = pdf ? f : await compressPhoto(f);
      if (blob.size > MAX_PHOTO_BYTES) {
        this.receiptError.set(`${f.name} is ${(blob.size / 1024 / 1024).toFixed(1)} MB${pdf ? '' : ' even after shrinking'}; at most 2 MB.` +
          (pdf ? ' Save the PDF smaller or photograph the bill.' : ' Take the photo again closer up.'));
        continue;
      }
      const name = pdf ? f.name : f.name.replace(/\.[^.]+$/, '') + '.jpg';
      this.receipts.update(r => [...r, { name, blob, url: pdf ? null : URL.createObjectURL(blob), pdf }]);
    }
    this.shrinking.set(false);
  }

  remove(i: number) {
    const r = this.receipts()[i];
    if (r.url) URL.revokeObjectURL(r.url);
    this.receipts.update(list => list.filter((_, j) => j !== i));
  }

  save() {
    const v = this.form.getRawValue();
    if (v.gstBill && !text(v.vendorGstin)) this.form.controls.vendorGstin.setErrors({ required: true });
    if (v.gstBill && Number(v.gstAmount) > 0 && !text(v.billNumber)) this.form.controls.billNumber.setErrors({ required: true });
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.busy.set(true);
    this.error.set('');
    const body = {
      expenseDate: v.expenseDate!, categoryId: v.categoryId!, costCentreId: v.costCentreId!, description: v.description!.trim(), payee: v.payee!.trim(),
      paymentMethod: v.paymentMethod!, amount: Number(v.amount), paymentReference: v.paymentMethod === 'CASH' ? null : text(v.paymentReference),
      vendorGstin: v.gstBill ? text(v.vendorGstin) : null, billNumber: v.gstBill ? text(v.billNumber) : null,
      gstAmount: v.gstBill ? Number(v.gstAmount) || 0 : 0, note: text(v.note), version: this.e?.version ?? null,
    };
    const pid = this.data.propertyId;
    const call = this.e ? this.api.update(pid, this.e.id, body) : this.api.create(pid, body, this.key);
    call.subscribe({
      next: saved => {
        from(this.receipts()).pipe(
          concatMap(r => this.api.addReceipt(pid, saved.id, r.blob, r.name).pipe(map(() => null), catchError(err => of(`${r.name}: ${toProblem(err).detail}`)))),
          toArray(),
        ).subscribe(problems => {
          this.receipts().forEach(r => r.url && URL.revokeObjectURL(r.url));
          const failed = problems.filter((x): x is string => !!x);
          if (!this.receipts().length) { this.ref.close({ expense: saved, failed }); return; }
          this.api.get(pid, saved.id).subscribe({ next: x => this.ref.close({ expense: x, failed }), error: () => this.ref.close({ expense: saved, failed }) });
        });
      },
      error: err => { this.busy.set(false); showError(this.form, err, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ categories and departments

export interface SetupItemData { propertyId: string; kind: 'category' | 'centre'; item: ExpenseCategory | CostCentre | null; }

/** A category (with whether its GST can be claimed back) or a department. */
@Component({
  selector: 'app-expense-setup-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSlideToggleModule],
  template: `
    <h2 mat-dialog-title>{{ data.item ? 'Change ' + data.item.name : data.kind === 'category' ? 'New category' : 'New department' }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="setup-form">
      <div class="two">
        <mat-form-field appearance="outline"><mat-label>Code</mat-label>
          <input matInput formControlName="code" maxlength="10" [placeholder]="data.kind === 'category' ? 'e.g. LAUNDRY' : 'e.g. SPA'" class="caps" />
          <mat-error>{{ server(form.get('code')) || 'The code is 1–10 letters or digits starting with a letter.' }}</mat-error></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Name</mat-label>
          <input matInput formControlName="name" maxlength="60" [placeholder]="data.kind === 'category' ? 'e.g. Laundry outsourcing' : 'e.g. Spa'" />
          <mat-error>{{ server(form.get('name')) || 'Give it a name, 2–60 characters.' }}</mat-error></mat-form-field>
      </div>
      @if (data.kind === 'category') {
        <mat-slide-toggle formControlName="gstClaimable">GST on it can be claimed back</mat-slide-toggle>
        <p class="hint">Switch off for spending whose input tax is blocked, such as food and drink for staff: then the whole amount counts against the budget.</p>
      }
      <mat-slide-toggle formControlName="isActive">In use</mat-slide-toggle>
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="setup-form" [disabled]="busy()">Save</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 6px; width: min(460px, 84vw); } .two { display: grid; grid-template-columns: 1fr 2fr; gap: var(--space-2); }
           input.caps { text-transform: uppercase; } .hint { color: var(--ro-mute); font-size: .85rem; margin: 0 0 6px; } .err { color: var(--ro-error); }`,
})
export class ExpenseSetupDialog {
  data = inject<SetupItemData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<ExpenseSetupDialog, ExpenseCategory | CostCentre>);
  private api = inject(ExpensesApi);
  server = server;
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).nonNullable.group({
    code: [this.data.item?.code ?? '', [Validators.required, Validators.pattern(/^[A-Za-z][A-Za-z0-9]{0,9}$/)]],
    name: [this.data.item?.name ?? '', [Validators.required, Validators.minLength(2), Validators.maxLength(60)]],
    gstClaimable: [(this.data.item as ExpenseCategory | null)?.gstClaimable ?? true],
    isActive: [this.data.item?.isActive ?? true],
  });

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.error.set('');
    const common = { code: v.code.trim().toUpperCase(), name: v.name.trim(), sortOrder: this.data.item?.sortOrder ?? 500, isActive: v.isActive, version: this.data.item?.version ?? null };
    const call = this.data.kind === 'category'
      ? this.api.saveCategory(this.data.propertyId, this.data.item?.id ?? null, { ...common, gstClaimable: v.gstClaimable })
      : this.api.saveCostCentre(this.data.propertyId, this.data.item?.id ?? null, common);
    call.subscribe({
      next: (x: ExpenseCategory | CostCentre) => this.ref.close(x),
      error: err => { this.busy.set(false); showError(this.form, err, m => this.error.set(m)); },
    });
  }
}

// ------------------------------------------------------------------ budgets

export interface NewBudgetData { propertyId: string; year: number; budgets: BudgetSummary[]; }

/** Starts a year's budget: empty, or copied from another year with an increase. */
@Component({
  selector: 'app-new-budget-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Budget for {{ label(data.year) }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="new-budget-form">
      <p class="hint">April {{ data.year }} to March {{ data.year + 1 }}. Plan each department's months after it is made.</p>
      @if (data.budgets.length) {
        <mat-form-field appearance="outline"><mat-label>Start from</mat-label>
          <mat-select formControlName="copyFromId" [aria-label]="'Start from'">
            <mat-option [value]="null">Nothing (fill it in)</mat-option>
            @for (b of data.budgets; track b.id) { <mat-option [value]="b.id">A copy of {{ b.label }} ({{ inr(b.total) }})</mat-option> }
          </mat-select>
          <mat-error>{{ server(form.get('copyFromId')) }}</mat-error></mat-form-field>
        @if (form.value.copyFromId) {
          <mat-form-field appearance="outline"><mat-label>Add to every month</mat-label>
            <input matInput type="number" formControlName="increasePercent" step="0.5" /><span matTextSuffix>&nbsp;%</span>
            <mat-error>{{ server(form.get('increasePercent')) || 'The increase is −50 % to 100 %.' }}</mat-error></mat-form-field>
        }
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="new-budget-form" [disabled]="busy()">Make the budget</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: var(--space-1); width: min(420px, 84vw); } .hint { color: var(--ro-mute); } .err { color: var(--ro-error); }`,
})
export class NewBudgetDialog {
  data = inject<NewBudgetData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<NewBudgetDialog, Budget>);
  private api = inject(ExpensesApi);
  server = server; inr = inr; label = yearLabel;
  busy = signal(false);
  error = signal('');
  form = inject(FormBuilder).group({
    copyFromId: [this.data.budgets[0]?.id ?? (null as string | null)],
    increasePercent: [0, [Validators.min(-50), Validators.max(100)]],
  });

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.error.set('');
    this.api.createBudget(this.data.propertyId, {
      year: this.data.year, name: null, note: null, copyFromId: v.copyFromId, increasePercent: v.copyFromId ? Number(v.increasePercent) || 0 : 0,
    }).subscribe({
      next: b => this.ref.close(b),
      error: err => { this.busy.set(false); showError(this.form, err, m => this.error.set(m)); },
    });
  }
}

export interface BudgetRowData { propertyId: string; budget: Budget; costCentreId: string; }

/** One department's plan for the year: the same every month (a yearly amount, spread), or month by month. */
@Component({
  selector: 'app-budget-row-dialog',
  imports: [ReactiveFormsModule, MatDialogModule, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>{{ row.costCentreName }}, {{ data.budget.label }}</h2>
    <form mat-dialog-content [formGroup]="form" (ngSubmit)="save()" id="budget-row-form">
      <mat-button-toggle-group [value]="mode()" (change)="mode.set($event.value)" aria-label="How to plan">
        <mat-button-toggle value="year">Same every month</mat-button-toggle>
        <mat-button-toggle value="month">Month by month</mat-button-toggle>
      </mat-button-toggle-group>
      @if (mode() === 'year') {
        <mat-form-field appearance="outline"><mat-label>For the whole year</mat-label>
          <span matTextPrefix>₹&nbsp;</span><input matInput type="number" inputmode="decimal" [formControl]="annual" min="0" step="1" />
          <mat-hint>{{ inr(spread(+annual.value! || 0)[0]) }} a month{{ marchNote() }}</mat-hint>
          <mat-error>The year's budget is ₹0 or more.</mat-error></mat-form-field>
      } @else {
        <div class="months">
          @for (c of months.controls; track $index; let i = $index) {
            <mat-form-field appearance="outline"><mat-label>{{ monthShort(data.budget.months[i], true) }}</mat-label>
              <span matTextPrefix>₹&nbsp;</span><input matInput type="number" inputmode="decimal" [formControl]="c" min="0" />
              <mat-error>{{ server(c) || '₹0 or more.' }}</mat-error></mat-form-field>
          }
        </div>
        <p class="total">Year: {{ inr(monthTotal()) }}</p>
      }
      @if (error()) { <p class="err" role="alert">{{ error() }}</p> }
    </form>
    <div mat-dialog-actions align="end">
      <button mat-button mat-dialog-close type="button">Cancel</button>
      <button mat-flat-button type="submit" form="budget-row-form" [disabled]="busy()">Save</button>
    </div>
  `,
  styles: `form { display: flex; flex-direction: column; gap: 10px; width: min(560px, 86vw); } .err { color: var(--ro-error); } .total { font-weight: 600; margin: 0; }
           .months { display: grid; grid-template-columns: repeat(3, 1fr); gap: 0 var(--space-2); }
           @media (max-width: 520px) { .months { grid-template-columns: repeat(2, 1fr); } }`,
})
export class BudgetRowDialog {
  data = inject<BudgetRowData>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<BudgetRowDialog, Budget>);
  private api = inject(ExpensesApi);
  server = server; inr = inr; spread = spread; monthShort = monthShort;
  row = this.data.budget.rows.find(r => r.costCentreId === this.data.costCentreId)!;
  /** Months that are all the same (or empty) open as "same every month". */
  mode = signal<'year' | 'month'>(this.row.months.slice(0, 11).every(m => m === this.row.months[0]) ? 'year' : 'month');
  annual = new FormControl<number | null>(this.row.total, [Validators.required, Validators.min(0)]);
  months = new FormArray(this.row.months.map(m => new FormControl<number | null>(m, [Validators.required, Validators.min(0)])));
  form = new FormGroup({ annual: this.annual, months: this.months });
  private monthValues = toSignal(this.months.valueChanges, { initialValue: this.months.value });
  private annualValue = toSignal(this.annual.valueChanges, { initialValue: this.annual.value });
  monthTotal = computed(() => this.monthValues().reduce((s: number, m) => s + (Number(m) || 0), 0));
  marchNote = computed(() => {
    const s = spread(Number(this.annualValue()) || 0);
    return s[11] !== s[0] ? `, ${inr(s[11])} in March` : '';
  });
  busy = signal(false);
  error = signal('');

  save() {
    const yearly = this.mode() === 'year';
    if (yearly ? this.annual.invalid : this.months.invalid) { this.annual.markAsTouched(); this.months.markAllAsTouched(); return; }
    const amounts = yearly ? spread(Number(this.annual.value) || 0) : this.months.value.map(m => Number(m) || 0);
    this.busy.set(true);
    this.error.set('');
    this.api.saveRow(this.data.propertyId, this.data.budget, this.row.costCentreId, amounts).subscribe({
      next: b => this.ref.close(b),
      error: err => {
        this.busy.set(false);
        const p = toProblem(err);
        const errs = p.code === 'VALIDATION_FAILED' ? Object.entries(p.errors ?? {}) : [];
        for (const [k, v] of errs) {
          const i = /Months\[(\d+)\]/.exec(k)?.[1];
          if (i !== undefined && !yearly) { this.months.at(+i).setErrors({ server: v[0] }); this.months.at(+i).markAsTouched(); }
        }
        this.error.set(errs.length ? errs.map(([, v]) => v[0]).filter((m, i, a) => a.indexOf(m) === i).join(' ') : p.detail);
      },
    });
  }
}

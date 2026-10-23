import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { BookingPaymentLinks, PayPage } from '../../core/models';
import { PayPageComponent } from '../pay/pay.page';
import { SendPaymentLinkDialog } from './payment-links-panel';
import { ALL_METHODS, METHODS, linkStatusClass, linkStatusLabel, methodLabel, onlineMethodText, payUrl } from './payments.api';

describe('Razorpay helpers (Sprint 11)', () => {
  it('keeps Online out of the desk methods but in the filters', () => {
    expect(METHODS).not.toContain('ONLINE');
    expect(ALL_METHODS).toContain('ONLINE');
    expect(methodLabel('ONLINE')).toBe('Online (Razorpay)');
  });

  it('says how an online payment was made', () => {
    expect(onlineMethodText('upi')).toBe('Online (Razorpay), by UPI');
    expect(onlineMethodText('netbanking')).toBe('Online (Razorpay), by net banking');
    expect(onlineMethodText(null)).toBe('Online (Razorpay)');
  });

  it('names link states and builds the full link', () => {
    expect(linkStatusLabel('CREATED')).toBe('Waiting for the guest');
    expect(linkStatusClass('PAID')).toBe('ok');
    expect(payUrl('/pay/abc', 'https://desk.example.in')).toBe('https://desk.example.in/pay/abc');
  });
});

describe('Send payment link dialog', () => {
  const links: BookingPaymentLinks = {
    reservationId: 'r1', reference: 'R260001', due: 10000, paid: 2000, owed: 8000, open: 3000, canCreate: true, cantCreateReason: null,
    fake: true, links: [],
  };
  let http: HttpTestingController;
  let close: jasmine.Spy;

  function create() {
    close = jasmine.createSpy('close');
    TestBed.configureTestingModule({
      imports: [SendPaymentLinkDialog],
      providers: [provideHttpClient(), provideHttpClientTesting(),
        { provide: MAT_DIALOG_DATA, useValue: { propertyId: 'p1', links } }, { provide: MatDialogRef, useValue: { close } }],
    });
    http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(SendPaymentLinkDialog);
    f.detectChanges();
    return f;
  }

  it('suggests what is left after open links and refuses more, saying why', () => {
    const c = create().componentInstance;
    expect(c.form.controls.amount.value).toBe(5000);
    c.form.controls.amount.setValue(6000);
    c.save();
    expect(c.amountMessage()).toBe('The link can be for at most ₹5,000.00: what R260001 still owes, less open links.');
    c.form.controls.amount.setValue(0.5);
    expect(c.amountMessage()).toContain('at least ₹1');
    http.expectNone(() => true);
  });

  it('refuses a card number in the note before sending', () => {
    const c = create().componentInstance;
    c.form.controls.note.setValue('card 4111 1111 1111 1111');
    c.save();
    expect(c.noteMessage()).toContain('looks like a card number');
    http.expectNone(() => true);
  });

  it('sends one Idempotency-Key and puts the API message under the field', () => {
    const c = create().componentInstance;
    c.save();
    const req = http.expectOne('/api/v1/properties/p1/payments/razorpay/orders');
    expect(req.request.headers.get('Idempotency-Key')).toBeTruthy();
    expect(req.request.body).toEqual({ reservationId: 'r1', amount: 5000, note: null });
    req.flush({ code: 'VALIDATION_FAILED', message: 'Check the amount.', details: { Amount: ['R260001 owes ₹4,000.00, so the link can be for at most that.'] } },
      { status: 400, statusText: 'Bad Request' });
    expect(c.err('amount')).toContain('at most that');
    expect(close).not.toHaveBeenCalled();
  });
});

describe('Guest pay page', () => {
  const page: PayPage = {
    propertyName: 'Coorg Hills', bookingReference: 'R260001', guestName: 'Asha Rao', checkIn: '2026-11-10', checkOut: '2026-11-12', amount: 5000,
    currency: 'INR', status: 'AWAITING_PAYMENT', message: 'Coorg Hills asks you to pay ₹5,000.00 for booking R260001.', keyId: 'rzp_test_fake',
    gatewayOrderId: 'order_1', fake: true, lastError: null, paymentNumber: null, paidAt: null,
  };
  let http: HttpTestingController;

  function create() {
    TestBed.configureTestingModule({
      imports: [PayPageComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(PayPageComponent);
    f.componentRef.setInput('token', 'tok');
    f.detectChanges();
    return f;
  }

  it('offers test buttons with the fake Razorpay, and shows the paid state', () => {
    const f = create();
    http.expectOne('/api/v1/pay/tok').flush(page);
    f.detectChanges();
    const el: HTMLElement = f.nativeElement;
    expect(el.querySelector('[data-testid=test-mode]')).toBeTruthy();
    f.componentInstance.fakePay(true);
    const req = http.expectOne('/api/v1/pay/tok/fake-checkout');
    expect(req.request.body).toEqual({ succeed: true, method: 'upi' });
    req.flush({ ...page, status: 'PAID', message: 'Paid. Thank you!', paymentNumber: 'P00001' });
    f.detectChanges();
    expect(el.querySelector('[data-status=PAID]')?.textContent).toContain('Paid. Thank you!');
  });

  it('checks again while Razorpay is confirming, and stops when paid', fakeAsync(() => {
    const f = create();
    http.expectOne('/api/v1/pay/tok').flush({ ...page, status: 'CONFIRMING', message: 'Confirming…' });
    tick(3000);
    http.expectOne('/api/v1/pay/tok').flush({ ...page, status: 'PAID', message: 'Paid.' });
    tick(20000);
    http.expectNone('/api/v1/pay/tok');
    f.destroy();
  }));

  it('shows the reason a link is not valid', () => {
    const f = create();
    http.expectOne('/api/v1/pay/tok').flush({ code: 'NOT_FOUND', message: "This payment link isn't valid. Check you opened the whole link, or ask the resort for a new one." },
      { status: 404, statusText: 'Not Found' });
    f.detectChanges();
    expect((f.nativeElement as HTMLElement).textContent).toContain("This payment link isn't valid");
  });
});

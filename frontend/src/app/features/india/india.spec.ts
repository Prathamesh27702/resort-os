import { FormCField, OrderBill, deadlineWords, urgency } from './india.api';
import { portalValue, sections } from './form-c-sheet.page';
import { amount, billHtml, kotHtml } from './receipt';

const TZ = 'Asia/Kolkata';
const bill = (over: Partial<OrderBill> = {}): OrderBill => ({
  property: { name: 'Palm Grove <Beach> Resort', legalName: 'Demo Resorts Pvt Ltd', addressLines: ['Beach Road', 'Malvan 416606'], phone: '+91 98200 00000',
              gstin: '27AABCT1234C1Z9', fssaiLicenceNo: '10020099000456' },
  orderId: 'o1', number: 'O00012', where: 'Table T4', orderType: 'DINE_IN', businessDate: '2026-11-14', printedAt: '2026-11-14T08:15:00Z', covers: 2,
  waiterName: 'Ravi', guestName: null,
  lines: [{ name: 'Paneer Tikka', detail: 'Full, Less spicy', quantity: 2, unitPrice: 280, amount: 560, foodType: 'VEG' },
          { name: 'Butter Naan', detail: null, quantity: 4, unitPrice: 60, amount: 240, foodType: 'VEG' }],
  subtotal: 800, discountAmount: 80, discountLabel: 'Discount 10%', net: 720,
  taxes: [{ code: 'CGST', rate: 2.5, taxableValue: 720, amount: 18 }, { code: 'SGST', rate: 2.5, taxableValue: 720, amount: 18 }],
  taxAmount: 36, total: 123756, taxNote: null, sacCode: '996331', status: 'OPEN', paidBy: null,
  upi: { upiId: 'palmgrove@okhdfcbank', payeeName: 'Palm Grove', amount: 123756, note: 'Bill O00012', link: 'upi://pay?pa=x', qrPng: 'data:image/png;base64,AAAA' },
  ...over,
});

describe('the printed restaurant bill', () => {
  it('carries the seller, GSTIN, FSSAI number, each dish, each tax, the total and the UPI QR', () => {
    const h = billHtml(bill(), TZ);
    expect(h).toContain('GSTIN: 27AABCT1234C1Z9');
    expect(h).toContain('FSSAI Lic. No. 10020099000456');
    expect(h).toContain('Bill no. <b>O00012</b>');
    expect(h).toContain('14 Nov 2026, 13:45');                       // the property's time, not the computer's
    expect(h).toContain('Table T4, 2 guests');
    expect(h).toContain('Paneer Tikka<small>Full, Less spicy</small>');
    expect(h).toContain('Discount 10%');
    expect(h).toContain('CGST 2.5%');
    expect(h).toContain('₹1,23,756.00');                             // Indian grouping
    expect(h).toContain('SAC 996331');
    expect(h).toContain('<img src="data:image/png;base64,AAAA" alt="UPI QR" />');
    expect(h).toContain('Scan to pay ₹1,23,756.00 by UPI');
  });

  it('escapes what people typed, so a name can never become markup on the paper', () => {
    const h = billHtml(bill(), TZ);
    expect(h).toContain('Palm Grove &lt;Beach&gt; Resort');
    expect(h).not.toContain('<Beach>');
  });

  it('a settled bill says how it was paid and asks for nothing; no FSSAI line when the property has none', () => {
    const b = bill({ status: 'SETTLED', paidBy: 'Paid by UPI (ref 4123)', upi: null });
    b.property.fssaiLicenceNo = null;
    const h = billHtml(b, TZ);
    expect(h).toContain('Paid by UPI (ref 4123)');
    expect(h).not.toContain('Scan to pay');
    expect(h).not.toContain('FSSAI');
  });

  it('a bill without GST says so', () => {
    expect(billHtml(bill({ taxes: [], taxAmount: 0, taxNote: 'Composition' }), TZ)).toContain('No GST charged');
  });

  it('formats rupees with two decimals', () => expect(amount(60)).toBe('60.00'));
});

describe('the printed kitchen ticket', () => {
  it('shows what to cook, the notes and the count without voided lines', () => {
    const h = kotHtml({
      number: 'K00031', orderNumber: 'O00012', where: 'Table T4', sentAt: '2026-11-14T08:15:00Z', sentByName: 'Ravi', note: 'Birthday', reprint: true,
      items: [{ quantity: 2, name: 'Paneer Tikka', modifiers: 'Less spicy', note: 'no onion', voided: false },
              { quantity: 1, name: 'Naan', modifiers: null, note: null, voided: true }],
    }, TZ);
    expect(h).toContain('KITCHEN TICKET');
    expect(h).toContain('(reprint)');
    expect(h).toContain('<b>2 ×</b> Paneer Tikka<small>Less spicy</small><small class="note">** no onion **</small>');
    expect(h).toContain('<li class="void">');
    expect(h).toContain('Note: Birthday');
    expect(h).toContain('2 item(s)');
    expect(h).not.toContain('₹');
  });
});

describe('Form C helpers', () => {
  it('says how long is left in words', () => {
    expect(deadlineWords(23.6)).toBe('23 h left');
    expect(deadlineWords(0.5)).toBe('30 min left');
    expect(deadlineWords(-0.01)).toBe('1 min overdue');
    expect(deadlineWords(-6.2)).toBe('6 h overdue');
  });

  it('colours by urgency: late past 24 hours, soon in the last 6, done when filed', () => {
    expect(urgency({ status: 'PENDING', hoursLeft: 20 })).toBe('fine');
    expect(urgency({ status: 'PENDING', hoursLeft: 6 })).toBe('soon');
    expect(urgency({ status: 'PENDING', hoursLeft: 0 })).toBe('late');
    expect(urgency({ status: 'SUBMITTED', hoursLeft: -50 })).toBe('done');
  });

  it('copies dates the way the FRRO portal takes them and everything else as typed', () => {
    expect(portalValue('date', '2031-01-14')).toBe('14/01/2031');
    expect(portalValue('text', '2031-01-14')).toBe('2031-01-14');
    expect(portalValue('time', '14:30')).toBe('14:30');
    expect(portalValue('date', null)).toBe('');
  });

  it('groups the fields into their sections in the portal’s order', () => {
    const f = (key: string, section: string) => ({ key, section } as FormCField);
    expect(sections([f('a', 'Personal details'), f('b', 'Personal details'), f('c', 'Passport')]).map(s => [s.title, s.fields.length]))
      .toEqual([['Personal details', 2], ['Passport', 1]]);
  });
});

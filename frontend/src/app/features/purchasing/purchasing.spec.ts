import { PurchaseOrder } from '../../core/models';
import { ORDER_STATUS_LABEL, lineTotals, orderActions, remapLines } from './purchasing.api';

const order = (status: PurchaseOrder['status'], toBill = 0, submittedBy: string | null = null) =>
  ({ status, submittedBy, lines: [{ toBill }] } as unknown as PurchaseOrder);
const all = () => true;
const only = (...perms: string[]) => (p: string) => perms.includes(p);

describe('purchasing helpers', () => {
  it('works a line out like the server: CGST and SGST halves rounded each, IGST in one', () => {
    expect(lineTotals(15, 42.5, 5, true)).toEqual({ taxable: 637.5, tax: 31.88 });   // 15.94 + 15.94
    expect(lineTotals(15, 42.5, 5, false)).toEqual({ taxable: 637.5, tax: 31.88 });
    expect(lineTotals(3, 33.33, 18, true)).toEqual({ taxable: 99.99, tax: 18 });     // 9 + 9
    expect(lineTotals(3, 33.33, 18, false)).toEqual({ taxable: 99.99, tax: 18 });
    expect(lineTotals(100, 2, 0, true)).toEqual({ taxable: 200, tax: 0 });
  });

  it('offers only the steps the status and the person’s permissions allow', () => {
    expect(orderActions(order('DRAFT'), all, 'me')).toEqual(['edit', 'submit', 'cancel']);
    expect(orderActions(order('PENDING_APPROVAL', 0, 'other'), all, 'me')).toEqual(['approve', 'reject', 'cancel']);
    expect(orderActions(order('APPROVED'), only('grn.create'), 'me')).toEqual(['receive']);
    expect(orderActions(order('PARTLY_RECEIVED', 4), all, 'me')).toEqual(['receive', 'bill', 'close']);
    expect(orderActions(order('RECEIVED', 0), all, 'me')).toEqual([]);
    expect(orderActions(order('DRAFT'), only('purchase.read'), 'me')).toEqual([]);
  });

  it('does not offer approval to the person who submitted the order', () => {
    expect(orderActions(order('PENDING_APPROVAL', 0, 'me'), all, 'me')).toEqual(['reject', 'cancel']);
  });

  it('puts a line error back on the form row it came from', () => {
    expect(remapLines({ 'lines[0].quantity': 'Too many', 'lines[1].unitPrice': 'Enter a price', supplierId: 'Choose one' }, [0, 2]))
      .toEqual({ 'lines[0].quantity': 'Too many', 'lines[2].unitPrice': 'Enter a price', supplierId: 'Choose one' });
    expect(ORDER_STATUS_LABEL.PARTLY_RECEIVED).toBeTruthy();
  });
});

import { GSTIN_PATTERN, creditTax, documentLabel, fileName, invoiceStatusClass, invoiceStatusLabel, lineTaxText, pdfName } from './invoices.api';

const roomNight = { taxes: [{ code: 'CGST' as const, rate: 2.5, taxableValue: 3500, amount: 87.5 }, { code: 'SGST' as const, rate: 2.5, taxableValue: 3500, amount: 87.5 }] };

describe('invoice helpers (Sprint 12)', () => {
  it('names the PDF after the document number, as the server does', () => {
    expect(pdfName('BEACH/2627/00001')).toBe('BEACH-2627-00001.pdf');
    expect(pdfName('BEACH/C2627/0007')).toBe('BEACH-C2627-0007.pdf');
  });

  it('reads the file name from Content-Disposition', () => {
    expect(fileName('inline; filename=BEACH-2627-00001.pdf')).toBe('BEACH-2627-00001.pdf');
    expect(fileName('attachment; filename="BEACH-2627-00001.pdf"; filename*=UTF-8\'\'BEACH-2627-00001.pdf')).toBe('BEACH-2627-00001.pdf');
    expect(fileName(null)).toBeNull();
  });

  it('says the status and the document type in words', () => {
    expect(invoiceStatusLabel('PARTLY_CREDITED')).toBe('Partly credited');
    expect(invoiceStatusClass('CANCELLED')).toBe('back');
    expect(documentLabel('BILL_OF_SUPPLY')).toBe('Bill of supply');
  });

  it('lists a line\'s taxes with their rates', () => {
    expect(lineTaxText(roomNight)).toBe('CGST 2.5% · SGST 2.5%');
    expect(lineTaxText({ taxes: [] })).toBe('');
  });

  it('works out the GST a part credit takes off, each tax at its own rate', () => {
    expect(creditTax(roomNight, 1000)).toBe(50);
    expect(creditTax(roomNight, 1166.67)).toBe(58.34);
    expect(creditTax(roomNight, 0)).toBe(0);
    expect(creditTax({ taxes: [] }, 300)).toBe(0);
  });

  it('checks the GSTIN format before sending (the server also checks the check digit)', () => {
    expect(GSTIN_PATTERN.test('27AAPFU0939F1ZV')).toBeTrue();
    expect(GSTIN_PATTERN.test('27AAPFU0939F1Z')).toBeFalse();
    expect(GSTIN_PATTERN.test('27aapfu0939f1zv')).toBeFalse();
  });
});

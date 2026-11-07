import { apiTime, hhmm, placeholders, preview, statusClass, unknownPlaceholders } from './messages.api';

describe('message helpers', () => {
  const vars = [
    { name: 'guest_name', description: "The guest's first name", sample: 'Asha' },
    { name: 'reference', description: 'The booking reference', sample: 'R00042' },
  ];

  it('finds each placeholder once, in order', () => {
    expect(placeholders('Hi {{guest_name}}, {{ reference }} and {{guest_name}} again')).toEqual(['guest_name', 'reference']);
    expect(placeholders('no variables')).toEqual([]);
  });

  it('names the placeholders the message cannot fill in, as the API would refuse them', () => {
    expect(unknownPlaceholders('Hi {{guest_name}}, room {{room_number}}', vars)).toEqual(['room_number']);
    expect(unknownPlaceholders('Hi {{guest_name}}', vars)).toEqual([]);
  });

  it('previews with the samples and leaves an unknown one as typed', () => {
    expect(preview('Hello {{guest_name}}, booking {{reference}}. {{oops}}', vars)).toBe('Hello Asha, booking R00042. {{oops}}');
  });

  it('turns times both ways and colours statuses', () => {
    expect(hhmm('10:00:00')).toBe('10:00');
    expect(apiTime('08:30')).toBe('08:30:00');
    expect(apiTime('08:30:00')).toBe('08:30:00');
    expect([statusClass('READ'), statusClass('DELIVERED'), statusClass('FAILED'), statusClass('SKIPPED'), statusClass('QUEUED')])
      .toEqual(['ok', 'ok', 'bad', 'off', 'wait']);
  });
});

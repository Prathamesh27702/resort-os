import { Injectable, inject } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { Observable, catchError, of, switchMap, throwError } from 'rxjs';
import { toProblem } from '../../core/http';
import { Reservation } from '../../core/models';
import {
  AssignRoomData, AssignRoomDialog, CheckInProblemData, CheckInProblemDialog, CheckInProblemResult, CheckOutData, CheckOutDialog,
} from './front-desk-dialogs';
import { DeskStay, FrontDeskApi } from './front-desk.api';

/**
 * The front desk's three actions, shared by the front desk lists and the booking page. Check-in is one click when
 * nothing is in the way; when the room isn't ready or is still occupied, the reason is shown with what can be done:
 * choose another room (then check in), or check in anyway with a reason. Each returns the updated booking, or null
 * when the user closed the dialog. API errors that no dialog handles are passed on for the page to show.
 */
@Injectable({ providedIn: 'root' })
export class DeskActions {
  private api = inject(FrontDeskApi);
  private dialog = inject(MatDialog);

  checkIn(propertyId: string, stay: DeskStay, today: string): Observable<Reservation | null> {
    return this.api.checkIn(propertyId, stay.reservationId, { version: null }).pipe(
      catchError(e => {
        const p = toProblem(e);
        if (p.code !== 'HOUSEKEEPING_NOT_READY' && p.code !== 'ROOM_OCCUPIED') return throwError(() => e);
        return this.dialog.open<CheckInProblemDialog, CheckInProblemData, CheckInProblemResult>(CheckInProblemDialog,
          { data: { propertyId, stay, code: p.code, message: p.detail } }).afterClosed().pipe(
          switchMap(result => {
            if (result !== 'assign') return of(result ?? null);
            // another room first, then check in again (which may still ask, e.g. if the chosen room isn't ready)
            return this.assignRoom(propertyId, stay, today).pipe(
              switchMap(r => r ? this.checkIn(propertyId, { ...stay, version: r.version, rooms: roomsOf(r) }, today) : of(null)));
          }));
      }));
  }

  assignRoom(propertyId: string, stay: DeskStay, today: string): Observable<Reservation | null> {
    return this.dialog.open<AssignRoomDialog, AssignRoomData, Reservation>(AssignRoomDialog, { data: { propertyId, stay, today } })
      .afterClosed().pipe(switchMap(r => of(r ?? null)));
  }

  checkOut(propertyId: string, stay: DeskStay, today: string): Observable<Reservation | null> {
    return this.dialog.open<CheckOutDialog, CheckOutData, Reservation>(CheckOutDialog, { data: { propertyId, stay, today } })
      .afterClosed().pipe(switchMap(r => of(r ?? null)));
  }
}

function roomsOf(r: Reservation): DeskStay['rooms'] {
  return r.units.filter(u => u.current && u.endDate === r.checkOut)
    .map(u => ({ reservationUnitId: u.id, unitName: u.unitName, bedCode: u.bedCode, housekeeping: null }));
}

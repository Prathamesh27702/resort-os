import { Component, inject } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { CanDirective } from '../../../shared/can.directive';
import { PropertyContext } from '../../../core/property-context';
import { GST_STATES } from '../../../core/models';

@Component({
  selector: 'app-properties',
  imports: [RouterLink, MatButtonModule, CanDirective],
  template: `
    <div class="page">
      <div class="page-head">
        <div><h1>Properties</h1><p>Each property keeps its own timezone, GST state and check-in times.</p></div>
        <a mat-flat-button routerLink="/settings/properties/new" *appCan="'property.create'; property: null">Add property</a>
      </div>
      <div class="panel">
        <table class="data">
          <thead><tr><th>Name</th><th>Code</th><th class="hide-sm">Location</th><th class="hide-sm">Timezone</th><th>Status</th></tr></thead>
          <tbody>
            @for (p of props.properties(); track p.id) {
              <tr class="clickable" (click)="open(p.id)" tabindex="0" (keydown.enter)="open(p.id)">
                <td><b>{{ p.name }}</b></td>
                <td class="num">{{ p.code }}</td>
                <td class="hide-sm">{{ location(p) }}</td>
                <td class="hide-sm">{{ p.timezone }}</td>
                <td>@if (p.isActive) { <span class="chip">Active</span> } @else { <span class="chip off">Inactive</span> }</td>
              </tr>
            } @empty {
              <tr><td colspan="5">No properties you can see yet.</td></tr>
            }
          </tbody>
        </table>
      </div>
    </div>
  `,
})
export class PropertiesPage {
  props = inject(PropertyContext);
  private router = inject(Router);
  constructor() { this.props.load(); }
  open(id: string) { this.router.navigateByUrl(`/settings/properties/${id}`); }
  location(p: { city: string | null; gstStateCode: string | null }) {
    return [p.city, this.stateName(p.gstStateCode)].filter(x => !!x).join(', ') || '—';
  }
  stateName(code: string | null) { return GST_STATES.find(s => s.code === code)?.name ?? null; }
}

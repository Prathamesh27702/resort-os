import { Directive, Input, TemplateRef, ViewContainerRef, effect, inject, signal } from '@angular/core';
import { AuthService } from '../core/auth.service';

/**
 * Shows content only if the user has the permission (org-wide or for `propertyId` when given).
 *   <button *appCan="'user.create'">New user</button>
 *   <button *appCan="'property.update'; property: p.id">Edit</button>
 * This is display only — the server is the authority.
 */
@Directive({ selector: '[appCan]' })
export class CanDirective {
  private tpl = inject(TemplateRef<unknown>);
  private vcr = inject(ViewContainerRef);
  private auth = inject(AuthService);
  private perm = signal('');
  private prop = signal<string | null | undefined>(undefined);
  private shown = false;

  @Input() set appCan(p: string) { this.perm.set(p); }
  @Input() set appCanProperty(id: string | null | undefined) { this.prop.set(id); }

  constructor() {
    effect(() => {
      this.auth.me();
      const ok = this.prop() === undefined ? this.auth.canAnywhere(this.perm()) : this.auth.can(this.perm(), this.prop());
      if (ok && !this.shown) { this.vcr.createEmbeddedView(this.tpl); this.shown = true; }
      if (!ok && this.shown) { this.vcr.clear(); this.shown = false; }
    });
  }
}

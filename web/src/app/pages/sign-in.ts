import { Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { ErrorBanner } from '../shared/ui';

@Component({
  selector: 'app-sign-in',
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatIconModule, MatProgressBarModule, ErrorBanner],
  template: `
    <div class="wrap">
      <form class="card" (ngSubmit)="submit()">
        <div class="brand"><img src="favicon.svg" alt="" width="40" height="40"><div><h1>Wings Git Manager</h1><div class="muted">Repository management, recovery and backups for Wings Code</div></div></div>
        @if (expired()) { <div class="callout warn">Your session expired or the token was rejected. Sign in again.</div> }
        <mat-form-field class="full">
          <mat-label>API token</mat-label>
          <input matInput [type]="show() ? 'text' : 'password'" name="token" [(ngModel)]="token" placeholder="wgm_…" autocomplete="off" required autofocus>
          <button mat-icon-button matSuffix type="button" (click)="show.set(!show())" [attr.aria-label]="show() ? 'Hide token' : 'Show token'">
            <mat-icon>{{ show() ? 'visibility_off' : 'visibility' }}</mat-icon>
          </button>
          <mat-hint>Kept only in this browser tab (sessionStorage) until you sign out or close it.</mat-hint>
        </mat-form-field>
        @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
        <app-error [error]="error()" title="Sign-in failed:" />
        <button mat-flat-button class="full" type="submit" [disabled]="!token.trim() || busy()">Sign in</button>
        <p class="small muted">Ask an administrator for a personal API token (Administration → API tokens). Tokens are validated with <code>GET /api/v1/me</code>.</p>
      </form>
    </div>`,
  styles: [`
    .wrap { min-height: 100vh; display: grid; place-items: center; padding: 16px; background: var(--canvas); }
    .card { width: min(440px, 100%); background: var(--surface); border: 1px solid var(--border); border-radius: 12px; padding: 24px; display: flex; flex-direction: column; gap: 12px; }
    .brand { display: flex; gap: 12px; align-items: center; margin-bottom: 8px; }`],
})
export class SignIn {
  private auth = inject(AuthService);
  private router = inject(Router);
  returnUrl = input<string>();
  expired = input<string>();
  token = '';
  show = signal(false);
  busy = signal(false);
  error = signal<unknown>(null);

  submit() {
    this.busy.set(true);
    this.error.set(null);
    this.auth.signIn(this.token).subscribe({
      next: () => { this.busy.set(false); this.router.navigateByUrl(this.returnUrl() || '/dashboard'); },
      error: e => { this.busy.set(false); this.error.set(e); },
    });
  }
}

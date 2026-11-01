import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Observable, catchError, firstValueFrom, tap, throwError } from 'rxjs';
import { ApiService } from './api.service';
import { Me } from './models';

const TOKEN_KEY = 'wgm.token';

function readToken(): string | null {
  try { return sessionStorage.getItem(TOKEN_KEY); } catch { return null; }
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private api = inject(ApiService);
  private router = inject(Router);

  readonly token = signal<string | null>(readToken());
  readonly me = signal<Me | null>(null);
  readonly signedIn = computed(() => !!this.token() && !!this.me());
  readonly isAdmin = computed(() => !!this.me()?.isSystemAdministrator);
  readonly canAdminister = computed(() => {
    const m = this.me();
    return !!m && (m.isSystemAdministrator || m.memberships.some(x => x.role === 'CompanyAdministrator'));
  });

  /** App initializer: re-validates a token kept in sessionStorage (never blocks start-up on failure). */
  async init(): Promise<void> {
    if (!this.token()) return;
    try { this.me.set(await firstValueFrom(this.api.me())); }
    catch { this.clear(); }
  }

  /** Validates a pasted token with GET /me and keeps it for this browser session. */
  signIn(token: string): Observable<Me> {
    this.token.set(token.trim());
    return this.api.me().pipe(
      tap(me => {
        try { sessionStorage.setItem(TOKEN_KEY, token.trim()); } catch { /* storage unavailable: keep in memory */ }
        this.me.set(me);
      }),
      catchError(err => { this.clear(); return throwError(() => err); }),
    );
  }

  signOut(): void {
    this.clear();
    this.router.navigate(['/sign-in']);
  }

  /** Called by the interceptor on 401. */
  expired(): void {
    if (!this.token()) return;
    this.clear();
    const returnUrl = this.router.url.startsWith('/sign-in') ? undefined : this.router.url;
    this.router.navigate(['/sign-in'], { queryParams: { returnUrl, expired: 1 } });
  }

  private clear() {
    try { sessionStorage.removeItem(TOKEN_KEY); } catch { /* ignore */ }
    this.token.set(null);
    this.me.set(null);
  }
}

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const token = auth.token();
  const isApi = req.url.startsWith('/api/') || req.url.startsWith('/hubs/');
  const r = token && isApi ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;
  return next(r).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse && err.status === 401 && isApi && !req.url.endsWith('/me')) auth.expired();
      return throwError(() => err);
    }),
  );
};

export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  if (auth.signedIn()) return true;
  return inject(Router).createUrlTree(['/sign-in'], { queryParams: { returnUrl: state.url } });
};

export const adminGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return auth.canAdminister() ? true : inject(Router).createUrlTree(['/dashboard']);
};

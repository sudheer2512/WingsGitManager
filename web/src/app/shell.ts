import { BreakpointObserver } from '@angular/cdk/layout';
import { Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatMenuModule } from '@angular/material/menu';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatBadgeModule } from '@angular/material/badge';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { map } from 'rxjs';
import { AuthService } from './core/auth.service';
import { RealtimeService, describeNotification } from './core/realtime.service';
import { UI } from './shared/ui';

interface NavItem { path: string; label: string; icon: string; admin?: boolean; }

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatSidenavModule, MatToolbarModule, MatListModule, MatIconModule, MatButtonModule,
    MatMenuModule, MatTooltipModule, MatDividerModule, MatSlideToggleModule, MatBadgeModule, ...UI],
  template: `
    <mat-sidenav-container class="shell">
      <mat-sidenav #nav [mode]="mobile() ? 'over' : 'side'" [opened]="!mobile()" class="nav">
        <a class="brand" routerLink="/dashboard">
          <img src="favicon.svg" alt="" width="28" height="28">
          <span><b>Wings</b> Git Manager</span>
        </a>
        <mat-nav-list>
          @for (item of main; track item.path) {
            <a mat-list-item [routerLink]="item.path" routerLinkActive="active" (click)="mobile() && nav.close()">
              <mat-icon matListItemIcon>{{ item.icon }}</mat-icon><span matListItemTitle>{{ item.label }}</span>
            </a>
          }
          @if (auth.canAdminister()) {
            <div class="section">Administration</div>
            @for (item of admin; track item.path) {
              <a mat-list-item [routerLink]="item.path" routerLinkActive="active" (click)="mobile() && nav.close()">
                <mat-icon matListItemIcon>{{ item.icon }}</mat-icon><span matListItemTitle>{{ item.label }}</span>
              </a>
            }
          }
        </mat-nav-list>
        <div class="foot small">
          <a href="/api-docs" target="_blank" rel="noopener">API reference</a>
        </div>
      </mat-sidenav>
      <mat-sidenav-content class="content">
        <mat-toolbar class="bar">
          @if (mobile()) { <button mat-icon-button (click)="nav.toggle()" aria-label="Menu"><mat-icon>menu</mat-icon></button> }
          <span class="spacer"></span>
          <span class="live" [matTooltip]="rt.connected() ? 'Real-time updates connected' : 'Real-time updates disconnected (retrying)'">
            <span class="dot" [class.on]="rt.connected()"></span>{{ rt.connected() ? 'Live' : 'Offline' }}
          </span>
          <button mat-icon-button [matMenuTriggerFor]="events" aria-label="Recent events" matTooltip="Recent events">
            <mat-icon aria-hidden="false" aria-label="Recent events" [matBadge]="rt.recent().length || null" matBadgeSize="small">notifications</mat-icon>
          </button>
          <mat-menu #events="matMenu" class="events-menu">
            <div class="menu-head" (click)="$event.stopPropagation()">
              <mat-slide-toggle [checked]="rt.toastsEnabled()" (change)="rt.toastsEnabled.set($event.checked)">Pop-up notifications</mat-slide-toggle>
            </div>
            <mat-divider />
            @for (n of rt.recent().slice(0, 15); track n.id) {
              <a mat-menu-item [routerLink]="n.repositoryId ? ['/repositories', n.repositoryId] : null">
                <span class="small">{{ describe(n) }}</span><br><span class="small muted">{{ n.createdAt | ago }}</span>
              </a>
            } @empty { <div class="small muted" style="padding:12px 16px">No events since you signed in</div> }
          </mat-menu>
          <button mat-button [matMenuTriggerFor]="user" class="user-btn">
            <mat-icon>account_circle</mat-icon>
            <span class="uname">{{ auth.me()?.displayName }}</span>
          </button>
          <mat-menu #user="matMenu">
            <div class="menu-head small">
              <div><b>{{ auth.me()?.displayName }}</b></div>
              <div class="muted">{{ auth.me()?.email }}</div>
              @if (auth.isAdmin()) { <div><span class="tag">System administrator</span></div> }
              @for (m of auth.me()?.memberships ?? []; track m.companyId) { <div><span class="tag">{{ m.companyName }} · {{ m.role | human }}</span></div> }
              <div class="muted" style="margin-top:4px">Scopes: {{ (auth.me()?.scopes ?? []).join(', ') || '—' }}</div>
            </div>
            <mat-divider />
            <button mat-menu-item (click)="auth.signOut()"><mat-icon>logout</mat-icon>Sign out</button>
          </mat-menu>
        </mat-toolbar>
        <main><router-outlet /></main>
      </mat-sidenav-content>
    </mat-sidenav-container>`,
  styles: [`
    .shell { height: 100vh; }
    .nav { width: var(--shell-w); background: var(--chrome); color: var(--chrome-fg); border-right: 0; }
    .brand { display: flex; align-items: center; gap: 10px; padding: 14px 16px; color: var(--chrome-fg); text-decoration: none; font-size: 15px; }
    .nav .mat-mdc-nav-list { --mat-list-list-item-label-text-color: var(--chrome-muted); --mat-list-list-item-leading-icon-color: var(--chrome-muted);
      --mat-list-list-item-hover-label-text-color: #fff; --mat-list-list-item-one-line-container-height: 40px; }
    .nav a.active { background: rgba(255,255,255,.1); --mat-list-list-item-label-text-color: #fff; --mat-list-list-item-leading-icon-color: #fff; }
    .section { padding: 14px 16px 4px; font-size: 11px; text-transform: uppercase; letter-spacing: .06em; color: var(--chrome-muted); opacity: .8; }
    .foot { position: absolute; bottom: 12px; left: 16px; } .foot a { color: var(--chrome-muted); }
    .content { background: var(--canvas); display: flex; flex-direction: column; }
    .bar { position: sticky; top: 0; z-index: 5; background: var(--surface); border-bottom: 1px solid var(--border); height: 52px; gap: 4px; font-size: 13px; }
    .live { display: inline-flex; align-items: center; gap: 6px; color: var(--muted); font-size: 12px; margin-right: 6px; }
    .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--off); } .dot.on { background: var(--good); }
    .menu-head { padding: 8px 16px; }
    .uname { max-width: 180px; overflow: hidden; text-overflow: ellipsis; display: inline-block; vertical-align: middle; }
    @media (max-width: 600px) { .uname { display: none; } }
    main { flex: 1; }`],
})
export class Shell {
  auth = inject(AuthService);
  rt = inject(RealtimeService);
  mobile = toSignal(inject(BreakpointObserver).observe('(max-width: 900px)').pipe(map(r => r.matches)), { initialValue: false });
  describe = describeNotification;
  main: NavItem[] = [
    { path: '/dashboard', label: 'Dashboard', icon: 'space_dashboard' },
    { path: '/repositories', label: 'Repositories', icon: 'source' },
    { path: '/sessions', label: 'AI sessions', icon: 'smart_toy' },
    { path: '/sync', label: 'Sync monitor', icon: 'sync' },
    { path: '/alerts', label: 'Alerts', icon: 'notification_important' },
    { path: '/system', label: 'System health', icon: 'monitor_heart' },
    { path: '/audit', label: 'Audit log', icon: 'receipt_long' },
  ];
  admin: NavItem[] = [
    { path: '/admin/companies', label: 'Companies', icon: 'domain' },
    { path: '/admin/users', label: 'Users', icon: 'group' },
    { path: '/admin/tokens', label: 'API tokens', icon: 'key' },
    { path: '/admin/policies', label: 'Policies', icon: 'policy' },
  ];
  unused = computed(() => 0);
}

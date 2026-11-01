import { HttpErrorResponse } from '@angular/common/http';
import { ApiProblem } from './models';

/** Normalises any error (problem+json, network, client) into {status, detail, code, correlationId}. */
export function toProblem(err: unknown): ApiProblem {
  if (err instanceof HttpErrorResponse) {
    const body = err.error;
    const correlationId = (body && typeof body === 'object' ? body.correlationId : null) ?? err.headers?.get('X-Correlation-Id') ?? null;
    if (body && typeof body === 'object' && (body.detail || body.title)) {
      return { status: err.status, code: body.code, detail: body.detail ?? body.title, correlationId, details: body.details };
    }
    if (err.status === 0) return { status: 0, detail: 'The Git Manager API could not be reached. Check that the service is running.', correlationId };
    if (err.status === 401) return { status: 401, detail: 'Not signed in or the token has expired.', correlationId };
    if (err.status === 403) return { status: 403, detail: 'You are not allowed to perform this action.', correlationId };
    if (err.status === 404) return { status: 404, detail: 'Not found (or not visible to you).', correlationId };
    return { status: err.status, detail: typeof body === 'string' && body ? body : err.message, correlationId };
  }
  if (err instanceof Error) return { status: -1, detail: err.message };
  return { status: -1, detail: String(err) };
}

export function problemText(err: unknown): string {
  const p = toProblem(err);
  return p.correlationId ? `${p.detail} (correlation id ${p.correlationId})` : p.detail;
}

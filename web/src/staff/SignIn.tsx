// Staff sign-in (server: Authentication/StaffSessionEndpoints.cs): the password, then an authenticator code or a
// recovery code. Staff without an authenticator set one up here first: staff must use two-step verification.
import { useEffect, useState, type FormEvent } from 'react';
import { api, ApiError } from '../shared/api';
import { Button, Status, type StatusMessage } from '../shared/components';
import { QrCode } from '../shared/QrCode';
import { siteIcon, useSiteName } from '../shared/site';
import { describe, type Session } from './StaffApp';
import { ERRORS, type StaffProfile } from './types';

type Step =
  | { kind: 'password' }
  | { kind: 'code'; challenge: string; username: string; remember: boolean }
  | { kind: 'enroll'; challenge: string; username: string; remember: boolean; secret?: string; uri?: string }
  | { kind: 'recovery'; codes: string[]; token: string; username: string; remember: boolean };

export function SignIn({ message, onSignedIn, onMessage }:
  { message: StatusMessage | null; onSignedIn: (s: Session, remember: boolean) => void; onMessage: (m: StatusMessage | null) => void }) {
  const [busy, setBusy] = useState(false);
  const [step, setStep] = useState<Step>({ kind: 'password' });
  const site = useSiteName();

  async function finish(token: string, username: string, remember: boolean) {
    const profile = await api<StaffProfile>('/api/admin/me', { token });
    onSignedIn({ token, username, profile }, remember);
  }
  function failed(error: unknown) {
    const text = error instanceof ApiError && error.status === 403 ? ERRORS.forbidden
      : error instanceof ApiError && error.status === 401 ? 'Sign-in failed. Check your details.' : describe(error);
    onMessage({ text, kind: 'error' });
  }

  // The enrolment step asks the server for a new authenticator key once.
  useEffect(() => {
    if (step.kind !== 'enroll' || step.secret) return;
    let live = true;
    api<{ secret: string; otpAuthUri: string }>('/api/admin/session/enroll', { method: 'POST', body: { challengeToken: step.challenge } })
      .then(r => { if (live) setStep({ ...step, secret: r.secret, uri: r.otpAuthUri }); })
      .catch(error => { if (live) { setStep({ kind: 'password' }); failed(error); } });
    return () => { live = false; };
  }, [step]);  // eslint-disable-line react-hooks/exhaustive-deps

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    setBusy(true);
    try {
      if (step.kind === 'password') {
        const username = String(data.get('username'));
        const remember = data.get('remember') === 'on';
        const reply = await api<{ step: 'code' | 'enroll'; challengeToken: string }>('/api/admin/session',
          { method: 'POST', body: { username, password: data.get('password'), remember } });
        if (reply.step === 'enroll') {
          setStep({ kind: 'enroll', challenge: reply.challengeToken, username, remember });
          onMessage({ text: 'Staff accounts need two-step verification. Set up an authenticator app to continue.', kind: '' });
        } else {
          setStep({ kind: 'code', challenge: reply.challengeToken, username, remember });
          onMessage(null);
        }
      } else if (step.kind === 'code') {
        const code = String(data.get('code') ?? '').trim();
        const isCode = /^[0-9]{6}$/.test(code);
        const { token } = await api<{ token: string }>('/api/admin/session/totp', {
          method: 'POST', body: { challengeToken: step.challenge, code: isCode ? code : null, recoveryCode: isCode ? null : code.toUpperCase() },
        });
        await finish(token, step.username, step.remember);
      } else if (step.kind === 'enroll') {
        const code = String(data.get('code') ?? '').trim();
        const r = await api<{ token: string; recoveryCodes: string[] }>('/api/admin/session/enroll/confirm',
          { method: 'POST', body: { challengeToken: step.challenge, code } });
        setStep({ kind: 'recovery', codes: r.recoveryCodes, token: r.token, username: step.username, remember: step.remember });
        onMessage({ text: 'Two-step verification is on.', kind: 'ok' });
      } else {
        await finish(step.token, step.username, step.remember);
      }
    } catch (error) {
      // A wrong code may be tried again (the server locks the challenge after five tries); anything else starts over.
      if (step.kind === 'code' && error instanceof ApiError && error.status === 401)
        onMessage({ text: 'That code did not work. Try again, or go back and sign in again if it keeps failing.', kind: 'error' });
      else if (step.kind === 'enroll' && error instanceof ApiError && error.status === 401)
        onMessage({ text: 'That code did not work. Check the time on your phone and try the next code.', kind: 'error' });
      else failed(error);
    } finally {
      setBusy(false);
      for (const name of ['password', 'code']) {
        const input = form.elements.namedItem(name);
        if (input instanceof HTMLInputElement) input.value = '';
      }
    }
  }

  const hint = step.kind === 'password' ? 'Use an account with a Gamemaster, Moderator or Administrator role.'
    : step.kind === 'code' ? 'Enter the code from your authenticator app, or a recovery code.'
    : step.kind === 'enroll' ? 'Scan the code with an authenticator app, then enter the 6-digit code it shows.'
    : 'Save these recovery codes somewhere safe. Each one works once if you lose your authenticator. They are shown only now.';
  const label = step.kind === 'password' ? 'Sign in' : step.kind === 'recovery' ? 'I saved them, continue'
    : step.kind === 'enroll' ? 'Turn on and sign in' : 'Verify';
  return (
    <main className="signin">
      <form className="card card-body stack signin-card" onSubmit={submit}>
        <div className="brand brand-center">
          <img className="brand-mark" src={siteIcon} alt="" />
        </div>
        <div className="stack-sm center">
          <h2>{site} staff sign-in</h2>
          <p className="muted">{hint}</p>
        </div>
        {step.kind === 'password' && (
          <>
            <div className="field">
              <label htmlFor="username">Username</label>
              <input id="username" name="username" className="input" autoComplete="username" required maxLength={32} />
            </div>
            <div className="field">
              <label htmlFor="password">Password</label>
              <input id="password" name="password" type="password" className="input" autoComplete="current-password" required maxLength={256} />
            </div>
            <label className="check">
              <input type="checkbox" name="remember" />
              <span>Keep me signed in on this computer (7 days)</span>
            </label>
          </>
        )}
        {step.kind === 'code' && (
          <div className="field">
            <label htmlFor="code">Authenticator code or recovery code</label>
            <input id="code" name="code" className="input" autoComplete="one-time-code" required maxLength={32} autoFocus />
          </div>
        )}
        {step.kind === 'enroll' && (step.secret ? (
          <>
            {step.uri && <div className="center"><QrCode value={step.uri} label="QR code for your authenticator app" /></div>}
            <div className="field">
              <span className="small muted">Can't scan it? Enter this key instead</span>
              <code className="break">{step.secret}</code>
            </div>
            <div className="field">
              <label htmlFor="code">6-digit code</label>
              <input id="code" name="code" className="input" inputMode="numeric" pattern="[0-9]{6}" autoComplete="one-time-code" required maxLength={6} autoFocus />
            </div>
          </>
        ) : <p className="muted">Preparing a key…</p>)}
        {step.kind === 'recovery' && (
          <ul className="stack-sm">{step.codes.map(c => <li key={c}><code>{c}</code></li>)}</ul>
        )}
        <Button type="submit" variant="primary" block busy={busy} disabled={step.kind === 'enroll' && !step.secret}>{label}</Button>
        {(step.kind === 'code' || step.kind === 'enroll') && (
          <Button type="button" variant="ghost" block onClick={() => { setStep({ kind: 'password' }); onMessage(null); }}>Back</Button>
        )}
        <Status message={message} />
      </form>
    </main>
  );
}

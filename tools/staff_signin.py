"""Staff dashboard sign-in for the live-host checks (server: Authentication/StaffSessionEndpoints.cs).

Staff sessions need an authenticator code. A staff account without one sets one up during the sign-in. These helpers
do the whole flow over HTTP and compute the codes (RFC 6238, SHA-1, 6 digits, 30 s) with the standard library.
"""
import base64
import hashlib
import hmac
import struct
import time


def totp_code(secret, at=None, step=30, digits=6):
    """The authenticator code of a base32 secret at a Unix time (now by default)."""
    key = base64.b32decode(secret.upper() + '=' * (-len(secret) % 8))
    counter = int((time.time() if at is None else at) // step)
    digest = hmac.new(key, struct.pack('>Q', counter), hashlib.sha1).digest()
    offset = digest[-1] & 0x0F
    value = struct.unpack('>I', digest[offset:offset + 4])[0] & 0x7FFFFFFF
    return str(value % 10 ** digits).zfill(digits)


def staff_session(request, username, password, secrets=None, remember=False):
    """A staff session token via request(path, method, body) -> (status, json, ...).

    secrets maps usernames to authenticator secrets: an account set up here is added, an account already in it signs
    in with its code. Raises RuntimeError when a step fails (wrong password, not staff, wrong code).
    """
    secrets = {} if secrets is None else secrets
    reply = request('/api/admin/session', 'POST', {'username': username, 'password': password, 'remember': remember})
    if reply[0] != 200:
        raise RuntimeError(f'staff sign-in of {username} failed: HTTP {reply[0]}')
    step, challenge = reply[1]['step'], reply[1]['challengeToken']
    if step == 'code':
        if username not in secrets:
            raise RuntimeError(f'{username} has an authenticator this check does not know')
        done = request('/api/admin/session/totp', 'POST', {'challengeToken': challenge, 'code': totp_code(secrets[username])})
    else:
        key = request('/api/admin/session/enroll', 'POST', {'challengeToken': challenge})
        if key[0] != 200:
            raise RuntimeError(f'authenticator setup of {username} failed: HTTP {key[0]}')
        secrets[username] = key[1]['secret']
        done = request('/api/admin/session/enroll/confirm', 'POST', {'challengeToken': challenge, 'code': totp_code(secrets[username])})
    if done[0] != 200:
        raise RuntimeError(f'staff code step of {username} failed: HTTP {done[0]}')
    return done[1]['token']

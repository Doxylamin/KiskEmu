import unittest

from tools.staff_signin import staff_session, totp_code

# RFC 6238 appendix B, SHA-1: the secret "12345678901234567890" in base32.
RFC_SECRET = 'GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ'


class TotpCodeTests(unittest.TestCase):
    def test_rfc_vectors(self):
        for at, code in [(59, '94287082'), (1111111109, '07081804'), (1234567890, '89005924'), (2000000000, '69279037')]:
            self.assertEqual(totp_code(RFC_SECRET, at=at, digits=8), code)

    def test_six_digits_and_unpadded_secret(self):
        self.assertEqual(totp_code(RFC_SECRET, at=59), '287082')
        self.assertEqual(totp_code(RFC_SECRET.lower(), at=59), '287082')


class StaffSessionTests(unittest.TestCase):
    def fake(self, step):
        calls = []

        def request(path, method='GET', body=None, token=None):
            calls.append((path, body))
            if path == '/api/admin/session':
                return 200, {'step': step, 'challengeToken': 'c'}
            if path == '/api/admin/session/enroll':
                return 200, {'secret': RFC_SECRET, 'otpAuthUri': 'otpauth://x'}
            return 200, {'token': 'staff-' + path.rsplit('/', 1)[-1]}
        return request, calls

    def test_enrolls_then_signs_in_with_a_code(self):
        request, calls = self.fake('enroll')
        known = {}
        self.assertEqual(staff_session(request, 'gm', 'pw', known), 'staff-confirm')
        self.assertEqual(known, {'gm': RFC_SECRET})
        self.assertEqual(calls[-1][1]['code'], totp_code(RFC_SECRET))
        request, calls = self.fake('code')
        self.assertEqual(staff_session(request, 'gm', 'pw', known), 'staff-totp')

    def test_unknown_authenticator_and_refusals_raise(self):
        request, _ = self.fake('code')
        with self.assertRaises(RuntimeError):
            staff_session(request, 'gm', 'pw', {})
        with self.assertRaises(RuntimeError):
            staff_session(lambda *a, **k: (403, {'error': 'forbidden'}), 'player', 'pw')


if __name__ == '__main__':
    unittest.main()

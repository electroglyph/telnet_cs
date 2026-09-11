# Security

This library is **experimental** (see `README.md`) and has not had a
security review. Report issues to the repository owner.

Known limitations, by design of Telnet itself:

- **No encryption.** Telnet is cleartext: credentials passed to
  `TryLoginAsync` / `AuthenticateAsync` travel unencrypted. There is no
  TLS handshake (the `StartTls` option number is not a transport).
  Terminate TLS outside the library or tunnel over SSH/stunnel.
- **No authentication framework.** `AuthenticateAsync` takes a
  caller-supplied validator and enforces an attempt budget, but
  lockout, hashing, and secret storage are the caller's job.
- **Passwords and echo.** A session that echoes (the default preset)
  sends typed passwords back down the wire; suppress echo before
  reading secrets.
- **Denial of service.** The server has no rate limiting, connection
  caps, or timeout policing beyond per-read timeouts; front it
  accordingly.

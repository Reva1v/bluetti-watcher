# Security

## Reporting

Report a vulnerability privately through GitHub's "Report a vulnerability" button on the Security tab of this
repository. Please do not open a public issue for it. Include the version (`/api/health` `version`), what an attacker
needs (a local account, a web page the user visits, a device on the network) and the steps. Expect a first answer
within a week.

## Scope

In scope:

- The HTTP API on `127.0.0.1`: the `Host` guard (DNS rebinding), the `POST /api/mode` guard (`X-Station: 1`, the
  `Origin` check, no CORS), anything that lets another origin, another user or the network read or change the state.
- The cloud token file (`cloud-token.json`): the token leaking into logs, errors, the page or the API; a write that
  could leave it readable to other users or torn.
- Read only toward the station: any path by which the service, the venv or the cloud client could send a write or
  control command to the station.
- The installer and the scheduled tasks it registers: the wake task's access list, anything that runs elevated.
- The power-mode scripts: the flag file, its mutex and the tasks they register.

Out of scope: an attacker who already runs code as the same Windows user (they can change the config, the scripts and
the data directly), and BLUETTI's cloud service itself.

## Design notes

- The service runs as the logged-on user without elevation; the API binds to `127.0.0.1` only.
- The token file lives in the user's data directory, is written atomically and never printed, logged or served.
- The Bluetooth library's write code is deleted from the venv and the service refuses to start while it is on disk.
- The cloud client's URLs are checked against a two-entry allow-list (the devices read and the token refresh).

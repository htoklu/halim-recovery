# Security Policy

## Supported versions

| Version | Supported |
|---|---|
| 0.5.x | ✅ |

## Security model

- Halim Recovery opens source volumes **read-only** and never transmits any user data
  over the network. There is no telemetry, no cloud dependency.
- Raw disk access requires Administrator rights; the app requests elevation via standard
  Windows UAC and uses it only for volume reads.
- File names and paths recovered from raw disk data are treated as **untrusted input**:
  they are sanitized (invalid characters, reserved device names, path traversal) before
  any filesystem operation, and output paths are verified to stay inside the chosen
  destination folder.
- Logs contain operational metadata only — never file contents, passwords or secrets.

## Reporting a vulnerability

Please report security issues privately by email: **htoklu1453@gmail.com**.
Do not open public issues for unpatched vulnerabilities. You should receive a response
within 7 days.

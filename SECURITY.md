# Security policy

## Supported version

Security fixes currently target the latest release only.

| Version | Supported |
|---|---|
| 0.1.x | Yes |

## Reporting a vulnerability

Do not disclose exploitable vulnerabilities in a public issue. Use the repository's private GitHub
Security Advisory reporting feature. Include affected versions, reproduction steps, impact, and any
suggested mitigation. Avoid including live database credentials, Steam Web API keys, server tokens,
or player data in reports.

SurfTimer does not require credentials in its repository or plugin directory. Database secrets belong
in SwiftlyS2's external database configuration or environment variables used by the optional website.

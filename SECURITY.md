# Security policy

## Supported version

Security fixes are made on the latest commit of the default branch.

## Deployment boundary

RTConsole is designed for local administration on Windows. Its HTTP listener is
bound to `localhost`; do not reverse-proxy or expose the panel to a LAN or the
public Internet.

The generated `rtconsole.json` contains an administration token. Keep it private,
do not commit it, and rotate the token if it is disclosed. Runtime logs, profiles,
world data, backups, and the dedicated server itself are intentionally excluded
from this repository.

Snapshot restore accepts only ZIP entries below `Assets/` and `Configs/`. Archives
are extracted and validated in an isolated staging directory before current world
data is replaced. A safety snapshot is created immediately before replacement.
Use only backups you trust.

## Reporting a vulnerability

Please use GitHub's private vulnerability reporting feature for this repository.
Do not include live tokens, player data, IP addresses, or world saves in a public
issue.

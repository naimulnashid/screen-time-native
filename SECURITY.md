# Security policy

## Reporting a vulnerability

Please report security issues **privately**, through GitHub's
**Security → Report a vulnerability** on this repository, not in a public
issue. Include what you found, how to reproduce it, and what an attacker could
do with it. You should hear back within a week.

Only the latest release is supported; there are no maintained release
branches.

## What is in scope

This is a local app with no network service and nothing that runs elevated,
so the interesting boundaries are local ones:

- **The sampler** (`ScreenTime.exe --sample`, started by the
  `Screen Time Native Sampler` task as the signed-in user). It records which
  program is in the foreground and for how long, and **never window titles**.
  Anything that makes it capture more than that - titles, window contents,
  keystrokes - or that lets another local account read what it writes, is in
  scope.
- **The app and its database**, which run and live as the signed-in user.
  Anything that lets another local account read or alter the history, or
  makes the app execute something it reads from the data folder (a logo, a
  settings file), is in scope.
- **The installer and uninstaller** (`tools\Install.ps1`, `Uninstall.ps1`),
  which register the task. Neither asks for, or should ever need,
  administrator rights.

Out of scope: anything requiring an already-elevated attacker, physical access
to an unlocked machine, or a data folder the user deliberately placed where
other accounts can write.

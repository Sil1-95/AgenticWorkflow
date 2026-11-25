AgenticWorkflow - Windows x64 release
=====================================

To install (or update) for your user account - no Administrator rights, no .NET, no Node.js:

    .\install.ps1

If Windows says running scripts is disabled, or warns about a downloaded file:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1

Then open a NEW terminal and run:

    aw doctor

and in Herdr press Ctrl+Shift+P and run "Open AgenticWorkflow".

Updating: extract the newer release and run install.ps1 again. Your run history is kept.
Uninstalling: run uninstall.ps1 (it keeps your run history; add -PurgeState to delete that too).

Where things go
    Application      %LOCALAPPDATA%\Programs\AgenticWorkflow\bin     (and on your user PATH)
    Run history      %LOCALAPPDATA%\AgenticWorkflow                  (never touched by install or update)
    Herdr cockpit    <Herdr home>\mods\agenticworkflow               (a copy; Herdr home is $env:HERDR_APP_HOME or ~\.config\herdr-app)

Contents
    install.ps1, uninstall.ps1, setup\   the installer
    bin\                                 aw, the evidence hook and everything they need (self-contained)
    cockpit\                             the Herdr cockpit
    docs\, examples\                     documentation and an example agenticworkflow.json
    manifest.json                        what this release contains (checked by install.ps1)
    LICENSE                              Apache License 2.0

AgenticWorkflow needs Git, Herdr (with `hc` on PATH) and Claude Code and/or Pi to be installed; this release does not include them.

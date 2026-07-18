Sefirah Fork x64 sideload installer

1. Close every running Sefirah window and tray process.
2. Double-click Install.cmd.
3. Accept the UAC prompt. The installer adds only the included fork certificate
   to the computer's TrustedPeople store, installs/updates the package, and
   migrates Microsoft Store pairing data on the first fork installation.

The migration creates and validates a backup under:
%LOCALAPPDATA%\Temp\.agents\Sefirah\official-data-import\

Existing fork pairing data is never overwritten. Future packages signed with
the same project key update in place and retain LocalState automatically.

Signing certificate thumbprints are printed during installation and can be
removed from Local Computer > Trusted People after the fork is uninstalled.

To install without importing Microsoft Store data:
powershell -ExecutionPolicy Bypass -File .\Install.ps1 -SkipOfficialDataMigration

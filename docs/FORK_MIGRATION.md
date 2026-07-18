# Fork migration and update safety

The fork must keep the desktop TLS identity (`Sefirah.pfx`), pairing database
(`sefirah.db`), user settings, and per-device settings together. Losing either
the certificate or database invalidates existing peer trust.

## First migration from the Microsoft Store build

1. Close both Sefirah builds. The installer refuses to continue while a
   `Sefirah` process exists.
2. Run `Install.ps1` from the sideload ZIP. It installs the fork side by side
   under the package identity `Meta.Sefirah.Fork` and then invokes the migration
   script automatically.
3. The migration script copies the complete official `LocalState` into a validated backup
   under `%LOCALAPPDATA%\Temp\.agents\Sefirah\official-data-import\` before it
   writes the empty fork destination. It refuses to overwrite an existing
   database or certificate.
4. Start the fork and verify every paired endpoint before uninstalling the Store
   build.

Pass `-SkipOfficialDataMigration` to `Install.ps1` only when a clean fork profile
is intentional. The standalone `tools/Migrate-OfficialData.ps1` remains
available for a manual migration.

## Future desktop updates

Database upgrades are additive and backed up under
`%LOCALAPPDATA%\Temp\.agents\Sefirah\database-backups\`. Missing migrations or
newer unknown schemas stop startup instead of deleting pairing data.

`tools/Build-SideloadPackage.ps1` keeps the signing private key and its
current-user DPAPI-protected password in the project-local, ignored `.signing`
directory. Back up that entire directory securely before deleting the checkout.
The build does not import the certificate into a Windows certificate store.
Future packages retain `Meta.Sefirah.Fork` and update its `LocalState` in place.

## Android signing

The official APK cannot be updated by the fork because the signing key differs,
and its Android Keystore TLS private key cannot be exported. The first fork APK
therefore requires one secure re-enrollment per Android device. Every later APK
must use the same private fork signing key and application ID so Room, DataStore,
and the Android Keystore survive in-place updates. Never commit that signing key
or its passwords to Git.

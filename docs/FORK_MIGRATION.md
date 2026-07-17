# Fork migration and update safety

The fork must keep the desktop TLS identity (`Sefirah.pfx`), pairing database
(`sefirah.db`), user settings, and per-device settings together. Losing either
the certificate or database invalidates existing peer trust.

## First migration from the Microsoft Store build

1. Install the fork side by side and record its empty `LocalState` path.
2. Close both Sefirah builds. The migration script refuses to run while a
   `Sefirah` process exists.
3. Run `tools/Migrate-OfficialData.ps1 -DestinationLocalState <path>`.
4. The script copies the complete official `LocalState` into a validated backup
   under `%LOCALAPPDATA%\Temp\.agents\Sefirah\official-data-import\` before it
   writes the empty fork destination. It refuses to overwrite an existing
   database or certificate.
5. Start the fork and verify every paired endpoint before uninstalling the Store
   build.

## Future desktop updates

Database upgrades are additive and backed up under
`%LOCALAPPDATA%\Temp\.agents\Sefirah\database-backups\`. Missing migrations or
newer unknown schemas stop startup instead of deleting pairing data.

## Android signing

The official APK cannot be updated by the fork because the signing key differs,
and its Android Keystore TLS private key cannot be exported. The first fork APK
therefore requires one secure re-enrollment per Android device. Every later APK
must use the same private fork signing key and application ID so Room, DataStore,
and the Android Keystore survive in-place updates. Never commit that signing key
or its passwords to Git.

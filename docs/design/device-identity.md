# Device identity and administrative recovery

Device ID selects a saved record. SHA-256 of DER SubjectPublicKeyInfo (SPKI) authenticates its key. The certificate carries that key through TLS. Addresses and ports locate a peer and never establish trust.

## Pins and compatibility

Windows/macOS and Android derive the SPKI pin from each existing stored certificate, so upgrades need no database migration or new pairing. Renewed certificates with the same key are accepted. Different keys and missing/malformed pins are rejected. Authentication.publicKey must still equal the public key of that exact TLS transport. Both incoming/outgoing control links and independent file/clipboard TLS connections use this policy. Existing verification codes already hash the two SPKIs in descending unsigned-byte order; their format stays unchanged.

This follows the public-key pinning approach used by OkHttp CertificatePinner (SHA-256 over SubjectPublicKeyInfo). Sefirah pins self-signed device identities rather than Web PKI hostnames; it does not replace pinning with CA trust or trust a claimed device ID.

## Local management API v2

The access boundaries remain the same-user desktop pipe and the Android DUMP + shell/root provider. status adds certificate (Base64 DER of the local PUBLIC certificate), publicKeyFingerprint (64 uppercase hex characters), and each saved/discovered peer's publicKeyFingerprint. No private key is exported. Android status waits until saved peers have loaded.

unpair deletes the local pairing and closes its active connection, feature consumers and retries. remove is an alias. Desktop takes deviceId in JSON; Android takes the ID directly, as disconnect does. This has the same local scope as Remove in the UI: the peer retains its own record until separately removed. Unknown IDs fail explicitly. Removal and trust replacement serialize with connection installation; pending handshakes recheck current trust.

rebind takes deviceId, certificate, expectedFingerprint (the currently saved old pin), publicKeyFingerprint (the explicitly approved new pin), and verificationCode (local/new-peer SPKIs). All three checks must match before a trust write. This is an explicit local administrator trust decision. Obtain the new certificate and fingerprint from the actual device's local management endpoint, then cross-check the verification code; never approve a broadcast or ID alone. Only the certificate field changes, preserving record ID, permissions, addresses, settings and device data. Connection restarts after persistence; accepted means the operation was accepted, and status is the source of connection completion. Full pins prevent stale/replayed management requests from replacing a different saved identity.

A peer that retains its key but loses its pairing database can request Pair on an already authenticated link. The trusted end acknowledges once per transport. The other end still requires its normal explicit local pair/approve with a matching verification code. This repairs asymmetric pairing without deleting the trusted end's settings or producing an acknowledgement loop.

## Local key persistence

Android serializes all CryptoUtils initialization across instances. Existing aliases must have a readable X.509 certificate and private key, and a sign/verify challenge proves they correspond. An atomic marker in noBackupFilesDir records the first adopted key's SPKI pin. An unreadable alias, missing key after initialization, or changed marker fails closed and reports an error; no replacement key is generated. The first upgrade adopts the existing key. A fresh install with no alias or marker creates one key. No automatic certificate/key renewal or alias deletion is performed.

Desktop keeps its existing lazy single initialization and atomic no-overwrite PFX creation. An unreadable existing PFX remains an error.

Uninstall, clear-data, factory reset or reflashing can remove both the key and local app state. SPKI cannot recreate a lost private key. Recovery is the explicit rebind above plus normal pairing on an end that lost its database. Signed automatic old-key-to-new-key rotation is a separate protocol extension and is not implemented in this release.

## Verification

Desktop transport regressions cover changed certificate serial/subject/validity with the same key, changed key rejection, empty pins, standard SPKI hashing and invalid old/new pins or verification codes. Actual zero-byte/bulk TLS transfers use a renewed-certificate pin. Android JVM tests generate different certificates for one EC key and an unrelated key and verify matching/rejection. Builds and per-device deployment receipts are recorded in docs/log.

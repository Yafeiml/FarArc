# FarArc security and secret storage

## Stored connection secrets

FarArc encrypts passwords, private keys, gateway credentials, secret application arguments, and data-source passwords before storing them in SQLite, MySQL, or PostgreSQL. It uses the in-tree `PortableStringCipher` implementation instead of the former `1Remote.Security` NuGet dependency.

The ciphertext format is versioned and authenticated:

```text
fararcsec:1:<key-id>:<nonce>:<ciphertext>:<authentication-tag>
```

- AES-256-GCM provides encryption and tamper detection.
- Every encryption operation uses a new 96-bit random nonce.
- HKDF-SHA256 derives the AES key from the configured key material.
- The HKDF salt and info strings use a FarArc-specific context so the derived keys are isolated from earlier development formats.
- The format version and key identifier are authenticated as additional data.
- The key identifier supports selecting old keys during a future key rotation.
- The payload is portable: it is not bound to one Windows account or device.

This format intentionally does not decrypt databases written by upstream 1Remote, the former `1Remote.Security` dependency, or earlier `1Remote.NET10` development builds that used the `rmsec:1` format. Start FarArc with a new database and a new program directory.

## Current key source and limitations

A tagged FarArc build injects `GLOBAL_STRING_ENCRYPTION_KEY` into the application. Clients built with the same key material can decrypt the same synchronized database. A remote storage or synchronization service only needs the ciphertext and does not need to perform decryption.

The injected key is present in the compiled desktop application and can be recovered by a determined user or attacker who controls the client. The current implementation therefore prevents accidental plaintext disclosure and detects ciphertext modification, but it does not protect one user's database from another person who possesses the same published client and obtains the database.

It also cannot protect plaintext while the application is using a credential. A process with sufficient rights to inspect the application can read decrypted credentials from memory.

## Future end-to-end synchronization

Before operating a multi-user synchronization service, replace the build-wide key source with a per-user envelope-encryption design while retaining the versioned `PortableStringCipher` payload:

1. Generate a random 256-bit data-encryption key for each user or vault.
2. Encrypt connection records locally with that data-encryption key.
3. Derive a key-encryption key from a user master password with a memory-hard password KDF, using a unique random salt and stored KDF parameters.
4. Wrap the data-encryption key locally and synchronize only the wrapped key, KDF metadata, and encrypted records.
5. Never send the master password or unwrapped data-encryption key to the synchronization service.
6. Store any locally cached unwrapped key with an operating-system credential vault.
7. Use the ciphertext key identifier to support rotation, recovery keys, and multiple authorized devices.

The in-tree cipher accepts additional decryption key material, so a future client can read records encrypted by a previous key while writing all new records with the current primary key.

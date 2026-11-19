# Bundled sqlite-vec native binary

`win-x64/vec0.dll` is the unmodified loadable extension from the official
Alex Garcia (`asg017`) **sqlite-vec v0.1.9** release. sqlite-vec is dual-licensed
under **MIT or Apache-2.0**; both upstream license texts are included here and copied to
`licenses/sqlite-vec/` in build output.

## Pinned provenance

- Project: <https://github.com/asg017/sqlite-vec>
- Release: <https://github.com/asg017/sqlite-vec/releases/tag/v0.1.9>
- Asset: `sqlite-vec-0.1.9-loadable-windows-x86_64.tar.gz`
- Asset URL: <https://github.com/asg017/sqlite-vec/releases/download/v0.1.9/sqlite-vec-0.1.9-loadable-windows-x86_64.tar.gz>
- Archive SHA-256: `51581189d52066b4dfc6631f6d7a3eab7dedc2260656ab09ca97ab3fb8165983`
- Extracted `vec0.dll` SHA-256: `fcf98662a7ad9dce394b96a88f91032047823831b951c76636787c312a6476e6`
- DLL size: 289,280 bytes
- SQLite entry point: `sqlite3_vec_init`

To reproduce, download that exact asset, verify the archive SHA-256 above, extract only
`vec0.dll` into `Native/win-x64/`, and verify the DLL SHA-256. The C# project copies the DLL
to `runtimes/win-x64/native/vec0.dll` in application and test output.

`SqliteConnectionFactory` tries the configured `DatabaseOptions.SqliteVecExtensionPath` first;
otherwise it loads only the application-local `runtimes/win-x64/native/vec0.dll` or `vec0.dll`.
It does not search `PATH`. Extension loading happens before migrations on every opened
connection. A missing, inaccessible, or incompatible binary is non-fatal: migration 3 records
a notice and skips vec0 DDL, `IsVec0AvailableAsync` returns false, and FTS-only search remains
available. On a later startup with the binary restored, initialization creates `embeddings`.

The Windows x64 binary is intentional: GSE Tech Pro targets 64-bit Windows 11. The 32-bit
device-host process does not open the application database or load this extension.

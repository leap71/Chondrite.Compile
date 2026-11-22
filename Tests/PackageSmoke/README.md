# Package smoke fixture

`scripts/package-smoke.py` copies this project into an isolated temporary
directory, supplies the version of the actual `.nupkg` being tested, and restores
it from an isolated local feed. It tests the packaged executable and automatic
MSBuild integration rather than referencing the compiler source project.

`StoreFixture.cs` is deliberately test-only. It provides the small
`Chondrite.Store` contract and two binary operations used by these integration
checks. It is not a replacement for the future Store package, does not implement
a storage backend, and is never shipped in the compiler package. The fixture
references the real published `Chondrite.Core` package.

The script verifies package contents, independent command-line execution,
editable-file preservation, generated source inclusion, an explicit
`ChondriteCompile` target, unchanged rebuilds, missing-output recovery, schema
edits, schema addition/removal, removal of a definition from an existing file,
removal of the final schema, populated enabled/disabled/enabled build transitions,
explicit generation opt-out, and paths containing spaces. These checks cover the
package/build contract, not the complete Store serialization protocol.

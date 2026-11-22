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

The base fixture disables editable scaffolding so its schema-removal and
generation-disabled checks remain independent of user source. A second temporary
consumer enables the default scaffolding behavior and uses `EditableProgram.cs`
to construct and round-trip a non-default value on its first build.

The script verifies package contents, independent command-line execution,
editable-file preservation, generated source inclusion, an explicit
`ChondriteCompile` target, unchanged rebuilds, missing-output recovery, schema
edits, schema addition/removal, removal of a definition from an existing file,
removal of the final schema, populated enabled/disabled/enabled build transitions,
explicit generation opt-out, and paths containing spaces. These checks cover the
package/build contract, not the complete Store serialization protocol.

Editable integration checks cover separate command-line output, declaration
discovery, first-build inclusion, moved and renamed files, multiple and empty
partials, additional instance state, linked and excluded sources, conditional
implementation opt-out, namespace changes, deleted-partial recovery, preservation
during `Clean`, and incompatible-declaration diagnostics. Roslyn's assemblies must
be bundled with the compiler and must not appear in the application's output.
Additional checks cover a schema-only library with no initial C# sources and an
explicitly excluded file occupying the exact scaffold destination.

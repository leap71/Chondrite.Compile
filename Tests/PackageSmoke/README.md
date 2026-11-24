# Package smoke fixture

`scripts/package-smoke.py` copies this project into an isolated temporary
directory, supplies the version of the actual `.nupkg` being tested, and restores
it from an isolated local feed. It tests the packaged executable and automatic
MSBuild integration rather than referencing the compiler source project.

The consumers use the published `Chondrite.Store` package, pinned to exactly
`0.9.0-alpha02`, with `Chondrite.Core` supplied transitively. The ordinary xUnit
suite does not reference Store.

The base fixture disables editable scaffolding so its schema-removal and
generation-disabled checks remain independent of user source. A second temporary
consumer enables the default scaffolding behavior and uses `EditableProgram.cs`
to construct and round-trip a non-default value on its first build.

The script verifies package contents, independent command-line execution,
editable-file preservation, generated source inclusion, an explicit
`ChondriteCompile` target, unchanged rebuilds, missing-output recovery, schema
edits, schema addition/removal, removal of a definition from an existing file,
removal of the final schema, populated enabled/disabled/enabled build transitions,
explicit generation opt-out, and paths containing spaces.

A separate consumer builds `Primitives.chondrite` with the packed compiler and
uses the scaffolded interface to serialize and deserialize valid values for every
v1 primitive. Each record wraps its primitive in a one-element fixed array so
readback exercises the generated collection guard and the measured payload has
no array count prefix. UTF-8 and bytes are checked with both empty and nonempty
contents. The ordinary tests check that the shared schema fixture covers the
entire catalog and that the emitted types, Store calls, and guard widths match
expectations.

Editable integration checks cover separate command-line output, declaration
discovery, first-build inclusion, moved and renamed files, multiple and empty
partials, additional instance state, linked and excluded sources, conditional
implementation opt-out, namespace changes, deleted-partial recovery, preservation
during `Clean`, and incompatible-declaration diagnostics. Roslyn's assemblies must
be bundled with the compiler and must not appear in the application's output.
Additional checks cover a schema-only library with no initial C# sources and an
explicitly excluded file occupying the exact scaffold destination.

Compile-input checks compare filesystem identity so Windows path casing and
short-name aliases do not cause false failures. Regression checks accept another
name for the same file and reject unrelated files with matching names or contents.
Failures report the expected source and the complete recorded compile-input list.

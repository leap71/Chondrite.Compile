# Chondrite.Compile

A small, deterministic schema compiler for Chondrite's immutable, content-addressed object protocol.

**One compiler project, one repository, one NuGet package.** The ordinary
`Chondrite.Compile` package contains the command-line executable, its dependencies,
and automatic MSBuild integration. It is consumed through `PackageReference`.

- Requires the **.NET 10 SDK** for development and consuming builds.
- Accepts `chondrite-schema 1` source files; the standard extension is `.chondrite`.
- Resolves schema references to exact immutable identities and generates canonical schemas and C# record structs/codecs.
- Calculates identities through **Chondrite.Core 0.9.0-alpha02**.
- Compilation is local and does not publish schemas or connect to a store. Package restore uses the configured NuGet feeds.

Generated C# uses the `Chondrite.Core` and `Chondrite.Store` runtime APIs.
A consuming application must reference those runtime components separately.
The compiler package contains private copies of its own dependencies under
`tools/`; these do not supply the application's runtime.

## Use in a project

After the package version has been published, add:

```xml
<ItemGroup>
  <PackageReference Include="Chondrite.Compile"
                    Version="0.1.0-alpha01"
                    PrivateAssets="all" />
</ItemGroup>
```

The version shown is the initial package version, not a claim that it is already
published. For testing a local package, use the directory containing the
`.nupkg` as an additional NuGet source.

Place schemas in `Schemas/**/*.chondrite` and build normally:

```bash
dotnet build
```

The package imports its build target automatically. It runs the bundled
compiler before C# compilation and includes the generated sources. There is
no separate tool installation or manual target import.

Default output is `$(IntermediateOutputPath)ChondriteSchemas/`, normally
`obj/Debug/net10.0/ChondriteSchemas/`. Configuration and target framework
have separate output directories. IDE design-time builds reuse existing
generated files; run a normal build to generate or update them.

For an explicit schema-only invocation:

```bash
dotnet msbuild MyProject.csproj -restore -target:ChondriteCompile
```

In a project with multiple target frameworks, select one for this explicit
target, for example `-property:TargetFramework=net10.0`.
A normal multitarget build generates independently for each framework.

### Build configuration

An explicit item list replaces default discovery:

```xml
<ItemGroup>
  <ChondriteSchema Include="Types/**/*.chondrite" />
  <ChondriteSchema Include="../Shared/Engineering.chondrite" />
</ItemGroup>
```

All selected files are compiled together so named dependencies can be resolved.

| Setting | Default | Purpose |
|---|---|---|
| `ChondriteCompileEnabled` | `true` | Enable automatic and explicit schema compilation |
| `ChondriteEnableDefaultSchemaItems` | `true` | Discover `Schemas/**/*.chondrite` when no explicit inputs were supplied |
| `ChondriteCompileOutputDir` | `$(IntermediateOutputPath)ChondriteSchemas/` | Dedicated generated-output directory |
| `ChondriteDotNetHost` | The current .NET host, with `dotnet` as fallback | Override the executable used to run the compiler |
| `ChondriteCompileAdditionalInputs` items | Empty | Extra files whose changes should trigger regeneration |

Keep the default output location unless a dedicated alternative is needed.
When overriding it for a multitarget build, retain configuration/framework
separation.

The build tracks source paths, source timestamps, compiler files, and generated
outputs. Adding, editing, or removing schemas triggers regeneration; removing
the last schema removes its generated files. Deleting an expected output also
triggers regeneration. An unchanged build skips the compiler.

## Schema source and LEAP 71 naming

`Schemas/Pascals.chondrite`:

```text
chondrite-schema 1
schema Chondrite.Examples.Pascals
{
    0: fValue float64
}
```

`Schemas/Record.chondrite`:

```text
chondrite-schema 1
schema Chondrite.Examples.Sample
{
    0: idNonce nonce
    1: vecPosition vector3f
    2: uPressure Chondrite.Examples.Pascals
    3: astrTags utf8[]
}
```

The examples use LEAP 71 semantic prefixes:

| Prefix | Meaning | Example |
|---|---|---|
| `f` | Floating-point scalar (`float` or `double`) | `fValue` |
| `n` | Integer/count/version | `nIndexVersion` |
| `id` | Identity | `idNonce`, `idRecordType` |
| `vec` | Vector | `vecPosition` |
| `u` | Strongly typed quantity | `uPressure` |
| `astr` | Array of strings | `astrTags` |
| `aby` | Byte array | `abyCursor` |

Collection prefixes identify the element semantics: `astr` for strings, `aby`
for bytes, and `a` alone for collections of `o` values. The `f` prefix applies to
both `float` and `double`.

The compiler preserves field names; it does not infer serialization or enforce
naming rules from these prefixes. Changing a field name changes the schema
identity. Renaming a source file or changing its extension does not.

A source file can contain several schema definitions and begins with one
`chondrite-schema 1` header. Formatting and comments are normalized.
Canonical text uses UTF-8 without a byte-order mark, LF line endings, four-space
field indentation, braces on their own lines, and a final newline.

Supported intrinsic types: `bool`, `int8`/`uint8`, `int16`/`uint16`, `int32`/`uint32`, `int64`/`uint64`, `float32`, `float64`, `vector2f`, `vector3f`, `vector4f`, `guid`, `nonce`, `objectid`, `utf8`, `bytes`;
plus nested schemas, `T[]`, `T[N]`, and `optional<T>`.

The permanent schema identity is:

```text
SHA256(32-byte intrinsic schema type 1 || UTF-8 canonical schema text)
```

The compiler uses `Chondrite.Core.StorageObject.idCalculateIdentity()`.
The canonical header, schema name, ordered fields, and exact dependency
identities are identity-bearing.

## Command-line use

The same executable can be run directly from the package's `tools/net10.0/`
directory:

```bash
dotnet "/path/to/package/tools/net10.0/Chondrite.Compile.dll" \
  --out Generated \
  Schemas/Pascals.chondrite Schemas/Record.chondrite
```

Keep the adjacent dependency assemblies, `.deps.json`, and
`.runtimeconfig.json` with the compiler. The package is a normal build
dependency, so it is not installed with `dotnet tool install`.

Explicit filenames may use any extension; the header and contents determine
whether they are valid schemas. `--inputs <file-list>` accepts a UTF-8 file
with one unquoted input path per line. Relative paths resolve from the working
directory. An empty input list clears the previous compiler-owned schema
outputs in the specified output directory.

Run from the source repository with:

```bash
dotnet run --project src/Chondrite.Compile -- \
  --out artifacts/generated \
  Schemas/Pascals.chondrite Schemas/Record.chondrite
```

## Generated files and editable partials

Each invocation writes:

- `<QualifiedSchemaName>.g.cs` — generated partial record and canonical binary codec.
- `<QualifiedSchemaName>.canonical.chondrite` — exact canonical schema text.
- `catalog.txt` — schema names and identities.
- `.chondrite.outputs` — ownership manifest for generated files.

The manifest allows a later invocation to remove only obsolete generated
outputs. Unrelated files and editable partials are preserved. Generated files
whose contents are unchanged retain their timestamps. Use a dedicated output
directory per independent set of input schemas.

`--editable` additionally creates `<QualifiedSchemaName>.cs` if absent.
These files provide editable constructors and accessors and are never
overwritten. Automatic builds generate only the replaceable files.

For initial scaffolding:

```bash
dotnet run --project src/Chondrite.Compile -- \
  --out artifacts/scaffold --editable \
  Schemas/Pascals.chondrite Schemas/Record.chondrite
```

Move the editable `.cs` partials into your source tree, retaining them under
version control. Leave the generated `.g.cs` files to the build.
The persistence fields remain inside the generated private `SchemaData`
record, independently of the editable API.

Generated code follows the established conventions: `o` for object/struct
values, `m_` for instance fields, and `c_` for static fields.
Each record implements `IStorageRecord<T>` and exposes `idSchema`,
`EnsureSchema`, `oSerialize`, `oDeserialize`, `idPut`, and `oGet`.

The generated code expects:

- `Chondrite.Core.StorageObject`, `StorageType`, and `StorageObjectId`.
- `Chondrite.Store.IObjectStore` and `StorageTypeRegistration.idRegisterType()`.
- `Chondrite.Store.Nonce`.
- `Chondrite.Store.Serialization.IStorageRecord<T>`, `StorageBinaryReader`, and `StorageBinaryWriter`.

## Build, test, and pack this repository

```bash
dotnet restore Chondrite.Compile.slnx
dotnet build Chondrite.Compile.slnx --configuration Release --no-restore
dotnet test --solution Chondrite.Compile.slnx --configuration Release --no-build
dotnet pack src/Chondrite.Compile/Chondrite.Compile.csproj \
  --configuration Release --no-build --output artifacts
python3 scripts/package-smoke.py \
  --package artifacts/Chondrite.Compile.0.1.0-alpha01.nupkg
```

The last check needs Python 3 and the .NET 10 SDK. It restores the actual packed
artifact into an isolated consuming project, checks standalone invocation,
compiles generated C#, and tests incremental builds, missing outputs, schema
removals, and editable-file preservation. Its small Store fixture is test-only;
it does not replace validation against the real Chondrite.Store runtime.

The solution has one production project and its test project. The compiler's
project packs its complete published output under `tools/net10.0/`, together
with `build/Chondrite.Compile.targets`. Compiler dependencies are bundled
privately and do not become NuGet dependencies of the consuming application.

## GitHub Actions and publishing

- `ci.yml` restores, builds, tests, packs, and runs the package smoke check
  on Linux, macOS, and Windows.
- `publish.yml` runs the same validation on Linux for a `v*` tag, then
  publishes the single package using NuGet trusted publishing.

Configure a NuGet trusted-publishing policy for the repository and
`publish.yml`, and set the GitHub repository variable `NUGET_USER` to the
NuGet username associated with that policy. A tag such as
`v0.1.0-alpha01` supplies the package version. The workflow does not require
a long-lived NuGet API key.

License: Apache-2.0.

# Chondrite.Compile

Schema compilation and C# generation for Chondrite. This package is in early
development; its public interface may change between releases.

Requires the .NET 10 SDK. Generated code uses the `Chondrite.Core` and
`Chondrite.Store` runtime APIs.

## Build integration

Reference the compiler as a build dependency:

```xml
<PackageReference Include="Chondrite.Compile"
                  Version="0.9.0-alpha02"
                  PrivateAssets="all" />
```

Place definitions in `Schemas/**/*.chondrite` and run `dotnet build`.

- Generated codecs, canonical schemas, and `catalog.txt` are written to
  `obj/<configuration>/<framework>/ChondriteSchemas/`.
- Missing editable partials are created in `Records/` and included in that same build.
- Existing editable partials are recognized by their declared namespace and type.
  Move, rename, or split them anywhere in the project's C# source files.
- Editable files are never overwritten or deleted by generation or `dotnet clean`.
  Commit them to source control.

Discovery reads the project's actual C# `Compile` inputs, including linked files.
An empty matching partial is sufficient. Incompatible or malformed declarations
produce diagnostics. Canonical generation remains incremental; editable discovery
runs independently so C# moves and edits are noticed without changing a schema.

The generated partial supplies `StructLayout(LayoutKind.Auto)`; do not repeat
that attribute in editable partials.

### Configuration

| Setting | Default | Purpose |
|---|---|---|
| `ChondriteCompileEnabled` | `true` | Enable the compiler build target |
| `ChondriteEnableDefaultSchemaItems` | `true` | Discover `Schemas/**/*.chondrite` |
| `ChondriteCompileOutputDir` | `$(IntermediateOutputPath)ChondriteSchemas/` | Generated output |
| `ChondriteGenerateEditable` | `true` | Create missing editable partials |
| `ChondriteEditableOutputDir` | `Records/` | Initial editable-file destination |

Explicit `ChondriteSchema` items replace default schema discovery. Use
`ChondriteEditableProvided` for types whose editable interface is supplied
conditionally or by another generator:

```xml
<ItemGroup>
  <ChondriteSchema Include="Schemas/**/*.chondrite" />
  <ChondriteEditableProvided Include="MyProject.Units.Pascals" />
</ItemGroup>
```

Discovery uses the current language version and conditional symbols. It does not
infer declarations from other build configurations. An existing editable destination
excluded from `Compile` produces a diagnostic instead of being silently re-included.
Projects with default `Compile` items disabled should explicitly include their
editable output directory. Keep editable output separate from generated output.

## Command line

The package bundles its executable and dependencies under `tools/net10.0/`:

```sh
dotnet /path/to/package/tools/net10.0/Chondrite.Compile.dll \
  --out obj/ChondriteSchemas --editable-out Records \
  Schemas/Example.chondrite
```

`--editable` creates editable files beside `--out`; `--editable-out` selects a
separate source directory. For declaration discovery outside that directory, pass
`--csharp-inputs <file-list>`. `--inputs <file-list>` selects schema files. Lists
contain one unquoted UTF-8 path per line, relative to the working directory or absolute.

`--editable-only --editable-out <directory>` performs scaffolding without rewriting
canonical outputs. Run `--help` for discovery and build-integration options.

## Development

```sh
dotnet build Chondrite.Compile.slnx --configuration Release
dotnet test --solution Chondrite.Compile.slnx --configuration Release --no-build
dotnet pack src/Chondrite.Compile/Chondrite.Compile.csproj --configuration Release --no-build --output artifacts
python3 scripts/package-smoke.py --package artifacts/Chondrite.Compile.0.9.0-alpha02.nupkg
```

The package smoke check requires Python 3. GitHub Actions builds and tests on
Linux, macOS, and Windows; a `v*` tag runs validation and NuGet trusted publishing.

License: Apache-2.0.

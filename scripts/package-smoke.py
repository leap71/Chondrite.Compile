#!/usr/bin/env python3
"""Exercise an actual Chondrite.Compile package in a fresh consuming project.

Uses only the Python 3.9+ standard library and the .NET 10 SDK. NuGet.org is needed
for the fixture's Chondrite.Core dependency. No compiler source checkout or
separately installed .NET tool is used by the temporary consumer.
"""

from __future__ import annotations

import argparse
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def require_compile_input(inputs: Path, source: Path, project: Path, message: str) -> None:
    """Require the actual source file, allowing filesystem aliases and native casing."""
    details = []
    for entry in inputs.read_text(encoding="utf-8-sig").splitlines():
        if not entry:
            continue
        candidate = Path(entry)
        if not candidate.is_absolute():
            candidate = project / candidate
        try:
            if source.samefile(candidate):
                return
            details.append(f"  {entry}")
        except OSError as error:
            details.append(f"  {entry} [could not compare: {error}]")
    raise AssertionError(
        f"{message}\nExpected source: {source}\nResolved source: {source.resolve()}\n"
        f"Compile-input manifest: {inputs}\nCompile inputs:\n" +
        ("\n".join(details) if details else "  <empty>")
    )


def local_name(element: ET.Element) -> str:
    return element.tag.rsplit("}", 1)[-1]


def inspect_package(package: Path) -> tuple[str, list[str]]:
    with zipfile.ZipFile(package) as archive:
        names = archive.namelist()
        nuspecs = [name for name in names if name.endswith(".nuspec")]
        require(len(nuspecs) == 1, "Expected exactly one package manifest.")
        metadata = next(
            element for element in ET.fromstring(archive.read(nuspecs[0]))
            if local_name(element) == "metadata"
        )
        values = {local_name(element): element.text for element in metadata}
        require(values.get("id") == "Chondrite.Compile", "Unexpected package identity.")
        version = values.get("version")
        require(bool(version), "Missing package version.")
        require(
            not any(local_name(element) == "packageType" and
                    element.attrib.get("name", "").lower() == "dotnettool"
                    for element in metadata.iter()),
            "The package must be a normal PackageReference dependency, not DotnetTool.",
        )
        require(
            not any(local_name(element) == "dependency" for element in metadata.iter()),
            "Compiler dependencies must be bundled privately, not added to the consuming application.",
        )
        required = {
            "build/Chondrite.Compile.targets",
            "tools/net10.0/Chondrite.Compile.dll",
            "tools/net10.0/Chondrite.Compile.deps.json",
            "tools/net10.0/Chondrite.Compile.runtimeconfig.json",
            "tools/net10.0/Chondrite.Core.dll",
            "tools/net10.0/Microsoft.CodeAnalysis.dll",
            "tools/net10.0/Microsoft.CodeAnalysis.CSharp.dll",
        }
        require(required.issubset(names), f"Missing packaged files: {sorted(required.difference(names))}")
        require(any(name.lower().endswith("semantics.dll") for name in names),
                "The compiler's Semantics runtime dependency was not bundled.")
        require(not any(name.startswith(("lib/", "ref/", "runtimes/")) for name in names),
                "Compiler assemblies must not become consumer compile/runtime assets.")
        require(not any(name.endswith(".runtimeconfig.dev.json") for name in names),
                "Development probing paths must not hide missing bundled dependencies.")
        require(not any("PackageSmoke" in name or name.endswith("StoreFixture.cs") for name in names),
                "Test fixtures must not ship in the package.")
        for name in names:
            path = PurePosixPath(name)
            require(not path.is_absolute() and ".." not in path.parts,
                    f"Unsafe package member: {name}")
        return str(version), names


def write_text(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(content.encode("utf-8"))


def run(command: list[str], cwd: Path, environment: dict[str, str]) -> str:
    result = subprocess.run(command, cwd=cwd, env=environment, text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if result.returncode:
        raise RuntimeError(
            f"Command failed ({result.returncode}): {subprocess.list2cmdline(command)}\n"
            f"Working directory: {cwd}\n{result.stdout}"
        )
    return result.stdout


def generated_snapshot(directory: Path) -> dict[str, tuple[bytes, int]]:
    return {
        path.name: (path.read_bytes(), path.stat().st_mtime_ns)
        for path in directory.iterdir()
        if path.name.endswith((".g.cs", ".canonical.chondrite"))
    }


def run_failure(command: list[str], cwd: Path, environment: dict[str, str]) -> str:
    result = subprocess.run(command, cwd=cwd, env=environment, text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    require(result.returncode != 0, f"Expected a diagnostic from: {subprocess.list2cmdline(command)}")
    return result.stdout


def schema(name: str, extra: str = "") -> str:
    return (
        f"chondrite-schema 1\nschema {name}\n{{\n"
        f"    0: fValue float64\n{extra}}}\n"
    )


def exercise_compile_input_paths(work: Path) -> None:
    print("[package smoke] Compile-input file identity and diagnostics", flush=True)
    project = work / "path identity checks"
    source = project / "Records" / "Sample.cs"
    alias = project / "Another Name.cs"
    inputs = project / "compile-items.txt"
    write_text(source, "// The same source can have several filesystem names.\n")
    os.link(source, alias)
    # A hard link exercises aliases on every platform without symlink privileges.
    write_text(inputs, "\ufeffMissing.cs\nAnother Name.cs\n")
    require_compile_input(inputs, source, project, "A source alias was not recognized.")

    # A path substring or identical contents do not establish file identity.
    for other in (source.with_suffix(".cs.backup"), project / "Elsewhere" / source.name):
        write_text(other, source.read_text(encoding="utf-8"))
        write_text(inputs, str(other) + "\n")
        try:
            require_compile_input(inputs, source, project, "Expected rejection.")
        except AssertionError as error:
            diagnostic = str(error)
            require(all(str(path) in diagnostic for path in (source, inputs, other)),
                    f"A failed comparison did not identify its paths:\n{diagnostic}")
        else:
            raise AssertionError(f"A different file was accepted as a compile input: {other}")


def exercise_schema_only(fixture: Path, version: str, dotnet: str,
                         work: Path, environment: dict[str, str]) -> None:
    print("[package smoke] Schema-only library starts without authored C#", flush=True)
    runtime = work / "schema runtime fixture"
    write_text(runtime / "RuntimeFixture.csproj", """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="Chondrite.Core" Version="0.9.0-alpha02" /></ItemGroup>
</Project>
""")
    write_text(runtime / "StoreFixture.cs", (fixture / "StoreFixture.cs").read_text(encoding="utf-8"))
    project = work / "schema only library"
    write_text(project / "SchemaOnly.csproj", f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Chondrite.Compile" Version="{version}" PrivateAssets="all" />
    <ProjectReference Include="../schema runtime fixture/RuntimeFixture.csproj" />
  </ItemGroup>
  <Target Name="PackageSmokeCompileInputs" AfterTargets="CoreCompile">
    <WriteLinesToFile File="$(IntermediateOutputPath)smoke.compile-items.txt"
                      Lines="@(Compile->'%(FullPath)')" Overwrite="true" />
  </Target>
</Project>
""")
    write_text(project / "Schemas" / "Only.chondrite", schema("Smoke.Only"))
    require(not list(project.rglob("*.cs")), "The schema-only fixture unexpectedly contains authored C#.")
    run([dotnet, "build", "SchemaOnly.csproj", "--configuration", "Debug",
         "--nologo", "-v:minimal", "-maxcpucount:1", "-nr:false"], project, environment)
    editable = project / "Records" / "Smoke.Only.cs"
    inputs = project / "obj" / "Debug" / "net10.0" / "smoke.compile-items.txt"
    require(editable.is_file() and (project / "bin" / "Debug" / "net10.0" / "SchemaOnly.dll").is_file(),
            "The schema-only project did not produce its editable partial and assembly.")
    require_compile_input(inputs, editable, project,
                          "The first editable source was not included when Compile began empty.")


def exercise_editable(fixture: Path, version: str, dotnet: str,
                      work: Path, environment: dict[str, str]) -> None:
    print("[package smoke] Default scaffolding in the first build", flush=True)
    project = work / "editable consumer with spaces"
    project.mkdir()
    definition = ET.fromstring((fixture / "Consumer.csproj").read_text(encoding="utf-8")
                               .replace("__PACKAGE_VERSION__", version))
    for properties in definition.findall("PropertyGroup"):
        for property_element in list(properties):
            if property_element.tag == "ChondriteGenerateEditable":
                properties.remove(property_element)
    project_file = project / "Consumer.csproj"
    ET.ElementTree(definition).write(project_file, encoding="utf-8", xml_declaration=True)
    for source, destination in (("EditableProgram.cs", "Program.cs"), ("StoreFixture.cs", "StoreFixture.cs")):
        write_text(project / destination, (fixture / source).read_text(encoding="utf-8"))
    sample_schema = project / "Schemas" / "Sample.chondrite"
    write_text(sample_schema, schema("Smoke.Sample"))
    generated = project / "obj" / "Debug" / "net10.0" / "ChondriteSchemas"
    editable = project / "Records" / "Smoke.Sample.cs"
    assembly = project / "bin" / "Debug" / "net10.0" / "Consumer.dll"
    build = [dotnet, "build", "Consumer.csproj", "--configuration", "Debug",
             "--nologo", "-v:minimal", "-maxcpucount:1", "-nr:false"]
    generate = [dotnet, "msbuild", "Consumer.csproj", "-target:ChondriteCompile",
                "-property:Configuration=Debug", "-nologo", "-verbosity:minimal",
                "-maxcpucount:1", "-nr:false"]

    def probe(*arguments: str) -> None:
        run([dotnet, str(assembly), *arguments], project, environment)

    def add_item(kind: str, **attributes: str) -> None:
        definition = ET.parse(project_file)
        group = ET.SubElement(definition.getroot(), "ItemGroup")
        ET.SubElement(group, kind, **attributes)
        definition.write(project_file, encoding="utf-8", xml_declaration=True)

    run(build, project, environment)
    require(editable.is_file(), "A fresh build did not create its editable partial.")
    probe()
    output = assembly.parent
    require(not (output / "Chondrite.Compile.dll").exists() and
            not any(output.glob("Microsoft.CodeAnalysis*.dll")),
            "Compiler or Roslyn dependencies leaked into application output.")
    inputs = project / "obj" / "Debug" / "net10.0" / "smoke.compile-items.txt"
    require_compile_input(inputs, editable, project,
                          "The newly created editable partial was not included in the first C# compilation.")

    print("[package smoke] Edited partials move and split without duplicate scaffolds", flush=True)
    generated_before_move = generated_snapshot(generated)
    moved = project / "Physics" / "Pressure Accessors.cs"
    contents = editable.read_text(encoding="utf-8")
    position = contents.rfind("}")
    require(position >= 0, "The editable template has no closing declaration.")
    contents = (contents[:position] +
                "    public string? strLocalLabel { get; init; }\n" + contents[position:] +
                "\n// User edit preserved across later builds.\n")
    write_text(moved, contents)
    editable.unlink()
    extra = project / "Physics" / "Pressure.Extra.cs"
    write_text(extra, "namespace Smoke;\npublic readonly partial record struct Sample { }\n")
    run(build + ["--no-restore"], project, environment)
    require(not editable.exists(), "Moving a partial caused a duplicate at its old path.")
    require(moved.read_text(encoding="utf-8") == contents, "The moved user implementation was rewritten.")
    require(generated_snapshot(generated) == generated_before_move,
            "Moving editable code unnecessarily rewrote schema outputs.")
    probe("custom")

    print("[package smoke] Existing empty partials, linked files, exclusions, and per-type opt-out", flush=True)
    for name in ("Manual", "Linked", "Excluded", "Conditional"):
        write_text(project / "Schemas" / f"{name}.chondrite", schema(f"Smoke.{name}"))
    manual = project / "User Code" / "An Empty Implementation.cs"
    write_text(manual, "namespace Smoke;\npublic readonly partial record struct Manual { }\n")
    linked = work / "linked source" / "Other Name.cs"
    write_text(linked, "namespace Smoke\n{\n    public readonly partial record struct Linked { }\n}\n")
    add_item("Compile", Include="../linked source/Other Name.cs", Link="Shared/Linked.cs")
    excluded = project / "Excluded" / "Not An Owner.cs"
    write_text(excluded, "namespace Smoke;\npublic class Excluded { }\n")
    add_item("Compile", Remove="Excluded/**/*.cs")
    conditional = project / "User Code" / "Conditional.cs"
    write_text(conditional, "#if SMOKE_EXTERNAL_INTERFACE\nnamespace Smoke;\n"
               "public readonly partial record struct Conditional { }\n#endif\n")
    add_item("ChondriteEditableProvided", Include="Smoke.Conditional")
    run(build + ["--no-restore"], project, environment)
    for name in ("Manual", "Linked", "Conditional"):
        require(not (project / "Records" / f"Smoke.{name}.cs").exists(),
                f"An already supplied or opted-out {name} implementation was scaffolded again.")
    require((project / "Records" / "Smoke.Excluded.cs").is_file(),
            "A source excluded from Compile was incorrectly treated as the implementation.")
    require(excluded.read_text(encoding="utf-8") == "namespace Smoke;\npublic class Excluded { }\n",
            "An excluded source file was modified.")
    probe("custom")

    print("[package smoke] C# declaration changes invalidate discovery independently of schemas", flush=True)
    before_source_change = generated_snapshot(generated)
    write_text(manual, "namespace Elsewhere;\npublic readonly partial record struct Manual { }\n")
    run(build + ["--no-restore"], project, environment)
    require((project / "Records" / "Smoke.Manual.cs").is_file(),
            "Changing a C# declaration's namespace was ignored until a schema changed.")
    require(generated_snapshot(generated) == before_source_change,
            "C# declaration discovery rewrote unchanged canonical schema outputs.")
    probe("custom")

    print("[package smoke] Deleting the last partial recreates it with unchanged schemas", flush=True)
    moved.unlink()
    extra.unlink()
    run(build + ["--no-restore"], project, environment)
    require(editable.is_file(), "A deleted editable partial was not recreated on an unchanged schema build.")
    probe()

    print("[package smoke] Configured editable destination with spaces", flush=True)
    write_text(project / "Schemas" / "CustomFolder.chondrite", schema("Smoke.CustomFolder"))
    custom_editable = project / "User Interfaces" / "Smoke.CustomFolder.cs"
    run(build + ["--no-restore", "-property:ChondriteEditableOutputDir=User Interfaces"],
        project, environment)
    require(custom_editable.is_file() and
            not (project / "Records" / "Smoke.CustomFolder.cs").exists(),
            "A configured editable destination was ignored.")
    require_compile_input(inputs, custom_editable, project,
                          "The configured destination's new source file was not compiled in the same build.")
    probe()

    print("[package smoke] Clean preserves editable source", flush=True)
    preserved = {path: path.read_bytes() for path in project.rglob("*.cs")
                 if not {"bin", "obj"}.intersection(path.relative_to(project).parts)}
    run([dotnet, "clean", "Consumer.csproj", "--configuration", "Debug",
         "--nologo", "-v:minimal", "-maxcpucount:1", "-nr:false"], project, environment)
    require(all(path.is_file() and path.read_bytes() == contents for path, contents in preserved.items()),
            "Clean removed or changed editable source files.")
    require(linked.is_file(), "Clean removed a linked source file.")

    print("[package smoke] Global scaffold opt-out and incompatible declaration diagnostics", flush=True)
    write_text(project / "Schemas" / "NoEditable.chondrite", schema("Smoke.NoEditable"))
    run(build + ["--no-restore", "-property:ChondriteGenerateEditable=false"], project, environment)
    require(not (project / "Records" / "Smoke.NoEditable.cs").exists() and
            (generated / "Smoke.NoEditable.g.cs").is_file(),
            "Disabling editable scaffolding did not preserve normal schema generation.")
    probe()
    # Prevent the diagnostic check from scaffolding this deliberately missing interface.
    add_item("ChondriteEditableProvided", Include="Smoke.NoEditable")
    write_text(project / "Schemas" / "Conflict.chondrite", schema("Smoke.Conflict"))
    conflict = project / "User Code" / "WrongKind.cs"
    conflict_text = "namespace Smoke;\npublic readonly partial struct Conflict { }\n"
    write_text(conflict, conflict_text)
    diagnostic = run_failure(generate, project, environment)
    require("Smoke.Conflict" in diagnostic and "WrongKind.cs" in diagnostic,
            f"The incompatible declaration diagnostic was not useful:\n{diagnostic}")
    require(not (project / "Records" / "Smoke.Conflict.cs").exists() and
            conflict.read_text(encoding="utf-8") == conflict_text,
            "A conflicting declaration was overwritten or accompanied by a duplicate scaffold.")

    print("[package smoke] Explicit exclusion of the exact scaffold destination is respected", flush=True)
    conflict.unlink()
    (project / "Schemas" / "Conflict.chondrite").unlink()
    excluded_owner = project / "Records" / "Smoke.ExcludedOwner.cs"
    excluded_owner_text = ("namespace Smoke;\npublic readonly partial record struct ExcludedOwner { }\n"
                           "// This existing file is intentionally excluded from Compile.\n")
    write_text(excluded_owner, excluded_owner_text)
    write_text(project / "Schemas" / "ExcludedOwner.chondrite", schema("Smoke.ExcludedOwner"))
    add_item("Compile", Remove="Records/Smoke.ExcludedOwner.cs")
    diagnostic = run_failure(generate, project, environment)
    require("Smoke.ExcludedOwner.cs" in diagnostic,
            f"The excluded-destination diagnostic did not identify the source file:\n{diagnostic}")
    require(excluded_owner.read_text(encoding="utf-8") == excluded_owner_text,
            "An explicitly excluded editable source was overwritten.")


def exercise(package: Path, version: str, dotnet: str, work: Path) -> None:
    exercise_compile_input_paths(work)
    environment = os.environ.copy()
    environment["PATH"] = str(Path(dotnet).parent) + os.pathsep + environment.get("PATH", "")
    environment["DOTNET_ROOT"] = str(Path(dotnet).parent)
    environment["DOTNET_CLI_HOME"] = str(work / "dotnet home")
    environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    environment["NUGET_PACKAGES"] = str(work / "nuget packages")

    print("[package smoke] Independent bundled compiler and editable files", flush=True)
    extracted = work / "standalone compiler"
    with zipfile.ZipFile(package) as archive:
        archive.extractall(extracted)
    compiler = extracted / "tools" / "net10.0" / "Chondrite.Compile.dll"
    standalone_input = work / "standalone schemas" / "sample input.chondrite"
    standalone_output = work / "standalone generated"
    write_text(standalone_input, schema("Smoke.Sample"))
    arguments = [dotnet, str(compiler), "--out", str(standalone_output),
                 "--editable", str(standalone_input)]
    run(arguments, work, environment)
    editable = standalone_output / "Smoke.Sample.cs"
    require(editable.is_file(), "Explicit --editable did not create the editable partial.")
    require((standalone_output / "Smoke.Sample.canonical.chondrite").is_file(),
            "The standalone compiler did not emit a canonical .chondrite file.")
    write_text(editable, editable.read_text(encoding="utf-8") + "\n// Hand-edited smoke marker.\n")
    edited_bytes = editable.read_bytes()
    run(arguments, work, environment)
    require(editable.read_bytes() == edited_bytes, "The compiler overwrote the editable partial.")

    handwritten = standalone_output / "Handwritten.g.cs"
    write_text(handwritten, "// Unowned file: never delete.\n")
    empty_inputs = work / "empty inputs.txt"
    write_text(empty_inputs, "")
    run([dotnet, str(compiler), "--out", str(standalone_output),
         "--inputs", str(empty_inputs)], work, environment)
    require(not (standalone_output / "Smoke.Sample.g.cs").exists(),
            "An empty input manifest left stale generated source.")
    require(not (standalone_output / "Smoke.Sample.canonical.chondrite").exists(),
            "An empty input manifest left a stale canonical schema.")
    require(editable.read_bytes() == edited_bytes and handwritten.is_file(),
            "Owned-output cleanup removed user-authored files.")

    print("[package smoke] Separate standalone editable output and declaration discovery", flush=True)
    split_output = work / "separate generated"
    source_output = work / "separate editable"
    authored_inputs = work / "authored CSharp sources.txt"
    schema_inputs = work / "schema sources.txt"
    editable_manifest = work / "editable output files.txt"
    write_text(authored_inputs, "")
    write_text(schema_inputs, str(standalone_input) + "\n")
    run([dotnet, str(compiler), "--out", str(split_output),
         "--editable-out", str(source_output), "--inputs", str(schema_inputs),
         "--csharp-inputs", str(authored_inputs)], work, environment)
    split_editable = source_output / "Smoke.Sample.cs"
    require(split_editable.is_file() and not (split_output / "Smoke.Sample.cs").exists(),
            "Separate editable output was not honored.")
    moved_standalone = work / "standalone authored" / "Pressure.cs"
    write_text(moved_standalone, split_editable.read_text(encoding="utf-8") + "\n// Moved source.\n")
    moved_contents = moved_standalone.read_bytes()
    split_editable.unlink()
    write_text(authored_inputs, str(moved_standalone) + "\n")
    split_snapshot = generated_snapshot(split_output)
    run([dotnet, str(compiler), "--editable-only", "--editable-out", str(source_output),
         "--inputs", str(schema_inputs), "--csharp-inputs", str(authored_inputs),
         "--editable-manifest", str(editable_manifest)], work, environment)
    require(not split_editable.exists() and moved_standalone.read_bytes() == moved_contents,
            "Standalone discovery failed to preserve a moved editable implementation.")
    require(editable_manifest.is_file() and
            not editable_manifest.read_text(encoding="utf-8-sig").strip(),
            "An already supplied standalone implementation was reported as a new source file.")
    require(generated_snapshot(split_output) == split_snapshot,
            "Editable-only generation rewrote canonical or generated-code outputs.")

    print("[package smoke] Fresh project consuming the actual NuGet package", flush=True)
    feed = work / "local feed"
    feed.mkdir()
    shutil.copy2(package, feed / package.name)
    configuration = ET.Element("configuration")
    sources = ET.SubElement(configuration, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="package-smoke", value=str(feed))
    ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
    mappings = ET.SubElement(configuration, "packageSourceMapping")
    local_mapping = ET.SubElement(mappings, "packageSource", key="package-smoke")
    ET.SubElement(local_mapping, "package", pattern="Chondrite.Compile")
    public_mapping = ET.SubElement(mappings, "packageSource", key="nuget.org")
    ET.SubElement(public_mapping, "package", pattern="*")
    ET.ElementTree(configuration).write(work / "NuGet.Config", encoding="utf-8", xml_declaration=True)

    project = work / "consumer with spaces"
    fixture = Path(__file__).resolve().parents[1] / "Tests" / "PackageSmoke"
    project.mkdir()
    for filename in ("Consumer.csproj", "Program.cs", "StoreFixture.cs"):
        write_text(project / filename,
                   (fixture / filename).read_text(encoding="utf-8").replace("__PACKAGE_VERSION__", version))
    first_schema = project / "Schemas" / "Sample Record.chondrite"
    write_text(first_schema, schema("Smoke.Sample"))
    generated = project / "obj" / "Debug" / "net10.0" / "ChondriteSchemas"
    assembly = project / "bin" / "Debug" / "net10.0" / "Consumer.dll"
    build = [dotnet, "build", "Consumer.csproj", "--configuration", "Debug",
             "--nologo", "-v:minimal", "-maxcpucount:1", "-nr:false"]

    def probe(*names: str) -> None:
        run([dotnet, str(assembly), *names], project, environment)

    def assert_outputs(names: set[str]) -> None:
        actual = {path.name.removesuffix(".g.cs") for path in generated.glob("*.g.cs")}
        require(actual == names, f"Wrong generated sources: {actual}; expected {names}.")
        actual_canonical = {
            path.name.removesuffix(".canonical.chondrite")
            for path in generated.glob("*.canonical.chondrite")
        }
        require(actual_canonical == names,
                f"Wrong canonical outputs: {actual_canonical}; expected {names}.")

    run(build, project, environment)
    assert_outputs({"Smoke.Sample"})
    probe("Smoke.Sample")
    compile_inputs = project / "obj" / "Debug" / "net10.0" / "smoke.compile-items.txt"
    require("Smoke.Sample.g.cs" in compile_inputs.read_text(encoding="utf-8-sig"),
            "Generated code was not passed to the C# compiler.")
    require(not (project / "bin" / "Debug" / "net10.0" / "Chondrite.Compile.dll").exists(),
            "The compiler leaked into application output.")

    print("[package smoke] Unchanged rebuild and explicit target/missing-output recovery", flush=True)
    snapshot = generated_snapshot(generated)
    run(build + ["--no-restore"], project, environment)
    require(generated_snapshot(generated) == snapshot,
            "An unchanged rebuild rewrote generated schema files.")
    (generated / "Smoke.Sample.g.cs").unlink()
    run([dotnet, "msbuild", "Consumer.csproj", "-restore", "-target:ChondriteCompile",
         "-property:Configuration=Debug", "-nologo", "-verbosity:minimal",
         "-maxcpucount:1", "-nr:false"], project, environment)
    assert_outputs({"Smoke.Sample"})

    print("[package smoke] Schema edits, additions, and removals", flush=True)
    old_canonical = (generated / "Smoke.Sample.canonical.chondrite").read_bytes()
    write_text(first_schema, schema("Smoke.Sample", "    1: nCount uint32\n"))
    run(build + ["--no-restore"], project, environment)
    new_canonical = (generated / "Smoke.Sample.canonical.chondrite").read_bytes()
    require(old_canonical != new_canonical and b"nCount uint32" in new_canonical,
            "A schema edit did not regenerate the canonical output.")
    probe("Smoke.Sample")

    second_schema = project / "Schemas" / "nested path" / "More Records.chondrite"
    write_text(second_schema, schema("Smoke.Second") + "\n" +
               schema("Smoke.Companion").split("\n", 1)[1])
    run(build + ["--no-restore"], project, environment)
    assert_outputs({"Smoke.Sample", "Smoke.Second", "Smoke.Companion"})
    probe("Smoke.Sample", "Smoke.Second", "Smoke.Companion")

    # Removing a definition changes the generated source set while the list of
    # input files stays identical. The assembly must lose that type as well.
    input_manifest = (generated / "sources.txt").read_bytes()
    write_text(second_schema, schema("Smoke.Second"))
    run(build + ["--no-restore"], project, environment)
    require((generated / "sources.txt").read_bytes() == input_manifest,
            "The in-file removal test unexpectedly changed the input file list.")
    assert_outputs({"Smoke.Sample", "Smoke.Second"})
    probe("Smoke.Sample", "Smoke.Second")

    print("[package smoke] Populated enabled/disabled build transitions", flush=True)
    run(build + ["--no-restore", "-property:ChondriteCompileEnabled=false"], project, environment)
    probe()
    disabled_compile_inputs = compile_inputs.read_text(encoding="utf-8-sig")
    require("Smoke.Sample.g.cs" not in disabled_compile_inputs and
            "Smoke.Second.g.cs" not in disabled_compile_inputs,
            "Disabling generation still passed generated sources to the C# compiler.")
    run(build + ["--no-restore"], project, environment)
    assert_outputs({"Smoke.Sample", "Smoke.Second"})
    probe("Smoke.Sample", "Smoke.Second")

    first_schema.unlink()
    run(build + ["--no-restore"], project, environment)
    assert_outputs({"Smoke.Second"})
    probe("Smoke.Second")

    second_schema.unlink()
    run(build + ["--no-restore"], project, environment)
    assert_outputs(set())
    probe()

    final_compile_inputs = compile_inputs.read_text(encoding="utf-8-sig")
    require("Smoke.Sample.g.cs" not in final_compile_inputs and
            "Smoke.Second.g.cs" not in final_compile_inputs,
            "The final build still references deleted generated code.")

    print("[package smoke] Explicit opt-out bypasses invalid schema input", flush=True)
    write_text(first_schema, "This deliberately invalid schema must not be compiled.\n")
    run(build + ["--no-restore", "-property:ChondriteCompileEnabled=false"], project, environment)
    assert_outputs(set())
    probe()

    exercise_editable(fixture, version, dotnet, work, environment)
    exercise_schema_only(fixture, version, dotnet, work, environment)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", required=True, type=Path, help="Actual .nupkg produced by dotnet pack")
    parser.add_argument("--dotnet", default="dotnet", help="dotnet executable, or an absolute SDK host path")
    parser.add_argument("--work", type=Path,
                        help="Retain an explicit empty work directory for debugging")
    arguments = parser.parse_args()
    package = arguments.package.resolve()
    require(package.is_file(), f"Package does not exist: {package}")
    resolved_dotnet = shutil.which(arguments.dotnet)
    require(resolved_dotnet is not None, f"Could not find dotnet executable: {arguments.dotnet}")
    dotnet = str(Path(str(resolved_dotnet)).resolve())
    print("[package smoke] Package structure", flush=True)
    version, _ = inspect_package(package)
    if arguments.work is not None:
        work = arguments.work.resolve()
        work.mkdir(parents=True, exist_ok=True)
        require(not any(work.iterdir()), f"The smoke work directory must be empty: {work}")
        exercise(package, version, dotnet, work)
    else:
        with tempfile.TemporaryDirectory(prefix="Chondrite package smoke ") as directory:
            exercise(package, version, dotnet, Path(directory))
    print(f"PASS: Chondrite.Compile {version} package and build integration.", flush=True)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (AssertionError, RuntimeError, OSError, zipfile.BadZipFile, ET.ParseError) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        sys.exit(1)

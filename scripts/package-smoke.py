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


def schema(name: str, extra: str = "") -> str:
    return (
        f"chondrite-schema 1\nschema {name}\n{{\n"
        f"    0: fValue float64\n{extra}}}\n"
    )


def exercise(package: Path, version: str, dotnet: str, work: Path) -> None:
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
             "--nologo", "-v:minimal", "-maxcpucount:1"]

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
         "-maxcpucount:1"], project, environment)
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


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", required=True, type=Path, help="Actual .nupkg produced by dotnet pack")
    parser.add_argument("--dotnet", default="dotnet", help="dotnet executable, or an absolute SDK host path")
    arguments = parser.parse_args()
    package = arguments.package.resolve()
    require(package.is_file(), f"Package does not exist: {package}")
    resolved_dotnet = shutil.which(arguments.dotnet)
    require(resolved_dotnet is not None, f"Could not find dotnet executable: {arguments.dotnet}")
    dotnet = str(Path(str(resolved_dotnet)).resolve())
    print("[package smoke] Package structure", flush=True)
    version, _ = inspect_package(package)
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

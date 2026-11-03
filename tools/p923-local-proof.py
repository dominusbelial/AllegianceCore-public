#!/usr/bin/env python3
"""Repeat P700 locally against HEAD or the working tree, with rig baseline settings.

All generated sources, builds and logs stay in .scratch/p923. Never contacts
remote game hosts. The HEAD snapshot uses tracked source files via git archive.
"""
import argparse
import io
import os
from pathlib import Path
import subprocess
import tarfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--base", action="store_true")
parser.add_argument("--seen", action="store_true")
parser.add_argument("--repeat", type=int, default=1)
args = parser.parse_args()
if args.base and args.seen:
    parser.error("HEAD has no seen-routing flag")
if args.repeat < 1:
    parser.error("--repeat must be positive")
label = "base" if args.base else ("on" if args.seen else "off")
work = ROOT / ".scratch/p923" / label
work.mkdir(parents=True, exist_ok=True)
for folder in ("tmp", "nuget-http"):
    (ROOT / ".scratch/p923" / folder).mkdir(parents=True, exist_ok=True)
source = ROOT
if args.base:
    source = work / "source"
    folders = ["src/Allegiance.Core", "src/Allegiance.Server", "src/Allegiance.Update",
               "src/Allegiance.Bot", "src/Allegiance.ClientCore", "tools/allegiance-mcp"]
    names = subprocess.check_output(["git", "ls-tree", "-r", "--name-only", "HEAD", *folders,
                                     "Directory.Build.props"], cwd=ROOT, text=True).splitlines()
    names = [n for n in names if Path(n).suffix in (".cs", ".csproj", ".props", ".targets", ".json")]
    archive = subprocess.check_output(["git", "archive", "HEAD", *names], cwd=ROOT)
    source.mkdir(exist_ok=True)
    with tarfile.open(fileobj=io.BytesIO(archive)) as tar:
        tar.extractall(source, filter="data")

project = work / "Allegiance.ClientCore.Tests.csproj"
refs = ["src/Allegiance.Server/Allegiance.Server.csproj", "src/Allegiance.Bot/Allegiance.Bot.csproj",
        "src/Allegiance.ClientCore/Allegiance.ClientCore.csproj", "tools/allegiance-mcp/allegiance-mcp.csproj"]
project.write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
<IsTestProject>true</IsTestProject><AssemblyName>Allegiance.ClientCore.Tests</AssemblyName>
<EnableDefaultCompileItems>false</EnableDefaultCompileItems>
</PropertyGroup><ItemGroup><Using Include="Xunit" />
<Compile Include="P660NextMatchLiveTests.cs" /><Compile Include="P700ShowcaseLiveTests.cs" />
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
<PackageReference Include="xunit" Version="2.9.3" />
<PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
<PackageReference Include="Xunit.SkippableFact" Version="1.5.85" />
''' + "\n".join(f'<ProjectReference Include="{source / r}" />' for r in refs)
    + f'<Compile Include="{ROOT / "tests/_shared/TestRequire.cs"}" Link="TestRequire.cs" />'
    + '</ItemGroup></Project>')
for name in ("P660NextMatchLiveTests.cs", "P700ShowcaseLiveTests.cs"):
    data = (ROOT / "tests/Allegiance.ClientCore.Tests" / name).read_text()
    if name.startswith("P700"):
        data = data.replace("server.Boot(P660NextMatchLiveTests.Config(false));",
                            "var config = P660NextMatchLiveTests.Config(false);\n"
                            + ("config.LiveSettings.SeenWarpRouting = true;\n" if args.seen else "")
                            + "server.Boot(config);")
    (work / name).write_text(data)
env = dict(os.environ, DOTNET_gcServer="1", ALLEGIANCE_BROADCAST_PARALLEL_BUILD="1",
           ALLEGIANCE_TRANSPORT_MODE="pool", ALLEGIANCE_SHIP_UPDATE_MODEL="native",
           ALLEGIANCE_SIM_NATIVE_GOTOPLAN="1", TMPDIR=str(ROOT / ".scratch/p923/tmp"),
           NUGET_HTTP_CACHE_PATH=str(ROOT / ".scratch/p923/nuget-http"))
dotnet = str(Path.home() / ".dotnet/dotnet")
with (work / "build.log").open("w") as log:
    subprocess.run([dotnet, "build", str(project)], cwd=ROOT, env=env, stdout=log,
                   stderr=subprocess.STDOUT, check=True)
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
failed = 0
for i in range(1, args.repeat + 1):
    with (work / f"run-{i}.log").open("w") as log:
        result = subprocess.run([dotnet, "test", str(project), "--no-build", "--filter",
                                 "FullyQualifiedName~Local_showcase_never_parks_with_target_for_five_seconds",
                                 "--logger", f"trx;LogFileName=run-{i}.trx", "--results-directory", str(work)],
                                cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT)
    tree = ET.parse(work / f"run-{i}.trx")
    row = tree.find(".//t:UnitTestResult", ns)
    print(f"{label} {i}: exit={result.returncode} {row.attrib}", flush=True)
    failed += result.returncode != 0
print(f"{label}: failures={failed}/{args.repeat}", flush=True)
raise SystemExit(1 if failed else 0)

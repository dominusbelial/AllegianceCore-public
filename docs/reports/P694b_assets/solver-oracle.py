"""Run after Debug and Release builds, from the repository root.

Compare the optimized hull support with the exact pre-P694b collision code.
The historical source is read with git show; no Git state is changed.
"""
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile

root = Path.cwd()
assets = root / "docs/reports/P694b_assets"
baseline = json.loads((assets / "solver-oracle.json").read_text())["baseline_commit"]
environment = os.environ.copy()
environment["NUGET_HTTP_CACHE_PATH"] = "/tmp/p694b-nuget-http"
with tempfile.TemporaryDirectory(prefix="p694b-oracle-") as directory:
    project = Path(directory)
    for name in ["Collider.cs", "NarrowPhase.cs", "Separate.cs"]:
        source = subprocess.check_output(
            ["git", "show", f"{baseline}:src/Allegiance.Core/Collision/{name}"], text=True
        )
        for name_before in ["Collider", "NarrowPhase", "CollisionTest", "Separate"]:
            source = re.sub(r"\b" + name_before + r"\b", "Baseline" + name_before, source)
        (project / name).write_text(source)
    (project / "Program.cs").write_text((assets / "solver-oracle.cs").read_text())
    (project / "oracle.csproj").write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
        '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
        '<Nullable>enable</Nullable></PropertyGroup><ItemGroup>'
        '<Reference Include="Allegiance.Core"><HintPath>'
        + str(root / "src/Allegiance.Core/bin/$(Configuration)/net10.0/Allegiance.Core.dll")
        + '</HintPath></Reference></ItemGroup></Project>'
    )
    for configuration in ["Release", "Debug"]:
        for attempt in range(1, 4):
            command = ["dotnet", "run", "--project", str(project / "oracle.csproj"),
                       "-c", configuration, "-p:UseSharedCompilation=false"]
            if attempt > 1:
                command.append("--no-build")
            print(configuration, attempt, flush=True)
            subprocess.run(command, check=True, cwd=root, env=environment)

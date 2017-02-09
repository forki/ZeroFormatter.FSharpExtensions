#r @"packages/build/FAKE/tools/FakeLib.dll"
open Fake
open Fake.Git
open Fake.AssemblyInfoFile
open Fake.ReleaseNotesHelper
open System
open System.IO
#if MONO
#else
#load "packages/build/SourceLink.Fake/tools/Fake.fsx"
open SourceLink
#endif
open Fake.Testing.NUnit3

let isAppVeyor = buildServer = AppVeyor

let isDotnetInstalled = DotNetCli.isInstalled() && not isAppVeyor

let outDir = "bin"

let project = "ZeroFormatter.FSharpExtensions"

let netCoreProject = "src/ZeroFormatter.FSharpExtensions.NETCore/ZeroFormatter.FSharpExtensions.NETCore.csproj"
let netCoreTestProject = "tests/ZeroFormatter.FSharpExtensions.NETCore.Tests/ZeroFormatter.FSharpExtensions.NETCore.Tests.fsproj"

let solutionFile  = sprintf "%s.sln" project

// Pattern specifying assemblies to be tested using NUnit
let testAssemblies = "tests/**/bin/Release/*Tests*.dll"

// Git configuration (used for publishing documentation in gh-pages branch)
// The profile where the project is posted
let gitOwner = "pocketberserer"
let gitHome = "https://github.com/" + gitOwner

// The name of the project on GitHub
let gitName = project

// The url for the raw files hosted
let gitRaw = environVarOrDefault "gitRaw" "https://raw.github.com/pocketberserker"

// --------------------------------------------------------------------------------------
// END TODO: The rest of the file includes standard build steps
// --------------------------------------------------------------------------------------

// Read additional information from the release notes document
let release = LoadReleaseNotes "RELEASE_NOTES.md"

// Helper active pattern for project types
let (|Fsproj|Csproj|Vbproj|) (projFileName:string) =
    match projFileName with
    | f when f.EndsWith("fsproj") -> Fsproj
    | f when f.EndsWith("csproj") -> Csproj
    | f when f.EndsWith("vbproj") -> Vbproj
    | _                           -> failwith (sprintf "Project file %s not supported. Unknown project type." projFileName)

// Generate assembly info files with the right version & up-to-date information
Target "AssemblyInfo" (fun _ ->
    let replace (oldValue:string) newValue (str:string) = str.Replace(oldValue, newValue)
    let getAssemblyInfoAttributes projectName =
        [ Attribute.Title projectName
          Attribute.Product project
          Attribute.Version release.AssemblyVersion
          Attribute.FileVersion release.AssemblyVersion
          Attribute.InformationalVersion release.NugetVersion ]

    let getProjectDetails projectPath =
        let projectName = System.IO.Path.GetFileNameWithoutExtension(projectPath)
        ( projectPath,
          projectName,
          System.IO.Path.GetDirectoryName(projectPath),
          (getAssemblyInfoAttributes projectName)
        )

    !! "src/**/*.??proj"
    |> Seq.map getProjectDetails
    |> Seq.filter (fun (_, projectName, _, _) ->
      not <| projectName.EndsWith("NETCore")
    )
    |> Seq.iter (fun (projFileName, projectName, folderName, attributes) ->
        match projFileName with
        | Fsproj -> CreateFSharpAssemblyInfo (("src" @@ folderName) @@ "AssemblyInfo.fs") attributes
        | Csproj -> CreateCSharpAssemblyInfo (("src" @@ folderName @@ "Properties") @@ "AssemblyInfo.cs") attributes
        | Vbproj -> CreateVisualBasicAssemblyInfo ((folderName @@ "My Project") @@ "AssemblyInfo.vb") attributes
        )
)

// Copies binaries from default VS location to exepcted bin folder
// But keeps a subdirectory structure for each project in the
// src folder to support multiple project outputs
Target "CopyBinaries" (fun _ ->
    !! "src/**/*.??proj"
    |> Seq.filter (fun p ->
      isDotnetInstalled
        || (not <| System.IO.Path.GetFileNameWithoutExtension(p).EndsWith("NETCore"))
    )
    |>  Seq.map (fun f -> ((System.IO.Path.GetDirectoryName f) @@ "bin/Release", outDir @@ (System.IO.Path.GetFileNameWithoutExtension f)))
    |>  Seq.iter (fun (fromDir, toDir) -> CopyDir toDir fromDir (fun _ -> true))
)

// --------------------------------------------------------------------------------------
// Clean build results

Target "Clean" (fun _ ->
  CleanDirs [outDir; "temp"]
  !! "./src/**/bin/Release"
  |> CleanDirs
)

// --------------------------------------------------------------------------------------
// Build library & test project

Target "Build" (fun _ ->
  !! solutionFile
  |> MSBuildRelease "" "Rebuild"
  |> ignore
)

Target "Build.NETCore" (fun _ ->

  DotNetCli.Restore (fun p ->
    { p with
        Project = netCoreProject
    }
  )
  DotNetCli.Build (fun p ->
    { p with
        Project = netCoreProject
    }
  )

  DotNetCli.Restore (fun p ->
    { p with
        Project = netCoreTestProject
    }
  )
  DotNetCli.Build (fun p ->
    { p with
        Project = netCoreTestProject
    }
  )
)

// --------------------------------------------------------------------------------------
// Run the unit tests using test runner
Target "RunTests" (fun _ ->
  !! testAssemblies
  |> NUnit3 (fun p ->
    { p with
        ToolPath = findToolInSubPath  "nunit3-console.exe" (currentDirectory @@ "packages" @@ "build" @@ "NUnit.ConsoleRunner" @@ "tools")
    }
  )
)

Target "RunTests.NETCore" (fun _ ->

  DotNetCli.Test (fun p ->
    { p with
        Project = netCoreTestProject
    }
  )
)

#if MONO
#else
// --------------------------------------------------------------------------------------
// SourceLink allows Source Indexing on the PDB generated by the compiler, this allows
// the ability to step through the source code of external libraries https://github.com/ctaggart/SourceLink

Target "SourceLink" (fun _ ->
    let baseUrl = sprintf "%s/%s/{0}/%%var2%%" gitRaw project
    !! "src/**/*.??proj"
    -- "src/**/*.shproj"
    |> Seq.iter (fun projFile ->
        let proj = VsProj.LoadRelease projFile
        SourceLink.Index proj.CompilesNotLinked proj.OutputFilePdb __SOURCE_DIRECTORY__ baseUrl
    )
)

#endif

// --------------------------------------------------------------------------------------
// Build a NuGet package

Target "NuGet.Pack" (fun _ ->

  let packagingDir = outDir @@ "nuget" @@ "ZeroFormatter.FSharpExtensions"
  [
    "bin/ZeroFormatter.FSharpExtensions/ZeroFormatter.FSharpExtensions.dll"
  ]
  |> CopyFiles (packagingDir @@ "lib" @@ "net45")

  let dependencies = [
    ("FSharp.Core", "4.0.0.1")
    ("ZeroFormatter", "1.6.2")
  ]

  NuGet (fun p ->
    {
      p with
        OutputPath = outDir
        WorkingDir = packagingDir
        Version = release.NugetVersion
        ReleaseNotes = toLines release.Notes
        DependenciesByFramework =
          [
            {
              FrameworkVersion = "net45"
              Dependencies = dependencies
            }
          ]
    }
  ) "src/ZeroFormatter.FSharpExtensions/ZeroFormatter.FSharpExtensions.nuspec"
)

Target "NuGet.AddNetCore" (fun _ ->
  if not isDotnetInstalled then failwith "You need to install .NET core to publish NuGet packages"

  DotNetCli.Pack (fun p ->
    { p with
        Project = netCoreProject
    }
  )

  let nupkg = sprintf "../../bin/ZeroFormatter.FSharpExtensions.%s.nupkg" release.NugetVersion
  let netcoreNupkg = sprintf "bin/Release/ZeroFormatter.FSharpExtensions.%s.nupkg" release.NugetVersion

  let mergeNupkg framework =
    let exitCode = Shell.Exec("dotnet", sprintf """mergenupkg --source "%s" --other "%s" --framework %s""" nupkg netcoreNupkg framework, "src/ZeroFormatter.FSharpExtensions.NETCore/")
    if exitCode <> 0 then failwithf "Command failed with exit code %i" exitCode

  mergeNupkg "netstandard1.6"
)

Target "PublishNuget" (fun _ ->
  Paket.Push(fun p ->
    { p with
        WorkingDir = outDir })
)

#load "paket-files/build/fsharp/FAKE/modules/Octokit/Octokit.fsx"
open Octokit

Target "Release" (fun _ ->
    StageAll ""
    Git.Commit.Commit "" (sprintf "Bump version to %s" release.NugetVersion)
    Branches.push ""

    Branches.tag "" release.NugetVersion
    Branches.pushTag "" "origin" release.NugetVersion

    // release on github
    createClient (getBuildParamOrDefault "github-user" "") (getBuildParamOrDefault "github-pw" "")
    |> createDraft gitOwner gitName release.NugetVersion (release.SemVer.PreRelease <> None) release.Notes
    // TODO: |> uploadFile "PATH_TO_FILE"
    |> releaseDraft
    |> Async.RunSynchronously
)

Target "NETCore" DoNothing

Target "NuGet" DoNothing

Target "All" DoNothing

"Clean"
  ==> "AssemblyInfo"
  ==> "Build"
  =?> ("NETCore", isDotnetInstalled)
  ==> "CopyBinaries"
  ==> "RunTests"
  ==> "All"

"Build.NETCore"
  ==> "RunTests.NETCore"
  ==> "NETCore"

"All"
#if MONO
#else
  =?> ("SourceLink", Pdbstr.tryFind().IsSome )
#endif
  ==> "NuGet.Pack"
  ==> "NuGet.AddNetCore"
  ==> "NuGet"

"NuGet"
  ==> "PublishNuget"
  ==> "Release"

RunTargetOrDefault "All"
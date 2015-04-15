// --------------------------------------------------------------------------------------
// FAKE build script
// --------------------------------------------------------------------------------------

#r @"packages/FAKE/tools/NuGet.Core.dll"
#r @"packages/FAKE/tools/FakeLib.dll"
open Fake
open Fake.Git
open Fake.AssemblyInfoFile
open Fake.ReleaseNotesHelper
open Fake.FileUtils
open System
#if MONO
#else
#load "packages/SourceLink.Fake/Tools/Fake.fsx"
open SourceLink
#endif


// --------------------------------------------------------------------------------------
// START TODO: Provide project-specific details below
// --------------------------------------------------------------------------------------

// Information about the project are used
//  - for version and project name in generated AssemblyInfo file
//  - by the generated NuGet package
//  - to run tests and to publish documentation on GitHub gh-pages
//  - for documentation, you also need to edit info in "docs/tools/generate.fsx"

// The name of the project
// (used by attributes in AssemblyInfo, name of a NuGet package and directory in 'src')
let project = "oxen"

// Short summary of the project
// (used as description in AssemblyInfo and as a short summary for NuGet package)
let summary = "fsharp implementation of Optimalbits/bull"

// Longer description of the project
// (used as a description for NuGet package; line breaks are automatically cleaned up)
let description = "fsharp implementation of Optimalbits/bull"

// List of author names (for NuGet package)
let authors = [ "Curit"; "albertjan"; "remkoboschker" ]

// Tags for your project (for NuGet package)
let tags = "redis queue fsharp oxen"

// File system information
let solutionFile  = "oxen.sln"

// Pattern specifying assemblies to be tested using NUnit
let testAssemblies = "tests/**/bin/Release/*Tests*.dll"

// Git configuration (used for publishing documentation in gh-pages branch)
// The profile where the project is posted
let gitHome = "https://github.com/curit"

// The name of the project on GitHub
let gitName = "oxen"

// The url for the raw files hosted
let gitRaw = environVarOrDefault "gitRaw" "https://raw.github.com/curit"

// --------------------------------------------------------------------------------------
// END TODO: The rest of the file includes standard build steps
// --------------------------------------------------------------------------------------

// Read additional information from the release notes document
Environment.CurrentDirectory <- __SOURCE_DIRECTORY__
let release = parseReleaseNotes (IO.File.ReadAllLines "RELEASE_NOTES.md")

let genFSAssemblyInfo (projectPath) =
    let projectName = System.IO.Path.GetFileNameWithoutExtension(projectPath)
    let basePath = "src/" + projectName
    let fileName = basePath + "/AssemblyInfo.fs"
    CreateFSharpAssemblyInfo fileName
      [ Attribute.Title (projectName)
        Attribute.Product project
        Attribute.Description summary
        Attribute.Version release.AssemblyVersion
        Attribute.FileVersion release.AssemblyVersion
        Attribute.InternalsVisibleTo "oxen.Tests" ]

let genCSAssemblyInfo (projectPath) =
    let projectName = System.IO.Path.GetFileNameWithoutExtension(projectPath)
    let basePath = "src/" + projectName + "/Properties"
    let fileName = basePath + "/AssemblyInfo.cs"
    CreateCSharpAssemblyInfo fileName
      [ Attribute.Title (projectName)
        Attribute.Product project
        Attribute.Description summary
        Attribute.Version release.AssemblyVersion
        Attribute.FileVersion release.AssemblyVersion ]

// Generate assembly info files with the right version & up-to-date information
Target "AssemblyInfo" (fun _ ->
  let fsProjs =  !! "src/**/*.fsproj"
  let csProjs = !! "src/**/*.csproj"
  fsProjs |> Seq.iter genFSAssemblyInfo
  csProjs |> Seq.iter genCSAssemblyInfo
)

// --------------------------------------------------------------------------------------
// Clean build results

Target "Clean" (fun _ ->
    CleanDirs ["bin"; "temp"; "StackExchange.Redis"]
)

Target "CleanDocs" (fun _ ->
    CleanDirs ["docs/output"]
)

Target "CloneStackExchangeRedis" (fun _ ->
    Repository.clone "./" "https://github.com/StackExchange/StackExchange.Redis" "StackExchange.Redis"
)

Target "BuildStackExchangeRedis" (fun _ ->
    Shell.Exec("sh", "monobuild.bash", "StackExchange.Redis") |> ignore
)

Target "CopyStackExchangeRedis" (fun _ ->
    "StackExchange.Redis/StackExchange.Redis/bin/mono/StackExchange.Redis.dll"
        |> CopyFile ("packages/StackExchange.Redis/lib/net45/")
)

// --------------------------------------------------------------------------------------
// Build library & test project

Target "Build" (fun _ ->

    !! solutionFile
    |> MSBuildRelease "" "Rebuild"
    |> ignore
)

// --------------------------------------------------------------------------------------
// Run the unit tests using test runner

Target "StartRedis" (fun _ ->
    async {
        Shell.Exec("./packages/Redis-64/redis-server.exe", "--maxheap 200mb") |> ignore
    } |> Async.Start
)

Target "RunNpmInstall" (fun _ ->
    match tryFindFileOnPath ("npm") with
    | Some x ->
        #if MONO
        let y = x
        #else
        let y = x + ".cmd"
        #endif
        trace ("npm found here: " + x)
        Shell.Exec(y, "install",  "tests/oxen.Tests/") |> ignore
    | None -> ()
)

Target "StartTestControlQueue" (fun _ ->
    let node =
        #if MONO
        "node"
        #else
        "node.exe"
        #endif
    do match tryFindFileOnPath (node) with
        | Some x ->
            trace ("node found here: " + x)
            Shell.AsyncExec(x, "test.js", "tests/oxen.Tests/") |> Async.Ignore |> Async.Start
        | None ->
            trace ("node not-found")
            ()
)

Target "RunTests" (fun _ ->
    !! testAssemblies
    |> xUnit (fun p ->
        { p with
            ToolPath = "./packages/xunit.runner.console/tools/xunit.console.exe"
            TimeOut = TimeSpan.FromMinutes 20.
            OutputDir = "./" })
)

#if MONO
#else
// --------------------------------------------------------------------------------------
// SourceLink allows Source Indexing on the PDB generated by the compiler, this allows
// the ability to step through the source code of external libraries https://github.com/ctaggart/SourceLink

Target "SourceLink" (fun _ ->
    let baseUrl = sprintf "%s/%s/{0}/%%var2%%" gitRaw (project.ToLower())
    use repo = new GitRepo(__SOURCE_DIRECTORY__)
    !! "src/**/*.fsproj"
    |> Seq.iter (fun f ->
        let proj = VsProj.LoadRelease f
        logfn "source linking %s" proj.OutputFilePdb
        let files = proj.Compiles -- "**/AssemblyInfo.fs"
        repo.VerifyChecksums files
        proj.VerifyPdbChecksums files
        proj.CreateSrcSrv baseUrl repo.Revision (repo.Paths files)
        Pdbstr.exec proj.OutputFilePdb proj.OutputFilePdbSrcSrv
    )
)
#endif

// --------------------------------------------------------------------------------------
// Build a NuGet package

Target "NuGet" (fun _ ->
    NuGet (fun p ->
        { p with
            Authors = authors
            Project = project
            Summary = summary
            Description = description
            Version = release.NugetVersion
            ReleaseNotes = String.Join(Environment.NewLine, release.Notes)
            Tags = tags
            OutputPath = "bin"
            AccessKey = getBuildParamOrDefault "nugetkey" ""
            Publish = hasBuildParam "nugetkey"
            Dependencies =
                [
                    "StackExchange.Redis", GetPackageVersion "./packages/" "StackExchange.Redis"
                    "log4net",  GetPackageVersion "./packages/" "log4net"
                    "Newtonsoft.Json", GetPackageVersion "./packages" "Newtonsoft.Json"
                ]
        })
        ("nuget/" + project + ".nuspec")
)

// --------------------------------------------------------------------------------------
// Generate the documentation

Target "GenerateReferenceDocs" (fun _ ->
    if not <| executeFSIWithArgs "docs/tools" "generate.fsx" ["--define:RELEASE"; "--define:REFERENCE"] [] then
      failwith "generating reference documentation failed"
)

Target "GenerateHelp" (fun _ ->
    if not <| executeFSIWithArgs "docs/tools" "generate.fsx" ["--define:RELEASE"; "--define:HELP"] [] then
      failwith "generating help documentation failed"
)

Target "GenerateDocs" DoNothing

// --------------------------------------------------------------------------------------
// Release Scripts

Target "ReleaseDocs" (fun _ ->
    let tempDocsDir = "temp/gh-pages"
    CleanDir tempDocsDir
    Repository.cloneSingleBranch "" (gitHome + "/" + gitName + ".git") "gh-pages" tempDocsDir

    CopyRecursive "docs/output" tempDocsDir true |> tracefn "%A"
    StageAll tempDocsDir
    Commit tempDocsDir (sprintf "Update generated documentation for version %s" release.NugetVersion)
    Branches.push tempDocsDir
)

Target "Release" (fun _ ->
    StageAll ""
    Commit "" (sprintf "Bump version to %s" release.NugetVersion)
    Branches.push ""

    Branches.tag "" release.NugetVersion
    Branches.pushTag "" "origin" release.NugetVersion
)

Target "BuildPackage" DoNothing

// --------------------------------------------------------------------------------------
// Run all targets by default. Invoke 'build <Target>' to override

Target "All" DoNothing

"Clean"
  ==> "AssemblyInfo"
  #if MONO
  ==> "CloneStackExchangeRedis"
  ==> "BuildStackExchangeRedis"
  ==> "CopyStackExchangeRedis"
  #endif
  ==> "Build"
  #if MONO
  #else
  ==> "StartRedis"
  #endif
  ==> "RunNpmInstall"
  ==> "StartTestControlQueue"
  ==> "RunTests"
  =?> ("GenerateReferenceDocs",isLocalBuild && not isMono)
  =?> ("GenerateDocs",isLocalBuild && not isMono)
  ==> "All"
  =?> ("ReleaseDocs",isLocalBuild && not isMono)

"All"
#if MONO
#else
  =?> ("SourceLink", Pdbstr.tryFind().IsSome )
#endif
  ==> "NuGet"
  ==> "BuildPackage"


"CleanDocs"
  ==> "GenerateHelp"
  ==> "GenerateReferenceDocs"
  ==> "GenerateDocs"

"ReleaseDocs"
  ==> "Release"

"BuildPackage"
  ==> "Release"

RunTargetOrDefault "All"

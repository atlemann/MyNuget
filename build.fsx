#r "paket: groupref Build //"
#load @".fake/build.fsx/intellisense.fsx"

#if !FAKE
  #r "netstandard"
#endif

open Fake.Core
open Fake.DotNet
open Fake.IO
open Fake.IO.Globbing.Operators
open Fake.IO.FileSystemOperators

let author = "My Name"
let product = "MyNuget"
let summary = "Silly NuGet package"

let nugetProject = "src" </> "MyFsNuget" </> "MyFsNuget.fsproj"
let solutionFile = "MyNuget.sln"

Target.create "Clean" (fun _ -> !!"./**/bin/" ++ "./**/obj/" |> Shell.cleanDirs)
Target.create "Test" (fun _ -> DotNet.test id solutionFile)

let createBuildTarget (semverInfo : SemVerInfo option) =
    Target.create "Build" (fun _ ->

        let customParams =
            [ yield ("Product", product)
              yield ("Authors", author)
              yield ("Owners", author)
              yield ("Description", summary)
              if semverInfo.IsSome then
                yield ("Version", semverInfo.Value.ToString()) ]

        DotNet.build (fun p ->
            { p with MSBuildParams = { p.MSBuildParams with Properties = customParams }})
            solutionFile)

/// Pass a single project to pack only that one.
/// Pass the .sln to pack all non-test projects.
let createPackTarget (semVerInfo : SemVerInfo) (projectOrSln : string)=
    Target.create "Pack" (fun _ ->

        let customParams =
            [ ("Product", product)
              ("Authors", author)
              ("Owners", author)
              ("Description", summary)
              ("Version", semVerInfo.ToString()) ]

        DotNet.pack (fun p ->
            { p with
                Configuration = DotNet.BuildConfiguration.Release
                MSBuildParams = { p.MSBuildParams with Properties = customParams }})
            projectOrSln)

Target.create "Push" (fun _ ->
    let nugetServer = Environment.environVarOrFail "NUGET_WRITE_URL"
    let apiKey = Environment.environVarOrFail "NUGET_WRITE_APIKEY"
    
    let result =
        !!"**/Release/*.nupkg"
        |> Seq.map (fun nupkg ->
            Trace.trace (sprintf "Publishing nuget package: %s" nupkg)
            (nupkg, DotNet.exec id "nuget" (sprintf "push %s --source %s --api-key %s" nupkg nugetServer apiKey)))
        |> Seq.filter (fun (_, p) -> p.ExitCode <> 0)
        |> List.ofSeq
        
    match result with
    | [] -> ()
    | failedAssemblies ->
        failedAssemblies
        |> List.map (fun (nuget, proc) -> 
            sprintf "Failed to push NuGet package '%s'. Process finished with exit code %d." nuget proc.ExitCode)
        |> String.concat System.Environment.NewLine
        |> exn
        |> raise)

let (|Release|CI|) input =
    if SemVer.isValid input then
        let semVer = SemVer.parse input
        Release semVer
    else
        CI

let branchName = Environment.environVarOrDefault "BRANCH_NAME" "1.2.3"

open Fake.Core.TargetOperators

let buildTarget =
    match branchName with
    | Release version ->
        createBuildTarget (Some version)
        createPackTarget version nugetProject
        Target.create "Release" ignore

        "Clean"
        ==> "Build"
//        ==> "Test"
        ==> "Pack"
//        ==> "Push"
        ==> "Release"

    | CI ->
        Target.create "CI" ignore
        
        createBuildTarget None

        "Clean"
        ==> "Build"
        ==> "Test"
        ==> "CI"

Target.runOrDefault buildTarget
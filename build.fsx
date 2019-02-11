open Fake.IO
#r "paket: groupref Build //"
#load @".fake/build.fsx/intellisense.fsx"

#if !FAKE
  #r "netstandard"
#endif

open Fake.Core
open Fake.DotNet
open Fake.IO.Globbing.Operators
open Fake.IO.FileSystemOperators

let author = "My Name"
let product = "MyNuget"
let summary = "Silly NuGet package"

let nugetProject = "src" </> "MyFsNuget" </> "MyFsNuget.fsproj"
let solutionFile = "MyNuget.sln"

let srcGlob = "src/**/*.?sproj"
let testsGlob = "tests/**/*.?sproj"

Target.create "Clean" (fun _ -> !!"./**/bin/" ++ "./**/obj/" |> Shell.cleanDirs)
Target.create "Build" (fun _ -> DotNet.build id solutionFile)
Target.create "Test" (fun _ -> DotNet.test id solutionFile)

let createAssemblyInfoTarget (semverInfo : SemVerInfo) =

    let assemblyVersion =
        sprintf "%d.%d.%d" semverInfo.Major semverInfo.Minor semverInfo.Patch

    let toAssemblyInfoAttributes projectName =
        [ AssemblyInfo.Title projectName
          AssemblyInfo.Product product
          AssemblyInfo.Description summary
          AssemblyInfo.Version assemblyVersion
          AssemblyInfo.FileVersion assemblyVersion ]

    // Helper active pattern for project types
    let (|Fsproj|Csproj|) (projFileName:string) =
        match projFileName with
        | f when f.EndsWith("fsproj") -> Fsproj
        | f when f.EndsWith("csproj") -> Csproj
        | _                           -> failwith (sprintf "Project file %s not supported. Unknown project type." projFileName)

    // Generate assembly info files with the right version & up-to-date information
    Target.create "AssemblyInfo" (fun _ ->
        let getProjectDetails projectPath =
            let projectName = System.IO.Path.GetFileNameWithoutExtension projectPath
            let directoryName = System.IO.Path.GetDirectoryName projectPath
            let assemblyInfo = projectName |> toAssemblyInfoAttributes
            (projectPath, directoryName, assemblyInfo)

        !! "src/**/*.??proj"
        |> Seq.map getProjectDetails
        |> Seq.iter (fun (projFileName, folderName, attributes) ->
            match projFileName with
            | Fsproj -> AssemblyInfoFile.createFSharp (folderName </> "AssemblyInfo.fs") attributes
            | Csproj -> AssemblyInfoFile.createCSharp ((folderName </> "Properties") </> "AssemblyInfo.cs") attributes)
    )

let createPackTarget (semVerInfo : SemVerInfo) (project : string)=
    Target.create "Pack" (fun _ ->

        // MsBuild uses ; and , as properties separator in the cli
        let escapeCommas (input : string) =
            input.Replace(",", "%2C")

        let customParams =
            [ (sprintf "/p:Authors=\"%s\"" author)
              (sprintf "/p:Owners=\"%s\"" author)
              (sprintf "/p:PackageVersion=\"%s\"" (semVerInfo.ToString()))
              (sprintf "/p:Description=\"%s\"" summary |> escapeCommas) ]
            |> String.concat " "

        DotNet.pack (fun p ->
            { p with
                Configuration = DotNet.BuildConfiguration.Release
                Common = DotNet.Options.withCustomParams (Some customParams) p.Common })
            project)

Target.create "Push" (fun _ ->
    let nugetServer = Environment.environVarOrFail "NUGET_WRITE_URL"
    let apiKey = Environment.environVarOrFail "NUGET_WRITE_APIKEY"
    
    let result =
        !!"**/Release/*.nupkg"
        |> Seq.map (fun nupkg ->
             Trace.trace (sprintf "Publishing nuget package: %s" nupkg)
             nupkg, DotNet.exec id "nuget" (sprintf "push %s --source %s --api-key %s" nupkg nugetServer apiKey))
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

let branchName = Environment.environVarOrDefault "BRANCH_NAME" ""

open Fake.Core.TargetOperators

let buildTarget =
    match branchName with
    | Release version ->
        createAssemblyInfoTarget version
        createPackTarget version nugetProject
        Target.create "Release" ignore

        "Clean"
        ==> "AssemblyInfo"
        ==> "Build"
        ==> "Test"
        ==> "Pack"
        ==> "Push"
        ==> "Release"

    | CI ->
        Target.create "CI" ignore

        "Clean"
        ==> "Build"
        ==> "Test"
        ==> "CI"

Target.runOrDefault buildTarget
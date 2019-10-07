﻿open System
open System.IO
open FSharp.Compiler.SourceCodeServices
open FSharp.Core
open Adaptify.Compiler

[<RequireQualifiedAccess>]
type Target =   
    | Exe
    | Library
    | WinExe
    | Module

[<RequireQualifiedAccess>]
type DebugType =
    | Off
    | Full
    | PdbOnly
    | Portable


type ProjectInfo =
    {
        project     : string
        isNewStyle  : bool
        references  : list<string>
        files       : list<string>
        defines     : list<string>
        target      : Target
        output      : Option<string>
        additional  : list<string>
        debug       : DebugType
    }

module ProjectInfo = 
    open Dotnet.ProjInfo
    open Dotnet.ProjInfo.Inspect
    open Dotnet.ProjInfo.Workspace

    let rec private projInfo additionalMSBuildProps (file : string) =

        let projDir = Path.GetDirectoryName file
        let runCmd exePath args = Utils.runProcess ignore projDir exePath (args |> String.concat " ")
    
        let additionalMSBuildProps = ("GenerateDomainTypes", "false") :: additionalMSBuildProps

        let netcore =
            match file with
            | ProjectRecognizer.NetCoreSdk -> true
            | _ -> false
    
        let projectAssetsJsonPath = Path.Combine(projDir, "obj", "project.assets.json")
        if netcore && not(File.Exists(projectAssetsJsonPath)) then
            let (s, a) = runCmd "dotnet" ["restore"; sprintf "\"%s\"" file]
            if s <> 0 then 
                failwithf "Cannot find restored info for project %s" file
    
        let getFscArgs = 
            if netcore then
                Dotnet.ProjInfo.Inspect.getFscArgs
            else
                let asFscArgs props =
                    let fsc = Microsoft.FSharp.Build.Fsc()
                    Dotnet.ProjInfo.FakeMsbuildTasks.getResponseFileFromTask props fsc
                Dotnet.ProjInfo.Inspect.getFscArgsOldSdk (asFscArgs >> Ok)

        let results =
            let msbuildExec =
                let msbuildPath =
                    if netcore then Dotnet.ProjInfo.Inspect.MSBuildExePath.DotnetMsbuild "dotnet"
                    else 
                        let all = 
                            BlackFox.VsWhere.VsInstances.getWithPackage "Microsoft.Component.MSBuild" true

                        let probes =
                            [
                                @"MSBuild\Current\Bin\MSBuild.exe"
                                @"MSBuild\15.0\Bin\MSBuild.exe"
                            ]

                        let msbuild =
                            all |> List.tryPick (fun i ->
                                probes |> List.tryPick (fun p ->
                                    let path = Path.Combine(i.InstallationPath, p)
                                    if File.Exists path then Some path
                                    else None
                                )
                            )

                        match msbuild with
                        | Some msbuild -> Dotnet.ProjInfo.Inspect.MSBuildExePath.Path msbuild
                        | None ->
                            failwith "no msbuild"
                Dotnet.ProjInfo.Inspect.msbuild msbuildPath runCmd

            let additionalArgs = additionalMSBuildProps |> List.map (Dotnet.ProjInfo.Inspect.MSBuild.MSbuildCli.Property)

            let log = ignore

            let projs = Dotnet.ProjInfo.Inspect.getResolvedP2PRefs

            file
            |> Inspect.getProjectInfos log msbuildExec [projs; getFscArgs] additionalArgs

        netcore, results

    let ofFscArgs (isNewStyle : bool) (path : string) (args : list<string>) =
        let mutable parsed = Set.empty

        let removeArg (a : string) = parsed <- Set.add a parsed

        let references = 
            args |> List.choose (fun a ->
                if a.StartsWith "-r:" then removeArg a; Some (a.Substring 3)
                elif a.StartsWith "--reference:" then removeArg a; Some (a.Substring 12)
                else None
            )

        let files =
            args |> List.filter (fun a -> 
                if not (a.StartsWith "-") then
                    let isAssemblyInfo = (Path.GetFileName(a).ToLower().EndsWith "assemblyinfo.fs")
                    removeArg a
                    not isAssemblyInfo
                else
                    false
            ) 

        let output =
            args |> List.tryPick (fun a ->
                if a.StartsWith "-o:" then removeArg a; Some (a.Substring 3)
                elif a.StartsWith "--out:" then removeArg a; Some (a.Substring 6)
                else None
            )

        let target =
            args |> List.tryPick (fun a ->
                if a.StartsWith "--target:" then
                    removeArg a
                    let target = a.Substring(9).Trim().ToLower()
                    match target with
                    | "exe" -> Some Target.Exe
                    | "library" -> Some Target.Library
                    | "winexe" -> Some Target.WinExe
                    | "module" -> Some Target.Module
                    | _ -> None
                else
                    None
            )

        let defines =
            args |> List.choose (fun a ->
                if a.StartsWith "-d:" then removeArg a; Some (a.Substring 3)
                elif a.StartsWith "--define:" then removeArg a; Some (a.Substring 9)
                else None
            )

        let hasDebug =
            args |> List.tryPick (fun a ->
                let rest = 
                    if a.StartsWith "-g" then Some (a.Substring(2).Replace(" ", ""))
                    elif a.StartsWith "--debug" then Some (a.Substring(7).Replace(" ", ""))
                    else None
                        
                match rest with
                | Some "" | Some "+" -> removeArg a; Some true
                | Some "-" -> removeArg a; Some false
                | _ -> None
            )

        let debugType =
            args |> List.tryPick (fun a ->
                let rest = 
                    if a.StartsWith "-g" then Some (a.Substring(2).Replace(" ", ""))
                    elif a.StartsWith "--debug" then Some (a.Substring(7).Replace(" ", ""))
                    else None
                        
                match rest with
                | Some ":full" -> removeArg a; Some DebugType.Full
                | Some ":pdbonly" -> removeArg a; Some DebugType.PdbOnly
                | Some ":portable" -> removeArg a; Some DebugType.Portable
                | _ -> None
            )

        let additional =
            args |> List.filter (fun a -> not (Set.contains a parsed))

        let debug =
            match hasDebug with
            | Some true -> defaultArg debugType DebugType.Full
            | Some false -> DebugType.Off
            | None -> defaultArg debugType DebugType.Full 

        {
            isNewStyle  = isNewStyle
            project = path
            //fscArgs     = args
            references  = references
            files       = files
            target      = defaultArg target Target.Library
            defines     = defines
            additional  = additional
            output      = output
            debug       = debug
        }

    let tryOfProject (additionalMSBuildProps : list<string * string>) (file : string) =
        let (netcore, info) = projInfo additionalMSBuildProps file

        match info with
        | Ok info ->
            let mutable errors = []
            let fscArgs = 
                info |> List.tryPick (fun res ->
                    match res with
                    | Ok res ->
                        match res with
                        | GetResult.FscArgs args -> Some (Ok args)
                        | _ -> None
                    | Error err ->
                        errors <- err :: errors
                        None
                )

            match fscArgs with
            | Some args -> 
                match args with
                | Ok args -> Ok (ofFscArgs netcore file args)
                | Error e -> Error [sprintf "%A" e]
            | None -> 
                let errors = 
                    errors |> List.map (fun e ->
                        match e with
                        | MSBuildFailed (code, err) ->
                            sprintf "msbuild error %d: %A" code err
                        | MSBuildSkippedTarget ->
                            sprintf "msbuild skipped target"
                        | UnexpectedMSBuildResult res ->
                            sprintf "msbuild error: %s" res
                    )
                Error errors
        | Error e ->
            match e with
            | MSBuildFailed (code, err) ->
                Error [sprintf "msbuild error %d: %A" code err]
            | MSBuildSkippedTarget ->
                Error [sprintf "msbuild skipped target"]
            | UnexpectedMSBuildResult res ->
                Error [sprintf "msbuild error: %s" res]

    let toFscArgs (info : ProjectInfo) =
        [
            match info.output with
            | Some o -> yield sprintf "-o:%s" o
            | None -> ()

            match info.debug with
            | DebugType.Off ->
                yield "--debug-"
            | DebugType.Full -> 
                yield "--debug:full"
            | DebugType.Portable -> 
                yield "--debug:portable"
            | DebugType.PdbOnly -> 
                yield "--debug:pdbonly"
                
            for a in info.additional do
                yield a

            for r in info.references do
                yield sprintf "-r:%s" r

            for f in info.files do
                yield f

        ]

let generateFilesForProject (checker : FSharpChecker) (info : ProjectInfo) =
        
    let args = ProjectInfo.toFscArgs info

    let projDir = Path.GetDirectoryName info.project
    let options =
        checker.GetProjectOptionsFromCommandLineArgs(info.project, List.toArray args, DateTime.Now)



    for file in info.files do
        let name = Path.GetFileNameWithoutExtension file

        let path = Path.Combine(projDir, file)
        let content = File.ReadAllText path
        let text = FSharp.Compiler.Text.SourceText.ofString content
        let (_parseResult, answer) = checker.ParseAndCheckFileInProject(file, 0, text, options) |> Async.RunSynchronously
        
        match answer with
        | FSharpCheckFileAnswer.Succeeded res ->
            let adaptors = 
                res.PartialAssemblySignature.Entities
                |> Seq.toList
                |> List.collect (fun e -> Adaptor.generate { qualifiedPath = Option.toList e.Namespace; file = path } e)
                |> List.groupBy (fun (f, _, _) -> f)
                |> List.map (fun (f, els) -> 
                    f, els |> List.map (fun (_,m,c) -> m, c) |> List.groupBy fst |> List.map (fun (m,ds) -> m, List.map snd ds) |> Map.ofList
                )
                |> Map.ofList

            let builder = System.Text.StringBuilder()

            for (ns, modules) in Map.toSeq adaptors do
                sprintf "namespace %s" ns |> builder.AppendLine |> ignore
                sprintf "open FSharp.Data.Adaptive" |> builder.AppendLine |> ignore
                for (m, def) in Map.toSeq modules do
                    sprintf "[<AutoOpen>]" |> builder.AppendLine |> ignore
                    sprintf "module rec %s =" m |> builder.AppendLine |> ignore
                    for d in def do
                        for l in d do
                            sprintf "    %s" l |> builder.AppendLine |> ignore

            if builder.Length > 0 then
                let file = Path.ChangeExtension(path, ".g.fs")
                File.WriteAllText(file, builder.ToString())



        | FSharpCheckFileAnswer.Aborted ->
            printfn "  aborted"

[<EntryPoint>]
let main argv =
    let projFiles = 
        if argv.Length > 0 then argv
        else [| Path.Combine(__SOURCE_DIRECTORY__, "..", "Example", "Example.fsproj") |]


    let checker = 
        FSharpChecker.Create(
            projectCacheSize = 0,
            keepAssemblyContents = true, 
            keepAllBackgroundResolutions = false
        )

    for projFile in projFiles do
        match ProjectInfo.tryOfProject [] projFile with
        | Ok info ->
            generateFilesForProject checker info

        | Error err ->
            printfn "ERRORS"
            for e in err do 
                printfn "  %s" e




    0 

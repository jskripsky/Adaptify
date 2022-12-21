﻿namespace Adaptify

open System
open System.IO
open Adaptify.Compiler
open FSharp.Compiler.Text

module PatchProject =
   open Microsoft.Build.Construction

   let patchProject (log : ILog) (projectFileName : string) (adaptifyFiles : Map<string, string>) = 
        let p = ProjectRootElement.Open(projectFileName)
        let mutable changed = false
        for g in p.ItemGroups do
            for i in g.Items do
                if i.ItemType = "Compile" && Path.GetExtension(i.Include) = ".fs" then
                    match Map.tryFind i.Include adaptifyFiles with
                    | None -> ()
                    | Some generatedFileName -> 
                        let correspondingGFile =
                            g.Items |> Seq.tryFind (fun o -> 
                                o.Include = generatedFileName
                            )
                        match correspondingGFile with
                        | Some gFile -> 
                            log.info Range.range0 "[PatchProject] skipped %s (already in project)" gFile.Include
                        | None -> 
                            let newElement = i.Clone() |> unbox<ProjectItemElement>
                            newElement.Include <- generatedFileName
                            g.InsertAfterChild(newElement,i)
                            changed <- true
        if changed then
            p.Save()
            log.info Range.range0 "[PatchProject] added .g files to project %s." projectFileName
        else
            log.info Range.range0 "[PatchProject] skipped all .g files in project -> nothing was added."
﻿namespace MBrace.Flow

open System

open Nessos.Streams

open MBrace.Core
open MBrace.Core.Internals
open MBrace.Store
open MBrace.Store.Internals

open MBrace.Flow
open MBrace.Flow.Internals

#nowarn "444"

type internal CloudChannel =

    /// <summary>Creates a CloudFlow from the ReceivePort of a CloudChannel</summary>
    /// <param name="channel">the ReceivePort of a CloudChannel.</param>
    /// <param name="degreeOfParallelism">The number of concurrently receiving tasks</param>
    /// <returns>The result CloudFlow.</returns>
    static member ToCloudFlow (channel : IReceivePort<'T>, degreeOfParallelism : int) : CloudFlow<'T> =
        { new CloudFlow<'T> with
            member self.DegreeOfParallelism = Some degreeOfParallelism
            member self.Apply<'S, 'R> (collectorf : Local<Collector<'T, 'S>>) (projection : 'S -> Local<'R>) (combiner : 'R [] -> Local<'R>) =
                cloud {
                    let! collector = collectorf 
                    let! workers = Cloud.GetAvailableWorkers() 
                    let workers = workers |> Array.sortBy (fun workerRef -> workerRef.Id)
                    let workerCount = defaultArg collector.DegreeOfParallelism workers.Length

                    let createTask () = local {
                        let! ctx = Cloud.GetExecutionContext()
                        let! collector = collectorf
                        let seq = Seq.initInfinite (fun _ -> Cloud.RunSynchronously(CloudChannel.Receive channel, ctx.Resources, ctx.CancellationToken))
                        let parStream = ParStream.ofSeq seq
                        let collectorResult = parStream.Apply (collector.ToParStreamCollector())
                        return! projection collectorResult
                    }
                    
                    let! targetedworkerSupport = Cloud.IsTargetedWorkerSupported
                    let! results = 
                        if targetedworkerSupport then
                            Seq.init workerCount (fun i -> (createTask (), workers.[i % workers.Length])) 
                            |> Cloud.Parallel
                        else
                            Seq.init workerCount (fun _ -> createTask ())
                            |> Cloud.Parallel

                    return! combiner results

                } }
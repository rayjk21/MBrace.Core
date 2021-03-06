﻿namespace MBrace.Flow.Tests

#nowarn "0444" // Disable mbrace warnings
open System
open System.Linq
open System.Collections.Generic
open System.IO
open FsCheck
open NUnit.Framework
open MBrace.Flow
open MBrace.Core
open MBrace.Store
open System.Text

// Helper type
type Separator = N | R | RN

[<TestFixture; AbstractClass>]
type ``CloudFlow tests`` () as self =
    let run (workflow : Cloud<'T>) = self.Run(workflow)
    let runLocally (workflow : Cloud<'T>) = self.RunLocally(workflow)

    let mkDummyWorker () = 
        { 
            new obj() with
                override __.Equals y =
                    match y with
                    | :? IWorkerRef as w -> w.Id = "foo"
                    | _ -> false

                // eirik's note to self: *ALWAYS* override .GetHashCode() if using in Seq.groupBy
                override __.GetHashCode() = hash "foo"

            interface IWorkerRef with 
                member __.Id = "foo" 
                member __.Type = "foo"
                member __.ProcessorCount = Environment.ProcessorCount
                member __.CompareTo y =
                    match y with
                    | :? IWorkerRef as w -> compare "foo" w.Id
                    | _ -> invalidArg "y" "invalid comparand"
        }

    abstract Run : Cloud<'T> -> 'T
    abstract RunLocally : Cloud<'T> -> 'T
    abstract FsCheckMaxNumberOfTests : int
    abstract FsCheckMaxNumberOfIOBoundTests : int

    // #region Cloud vector tests

    [<Test>]
    member __.``1. CloudVector : simple cloudvector`` () =
        let inputs = [|1L .. 1000000L|]
        let vector = inputs |> CloudFlow.OfArray |> CloudFlow.toCloudVector |> run
        let workers = Cloud.GetWorkerCount() |> run
        vector.IsCachingEnabled |> shouldEqual false
        vector.PartitionCount |> shouldEqual workers
        cloud { return! vector.ToEnumerable() } |> runLocally |> Seq.toArray |> shouldEqual inputs
        vector |> CloudFlow.sum |> run |> shouldEqual (Array.sum inputs)

    [<Test>]
    member __.``1. CloudVector : caching`` () =
        let inputs = [|1L .. 1000000L|]
        let vector = inputs |> CloudFlow.OfArray |> CloudFlow.toCachedCloudVector |> run
        let workers = Cloud.GetWorkerCount() |> run
        vector.PartitionCount |> shouldEqual workers
        vector.IsCachingEnabled |> shouldEqual true
        cloud { return! vector.ToEnumerable() } |> runLocally |> Seq.toArray |> shouldEqual inputs
        vector |> CloudFlow.sum |> run |> shouldEqual (Array.sum inputs)

    [<Test>]
    member __.``1. CloudVector : disposal`` () =
        let inputs = [|1 .. 1000000|]
        let vector = inputs |> CloudFlow.OfArray |> CloudFlow.toCloudVector |> run
        vector |> Cloud.Dispose |> run
        shouldfail(fun () -> cloud { return! vector.ToEnumerable() } |> runLocally |> Seq.iter ignore)

    [<Test>]
    member __.``1. CloudVector : merging`` () =
        let inputs = [|1 .. 1000000|]
        let N = 10
        let vector = inputs |> CloudFlow.OfArray |> CloudFlow.toCloudVector |> run
        let merged = CloudVector.Concat(Array.init N (fun _ -> vector))
        merged.PartitionCount |> shouldEqual (N * vector.PartitionCount)
        merged.IsCachingEnabled |> shouldEqual false
        merged.Partitions
        |> Seq.groupBy (fun p -> p.Path)
        |> Seq.map (fun (_,ps) -> Seq.length ps)
        |> Seq.forall ((=) N)
        |> shouldEqual true

        for i = 0 to merged.PartitionCount - 1 do
            merged.[i].Path |> shouldEqual (vector.[i % vector.PartitionCount].Path)

        cloud { return! merged.ToEnumerable() }
        |> runLocally
        |> Seq.toArray
        |> shouldEqual (Array.init N (fun _ -> inputs) |> Array.concat)

    [<Test>]
    member __.``1. CloudVector : merged disposal`` () =
        let inputs = [|1 .. 1000000|]
        let N = 10
        let vector = inputs |> CloudFlow.OfArray |> CloudFlow.toCloudVector |> run
        let merged = CloudVector.Concat(Array.init N (fun _ -> vector))
        merged |> Cloud.Dispose |> run
        shouldfail(fun () -> cloud { return! vector.ToEnumerable() } |> runLocally |> Seq.iter ignore)

    // #region Streams tests

    [<Test>]
    member __.``2. CloudFlow : ofArray`` () =
        let f(xs : int []) =
            let x = xs |> CloudFlow.OfArray |> CloudFlow.length |> run
            let y = xs |> Seq.map ((+)1) |> Seq.length
            Assert.AreEqual(y, int x)
        Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfTests)

    [<Test>]
    member __.``2. CloudFlow : ofCloudVector`` () =
        let f(xs : int []) =
            let CloudVector = xs |> CloudFlow.OfArray |> CloudFlow.toCloudVector |> run
            let x = CloudVector |> CloudFlow.OfCloudVector |> CloudFlow.length |> run
            let y = xs |> Seq.map ((+)1) |> Seq.length
            Assert.AreEqual(y, int x)
        Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfTests)


    [<Test>]
    member __.``2. CloudFlow : toCloudVector`` () =
        let f(xs : int[]) =            
            let x = xs |> CloudFlow.OfArray |> CloudFlow.map ((+)1) |> CloudFlow.toCloudVector |> run
            let y = xs |> Seq.map ((+)1) |> Seq.toArray
            Assert.AreEqual(y, cloud { return! x.ToEnumerable() } |> runLocally)
        Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfTests)


    [<Test>]
    member __.``2. CloudFlow : toCachedCloudVector`` () =
        let f(xs : string[]) =            
            let cv = xs |> CloudFlow.OfArray |> CloudFlow.map (fun x -> new StringBuilder(x)) |> CloudFlow.toCachedCloudVector |> run
            let x = cv |> CloudFlow.OfCloudVector |> CloudFlow.map (fun sb -> sb.GetHashCode()) |> CloudFlow.toArray |> run
            let y = cv |> CloudFlow.OfCloudVector |> CloudFlow.map (fun sb -> sb.GetHashCode()) |> CloudFlow.toArray |> run
            Assert.AreEqual(x, y)
        Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfTests)

    [<Test>]
    member __.``2. CloudFlow : cache`` () =
        let f(xs : int[]) =
            let v = xs |> CloudFlow.OfArray |> CloudFlow.toCloudVector |> run
//            v.Cache() |> run 
            let x = v |> CloudFlow.OfCloudVector |> CloudFlow.map  (fun x -> x * x) |> CloudFlow.toCloudVector |> run
            let x' = v |> CloudFlow.OfCloudVector |> CloudFlow.map (fun x -> x * x) |> CloudFlow.toCloudVector |> run
            let y = xs |> Array.map (fun x -> x * x)
            
            let _x = cloud { return! x.ToEnumerable() } |> runLocally |> Seq.toArray
            let _x' = cloud { return! x'.ToEnumerable() } |> runLocally |> Seq.toArray
            
            Assert.AreEqual(y, _x)
            Assert.AreEqual(_x', _x)
        Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfTests)

    [<Test>]
    member __.``2. CloudFlow : ofSeqs`` () =
        let tester (xs : int [] []) =
            let flowResult =
                xs
                |> CloudFlow.OfSeqs
                |> CloudFlow.map (fun x -> x * x)
                |> CloudFlow.sum
                |> run

            let seqResult =
                xs
                |> Seq.concat
                |> Seq.map (fun x -> x * x)
                |> Seq.sum

            Assert.AreEqual(seqResult, flowResult)

        Check.QuickThrowOnFail(tester, self.FsCheckMaxNumberOfTests)

    [<Test>]
    member __.``2. CloudFlow : ofCloudFiles with ReadAllText`` () =
        let f(xs : string []) =
            let cfs = xs 
                     |> Array.map(fun text -> CloudFile.WriteAllText(text))
                     |> Cloud.Parallel
                     |> run

            let x = cfs |> Array.map (fun cf -> cf.Path)
                        |> CloudFlow.OfTextFiles
                        |> CloudFlow.toArray
                        |> run
                        |> Set.ofArray

            let y = cfs |> Array.map (fun f -> __.RunLocally(cloud { return! CloudFile.ReadAllText f.Path }))
                        |> Set.ofSeq

            Assert.AreEqual(y, x)
        Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfIOBoundTests)

    [<Test>]
    member __.``2. CloudFlow : ofCloudFiles with ReadLines`` () =
        let f(xs : string [][]) =
            let cfs = xs 
                     |> Array.map(fun text -> CloudFile.WriteAllLines(text))
                     |> Cloud.Parallel
                     |> run

            let x = cfs |> Array.map (fun cf -> cf.Path)
                        |> CloudFlow.OfTextFilesByLine
                        |> CloudFlow.toArray
                        |> run
                        |> Set.ofArray
            
            let y = cfs |> Array.map (fun f -> __.RunLocally(cloud { return! CloudFile.ReadAllLines f.Path }))
                        |> Seq.collect id
                        |> Set.ofSeq

            Assert.AreEqual(y, x)
        Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfIOBoundTests)

    [<Test>]
    member __.``2. CloudFlow : ofCloudFilesByLine with ReadLines`` () =
        let f(xs : string [][]) =
            let cfs = xs 
                     |> Array.map(fun text -> CloudFile.WriteAllLines(text))
                     |> Cloud.Parallel
                     |> run

            let x = cfs |> Array.map (fun cf -> cf.Path)
                        |> CloudFlow.OfTextFilesByLine
                        |> CloudFlow.toArray
                        |> run
                        |> Set.ofArray
            
            let y = cfs |> Array.map (fun f -> __.RunLocally(cloud { return! CloudFile.ReadAllLines f.Path }))
                        |> Seq.collect id
                        |> Set.ofSeq

            Assert.AreEqual(y, x)
        Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfIOBoundTests)
    
    
    [<Test>]
    member __.``2. CloudFlow : ofTextFileByLine`` () =
        
        let f(xs : string [], separator : Separator) =
            let separator = 
                match separator with
                | N -> "\n" 
                | R -> "\r"
                | RN -> "\r\n"
            let cf = CloudFile.WriteAllText(xs |> String.concat separator) |> run
            let path = cf.Path

            let x = 
                path 
                |> CloudFlow.OfTextFileByLine
                |> CloudFlow.toArray
                |> run
                |> Array.sortBy id
                    
            
            let y = 
                __.RunLocally(cloud { return! CloudFile.ReadLines cf.Path })
                |> Seq.sortBy id
                |> Seq.toArray
                    
            Assert.AreEqual(y, x)
        Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfIOBoundTests)

    [<Test>]
    member __.``2. CloudFlow : ofCloudFiles with ReadAllLines`` () =
        let f(xs : string [][]) =
            let cfs = xs 
                     |> Array.map(fun text -> CloudFile.WriteAllLines(text))
                     |> Cloud.Parallel
                     |> run

            let x = cfs 
                        |> Array.map (fun cf -> cf.Path)
                        |> CloudFlow.OfTextFilesByLine
                        |> CloudFlow.toArray
                        |> run
                        |> Set.ofArray

            let y = cfs |> Array.map (fun f -> __.RunLocally(cloud { return! CloudFile.ReadAllLines f.Path }))
                        |> Seq.collect id
                        |> Set.ofSeq

            Assert.AreEqual(y, x)
        Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfIOBoundTests)


    [<Test>]
    member __.``2. CloudFlow : map`` () =
        let f(xs : int[]) =
            let x = xs |> CloudFlow.OfArray |> CloudFlow.map (fun n -> 2 * n) |> CloudFlow.toArray |> run
            let y = xs |> Seq.map (fun n -> 2 * n) |> Seq.toArray
            Assert.AreEqual(y, x)
        Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfTests)

    [<Test>]
    member __.``2. CloudFlow : filter`` () =
        let f(xs : int[]) =
            let x = xs |> CloudFlow.OfArray |> CloudFlow.filter (fun n -> n % 2 = 0) |> CloudFlow.toArray |> run
            let y = xs |> Seq.filter (fun n -> n % 2 = 0) |> Seq.toArray
            Assert.AreEqual(y, x)
        Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfTests)


    [<Test>]
    member __.``2. CloudFlow : collect`` () =
        let f(xs : int[]) =
            let x = xs |> CloudFlow.OfArray |> CloudFlow.collect (fun n -> [|1..n|] :> _) |> CloudFlow.toArray |> run
            let y = xs |> Seq.collect (fun n -> [|1..n|]) |> Seq.toArray
            Assert.AreEqual(y, x)
        Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfTests)

    [<Test>]
    member __.``2. CloudFlow : fold`` () =
        let f(xs : int[]) =
            let x = xs |> CloudFlow.OfArray |> CloudFlow.map (fun n -> 2 * n) |> CloudFlow.fold (+) (+) (fun () -> 0) |> run
            let y = xs |> Seq.map (fun n -> 2 * n) |> Seq.fold (+) 0 
            Assert.AreEqual(y, x)
        Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfTests)

    [<Test>]
    member __.``2. CloudFlow : sum`` () =
        let f(xs : int[]) =
            let x = xs |> CloudFlow.OfArray |> CloudFlow.map (fun n -> 2 * n) |> CloudFlow.sum |> run
            let y = xs |> Seq.map (fun n -> 2 * n) |> Seq.sum
            Assert.AreEqual(y, x)
        Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfTests)

    [<Test>]
    member __.``2. CloudFlow : length`` () =
        let f(xs : int[]) =
            let x = xs |> CloudFlow.OfArray |> CloudFlow.filter (fun n -> n % 2 = 0) |> CloudFlow.length |> run
            let y = xs |> Seq.filter (fun n -> n % 2 = 0) |> Seq.length
            Assert.AreEqual(y, int x)
        Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfTests)


    [<Test>]
    member __.``2. CloudFlow : countBy`` () =
        let f(xs : int[]) =
            let x = xs |> CloudFlow.OfArray |> CloudFlow.countBy id |> CloudFlow.toArray |> run
            let y = xs |> Seq.countBy id |> Seq.map (fun (k,c) -> k, int64 c) |> Seq.toArray
            Assert.AreEqual(set y, set x)
        Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfTests)


    [<Test>]
    member __.``2. CloudFlow : sortBy`` () =
        let f(xs : int[]) =
            let x = xs |> CloudFlow.OfArray |> CloudFlow.sortBy id 10 |> CloudFlow.toArray |> run
            let y = (xs |> Seq.sortBy id).Take(10).ToArray()
            Assert.AreEqual(y, x)
        Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfTests)

    [<Test>]
    member __.``2. CloudFlow : take`` () =
        let f (xs : int[], n : int) =
            let n = System.Math.Abs(n)
            let x = xs |> CloudFlow.OfArray |> CloudFlow.take n |> CloudFlow.toArray |> run
            Assert.AreEqual(min xs.Length n, x.Length)
        Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfTests)

    [<Test>]
    member __.``2. CloudFlow : withDegreeOfParallelism`` () =
        let f(xs : int[]) = 
            let r = xs 
                    |> CloudFlow.OfArray
                    |> CloudFlow.map (fun _ -> System.Diagnostics.Process.GetCurrentProcess().Id)
                    |> CloudFlow.withDegreeOfParallelism 1
                    |> CloudFlow.toArray
                    |> run
            let x = r
                    |> Set.ofArray
                    |> Seq.length
            if xs.Length = 0 then x = 0
            else x = 1
        Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfTests)

    [<Test>]
        member __.``2. CloudFlow : tryFind`` () =
            let f(xs : int[]) =
                let x = xs |> CloudFlow.OfArray |> CloudFlow.tryFind (fun n -> n = 0) |> run
                let y = xs |> Seq.tryFind (fun n -> n = 0) 
                x = y
            Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfTests)

        [<Test>]
        member __.``2. CloudFlow : find`` () =
            let f(xs : int[]) =
                let x = try xs |> CloudFlow.OfArray |> CloudFlow.find (fun n -> n = 0) |> run with | :? KeyNotFoundException -> -1
                let y = try xs |> Seq.find (fun n -> n = 0) with | :? KeyNotFoundException -> -1
                x = y
            Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfTests)

        [<Test>]
        member __.``2. CloudFlow : tryPick`` () =
            let f(xs : int[]) =
                let x = xs |> CloudFlow.OfArray |> CloudFlow.tryPick (fun n -> if n = 0 then Some n else None) |> run
                let y = xs |> Seq.tryPick (fun n -> if n = 0 then Some n else None) 
                x = y
            Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfTests)

        [<Test>]
        member __.``2. CloudFlow : pick`` () =
            let f(xs : int[]) =
                let x = try xs |> CloudFlow.OfArray |> CloudFlow.pick (fun n -> if n = 0 then Some n else None) |> run with | :? KeyNotFoundException -> -1
                let y = try xs |> Seq.pick (fun n -> if n = 0 then Some n else None)  with | :? KeyNotFoundException -> -1
                x = y
            Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfTests)

        [<Test>]
        member __.``2. CloudFlow : exists`` () =
            let f(xs : int[]) =
                let x = xs |> CloudFlow.OfArray |> CloudFlow.exists (fun n -> n = 0) |> run
                let y = xs |> Seq.exists (fun n -> n = 0) 
                x = y
            Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfTests)


        [<Test>]
        member __.``2. CloudFlow : forall`` () =
            let f(xs : int[]) =
                let x = xs |> CloudFlow.OfArray |> CloudFlow.forall (fun n -> n = 0) |> run
                let y = xs |> Seq.forall (fun n -> n = 0) 
                x = y
            Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfTests)


        [<Test>]
        member __.``2. CloudFlow : forall/CloudFiles`` () =
            let f(xs : int []) =
                let cfs = xs 
                         |> Array.map (fun x -> CloudFile.WriteAllText(string x))
                         |> Cloud.Parallel
                         |> run
                let x = cfs |> Array.map (fun cf -> cf.Path) |> CloudFlow.OfTextFiles |> CloudFlow.forall (fun x -> Int32.Parse(x) = 0) |> run
                let y = xs |> Seq.forall (fun n -> n = 0) 
                x = y
            Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfTests)



        [<Test>]
        member __.``2. CloudFlow : ofCloudChannel`` () =
            let f(_ : int) =
                let x =
                    cloud {
                        let! sendPort, receivePort = CloudChannel.New()
                        let! n =
                            Cloud.Choice [
                                cloud { 
                                    for i in [|1..1000|] do
                                        do! CloudChannel.Send(sendPort, i)
                                        do! Cloud.Sleep(100)
                                    return None
                                };
                                cloud {
                                    let! n =  
                                        CloudFlow.OfCloudChannel(receivePort, 1)
                                        |> CloudFlow.take 2
                                        |> CloudFlow.length
                                    return Some n
                                }]
                        return Option.get n
                    }
                    |> run
                x = 2L
            Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfIOBoundTests)


        [<Test>]
        member __.``2. CloudFlow : toCloudChannel`` () =
            let f(xs : int[]) =
                let sendPort, receivePort = CloudChannel.New() |> run
                let x = 
                    xs
                    |> CloudFlow.OfArray
                    |> CloudFlow.map (fun v -> v + 1)
                    |> CloudFlow.toCloudChannel sendPort
                    |> run
                let x = 
                    cloud {
                        let list = ResizeArray<int>()
                        for x in xs do 
                            let! v = CloudChannel.Receive(receivePort)
                            list.Add(v)
                        return list
                    } |> run
                let y = xs |> Seq.map (fun v -> v + 1) |> Seq.toArray
                (set x) = (set y)
            Check.QuickThrowOnFail(f, self.FsCheckMaxNumberOfIOBoundTests)



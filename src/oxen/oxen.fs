namespace oxen
open System
open StackExchange.Redis
//open Xunit
//open FsUnit.Xunit
open System.Threading.Tasks
open Newtonsoft.Json

[<AutoOpen>]
module OxenConvenience = 
    let toValueStr (x:string) = RedisValue.op_Implicit(x:string)
    let toValueI64 (x:int64) = RedisValue.op_Implicit(x:int64)
    let toValueI32 (x:int32) = RedisValue.op_Implicit(x:int32)

module Async =
    let inline awaitPlainTask (task: Task) = 
        // rethrow exception from preceding task if it fauled
        let continuation (t : Task) : unit =
            match t.IsFaulted with
            | true -> raise t.Exception
            | arg -> ()
        task.ContinueWith continuation |> Async.AwaitTask
 
    let inline startAsPlainTask (work : Async<unit>) = Task.Factory.StartNew(fun () -> work |> Async.RunSynchronously)
    
type Job<'a> = 
    {
        queue: Queue
        data: 'a
        jobId: int64
        opts: Map<string,string> option
        _progress: int
    }
    member this.toData () =  
        let jsData = JsonConvert.SerializeObject(this.data)
        let jsOpts = JsonConvert.SerializeObject(this.opts)

        [|
            HashEntry(toValueStr "data", toValueStr jsData)
            HashEntry(toValueStr "opts", toValueStr jsOpts)
            HashEntry(toValueStr "progress", toValueI32 this._progress )
        |]
    member this.remove () = async { raise (NotImplementedException ()) }
    member this.progress cnt = async { raise (NotImplementedException ()) }

    static member create (queue, jobId, data, opts) = 
        async { 
            let job = { queue = queue; data = data; jobId = jobId; opts = opts; _progress = 0 }
            let client:IDatabase = queue.client
            do! client.HashSetAsync (queue.toKey (jobId.ToString ()), job.toData ()) |> Async.awaitPlainTask
            return job 
        }


and Queue (name, db:IDatabase) as q =
    member this.toKey (kind:string) = RedisKey.op_Implicit(("bull:" + name + ":" + kind))
    member this.client = db
    member this.process (handler:(Job<_> * unit -> unit) -> unit) = () //process is reserved for future use
    member this.add (data, ?opts:Map<string,string>) = 
        async {
            let! jobId = q.client.StringIncrementAsync (this.toKey "id") |> Async.AwaitTask
            let! job = Job<_>.create (q, jobId, data, opts) 
            let key = this.toKey "wait"
            let! res = 
                Async.AwaitTask <|
                match opts with
                | None -> q.client.ListLeftPushAsync (key, toValueI64 jobId) 
                | Some x -> 
                    if x.ContainsKey "lifo" && bool.Parse (x.Item "lifo") 
                    then  q.client.ListRightPushAsync (key, toValueI64 jobId) 
                    else q.client.ListLeftPushAsync (key, toValueI64 jobId) 
            return job
        }
    member this.pause () = async { raise (NotImplementedException ()) }
    member this.resume () = async { raise (NotImplementedException ()) }
    member this.on () = async { raise (NotImplementedException ()) }
    member this.count () = async { raise (NotImplementedException ()) }
    member this.empty () = async { raise (NotImplementedException ()) }
    member this.getJob (id:string) = async { raise (NotImplementedException ()) }
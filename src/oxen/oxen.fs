namespace oxen

open System
open StackExchange.Redis
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
            HashEntry(toValueStr "progress", toValueI32 this._progress)
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


and Queue (name, db:IDatabase) as this =
    member x.toKey (kind:string) = RedisKey.op_Implicit(("bull:" + name + ":" + kind))
    member x.client = db
    member x.process (handler:(Job<_> * unit -> unit) -> unit) = () //process is reserved for future use
    member x.add (data, ?opts:Map<string,string>) = 
        async {
            let! jobId = this.client.StringIncrementAsync (this.toKey "id") |> Async.AwaitTask
            let! job = Job<_>.create (this, jobId, data, opts) 
            let key = this.toKey "wait"
            let! res = 
                Async.AwaitTask <|
                match opts with
                | None -> this.client.ListLeftPushAsync (key, toValueI64 jobId) 
                | Some opts -> 
                    if opts.ContainsKey "lifo" && bool.Parse (opts.Item "lifo") 
                    then this.client.ListRightPushAsync (key, toValueI64 jobId) 
                    else this.client.ListLeftPushAsync (key, toValueI64 jobId) 

            return job
        }
    member x.pause () = async { raise (NotImplementedException ()) }
    member x.resume () = async { raise (NotImplementedException ()) }
    member x.on () = async { raise (NotImplementedException ()) }
    member x.count () = async { raise (NotImplementedException ()) }
    member x.empty () = async { raise (NotImplementedException ()) }
    member x.getJob (id:string) = async { raise (NotImplementedException ()) }
namespace oxen

open System
open StackExchange.Redis
open System.Threading.Tasks
open Newtonsoft.Json
open System.Threading

[<AutoOpen>]
module OxenConvenience = 
    let toValueStr (x:string) = RedisValue.op_Implicit(x:string)
    let toValueI64 (x:int64) = RedisValue.op_Implicit(x:int64)
    let toValueI32 (x:int32) = RedisValue.op_Implicit(x:int32)
    let fromValueStr (x:RedisValue):string = string(x)
    let fromValueI64 (x:RedisValue):int64 = int64(x)
    let fromValueI32 (x:RedisValue):int = int(x)
    let valueToKeyLong (x:RedisValue):RedisKey = RedisKey.op_Implicit(int64(x).ToString ())

    /// <summary>
    /// Option coalesing operator 
    /// <c>
    /// None |? 4 -> 4
    /// Some 4 |? 3 -> 4
    /// </c>
    /// </summary>
    let (|?) (x: 'a option) (y: 'a) =  match x with | None -> y | Some z -> z

    let LOCK_RENEW_TIME = 5000.0

    exception JobHandleException of string

module Async =
    let inline awaitPlainTask (task: Task) = 
        // rethrow exception from preceding task if it fauled
        let continuation (t : Task) : unit =
            match t.IsFaulted with
            | true -> raise t.Exception
            | arg -> ()
        task.ContinueWith continuation |> Async.AwaitTask
 
    let inline startAsPlainTask (work : Async<unit>) = 
        Task.Factory.StartNew(fun () -> work |> Async.RunSynchronously)

type EventType = 
    | Completed
    | Progress
    | Failed
    | Paused
    | Resumed
    | NewJob

type Job<'a> = 
    {
        queue: Queue<'a>
        data: 'a
        jobId: int64
        opts: Map<string,string> option
        _progress: int
    }
    member this.lockKey () = this.queue.toKey((this.jobId.ToString ()) + ":lock")
    member this.toData () =  
        let jsData = JsonConvert.SerializeObject (this.data)
        let jsOpts = JsonConvert.SerializeObject (this.opts)
        [|
            HashEntry(toValueStr "id", toValueI64 this.jobId)
            HashEntry(toValueStr "data", toValueStr jsData)
            HashEntry(toValueStr "opts", toValueStr jsOpts)
            HashEntry(toValueStr "progress", toValueI32 this._progress)
        |]
    member this.progress progress = 
        async {
            let client:IDatabase = this.queue.client()
            do! client.HashSetAsync (
                    this.queue.toKey(this.jobId.ToString ()), 
                    [| HashEntry (toValueStr "progress", toValueI32 progress) |]
                ) |> Async.awaitPlainTask
            do! this.queue.emitJobEvent(EventType.Progress, this, progress)
        }
    member this.remove () = async { raise (NotImplementedException ()) }
    member this.takeLock (token, ?renew) = async {
            let nx = match renew with 
                     | Some x -> When.NotExists
                     | None -> When.Always     
            let value = (token.ToString ()) |> toValueStr
            let client:IDatabase = this.queue.client()
            return! 
                client.StringSetAsync (
                    this.lockKey (), 
                    value, 
                    Nullable (TimeSpan.FromMilliseconds (LOCK_RENEW_TIME)), 
                    nx 
                ) |> Async.AwaitTask
        }
    member this.renewLock token = this.takeLock (token, true)
    member this.releaseLock (token):Async<int> = async {
        let script = 
            "if redis.call(\"get\", KEYS[1]) == ARGV[1] \n\
            then \n\
            return redis.call(\"del\", KEYS[1]) \n\
            else \n\
            return 0 \n\
            end \n"

        let! result = 
            this.queue.client().ScriptEvaluateAsync (
                script, 
                [|this.lockKey()|], 
                [|(token.ToString ()) |> toValueStr|]
             ) |> Async.AwaitTask
        return result |> RedisResult.op_Explicit
    }
    member private this.moveToSet set =
        async {
            let queue = this.queue
            let activeList = queue.toKey("active")
            let dest = queue.toKey(set)
            let client:IDatabase = queue.client()
            let multi = client.CreateTransaction()
            do! multi.ListRemoveAsync (activeList, toValueI64 this.jobId) 
                |> Async.AwaitTask 
                |> Async.Ignore

            do! multi.SetAddAsync (dest, toValueI64 this.jobId) |> Async.AwaitTask |> Async.Ignore
            return! multi.ExecuteAsync() |> Async.AwaitTask                       
        }
    member private this.isDone list =
        async {
            let client:IDatabase = this.queue.client()
            return! client.SetContainsAsync(this.queue.toKey(list), toValueI64 this.jobId) |> Async.AwaitTask
        }
    member this.moveToCompleted () = this.moveToSet("completed")
    member this.moveToFailed () = this.moveToSet("failed")
    member this.isCompleted = this.isDone("completed");
    member this.isFailed = this.isDone("failed");
    
    static member create (queue, jobId, data:'a, opts) = 
        async { 
            let job = { queue = queue; data = data; jobId = jobId; opts = opts; _progress = 0 }
            let client:IDatabase = queue.client()
            do! client.HashSetAsync (queue.toKey (jobId.ToString ()), job.toData ()) |> Async.awaitPlainTask
            return job 
        }
    static member fromId<'a> (queue: Queue<'a>, jobId: RedisKey) = 
        async {
            let client = queue.client
            let! job = client().HashGetAllAsync (jobId) |> Async.AwaitTask
            //staan hash values altijd op dezelfde volgorde
            return Job.fromData (
                queue, 
                job.[0].Value |> fromValueI64, 
                job.[1].Value |> fromValueStr, 
                job.[2].Value |> fromValueStr, 
                job.[3].Value |> fromValueI32) 
        }
    static member fromData (queue:Queue<'a>, jobId: Int64, data: string, opts: string, progress: int) =
        let sData = JsonConvert.DeserializeObject<'a>(data)
        let sOpts = JsonConvert.DeserializeObject<Map<string, string> option>(opts)
        { queue = queue; data = sData; jobId = jobId; opts = sOpts; _progress = progress }

and OxenJobEvent<'a> =
    {
        job: Job<'a>
        data: obj option
        progress: int option
        err: exn option
    }

and OxenQueueEvent<'a> =
    {
        queue: Queue<'a>
    }

and Events<'a> = {
    Completed: IEvent<OxenJobEvent<'a>>
    Progress: IEvent<OxenJobEvent<'a>>
    Failed: IEvent<OxenJobEvent<'a>>
    Paused: IEvent<OxenQueueEvent<'a>>
    Resumed: IEvent<OxenQueueEvent<'a>>
    NewJob: IEvent<OxenJobEvent<'a>>
}

and LockRenewer<'a> (job:Job<'a>) =
    let cts = new CancellationTokenSource ()

    let rec lockRenewer (job:Job<'a>) = 
        async {
            do! job.renewLock() |> Async.Ignore
            do! Async.Sleep (int(LOCK_RENEW_TIME / 2.0)) 
            return! lockRenewer job
        }

    do Async.Start (lockRenewer job, cts.Token)

    interface IDisposable with
        member x.Dispose() =
            x.Dispose(true)
            GC.SuppressFinalize(x);

    member x.Dispose(disposing) = 
        if (disposing) then
            cts.Cancel ()

    override x.Finalize () =
        x.Dispose(false)
            
/// <summary>
/// The queue
/// </summary>
/// <param name="name">Name of the queue</param>
/// <param name="connection">Redis connectionstring</param>
and Queue<'a> (name, dbFactory:(unit -> IDatabase), subscriberFactory:(unit -> ISubscriber)) as this =
    let mutable paused = false
    let mutable processing = false

    do async {
           do! subscriberFactory().SubscribeAsync (
                    this.toChannel("jobchannel"),
                    (fun c v -> 
                        async {
                            let! job = Job.fromId(this, valueToKeyLong v)
                            do! this.emitJobEvent(NewJob, job)
                            ()
                        } |> Async.RunSynchronously)) |> Async.awaitPlainTask
               
           ()
       } |> Async.RunSynchronously

    let token = Guid.NewGuid ()

    let completedEvent = new Event<OxenJobEvent<'a>> ()
    let progressEvent = new Event<OxenJobEvent<'a>> ()
    let failedEvent = new Event<OxenJobEvent<'a>> ()
    let pausedEvent = new Event<OxenQueueEvent<'a>> ()
    let resumedEvent = new Event<OxenQueueEvent<'a>> ()
    let newJobEvent = new Event<OxenJobEvent<'a>> ()

    let once s = async { () }

    let processJob handler job = 
        async { 
            if paused then ()
            processing <- true
            use lr = new LockRenewer<'a>(job)
            try
                let! data = handler job 
                do! job.moveToCompleted () |> Async.Ignore
                do! this.emitJobEvent(Completed, job, data = data)
                data
            with 
                | _ as e -> 
                    do! job.moveToFailed () |> Async.Ignore
                    do! job.releaseLock () |> Async.Ignore
                    do! this.emitJobEvent(Failed, job, exn = e) 
        }

    let rec getNextJob () =
        async {
            do! this.on.NewJob |> Async.AwaitEvent |> Async.Ignore
            let! (gotIt:RedisValue) = this.moveJob(this.toKey("wait"), this.toKey("active")) 
                
            return! 
                match gotIt with 
                | g when g.HasValue ->this.getJob (valueToKeyLong g)
                | _ -> getNextJob ()
        }

    let rec processJobs handler = 
        async {
            let! job = getNextJob ()
            do! processJob handler job
            if not(paused) then 
                do! processJobs handler
        }

    let processStalledJob handler (job:Job<'a>) = 
        async { 
            let! lock = job.takeLock token
            match lock with
            | true -> 
                let key = this.toKey("completed");
                let! contains = 
                    this.client().SetContainsAsync (key, job.jobId |> toValueI64) 
                    |> Async.AwaitTask
                
                if contains then 
                    return! processJob handler job
                    
            | false -> ()
        }

    let processStalledJobs handler = 
        async {
            let! range = this.client().ListRangeAsync (this.toKey ("active"), 0L, -1L) |> Async.AwaitTask
            let! jobs = 
                range 
                |> Seq.map fromValueStr
                |> Seq.map RedisKey.op_Implicit
                |> Seq.map (fun x -> Job.fromId (this, x))
                |> Async.Parallel

            let stalledJobsHandler = processStalledJob handler

            return! jobs |> Seq.map stalledJobsHandler |> Async.Parallel |> Async.Ignore
        } 

    member x.``process`` (handler:Job<'a> -> Async<unit>) = 
        async {
            do! this.run handler
        }
    
    member x.run handler = 
        async {
            do! processStalledJobs handler
            do! processJobs handler
        }
    
    member x.add (data, ?opts:Map<string,string>) = 
        async {
            let! jobId = this.client().StringIncrementAsync (this.toKey "id") |> Async.AwaitTask
            let! job = Job<'a>.create (this, jobId, data, opts) 
            let key = this.toKey "wait"
            let! res = 
                Async.AwaitTask <|
                match opts with
                | None -> this.client().ListLeftPushAsync (key, toValueI64 jobId) 
                | Some opts -> 
                    if opts.ContainsKey "lifo" && bool.Parse (opts.Item "lifo") 
                    then this.client().ListRightPushAsync (key, toValueI64 jobId) 
                    else this.client().ListLeftPushAsync (key, toValueI64 jobId) 

            do! this.emitJobEvent(NewJob, job)

            return job
        }

    member x.pause () = 
        async {
            if paused then 
                return paused
            else 
                if processing then 
                    do! this.on.Completed |> Async.AwaitEvent |> Async.Ignore
                
                paused <- true
                do! this.emitQueueEvent(Paused, this)
                return paused
        }
        
    member x.resume handler = 
        async { 
            if paused then
                paused <- false
                do! this.emitQueueEvent(Resumed, this)
                do! this.run handler
            else
                failwith "Cannot resume running queue"
        }

    member x.count () = 
        async { 
            let multi = (this.client ()).CreateTransaction()
            let! waitLength = multi.ListLengthAsync (this.toKey("wait")) |> Async.AwaitTask
            let! pausedLength = multi.ListLengthAsync (this.toKey("paused")) |> Async.AwaitTask
            do! multi.ExecuteAsync () |> Async.AwaitTask |> Async.Ignore
            return [| waitLength; pausedLength; |] |> Seq.max
        }
    
    member x.empty () = 
        async { 
            let multi = (this.client ()).CreateTransaction()
            let! waiting = multi.ListRangeAsync (this.toKey("wait"), 0L, -1L) |> Async.AwaitTask
            let! paused = multi.ListRangeAsync (this.toKey("paused"), 0L, -1L) |> Async.AwaitTask
            do! multi.KeyDeleteAsync(this.toKey("wait")) |> Async.AwaitTask |> Async.Ignore
            do! multi.KeyDeleteAsync(this.toKey("paused")) |> Async.AwaitTask |> Async.Ignore
            do! multi.ExecuteAsync() |>  Async.AwaitTask |> Async.Ignore
            
            let jobKeys = Array.concat [|waiting; paused|]
            let multi2 = (this.client ()).CreateTransaction()
            jobKeys |> Seq.iter (fun k -> multi2.KeyDeleteAsync(valueToKeyLong k) |> Async.AwaitTask |> Async.RunSynchronously |> ignore)
            return! multi2.ExecuteAsync () |> Async.AwaitTask
        }

    member x.moveJob (src, dest) = this.client().ListRightPopLeftPushAsync(src, dest) |> Async.AwaitTask
    member x.getJob id = Job<'a>.fromId (this, id)
    member x.getJobs (queueType, ?isList, ?start, ?stop) =
        async {
            let key = this.toKey(queueType)
            let! jobsIds = 
                match isList |? false with 
                | true -> this.client().ListRangeAsync(key, (start |? 0L), (stop |? -1L)) |> Async.AwaitTask
                | false -> this.client().SetMembersAsync(key) |> Async.AwaitTask

            return!
                jobsIds
                |> Seq.map valueToKeyLong
                |> Seq.map this.getJob
                |> Async.Parallel
        }
    member x.getFailed () = this.getJobs "failed"
    member x.getCompleted () = this.getJobs "completed"
    member x.getWaiting () = this.getJobs "wait", true
    member x.getActive () = this.getJobs "active", true
        
    //Events
    member x.on = { 
        Paused = pausedEvent.Publish
        Resumed = resumedEvent.Publish
        Completed = completedEvent.Publish
        Progress = progressEvent.Publish
        Failed = failedEvent.Publish
        NewJob = newJobEvent.Publish
    }

    //Internals
    member internal x.toKey (kind:string) = RedisKey.op_Implicit ("bull:" + name + ":" + kind)
    member internal x.toChannel (kind:string) = RedisChannel.op_Implicit ("bull:" + name + ":" + kind)
    member internal x.emitQueueEvent (eventType, queue:Queue<'a>) = 
        async {
            match eventType with 
            | Paused -> pausedEvent.Trigger({ queue = queue });
            | Resumed -> resumedEvent.Trigger({ queue = queue });
            | _ -> failwith "Not a queue event!"
        }
    member internal x.emitJobEvent (eventType, job:Job<'a>, ?value, ?exn, ?data) = 
        async {
            let eventData = 
                {
                    job = job
                    progress = value
                    err = exn
                    data = None
                }
            
            match eventType with 
            | Completed -> completedEvent.Trigger(eventData)
            | Progress -> progressEvent.Trigger(eventData)
            | Failed -> failedEvent.Trigger(eventData)
            | NewJob -> newJobEvent.Trigger(eventData)
            | _ -> failwith "Not a job event!"
        }
    member internal x.client = dbFactory
namespace oxen

open System
open System.Collections.Generic
open System.Threading.Tasks
open System.Threading

open StackExchange.Redis

open Newtonsoft.Json
open Newtonsoft.Json.Serialization

/// Convenience methods to interop with StackExchange.Redis
[<AutoOpen>]
module OxenConvenience = 
    /// epoch
    let epoch = new DateTime (1970,1,1)
    /// make RedisValue from string
    let toValueStr (x:string) = RedisValue.op_Implicit(x:string)
    /// make RedisValue from long
    let toValueI64 (x:int64) = RedisValue.op_Implicit(x:int64)
    /// make RedisValue from int
    let toValueI32 (x:int32) = RedisValue.op_Implicit(x:int32)
    /// make RedisValue from float
    let toValueFloat (x:float) = RedisValue.op_Implicit(x:float)
    ///get int from RedisValue
    let fromValueI32 (x:RedisValue):int = x |> int
    ///get int64 from RedisValue
    let fromValueI64 (x:RedisValue):int64 = x |> int64
    ///get string from RedisValue
    let fromValueString (x:RedisValue):string = x |> string
    ///get float from RedisValue
    let fromValueFloat (x:RedisValue):float = x |> float
    /// get RedisValue from RedisKey with a long in the middle
    let valueToKeyLong (x:RedisValue):RedisKey = RedisKey.op_Implicit(int64(x).ToString ())
    /// get RedisChannel from RedisKey
    let keyToChannel (x:RedisKey):RedisChannel = RedisChannel.op_Implicit(x.ToString())

    /// <summary>
    /// Option coalesing operator 
    /// `
    /// None |? 4 -> 4
    /// Some 4 |? 3 -> 4
    /// `
    /// </summary>
    let (|?) (x: 'a option) (y: 'a) =  match x with | None -> y | Some z -> z

    /// get the entry from the hash that correspons with the name
    let getHashEntryByName (hash: HashEntry array) name =
        hash |> Seq.tryFind (fun h -> h.Name |> fromValueString = name)
    
    /// calc unix time from millisecs since 1-1-1970
    let toUnixTime (dt:DateTime) = (dt - epoch).TotalMilliseconds
           
    /// calc DateTime from millisecs since 1-1-1970
    let fromUnixTime (dt:float) =
         epoch + (dt |> TimeSpan.FromMilliseconds)
        
    /// lock renew time = 5000 milliseconds
    let LOCK_RENEW_TIME = 5000.0   

/// [omit]
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

/// [omit]
type EventType = 
    | Completed
    | Progress
    | Failed
    | Paused
    | Resumed
    | NewJob

/// [omit]
type ListType = 
    | List
    | Set
    | SortedSet

/// A job that can be added to the queue. Where the generic type specifies the type of the data property
type Job<'a> = 
    {
        /// the queue the job belongs to
        queue: Queue<'a>
        /// the data of the job
        data: 'a
        /// the job Id
        jobId: int64
        /// the job options
        opts: Map<string,string> option
        /// the progress
        _progress: int
        /// delay
        delay: float option
        /// timestamp
        timestamp: DateTime
    }
    static member private logger = LogManager.getLogger()
    member private this._logger = Job<'a>.logger
    /// returns the redis key for the lock for this job
    member this.lockKey () = this.queue.toKey((this.jobId.ToString ()) + ":lock")
    /// retuns a HashEntry [] containing the data as it's stored in redis
    member this.toData () =
        this._logger.Info "creating redis hash for job %i" this.jobId  
        let jsData = JsonConvert.SerializeObject (this.data)
        let jsOpts = JsonConvert.SerializeObject (this.opts |? Map.empty)
        [|
            HashEntry(toValueStr "data", toValueStr jsData)
            HashEntry(toValueStr "opts", toValueStr jsOpts)
            HashEntry(toValueStr "progress", toValueI32 this._progress)
            HashEntry(toValueStr "timestamp", this.timestamp |> toUnixTime |> toValueFloat)
        |] |> Array.append(
            match this.delay with 
            | None -> [||]
            | Some d -> [| HashEntry(toValueStr "delay", toValueFloat d) |]
        )


    /// report the progres of this job
    member this.progress progress = 
        async {
            this._logger.Info "reporting progress %i for job %i" progress this.jobId
            let client:IDatabase = this.queue.client()
            do! client.HashSetAsync (
                    this.queue.toKey(this.jobId.ToString ()), 
                    [| HashEntry (toValueStr "progress", toValueI32 progress) |]
                ) |> Async.awaitPlainTask
            do! this.queue.emitJobEvent(EventType.Progress, this, progress)
        }
    /// remove all traces of this job atomically
    member this.remove () = 
        async { 
            this._logger.Info "removeing job %i" this.jobId
            let script =
                "if (redis.call(\"SISMEMBER\", KEYS[5], ARGV[1]) == 0) and (redis.call(\"SISMEMBER\", KEYS[6], ARGV[1]) == 0) then\n
                  redis.call(\"LREM\", KEYS[1], 0, ARGV[1])\n
                  redis.call(\"LREM\", KEYS[2], 0, ARGV[1])\n
                  redis.call(\"ZREM\", KEYS[3], ARGV[1])\n
                  redis.call(\"LREM\", KEYS[4], 0, ARGV[1])\n
                end\n
                redis.call(\"SREM\", KEYS[5], ARGV[1])\n
                redis.call(\"SREM\", KEYS[6], ARGV[1])\n
                redis.call(\"DEL\", KEYS[7])\n"

            let keys = 
                [| "active"; "wait"; "delayed"; "paused"; "completed"; "failed"; this.jobId.ToString() |]  
                |> Array.map this.queue.toKey

            let client = this.queue.client ()
            return! client.ScriptEvaluateAsync (script, keys, [|this.jobId |> toValueI64|]) |> Async.AwaitTask |> Async.Ignore
        }
    /// take a lock on this job, tell other queue's you're working on it.
    member this.takeLock (token, ?renew) = 
        async {
            this._logger.Info "taking lock with token %A for job %i renewed %b" token this.jobId (renew |? false)
            let nx = match renew with 
                     | Some x when x -> When.Always
                     | _ -> When.NotExists
                     
            
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
    /// renew the lock on this job.
    member this.renewLock token = this.takeLock (token, true)
    
    /// release lock on this job
    member this.releaseLock (token) = 
        async {
            this._logger.Info "releasing lock with token %A for job %i" token this.jobId
            let script = 
                "if redis.call(\"get\", KEYS[1]) == ARGV[1] \n
                then \n
                return redis.call(\"del\", KEYS[1]) \n
                else \n
                return 0 \n
                end \n"

            let! result = 
                this.queue.client().ScriptEvaluateAsync (
                    script, 
                    [|this.lockKey()|], 
                    [|(token.ToString ()) |> toValueStr|]
                 ) |> Async.AwaitTask
            return int64(result)
        }

    /// retry job
    member this.retry () = 
        async {
            let client = this.queue.client ()
            let failed = this.queue.toKey "failed"
            let key = this.queue.toKey "wait"
            let multi = client.CreateTransaction()
            
            do multi.SetRemoveAsync(failed, toValueI64 this.jobId) |> ignore
            
            let res = 
                match this.opts with
                | None -> multi.ListLeftPushAsync (key, toValueI64 this.jobId) 
                | Some opts -> 
                    if opts.ContainsKey "lifo" && bool.Parse (opts.Item "lifo") 
                    then multi.ListRightPushAsync (key, toValueI64 this.jobId) 
                    else multi.ListLeftPushAsync (key, toValueI64 this.jobId) 
           
            let result =  multi.PublishAsync (this.queue.toKey("jobs") |> keyToChannel, toValueI64 this.jobId)
           
            do! multi.ExecuteAsync() |> Async.AwaitTask |> Async.Ignore
                       
            if result.Result < 1L then failwith "must have atleast one subscriber, me"

            return this
        }

    member private this.moveToSet (set, ?timestamp:float) =
        async {
            let queue = this.queue
            let client:IDatabase = queue.client()
            let multi = client.CreateTransaction()
            let activeList = queue.toKey("active")
            let dest = queue.toKey(set)
            match timestamp with 
            | None ->
                do multi.ListRemoveAsync (activeList, toValueI64 this.jobId) |> ignore
                do multi.SetAddAsync (dest, toValueI64 this.jobId) |> ignore
            | Some t ->
                let score = if t < 0. then 0. else t;
                multi.SortedSetAddAsync(dest, toValueI64 this.jobId, score) |> ignore
                multi.PublishAsync(dest |> keyToChannel, t |> toValueFloat) |> ignore

            return! multi.ExecuteAsync() |> Async.AwaitTask                        
        }

    member private this.isDone list =
        async {
            this._logger.Info "checking if job %i is done (%s)" this.jobId list
            let client:IDatabase = this.queue.client()
            return! client.SetContainsAsync(this.queue.toKey(list), toValueI64 this.jobId) |> Async.AwaitTask
        }

    /// move this job to the completed list.
    member this.moveToCompleted () = this.moveToSet("completed")

    /// move this job to the failed list.
    member this.moveToFailed () = this.moveToSet("failed")

    member this.moveToDelayed timestamp =
        this.moveToSet ("delayed", timestamp)
    
    /// see if the job is completed.
    member this.isCompleted = this.isDone("completed")
    
    /// see if job failed
    member this.isFailed = this.isDone("failed")
   
    /// create a new job (note: The job will be stored in redis but it won't be added to the waiting list. You should probably use `Queue.add`)
    static member create (queue, jobId, data:'a, opts) = 
        async { 
            Job<_>.logger.Info "creating job for queue %s with id %i and data %A and options %A" (queue:Queue<'a>).name jobId data (opts:Map<string, string> option)
            let delay = 
                match opts with 
                | Some o when (o.ContainsKey "delay") -> Some (o.["delay"] |> Double.Parse)
                | _ -> None

            let timestamp = 
                match opts with 
                | Some o when (o.ContainsKey "timestamp") -> o.["timestamp"] |> Double.Parse |> fromUnixTime
                | _ -> DateTime.Now

            let job = { queue = queue; data = data; jobId = jobId; opts = opts; _progress = 0; timestamp = timestamp; delay = delay }
            let client:IDatabase = queue.client()
            do! client.HashSetAsync (queue.toKey (jobId.ToString ()), job.toData ()) |> Async.awaitPlainTask
            return job 
        }

    /// get job from redis by job id and queue
    static member fromId<'a> (queue: Queue<'a>, jobId:int64) = 
        async {
            let client = queue.client
            let! job = client().HashGetAllAsync (queue.toKey(jobId.ToString())) |> Async.AwaitTask
            let jobHash = job |> getHashEntryByName 
            return Job.fromData (
                queue, 
                jobId,
                jobHash("data").Value.Value |> fromValueString, 
                jobHash("opts").Value.Value |> fromValueString, 
                jobHash("progress").Value.Value |> fromValueI32,
                jobHash("timestamp").Value.Value |> fromValueFloat,
                match jobHash("delay") with
                | None -> None
                | Some x when x.Value |> fromValueString = "undefined" -> None
                | Some x when x.Value |> fromValueFloat = 0. -> None
                | Some x -> x.Value |> fromValueFloat |> Some)
        }

    /// create a job from a redis hash
    static member fromData (queue:Queue<'a>, jobId: Int64, data: string, opts: string, progress: int, timestamp: float, delay: float option) =
        let sData = JsonConvert.DeserializeObject<'a>(data)
        let sOpts = 
            match JsonConvert.DeserializeObject<Dictionary<string, string>>(opts) with 
            | null -> None
            | _ as x -> x |> Seq.map (fun kv -> (kv.Key, kv.Value)) |> Map.ofSeq |> Some
        { queue = queue; data = sData; jobId = jobId; opts = sOpts; _progress = progress; delay = delay; timestamp = timestamp |> fromUnixTime }

/// a job event 
/// i.e.: Completed, Progress, Failed
and OxenJobEvent<'a> =
    {
        job: Job<'a>
        data: obj option
        progress: int option
        err: exn option
    }
/// a queue event 
/// i.e.: Paused, Resumed
and OxenQueueEvent<'a> =
    {
        queue: Queue<'a>
    }
/// [omit]
and OxenNewJobEvent = 
    {
        jobId: int64
    }
/// [omit]
and Events<'a> = {
    Completed: IEvent<OxenJobEvent<'a>>
    Progress: IEvent<OxenJobEvent<'a>>
    Failed: IEvent<OxenJobEvent<'a>>
    Paused: IEvent<OxenQueueEvent<'a>>
    Resumed: IEvent<OxenQueueEvent<'a>>
}
/// [omit]
and LockRenewer<'a> (job:Job<'a>, token:Guid) =
    static let logger = LogManager.getLogger()
    
    let cts = new CancellationTokenSource ()
    let rec lockRenewer (job:Job<'a>) = 
        async {
            do! job.renewLock token |> Async.Ignore
            do! Async.Sleep (int(LOCK_RENEW_TIME / 2.0)) 
            return! lockRenewer job
        }

    do logger.Info "starting lock renewer for job %i with token %A" job.jobId token
    do Async.Start (lockRenewer job, cts.Token)

    interface IDisposable with
        member x.Dispose() =
            x.Dispose(true)
            GC.SuppressFinalize(x)

    member x.Dispose(disposing) = 
        logger.Debug "disposing lock renewer for job %i and token %A" job.jobId token
        if (disposing) then
            cts.Cancel ()

    override x.Finalize () =
        x.Dispose(false)
            
/// <summary>
/// The queue
/// </summary>
/// <param name="name">Name of the queue</param>
/// <param name="dbFactory">a function returning a new instance of IDatabase</param>
/// <param name="subscriberFactory">a function returning a new instance of ISubscriber</param>
/// <param name="forceSequentialProcessing">a boolean specifying whether or not this queue will handle jobs sequentially, not in parallel</param>
and Queue<'a> (name, dbFactory:(unit -> IDatabase), subscriberFactory:(unit -> ISubscriber), ?forceSequentialProcessing:bool) as this =
    static let logger = LogManager.getLogger()
    static do JsonConvert.DefaultSettings <- Func<JsonSerializerSettings>(fun () -> 
        let settings = JsonSerializerSettings()
        settings.ContractResolver <- CamelCasePropertyNamesContractResolver()
        settings
    )

    let mutable paused = false
    let mutable processing = false
    let mutable delayedTimeStamp = Double.MaxValue
    let mutable delayedJobsCancellationTokenSource = new CancellationTokenSource ()

    let _toKey kind = RedisKey.op_Implicit ("bull:" + name + ":" + kind)

    
    let newJobChannel = _toKey("jobs") |> keyToChannel 
    let delayedJobChannel = _toKey("delayed") |> keyToChannel 

    let sub = subscriberFactory ()
    do sub.Subscribe(
                newJobChannel,
                (fun c v -> 
                    async {
                        let jobId = v |> fromValueI64
                        return! this.emitNewJobEvent(jobId)
                    } |> Async.RunSynchronously))
    
    do sub.Subscribe(
                delayedJobChannel,
                (fun c v -> 
                    async {
                        this.updateDelayTimer(v |> fromValueFloat)
                    } |> Async.RunSynchronously))

    do logger.Info "subscribed to %A" newJobChannel


    let token = Guid.NewGuid ()

    let completedEvent = new Event<OxenJobEvent<'a>> ()
    let progressEvent = new Event<OxenJobEvent<'a>> ()
    let failedEvent = new Event<OxenJobEvent<'a>> ()
    let pausedEvent = new Event<OxenQueueEvent<'a>> ()
    let resumedEvent = new Event<OxenQueueEvent<'a>> ()
    let newJobEvent = new Event<OxenNewJobEvent> ()
    let onNewJob = newJobEvent.Publish

    let processJob handler job = 
        async { 
            if paused then ()
            processing <- true
            use lr = new LockRenewer<'a>(job, token)
            try
                logger.Info "running handler on job %i queue %s" job.jobId name
                let! data = handler job 
                do! job.moveToCompleted () |> Async.Ignore
                do! this.emitJobEvent(Completed, job, data = data)
            with 
                | _ as e -> 
                    logger.Error "handler failed for job %i with exn %A for queue %s" job.jobId e name
                    do! job.moveToFailed () |> Async.Ignore
                    do! job.releaseLock token |> Async.Ignore
                    do! this.emitJobEvent(Failed, job, exn = e) 
        }

    let rec getNextJob () =
        async {
            let! (gotIt:RedisValue) = this.moveJob(this.toKey("wait"), this.toKey("active")) 
            match gotIt with 
            | g when g.HasValue -> return! this.getJob (g |> fromValueI64)
            | _ -> 
                do! onNewJob |> Async.AwaitEvent |> Async.Ignore
                return! getNextJob ()
        }

    let rec processJobs handler = 
        async {
            let! job = getNextJob ()
            match job.delay with 
            | Some d -> 
                let timestamp = (job.timestamp |> toUnixTime) + d |> float
                do! job.moveToDelayed(timestamp) |> Async.Ignore
                return! processJobs handler
            | None ->  
                match forceSequentialProcessing |? false with
                | true -> do! processJob handler job
                | false -> do processJob handler job |> Async.Start
            
                if not(paused) then 
                    return! processJobs handler
        }

    let processStalledJob handler (job:Job<'a>) = 
        async { 
            logger.Info "processing stalled job %i for queue %s" job.jobId name
            let! lock = job.takeLock token
            match lock with
            | true -> 
                let key = this.toKey("completed")
                let! contains = 
                    this.client().SetContainsAsync (key, job.jobId |> toValueI64) 
                    |> Async.AwaitTask
                
                if not(contains) then 
                    return! processJob handler job
                    
            | false -> ()
        }

    let processStalledJobs handler = 
        async {
            logger.Debug "processing stalled jobs for queue: %s" name
            let! range = 
                this.client().ListRangeAsync (this.toKey ("active"), 0L, -1L)
                |> Async.AwaitTask
            let! jobs = 
                range 
                |> Seq.map int64
                |> Seq.map (fun x -> Job.fromId (this, x))
                |> Async.Parallel

            let stalledJobsHandler = processStalledJob handler

            return! jobs |> Seq.map stalledJobsHandler |> Async.Parallel |> Async.Ignore
        } 

    /// create a new queue
    new (name, mp:ConnectionMultiplexer) = 
        Queue<'a>(name, mp.GetDatabase, mp.GetSubscriber)

    /// the name of the queue use this to uniquely identify the queue. (note: will be used in the redis keys like "bull:<queuename>:wait"
    member x.name = name

    /// start the queue. 
    /// Takes handler: `Job<'a> -> Async<unit>` that will be called for every job this queue handles.
    member x.``process`` (handler:Job<'a> -> Async<unit>) = 
        logger.Info "start processing queue %s" name
        this.run handler |> Async.Start
   
    /// run the handler. 
    /// note: (you should probably use: ``process`` but if you want to keep the returned async to control it cancel it for example, use this.)
    /// note 2: (async switches to a different thread) 
    member x.run handler = 
        async {
            do! Async.SwitchToNewThread ()
            do! [| (processStalledJobs handler); (processJobs handler) |] |> Async.Parallel |> Async.Ignore
        }
    
    /// add a job to the queue.
    member x.add (data, ?opts:(string * string) list) = 
        async {
            let opts = 
                match opts with 
                | Some o -> Some (o |> Map.ofList)
                | None -> None
            let! jobId = this.client().StringIncrementAsync (this.toKey "id") |> Async.AwaitTask
            logger.Info "adding job %i to the queue %s" jobId name
            let! job = Job<'a>.create (this, jobId, data, opts) 
            let key = this.toKey "wait"
            let multi = this.client().CreateTransaction()

            let res = 
                match opts with
                | None -> multi.ListLeftPushAsync (key, toValueI64 jobId) 
                | Some opts -> 
                    if opts.ContainsKey "lifo" && bool.Parse (opts.Item "lifo") 
                    then multi.ListRightPushAsync (key, toValueI64 jobId) 
                    else multi.ListLeftPushAsync (key, toValueI64 jobId) 
           
            let result =  multi.PublishAsync (newJobChannel, toValueI64 jobId)
           
            do! multi.ExecuteAsync() |> Async.AwaitTask |> Async.Ignore
                       
            if result.Result < 1L then failwith "must have atleast one subscriber, me"

            return job
        }

    /// pause the queue
    /// note: (waits for the last job to finish before pausing the queue.)
    /// note 2: (this turns a bit akward when the jobs run in parallel so it might just do two more jobs before it pauses, but it will pause I promise)
    member x.pause () = 
        async {
            logger.Info "pausing queue %s" name
            if paused then 
                return paused
            else 
                if processing then 
                    do! this.on.Completed |> Async.AwaitEvent |> Async.Ignore
                
                paused <- true
                do! this.emitQueueEvent Paused 
                return paused
        }
      
    /// resume a paused queue  
    member x.resume handler = 
        async { 
            logger.Info "resuming queue %s" name
            if paused then
                paused <- false
                do! this.emitQueueEvent Resumed
                this.run handler |> Async.Start
            else
                failwith "Cannot resume running queue"
        }

    /// return the length of the queue
    member x.count () = 
        async { 
            logger.Info "getting queue length for queue %s" name
            let multi = (this.client ()).CreateTransaction()
            let waitLength = multi.ListLengthAsync (this.toKey("wait"))
            let pausedLength = multi.ListLengthAsync (this.toKey("paused"))
            do! multi.ExecuteAsync () |> Async.AwaitTask |> Async.Ignore
            return [| waitLength.Result; pausedLength.Result; |] |> Seq.max
        }
    
    /// empty the queue doesn't remove the jobs just removes the wait list so it won't do anything else.
    member x.empty () = 
        async { 
            logger.Info "emptying queue %s" name
            let multi = (this.client ()).CreateTransaction()
            let waiting = multi.ListRangeAsync (this.toKey("wait"), 0L, -1L) 
            let paused = multi.ListRangeAsync (this.toKey("paused"), 0L, -1L) 
            do multi.KeyDeleteAsync(this.toKey("wait")) |> ignore
            do multi.KeyDeleteAsync(this.toKey("paused")) |> ignore
            do multi.ExecuteAsync() |> ignore
            
            let jobKeys = Array.concat [|waiting.Result; paused.Result|]
            let multi2 = (this.client ()).CreateTransaction()
            jobKeys |> Seq.iter (fun k -> multi2.KeyDeleteAsync(valueToKeyLong k) |> ignore)
            return! multi2.ExecuteAsync () |> Async.AwaitTask
        }
    
    /// move job from one list to another atomically
    member x.moveJob (src, dest) = 
        this.client().ListRightPopLeftPushAsync(src, dest) |> Async.AwaitTask

    /// get a job by it's id
    member x.getJob id = Job<'a>.fromId (this, id)

    /// get all jobs from the queue
    member x.getJobs (queueType, listType:ListType, ?start, ?stop) =
        async {
            logger.Info "getting %s jobs for queue %s" queueType name
            let key = this.toKey(queueType)
            let! jobIds =
                match listType with
                | List -> 
                    this.client().ListRangeAsync(key, (start |? 0L), (stop |? -1L)) |> Async.AwaitTask
                | Set ->
                    this.client().SetMembersAsync(key) |> Async.AwaitTask
                | SortedSet ->
                    this.client().SortedSetRangeByRankAsync(key, start|? 0L, stop |? -1L) |> Async.AwaitTask

            return!
                jobIds
                |> Seq.map int64
                |> Seq.map this.getJob
                |> Async.Parallel
        }

    /// retry jobs
    member x.retryJob (job:Job<'a>) = job.retry()

    /// get all failed jobs
    member x.getFailed () = this.getJobs ("failed", Set)
    /// get all completed jobs
    member x.getCompleted () = this.getJobs ("completed", Set)
    /// get waiting jobs
    member x.getWaiting () = this.getJobs ("wait", List)
    /// get active jobs
    member x.getActive () = this.getJobs ("active", List)
    /// get active jobs
    member x.getActive (start:int64, stop:int64) = this.getJobs ("active", List, start, stop)
    /// get delayed jobs
    member x.getDelayed () = this.getJobs ("delayed", SortedSet)
    /// get delayed jobs
    member x.getDelayed (start:int64, stop:int64) = this.getJobs ("delayed", List, start, stop)
        
    //Events
    member x.on = { 
        Paused = pausedEvent.Publish
        Resumed = resumedEvent.Publish
        Completed = completedEvent.Publish
        Progress = progressEvent.Publish
        Failed = failedEvent.Publish
    }

    //Internals
    member internal x.toKey kind = _toKey kind
    member internal x.emitQueueEvent (eventType) = 
        async {
            logger.Info "emitting new queue-event %A for queue %s" eventType name
            match eventType with 
            | Paused -> pausedEvent.Trigger({ queue = this })
            | Resumed -> resumedEvent.Trigger({ queue = this })
            | _ -> failwith "Not a queue event!"
        }

    member internal x.emitNewJobEvent jobId =
        async {
            newJobEvent.Trigger({ jobId = jobId })
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
            | _ -> failwith "Not a job event!"
        }
   
    member internal x.updateDelayTimer timestamp =
        if timestamp < delayedTimeStamp then
            delayedJobsCancellationTokenSource.Cancel()
            delayedJobsCancellationTokenSource <- new CancellationTokenSource()
            delayedTimeStamp <- timestamp
            let nextDelayedJob = timestamp - (DateTime.Now |> toUnixTime) |> int
            let nextDelayedJob = if nextDelayedJob < 0 then 0 else nextDelayedJob
            do Async.Start (async {
                do! Async.Sleep nextDelayedJob
                do! this.updateDelaySet delayedTimeStamp
                delayedTimeStamp <- Double.MaxValue
            }, delayedJobsCancellationTokenSource.Token)

    member internal x.updateDelaySet timestamp =
        async {
            let script = " 
                 local RESULT = redis.call(\"ZRANGE\", KEYS[1], 0, 0, \"WITHSCORES\")
                 local jobId = RESULT[1]
                 local score = RESULT[2]
                 if (score ~= nil) then
                  if (score <= ARGV[1]) then
                   redis.call(\"ZREM\", KEYS[1], jobId)
                   redis.call(\"LREM\", KEYS[2], 0, jobId)
                   redis.call(\"RPUSH\", KEYS[3], jobId)
                   redis.call(\"PUBLISH\", KEYS[4], jobId)
                   redis.call(\"HSET\", KEYS[5] .. jobId, \"delay\", 0)
                   local nextTimestamp = redis.call(\"ZRANGE\", KEYS[1], 0, 0, \"WITHSCORES\")[2]
                   if(nextTimestamp ~= nil) then
                    redis.call(\"PUBLISH\", KEYS[1], nextTimestamp)
                   end
                   return nextTimestamp
                  end
                 end"

            let keys = [|"delayed"; "active"; "wait"; "jobs"; ""|] |> Array.map this.toKey
            let client = this.client ()
            let! res =
                client.ScriptEvaluateAsync (script, keys, [|timestamp |> int64 |> toValueI64|]) 
                |> Async.AwaitTask 
                
            ()
        }

    member internal x.client = dbFactory
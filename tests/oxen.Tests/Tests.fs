module oxen.Tests

open System
open oxen
open Foq
open StackExchange.Redis
open Xunit
open FsUnit.Xunit
open System.Threading.Tasks

type Data = {
    value: string
}

let taskHash = Task.Factory.StartNew(fun () -> ())
let taskIncr = Task.Factory.StartNew(fun () -> 1L)
let taskLPush = Task.Factory.StartNew(fun () -> 1L)
let taskLong = Task.Factory.StartNew(fun () -> 1L)
let taskTrue = Task.Factory.StartNew(fun () -> true)
let taskJobHash = Task.Factory.StartNew(fun () -> 
    [|
        HashEntry(toValueStr "id", toValueI64 1L)
        HashEntry(toValueStr "data", toValueStr "{ \"value\": \"test\" }")
        HashEntry(toValueStr "opts", toValueStr "")
        HashEntry(toValueStr "progress", toValueI32 1)
    |])

type JobFixture () = 
    [<Fact>]
    let ``should create a new job from given json data`` () =
        // Given
        let q = Mock<Queue<Data>>().Create()

        // When
        let job = Job.fromData(q, 1L, "{ \"value\": \"test\" }", "", 1)

        // Then
        job.data.value |> should equal "test"
        job.jobId |> should equal 1L
        job._progress |> should equal 1

    [<Fact>]
    let ``should get a job from the cache and make it into a real one`` () =
        // Given
        let db = Mock<IDatabase>.With(fun d ->
            <@
                d.HashGetAllAsync (any(), any()) --> taskJobHash
            @>
        )

        let q = Queue<Data>("stuff", db)
        let key:RedisKey = RedisKey.op_Implicit("1")

        // When 
        let job = Job.fromId(q, key) |> Async.RunSynchronously
        
        // Then
        job.data.value |> should equal "test"
        job.jobId |> should equal 1L
        job._progress |> should equal 1

    [<Fact>]
    let ``should be able to take a lock on a job`` () =
        // Given
        let db = Mock<IDatabase>.With(fun d ->
            <@
                d.StringSetAsync (any(), any(), any(), any()) --> taskTrue
            @>
        )
        
        let q = Queue<Data>("test", db);
        let job = {
            jobId = 1L
            queue = q
            data = { value = "string" }
            opts = None
            _progress = 0
        }
        
        // When
        let taken = job.takeLock (Guid.NewGuid ()) |> Async.RunSynchronously

        // Then
        taken |> should be True
        verify <@ db.StringSetAsync (any(), any(), any(), any()) @> once

    
    [<Fact>]
    let ``should be able to renew a lock on a job`` () =
        // Given
        let db = Mock<IDatabase>.With(fun d ->
            <@
                d.StringSetAsync (any(), any(), any(), any()) --> taskTrue
            @>
        )
                
        let q = Queue<Data>("test", db);
        let job = {
            jobId = 1L
            queue = q
            data = { value = "string" }
            opts = None
            _progress = 0
        }
        
        // When
        let taken = job.takeLock (Guid.NewGuid (), true) |> Async.RunSynchronously

        // Then
        taken |> should be True
        verify <@ db.StringSetAsync (any(), any(), any(), When.NotExists) @> once

    [<Fact>]
    let ``should be able to move job to completed`` () = 
        async {
            // Given
            let trans = Mock<ITransaction>.With(fun t ->
                <@
                    t.ListRemoveAsync (any(), any(), any(), any()) --> taskLong
                    t.SetAddAsync (any(), (any():RedisValue), any()) --> taskTrue
                    t.ExecuteAsync () --> taskTrue
                @>
            )

            let db = Mock<IDatabase>.With(fun d -> 
                <@ 
                    d.HashSetAsync(any(), any()) --> taskHash
                    d.StringIncrementAsync(any()) --> taskIncr
                    d.ListLeftPushAsync(any(), any(), any(), any()) --> taskLPush
                    d.CreateTransaction() --> trans
                @>
            )
            let queue = Queue<Data> ("test", db)
            let! job = Job<Data>.create(queue, 1L, { value = "test" }, None)
            
            // When
            let! result = job.moveToCompleted() 

            // Then
            result |> should be True
            verify <@ trans.ExecuteAsync () @> once
            verify <@ trans.ListRemoveAsync (any(), any(), any(), any()) @> once
            verify <@ trans.SetAddAsync (any(), (any():RedisValue), any()) @> once
            verify <@ db.CreateTransaction () @> once
            ()
        } |> Async.RunSynchronously


type QueueFixture () =

    [<Fact>]
    let ``should be able to add a job to the queue`` () =
  
        // Given
        let db = Mock<IDatabase>.With(fun d -> 
            <@ 
                d.HashSetAsync(any(), any()) --> taskHash
                d.StringIncrementAsync(any()) --> taskIncr
                d.ListLeftPushAsync(any(), any(), any(), any()) --> taskLPush
            @>
        )
        let queue = Queue ("test", db)

        // When
        let job = queue.add ({value = "test"}) |> Async.RunSynchronously
    
        // Then
        job.data.value |> should equal "test"
        job.jobId |> should equal 1L
        verify <@ db.HashSetAsync(any(), any()) @> once
        verify <@ db.StringIncrementAsync(any()) @> once
        verify <@ db.ListLeftPushAsync(any(), any(), any(), any()) @> once

    [<Fact>]
    let ``toKey should return a key that works with bull`` () = 
        // Given
        let db = Mock<IDatabase>().Create();
        let queue = Queue ("test", db)

        // When
        let result = queue.toKey("stuff")

        // Then
        result |> should equal "bull:test:stuff"

    [<Fact>]
    let ``report progress and listen to event on queue`` () =
        async {
            // Given 
            let db = Mock<IDatabase>.With(fun d -> 
                <@ 
                    d.HashSetAsync(any(), any()) --> taskHash
                    d.StringIncrementAsync(any()) --> taskIncr
                    d.ListLeftPushAsync(any(), any(), any(), any()) --> taskLPush
                @>
            )

            let queue = Queue<Data> ("test", db)
            let! job = queue.add({ value = "test" })
            let eventFired = ref false
            queue.on.Progress.Add(
                fun e -> 
                    eventFired := true
                    match e.progress with 
                    | Some x -> x |> should equal 100
                    | None -> failwith "progress should not be null"
            )

            // When
            do! job.progress 100
        
            // Then 
            !eventFired |> should be True
            verify <@ db.ListLeftPushAsync(any(), any(), any(), any()) @> once
        } |> Async.RunSynchronously     


//type vl = {id:string; status:string}
//let redis = ConnectionMultiplexer.Connect("curittest.redis.cache.windows.net:6379,password=T/ncgOLWjN8DlIz3g/fzG9qgdTZiN+n2b4QCNQv3PzQ=")  
//let queue = Queue ("vragenlijstsessies", redis)
//queue.add({id = "TMART^SLAAP_V^1467^4522^C9[21PR899"; status = "klaar"}, None) |> Async.RunSynchronously |> ignore

// bulljs api
// Queue constructor Queue(naam, port, ip, { other node_redis options })
// queue.process(function(job, done){})
// queue.add(job, opts) opts.lifo opts{} returns promise
// queue.pause().then(function(){})
// queue.resume().then(function (){})
// queue.on(completed/failed/progress/paused/resumed)
// queue.count() returns promise
// queue.empty() returns promise
// queue.getJob(id) returns promise
// job.remove() returns promise



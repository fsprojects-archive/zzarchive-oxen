module oxen.Tests

open System
open System.IO
open oxen
open Foq
open StackExchange.Redis
open Xunit
open FsUnit.Xunit
open System.Threading.Tasks

type Data = {
    value: string
}

let taskUnit () = Task.Factory.StartNew(fun () -> ())
let taskIncr () = Task.Factory.StartNew(fun () -> 1L)
let taskLPush () = Task.Factory.StartNew(fun () -> 1L)
let taskLong () = Task.Factory.StartNew(fun () -> 1L)
let taskTrue () = Task.Factory.StartNew(fun () -> true)
let taskJobHash () = Task.Factory.StartNew(fun () -> 
    [|
        HashEntry(toValueStr "id", toValueI64 1L)
        HashEntry(toValueStr "data", toValueStr "{ \"value\": \"test\" }")
        HashEntry(toValueStr "opts", toValueStr "")
        HashEntry(toValueStr "progress", toValueI32 1)
    |])

let taskValues (value:int64) = Task.Factory.StartNew(fun () -> [| RedisValue.op_Implicit(value) |])

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
                d.HashGetAllAsync (any(), any()) --> taskJobHash()
            @>
        )
        let sub = Mock<ISubscriber>().Create();

        let q = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))
        
        // When 
        let job = Job.fromId(q, 1L) |> Async.RunSynchronously
        
        // Then
        job.data.value |> should equal "test"
        job.jobId |> should equal 1L
        job._progress |> should equal 1

    [<Fact>]
    let ``should be able to take a lock on a job`` () =
        // Given
        let db = Mock<IDatabase>.With(fun d ->
            <@
                d.StringSetAsync (any(), any(), any(), any()) --> taskTrue()
            @>
        )
        let sub = Mock<ISubscriber>().Create();

        let q = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))
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
                d.StringSetAsync (any(), any(), any(), any()) --> taskTrue()
            @>
        )
        let sub = Mock<ISubscriber>().Create();

        let q = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))
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
                    t.ListRemoveAsync (any(), any(), any(), any()) --> taskLong()
                    t.SetAddAsync (any(), (any():RedisValue), any()) --> taskTrue()
                    t.ExecuteAsync () --> taskTrue()
                @>
            )

            let db = Mock<IDatabase>.With(fun d -> 
                <@ 
                    d.HashSetAsync(any(), any()) --> taskUnit()
                    d.StringIncrementAsync(any()) --> taskIncr()
                    d.ListLeftPushAsync(any(), any(), any(), any()) --> taskLPush()
                    d.CreateTransaction() --> trans
                @>
            )
            let sub = Mock<ISubscriber>().Create();

            let q = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))
            let! job = Job<Data>.create(q, 1L, { value = "test" }, None)
            
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
                d.HashSetAsync(any(), any()) --> taskUnit()
                d.StringIncrementAsync(any()) --> taskIncr()
                d.ListLeftPushAsync(any(), any(), any(), any()) --> taskLPush()
            @>
        )
        let sub = Mock<ISubscriber>.With(fun s ->
            <@
                s.PublishAsync(any(), any(), any()) --> taskLong()
            @>
        )
        let eventFired = ref false
        let queue = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))
        
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
        let sub = Mock<ISubscriber>().Create();

        let queue = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))
        
        // When
        let result = queue.toKey("stuff")

        // Then
        result.ToString() |> should equal "bull:stuff:stuff"

    [<Fact>]
    let ``report progress and listen to event on queue`` () =
        async {
            // Given 
            let db = Mock<IDatabase>.With(fun d -> 
                <@ 
                    d.HashSetAsync(any(), any()) --> taskUnit()
                    d.StringIncrementAsync(any()) --> taskIncr()
                    d.ListLeftPushAsync(any(), any(), any(), any()) --> taskLPush()
                @>
            )
            let sub = Mock<ISubscriber>.With(fun s ->
                <@
                    s.PublishAsync(any(), any(), any()) --> taskLong()
                @>
            )

            let queue = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))
            
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
        
    [<Fact>]
    let ``should be able to get failed jobs`` () = 
        async {
            // Given
            let trans = Mock<ITransaction>.With(fun t ->
                <@
                    t.ListRemoveAsync (any(), any(), any(), any()) --> taskLong()
                    t.SetAddAsync (any(), (any():RedisValue), any()) --> taskTrue()
                    t.ExecuteAsync () --> taskTrue()
                @>
            )

            let db = Mock<IDatabase>.With(fun d -> 
                <@ 
                    d.HashSetAsync(any(), any()) --> taskUnit()
                    d.StringIncrementAsync(any()) --> taskIncr()
                    d.ListLeftPushAsync(any(), any(), any(), any()) --> taskLPush()
                    d.CreateTransaction() --> trans
                    d.SetMembersAsync(any()) --> taskValues(1L)
                    d.HashGetAllAsync(any()) --> taskJobHash()
                @>
            )
            let sub = Mock<ISubscriber>.With(fun s ->
                <@
                    s.PublishAsync(any(), any(), any()) --> taskLong()
                @>
            )

            let queue = Queue<Data>("stuff", (fun () -> db), (fun () -> sub))
            let! job = queue.add({ value = "test" });

            //When
            do! job.moveToFailed() |> Async.Ignore
            let! jobs = queue.getFailed() 

            //Then
            (jobs |> Seq.head).data.value |> should equal "test"

            let value = toValueI64 1L
            let key = queue.toKey("failed")
            verify <@ db.ListLeftPushAsync(any(), value) @> once
            verify <@ trans.ListRemoveAsync(any(), any(), any(), any()) @> once
            verify <@ trans.SetAddAsync(any(), value, any()) @> once
            verify <@ db.SetMembersAsync(key) @> once
            verify <@ db.HashGetAllAsync(any()) @> once
             
        } |> Async.RunSynchronously

    type IntegrationTests () = 
        do log4net.Config.XmlConfigurator.ConfigureAndWatch(FileInfo("log4net.config")) |> ignore

        [<Fact>]
        let ``should call handler when a new job is added`` () = 
            let mp = ConnectionMultiplexer.Connect("127.0.0.1, allowAdmin=true")
            
            let queue = Queue<Data>((Guid.NewGuid ()).ToString(), mp.GetDatabase, mp.GetSubscriber)

            let newJob = ref false

            do queue.``process`` (fun j -> async { newJob := true })
            
            async {
                let! job = queue.add({value = "test"})
                do! queue.on.Completed |> Async.AwaitEvent |> Async.Ignore
                let! active = queue.getActive()
                let! completed = queue.getCompleted()
                !newJob |> should be True
                (active |> Array.length) |> should equal 0
                (completed |> Array.length) |> should equal 1
            } |> Async.RunSynchronously

        [<Fact>]
        let ``should report the correct length of the queue`` () =
            let mp = ConnectionMultiplexer.Connect("127.0.0.1, allowAdmin=true")
            
            let queue = Queue<Data>((Guid.NewGuid ()).ToString(), mp.GetDatabase, mp.GetSubscriber)

            async {
                do! queue.add({ value = "bert1" }) |> Async.Ignore
                do! queue.add({ value = "bert2" }) |> Async.Ignore
                do! queue.add({ value = "bert3" }) |> Async.Ignore
                do! queue.add({ value = "bert4" }) |> Async.Ignore
                do! queue.add({ value = "bert5" }) |> Async.Ignore
                do! queue.add({ value = "bert6" }) |> Async.Ignore
                do! queue.add({ value = "bert7" }) |> Async.Ignore

                let! count = queue.count ()
                count |> should equal 7L
            } |> Async.RunSynchronously

        [<Fact>]
        let ``should be able to empty the queue`` () =
            let mp = ConnectionMultiplexer.Connect("127.0.0.1, allowAdmin=true")
            
            let queue = Queue<Data>((Guid.NewGuid ()).ToString(), mp.GetDatabase, mp.GetSubscriber)

            async {
                do! queue.add({ value = "bert1" }) |> Async.Ignore
                do! queue.add({ value = "bert2" }) |> Async.Ignore
                do! queue.add({ value = "bert3" }) |> Async.Ignore
                do! queue.add({ value = "bert4" }) |> Async.Ignore
                do! queue.add({ value = "bert5" }) |> Async.Ignore
                do! queue.add({ value = "bert6" }) |> Async.Ignore
                do! queue.add({ value = "bert7" }) |> Async.Ignore

                let! count = queue.count () 
                count |> should equal 7L
                do! queue.empty () |> Async.Ignore
                let! empty = queue.count () 
                empty |> should equal 0L
            } |> Async.RunSynchronously

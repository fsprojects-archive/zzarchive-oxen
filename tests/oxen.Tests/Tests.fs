module oxen.Tests

open System
open oxen
open Foq
open StackExchange.Redis
open Xunit
open FsUnit.Xunit
open System.Threading.Tasks
open Newtonsoft.Json

type Data = {
    value: string
}
let taskHash = Task.Factory.StartNew(fun () -> ())
let taskIncr = Task.Factory.StartNew(fun () -> 1L)
let taskLPush = Task.Factory.StartNew(fun () -> 1L)


[<Fact>]
let ``should be able to add a job to the queue`` () =
  
    // Given
    let db = Mock<IDatabase>.With(fun d -> 
        <@ 
            d.HashSetAsync((any()), (any())) --> taskHash
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



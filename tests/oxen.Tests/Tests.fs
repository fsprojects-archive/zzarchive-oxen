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
let taskIncr = Task.Factory.StartNew(fun () -> int64 1)
let taskLPush = Task.Factory.StartNew(fun () -> int64 1)


[<Fact>]
let ``should be able to add a job to the queue`` () =
  
    let db = Mock<IDatabase>.With(fun d -> 
        <@ 
            d.HashSetAsync((any()), (any())) --> taskHash
            d.StringIncrementAsync(any()) --> taskIncr
            d.ListLeftPushAsync(any(), any(), any()) --> taskLPush
        @>
    )

    let queue = Queue ("test", db)
    let pietje = queue.add ({value = "test"}, [("d","d")] |> Map.ofList )
    let job = pietje |> Async.RunSynchronously
    job.data.value |> should equal "test"

    verify <@ db.HashSetAsync(any(), any()) @> once
   
//type vl = {id:string; status:string}
//let redis = ConnectionMultiplexer.Connect("curittest.redis.cache.windows.net:6379,password=T/ncgOLWjN8DlIz3g/fzG9qgdTZiN+n2b4QCNQv3PzQ=")  
//let queue = Queue ("vragenlijstsessies", redis)
//queue.add({id = "TMART^SLAAP_V^1467^4522^C9[21PR899"; status = "klaar"}, None) |> Async.RunSynchronously |> ignore

//[<Fact>]
//let ``f`` () = ()

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



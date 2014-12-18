(*** hide ***)
// This block of code is omitted in the generated HTML documentation. Use 
// it to define helpers that you do not want to show in the documentation.
#I "../../bin"
#r "log4net.dll"
(**
oxen
===================

Documentation

<div class="row">
  <div class="span1"></div>
  <div class="span6">
    <div class="well well-small" id="nuget">
      oxen can be <a href="https://nuget.org/packages/oxen">installed from NuGet</a>:
      <pre>PM> Install-Package oxen</pre>
    </div>
  </div>
  <div class="span1"></div>
</div>

Example
-------

This example demonstrates using a function defined in this sample library.

*)
#r "oxen.dll"
#r "StackExchange.Redis"

open oxen
open StackExchange.Redis

module example =
    type QueueData = {
        value: string
    }


    let multiplexer = ConnectionMultiplexer.Connect("localhost")
    let queue = Queue<QueueData>("examplequeue", multiplexer.GetDatabase, multiplexer.GetSubscriber)

    queue.``process`` (fun job -> 
        async {
            printfn "handling job with id %i and value %s" job.jobId job.data.value
        }
    )

    queue.add({ value = "testing 123"})

(**
Some more info

Samples & documentation
-----------------------

 * [Tutorial](tutorial.html) contains a further explanation of oxen and contains examples of interop with bull.

 * [API Reference](reference/index.html) contains automatically generated documentation for all types, modules
   and functions in the library. This includes additional brief samples on using most of the
   functions.
 
Contributing and copyright
--------------------------

The project is hosted on [GitHub][gh] where you can [report issues][issues], fork 
the project and submit pull requests. If you're adding a new public API, please also 
consider adding [samples][content] that can be turned into a documentation.
The library is available under Public Domain license, which allows modification and 
redistribution for both commercial and non-commercial purposes. For more information see the 
[License file][license] in the GitHub repository. 

  [content]: https://github.com/curit/oxen/tree/master/docs/content
  [gh]: https://github.com/curit/oxen/
  [issues]: https://github.com/curit/oxen/issues
  [readme]: https://github.com/curit/oxen/master/README.md
  [license]: https://github.com/curit/oxen/blob/master/LICENSE.txt
*)

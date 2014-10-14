module oxen.Tests

open oxen
open Xunit
open FsUnit.Xunit

[<Fact>]
let ``hello returns 42`` () =
  let result = Library.hello 42
  printfn "%i" result
  result |> should equal 42

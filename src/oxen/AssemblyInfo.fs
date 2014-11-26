namespace System
open System.Reflection
open System.Runtime.CompilerServices

[<assembly: AssemblyTitleAttribute("oxen")>]
[<assembly: AssemblyProductAttribute("oxen")>]
[<assembly: AssemblyDescriptionAttribute("fsharp client for bull.js")>]
[<assembly: AssemblyVersionAttribute("0.0.0")>]
[<assembly: AssemblyFileVersionAttribute("0.0.0")>]
[<assembly: InternalsVisibleToAttribute("oxen.Tests")>]
do ()

module internal AssemblyVersionInformation =
    let [<Literal>] Version = "0.0.0"

namespace System
open System.Reflection
open System.Runtime.CompilerServices

[<assembly: AssemblyTitleAttribute("oxen")>]
[<assembly: AssemblyProductAttribute("oxen")>]
[<assembly: AssemblyDescriptionAttribute("fsharp implementation of Optimalbits/bull")>]
[<assembly: AssemblyVersionAttribute("0.2.1")>]
[<assembly: AssemblyFileVersionAttribute("0.2.1")>]
[<assembly: InternalsVisibleToAttribute("oxen.Tests")>]
do ()

module internal AssemblyVersionInformation =
    let [<Literal>] Version = "0.2.1"

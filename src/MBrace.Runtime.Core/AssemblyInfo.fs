﻿namespace System
open System.Reflection

[<assembly: AssemblyTitleAttribute("MBrace.Core")>]
[<assembly: AssemblyProductAttribute("MBrace.Core")>]
[<assembly: AssemblyCompanyAttribute("Nessos Information Technologies")>]
[<assembly: AssemblyCopyrightAttribute("© Nessos Information Technologies.")>]
[<assembly: AssemblyTrademarkAttribute("MBrace")>]
[<assembly: AssemblyVersionAttribute("0.9.9")>]
[<assembly: AssemblyFileVersionAttribute("0.9.9")>]
do ()

module internal AssemblyVersionInformation =
    let [<Literal>] Version = "0.9.9"

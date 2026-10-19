// Regenerates chunk-uri-dotnet.tsv: for each endpoint in its first column, the AbsoluteUri .NET 10 builds for the chunk
// request (the body of CultMeshHttpsContentTransportConnector.BuildChunkUri; recordKey "k" and the empty-content hash),
// or "THROW <exception type>" where UriBuilder refuses it. The expectations in content.test.ts are this file's output and
// nothing else; never edit the second column by hand.
//   docker run --rm -v "$PWD":/w -w /w mcr.microsoft.com/dotnet/sdk:10.0 dotnet fsi packages/cultmesh-browser/test/fixtures/chunk-uri-dotnet.fsx
open System
open System.IO
let hex = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
let path = Path.Combine(__SOURCE_DIRECTORY__, "chunk-uri-dotnet.tsv")
let rows =
  File.ReadAllLines(path, Text.UTF8Encoding(false))
  |> Array.filter (fun line -> line <> "")
  |> Array.map (fun line ->
    let endpoint = line.Split('\t').[0]
    try
      let b = UriBuilder(endpoint)
      let sep = if String.IsNullOrWhiteSpace(b.Query) then "" else b.Query.TrimStart('?') + "&"
      b.Query <- sep + "chunkHash=" + Uri.EscapeDataString(hex) + "&recordKey=" + Uri.EscapeDataString("k")
      endpoint + "\t" + b.Uri.AbsoluteUri
    with e -> endpoint + "\tTHROW " + e.GetType().Name)
File.WriteAllText(path, String.Join("\n", rows) + "\n", Text.UTF8Encoding(false))

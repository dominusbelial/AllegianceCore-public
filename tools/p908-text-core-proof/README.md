# P908 native AOT text-core proof

This local proof exports `PCore001.igc`, starts the published native server with
only the text folder, and joins it using the production `ConnectionSession` and
default client core resolver. It checks version 1337 on both ends and waits for
the real launch burst. No remote host is contacted.

From the repository root (artwork must be configured):

```sh
~/.dotnet/dotnet publish src/Allegiance.Server/Allegiance.Server.csproj \
  -c Release -r linux-x64 --self-contained -p:PublishAot=true \
  -o .scratch/p908/aot
~/.dotnet/dotnet run --project tools/p908-text-core-proof -- \
  .scratch/p908/aot/Allegiance.Server "$ALLEGIANCE_ARTWORK_PATH" .scratch/p908/aot-run
```

The proof writes its config, exported text and `native-server.log` into the
supplied scratch directory. It terminates only its own server child on exit.
The config explicitly uses pool transport, native updates/GotoPlan, immutable
snapshot/encode-once and parallel broadcast; the published server uses server GC.

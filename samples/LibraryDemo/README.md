# Library integration sample

Requires the .NET SDK pinned in the repository's `global.json`.

Start the server:

```sh
dotnet run --project samples/LibraryDemo -- server
```

In another terminal, send an echo request through a direct client stream:

```sh
dotnet run --project samples/LibraryDemo -- client
```

The server limits concurrent connections to 64. Ctrl+C cancels acceptance and active
handlers and waits for cleanup. Each accepted connection receives a separate stream.

To expose the direct server through a normal TCP port, leave the server running and run:

```sh
dotnet run --project samples/LibraryDemo -- forward-client
```

Applications can then use ordinary TCP to `127.0.0.1:9000` for the echo service. To test
a direct client against an embedded forwarding server, run an echo service on port 9002,
run `forward-server` instead of `server`, and run `client`.

By default the sample uses LZ4 without encryption on loopback. Generate a key with
`EncryptionOptions.GenerateKey()` or `tcptunnel --generate-key` and set `TUNNEL_KEY` to
that Base64 value in both processes to enable AES-GCM. Do not store the key in source.

See the [API guide](../../docs/api.md) for options, lifetime, and error contracts.

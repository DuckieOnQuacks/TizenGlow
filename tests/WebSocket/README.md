# WebSocket connection regression checks

Run with the .NET 8 SDK, without a TV, Tizen SDK or additional test packages:

```sh
dotnet run --project tests/WebSocket/WebSocket.Tests.csproj
```

The harness compiles the production connection handler directly and supplies a scripted WebSocket plus an HTTP-response
release callback. It checks cleanup after peer close, receive/handler/close failures and close-reply cancellation;
fragmented UTF-8, sequential message handling, binary/oversized message rejection, and cleanup when disposal fails.
Text messages are limited to 8 KiB. Incomplete messages are never dispatched.

These are host-side behavior checks, not a Tizen resource-leak benchmark. During development on a QN95A with Tizen 6.0,
short status connections had increased open file descriptors from 117 to 120. Replying to Close and releasing both the
WebSocket and upgraded HTTP response held repeated idle probes at 109, and later capture-run probes at 111. Those
observations came from a separate experimental build; this extracted patch still needs regression testing on a TV
running the normal application.

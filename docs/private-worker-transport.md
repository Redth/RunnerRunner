# Private worker transport

Set `HostWorker:ServerCaCertificatePath` on a worker to a PEM public CA certificate when its HTTPS control endpoint uses a private issuer. This trust applies only to the worker connection; the machine trust store stays unchanged. Hostname, expiry and server-auth checks still apply. Invalid files and HTTP endpoints fail closed. Private-CA revocation checks are disabled, so operators must replace trust material when revoking an issuer.

Omit the setting to keep platform trust. Existing proxy settings still apply.

On the server, `HostWorker:GrpcOnlyPort` optionally restricts one already-configured Kestrel listener to the worker Connect RPC. Other paths return 404 on that port, including the first-run administrator form. This does not create a listener, add authentication, or change access on other ports. Configure a separate administrative listener and its access policy.

`Orleans:ListenIPAddress` optionally changes the silo and gateway bind address when `Orleans:AdvertisedIPAddress` is configured. It defaults to all IPv4 interfaces. These endpoints must remain reachable by the cluster; use a loopback bind only for a single-machine deployment.

# Mock DNS map

- `sas.bpsecure.com` → `Features/Sas/Authentication/SasAuthenticationEndpoint.cs` → flow ids `6ce9ac9d-f58a-42c9-9267-238d04b2dd58`, `0d3504c7-3271-40c3-87d9-23488aa4d93c`
- `www.darkorbit.com` → `Features/Mocks/WwwDarkorbitCom/HomeEndpoint.cs` → flow ids `3506f878-76e8-4780-9094-e59531a26ae7`, `12941274-e50d-4750-bd26-f53eb59d974e`
- `www.darkorbit.com` / `gbl3.darkorbit.com` → `Features/ProjectApi/Authentication/ProjectApiAuthenticationEndpoint.cs` → flow ids `fb855741-a543-4f04-9875-b87d46617f36`, `53a2ecd3-e380-45e4-84a4-e28e4d6fc123`
- `gbl3.darkorbit.com` → `Features/IndexInternal/InternalStart/IndexInternalStartEndpoint.cs` → flow ids `771b953a-11da-47a5-9b69-8736a73849f0`, `5ac0f020-1134-40af-9d67-7fd7ab4fa419`
- `chrome.cloudflare-dns.com` → `Features/Mocks/ChromeCloudflareDnsCom/DnsQueryEndpoint.cs` → flow ids `194b8112-d6a7-465b-9714-e6b2effe01ca` ... `fe724fcc-a072-4e56-aeb8-47e576164896`
- Static CDN / telemetry endpoints are grouped by DNS host folder under `Features/Mocks/*`

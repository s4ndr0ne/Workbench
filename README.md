![Workbench logo](https://raw.githubusercontent.com/s4ndr0ne/Workbench/main/assets/icon.png)

# Workbench

Drop-in monitoring workbench for ASP.NET Core Web APIs. Live runtime metrics, health checks, request log and an API explorer — served in-process, zero external dependencies.

[![CI](https://github.com/s4ndr0ne/Workbench/actions/workflows/ci.yml/badge.svg)](https://github.com/s4ndr0ne/Workbench/actions/workflows/ci.yml)
[![Release](https://github.com/s4ndr0ne/Workbench/actions/workflows/release.yml/badge.svg)](https://github.com/s4ndr0ne/Workbench/actions/workflows/release.yml)
[![NuGet](https://img.shields.io/nuget/v/s4ndr0ne.Workbench.svg?logo=nuget)](https://www.nuget.org/packages/s4ndr0ne.Workbench)
[![Downloads](https://img.shields.io/nuget/dt/s4ndr0ne.Workbench.svg)](https://www.nuget.org/packages/s4ndr0ne.Workbench)
[![MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/s4ndr0ne/Workbench/blob/main/LICENSE)
![.NET 8 / 9 / 10](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512BD4?logo=dotnet)

---

![Workbench overview](https://raw.githubusercontent.com/s4ndr0ne/Workbench/main/assets/screenshot-overview.png)

## Features

- **Live metrics** — CPU, working set and managed heap sampled in a rolling window, streamed over Server-Sent Events.
- **Health checks** — the standard `IHealthCheck` report, rendered with status, duration and custom data.
- **Request log** — every inbound request (method, path, status, latency, client, body up to 64 KB) with filters, pause and live throughput.
- **API explorer** — discovers your mapped endpoints and lets you invoke them with path/query/header/body editors, JSON highlighting and *copy as cURL*.
- **Zero dependencies** — a single embedded HTML file, no CDN, no npm; works offline and behind air-gapped networks.
- **Light & dark theme**, responsive layout, keyboard friendly (`⌘/Ctrl + Enter` sends a request).

### More screenshots

![Request log](https://raw.githubusercontent.com/s4ndr0ne/Workbench/main/assets/screenshot-requests.png)

![API explorer](https://raw.githubusercontent.com/s4ndr0ne/Workbench/main/assets/screenshot-explorer.png)

## Installation

```bash
dotnet add package s4ndr0ne.Workbench
```

Targets `net8.0`, `net9.0` and `net10.0`.

## Quick start

```csharp
using Workbench.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddWorkbench();          // services + metrics collector

var app = builder.Build();

app.UseWorkbench();                       // mount early: the request log sees everything after it

app.MapGet("/ping", () => "pong");
app.Run();
```

Open **`http://localhost:<port>/workbench`**.

## Configuration

```csharp
builder.Services.AddWorkbench(options =>
{
    options.Path = "/monitoring";                         // default: /workbench
    options.MetricsSampleInterval = TimeSpan.FromSeconds(5);
    options.MetricsHistory = TimeSpan.FromMinutes(10);
    options.EnableHealthReport = true;
    options.RequestLogCapacity = 500;                     // ring-buffer size
    options.CaptureRequestBody = true;                    // bodies up to 64 KB, multipart excluded
});
```

| Option | Default | Description |
| --- | --- | --- |
| `Path` | `/workbench` | Base path of the dashboard and its API. |
| `MetricsSampleInterval` | `5s` | How often runtime metrics are sampled and pushed to the UI (minimum 100 ms). |
| `MetricsHistory` | `10m` | Length of the rolling metrics window (at least the sample interval, maximum 7 days). |
| `EnableHealthReport` | `true` | Expose the health report (`/api/health`). |
| `RequestLogCapacity` | `500` | Number of requests kept in memory (50–10,000). |
| `CaptureRequestBody` | `true` | Capture body bytes as the application reads them (chunked bodies included), without pre-reading or enabling buffering. |

Body capture retains at most 64 KB of content. Known-length bodies larger than 64 KB and multipart bodies are excluded; streamed bodies are truncated in the log. Unread body content is not captured. Downstream middleware can still configure request-size limits and enable buffering when needed.

## Endpoints

| Path | Description |
| --- | --- |
| `/workbench` | Dashboard (single-page app) |
| `/workbench/api/overview` | Process info, runtime metrics, sample history, recent requests |
| `/workbench/api/health` | Health report (`403` when disabled) |
| `/workbench/api/endpoints` | Discovered application endpoints |
| `/workbench/api/stream` | Server-Sent Events: `overview`, `health`, `request` |

All API endpoints are `GET` only.

## Securing the dashboard

Workbench exposes process details and request bodies. In production, put it behind authentication or restrict it by network. The simplest approach is a small guard before `UseWorkbench()`:

```csharp
app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/workbench"), branch =>
{
    branch.Use(async (ctx, next) =>
    {
        if (!ctx.User.Identity?.IsAuthenticated ?? true) { ctx.Response.StatusCode = 401; return; }
        await next();
    });
});
app.UseWorkbench();
```

Or disable body capture with `CaptureRequestBody = false` if payloads may contain secrets.

## Project layout

```
src/Workbench/            library (net8.0 / net9.0 / net10.0)
  Extensions/             AddWorkbench + UseWorkbench
  Middleware/             dashboard router, API, SSE stream, request log
  Models/                 response DTOs
  Options/                WorkbenchOptions
  Services/               metrics collector, request-log ring buffer
  wwwroot/index.html      the whole frontend, embedded in the assembly
samples/Workbench.Sample  demo Web API
tests/Workbench.Tests     xunit integration tests (TestServer)
```

## Development

```bash
dotnet build Workbench.slnx
dotnet test  Workbench.slnx
node --test tests/Workbench.Tests/dashboard-regressions.cjs  # Node.js 22+
dotnet run --project samples/Workbench.Sample     # http://localhost:5036/workbench
```

See [CONTRIBUTING.md](https://github.com/s4ndr0ne/Workbench/blob/main/CONTRIBUTING.md) for the release process. Changes are tracked in the [CHANGELOG.md](https://github.com/s4ndr0ne/Workbench/blob/main/CHANGELOG.md).

## License

[MIT](https://github.com/s4ndr0ne/Workbench/blob/main/LICENSE)

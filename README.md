# Workbench

Una libreria .NET che si innesta in un progetto ASP.NET Core Web API e apre una **workbench di monitoraggio** in-app: dashboard di health checks e metriche runtime servita automaticamente dal processo host.

## Installazione

```bash
dotnet add package Workbench
```

## Utilizzo

In `Program.cs`:

```csharp
using Workbench.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddWorkbench();               // registra i servizi + metriche

var app = builder.Build();

app.UseWorkbench();                            // mappa la dashboard

app.Run();
```

Apri il browser su **`http://localhost:<port>/workbench`** per il monitoraggio.

## Endpoint esposti

| Percorso | Descrizione |
| --- | --- |
| `/workbench` | Dashboard web (single-page) |
| `/workbench/api/overview` | JSON con info processo, metriche runtime e serie storica |
| `/workbench/api/health` | Report health checks (se HealthChecks è registrato) |

## Configurazione

```csharp
builder.Services.AddWorkbench(options =>
{
    options.Path = "/monitoring";              // rotta personalizzata (default: /workbench)
    options.MetricsSampleInterval = TimeSpan.FromSeconds(5);
    options.MetricsHistory = TimeSpan.FromMinutes(10);
    options.EnableHealthReport = true;
});
```

## Struttura

```
src/Workbench/              # libreria (net8.0)
  Extensions/               # AddWorkbench + UseWorkbench
  Middleware/               # router SPA/API + asset embedded
  Models/                   # DTO di risposta
  Options/                  # WorkbenchOptions
  Services/                 # WorkbenchMetricsCollector (metriche runtime)
  wwwroot/                  # frontend embedded (HTML/CSS/JS in un unico file)
samples/Workbench.Sample/   # progetto di esempio WebAPI
```

## Come funziona

Il frontend è incorporato nella DLL come risorse embedded, quindi non c'è nessuna dipendenza esterna. `WorkbenchMetricsCollector` campiona CPU, memoria e heap gestito in finestra mobile; la dashboard interroga le API JSON e aggiorna i grafici ogni 5 secondi.
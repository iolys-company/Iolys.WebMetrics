using Iolys.WebMetrics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddWebMetrics(
    builder.Configuration.GetSection(WebMetricsOptions.SectionName));

var app = builder.Build();

app.UseRouting();
app.UseWebMetrics();

app.MapGet("/", () => Results.Content(
    """
    <!doctype html>
    <html lang="en">
    <head><meta charset="utf-8"><title>Iolys.WebMetrics sample</title></head>
    <body>
      <h1>Iolys.WebMetrics sample</h1>
      <p><a href="/about">Open another tracked page</a></p>
      <p><a href="/missing">Generate a 404</a></p>
      <p><a href="/admin/metrics">View the development-only metrics JSON</a></p>
    </body>
    </html>
    """,
    "text/html; charset=utf-8"));

app.MapGet("/about", () => Results.Content(
    "<h1>About</h1><p><a href=\"/\">Home</a></p>",
    "text/html; charset=utf-8"));

if (app.Environment.IsDevelopment())
{
    app.MapGet("/admin/metrics", async (
        IAnalyticsReportReader metrics,
        CancellationToken cancellationToken) =>
        Results.Json(await metrics.GetDashboardAsync(30, cancellationToken: cancellationToken)));
}

app.Run();

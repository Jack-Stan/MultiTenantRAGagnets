using MultiTenantRAGagnets.Core.Providers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// Provider selection: "Fake" (default, keyless, the CI path) or "Ollama".
builder.Services.AddRagProviders(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// Exposed so integration tests can use WebApplicationFactory<Program>.
public partial class Program;

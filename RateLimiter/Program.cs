using RateLimiter.RateLimiter.Api.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add Services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "Rate Limiter API",
        Version = "v1",
        Description = "API for managing rate limits",
    });
    
});

builder.Services.AddRateLimitService(builder.Configuration);

// Add Health Checks
builder.Services.AddHealthChecks().AddRateLimiterHealthCheck(timeout: TimeSpan.FromSeconds(5));

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseRateLimiting();

app.UseAuthorization();
app.MapControllers();

app.MapHealthChecks("/health");

app.Run();
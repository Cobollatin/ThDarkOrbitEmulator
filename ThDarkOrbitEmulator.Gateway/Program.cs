var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddKeycloakJwtAuthentication(builder.Configuration);
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .SetIsOriginAllowed(origin =>
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return uri.Host is "localhost" or "127.0.0.1";
        })
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var app = builder.Build();

// After an in-game logout the Unity client reopens "{host}/" + "/indexInternal.es?action=internalWebGL".
// Collapse the leading "//" before routing so that page still returns the dosid the client needs for login.php.
app.Use((context, next) =>
{
    if (context.Request.Path.Value is { Length: > 1 } path && path.StartsWith("//", StringComparison.Ordinal))
        context.Request.Path = "/" + path.TrimStart('/');
    return next(context);
});
app.UseRouting();
app.UseExceptionHandler();
app.UseServiceDefaults();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapReverseProxy();
app.MapDefaultEndpoints();

app.Run();

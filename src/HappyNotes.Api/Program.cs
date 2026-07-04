using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;
using Api.Framework;
using Api.Framework.Database;
using Api.Framework.Exceptions;
using Api.Framework.Extensions;
using Api.Framework.Models;
using HappyNotes.Api;
using HappyNotes.Common;
using HappyNotes.Dto;
using HappyNotes.Services;
using HappyNotes.Services.interfaces;
using HappyNotes.Services.SyncQueue.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using NLog.Web;
using Polly;
using Polly.Extensions.Http;
using Swashbuckle.AspNetCore.SwaggerGen;

var builder = WebApplication.CreateBuilder(args);
var logger = LoggerFactory.Create(config =>
{
    config.AddConsole();
    config.AddConfiguration(builder.Configuration.GetSection("Logging"));
}).CreateLogger("Program");

var envName = builder.Environment.EnvironmentName;
builder.Host.UseNLog();

builder.Services.AddControllers().AddJsonOptions(options =>
{
    var defaultOptions = JsonSerializerConfig.Default;
    options.JsonSerializerOptions.PropertyNamingPolicy = defaultOptions.PropertyNamingPolicy;
    options.JsonSerializerOptions.PropertyNameCaseInsensitive = defaultOptions.PropertyNameCaseInsensitive;
    options.JsonSerializerOptions.DefaultIgnoreCondition = defaultOptions.DefaultIgnoreCondition;
    options.JsonSerializerOptions.WriteIndented = defaultOptions.WriteIndented;
    foreach (var converter in defaultOptions.Converters)
    {
        options.JsonSerializerOptions.Converters.Add(converter);
    }
});
builder.Services.AddAutoMapper(typeof(AutoMapperProfile));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(SetupSwaggerGen());
builder.Services.AddScoped(typeof(IRepositoryBase<>), typeof(RepositoryBase<>));
builder.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

builder.Services.AddSqlSugarSetup(builder.Configuration.GetSection("DatabaseConnectionOptions")
    .Get<DatabaseConnectionOptions>()!, logger);

var manticoreOptions = builder.Configuration.GetSection("ManticoreConnectionOptions").Get<ManticoreConnectionOptions>();
if (manticoreOptions != null)
{
    builder.Services.AddSingleton(manticoreOptions);
}

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddCors(SetupCors(builder));
ConfigAuthentication(builder);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

// Configure HttpClientFactory for TelegramBotClient with Polly retry policy
var retryPolicyConfig = builder.Configuration.GetSection("PollyPolicies:TelegramRetry");
var sleepDurations = retryPolicyConfig
    .GetSection("SleepDurations")
    .Get<string[]>()?
    .Select(TimeSpan.Parse)
    .ToArray() ?? new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) }; // Fallback

builder.Services.AddHttpClient("TelegramBotClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(100); // Keep existing timeout
})
.AddPolicyHandler((serviceProvider, request) =>
{
    var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(sleepDurations, onRetry: (outcome, timeSpan, retryCount, context) =>
        {
            logger.LogWarning(outcome.Exception,
                "Polly retry {RetryCount} for {Url} due to {StatusCode}. Waited {TimeSpan}s.",
                retryCount,
                request.RequestUri,
                outcome.Result?.StatusCode,
                timeSpan.TotalSeconds);
        });
});

builder.Services.RegisterServices();

// Add TimeProvider for time dependency injection
builder.Services.AddSingleton(TimeProvider.System);

// Add Sync Queue services
builder.Services.AddSyncQueue(builder.Configuration);

var app = builder.Build();
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Local"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowOrigins");
app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => "Healthy").AllowAnonymous();
logger.LogInformation(envName);
app.Run();
return;

///////////// main program ends here, and the following are local methods /////////////////////////////////////

void ConfigAuthentication(WebApplicationBuilder b)
{
    var services = b.Services;

    var configuration = b.Configuration;
    services.Configure<JwtConfig>(configuration.GetSection("Jwt"));
    services.Configure<GoogleOAuthConfig>(configuration.GetSection("GoogleOAuth"));
    var jwtConfig = configuration.GetSection("Jwt").Get<JwtConfig>();
    services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwtConfig!.Issuer,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey =
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtConfig.SymmetricSecurityKey)),
                ValidateAudience = false,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(5)
            };
        });

    JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
}

Action<SwaggerGenOptions> SetupSwaggerGen()
{
    return c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "Happy Notes API",
            Version = "v1"
        });
        c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.ApiKey,
            Scheme = "Bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description =
                "JWT Authorization header using the Bearer scheme. \r\n\r\n Enter 'Bearer' [space] and then your token in the text input below.\r\n\r\nExample: \"Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJodHRwO\"",
        });
        c.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
        {
            { new OpenApiSecuritySchemeReference("Bearer"), [] }
        });
    };
}

Action<CorsOptions> SetupCors(WebApplicationBuilder webApplicationBuilder)
{
    return opts =>
    {
        string[] originList = webApplicationBuilder.Configuration.GetSection("AllowedCorsOrigins").Get<List<string>>()?.ToArray() ?? [];
        opts.AddPolicy("AllowOrigins", policy => policy.WithOrigins(originList)
            .AllowCredentials()
            .AllowAnyMethod()
            .AllowAnyHeader()
        );
    };
}

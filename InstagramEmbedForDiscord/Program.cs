using InstagramEmbed.Application.Services;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Encoder =
            System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping);

builder.Services.AddMemoryCache(o =>
{

    o.SizeLimit = 5_000;
});

builder.Services.AddHttpClient("regular", c =>
{
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36");
    c.Timeout = TimeSpan.FromMinutes(5);
});

// Talks only to instagram.com / i.instagram.com — no third-party downloader.
builder.Services.AddHttpClient("instagram", c =>
{
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
    c.DefaultRequestHeaders.Accept.ParseAdd("*/*");
    c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    c.Timeout = TimeSpan.FromSeconds(15);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    // We manage the Cookie header ourselves per-request (see InstagramMediaService),
    // so cookies must not be handled by a shared CookieContainer here.
    UseCookies = false,
    AllowAutoRedirect = true
});

builder.Services.AddSingleton<PostCacheService>();
builder.Services.AddSingleton<InstagramMediaService>();
builder.Services.AddSingleton<DonateMessageService>();

builder.Services.Configure<InstagramSettings>(builder.Configuration.GetSection("Instagram"));

builder.Services.Configure<DonationSettings>(options =>
{
    options.Password = builder.Configuration.GetValue("APP_PASSWORD", "password") ?? "password";
    options.Current = builder.Configuration.GetValue("DONATION_CURRENT", 0);
    options.Target = builder.Configuration.GetValue("DONATION_TARGET", 100);
});


var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.MapControllers();

app.Run();

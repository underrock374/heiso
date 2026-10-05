using System.Diagnostics;
using System.Net;
using Fh6.Core;
using Fh6.Recorder;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// 画面の言語(appsettings の Recorder.Language。auto なら Windows の表示言語)。--setup-obs-profiles のプロファイル名にも使うので先に決める
Strings.Use(new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true).AddJsonFile("appsettings.Local.json", optional: true)
    .AddCommandLine(args.Where(a => a.StartsWith("--Recorder:", StringComparison.Ordinal)).ToArray())
    .Build()["Recorder:Language"]);

// 記録済みセッションの同期情報を作り直す: HeisoRecorder --resync <フォルダ>...
if (args.Length >= 2 && args[0] == "--resync")
    return Resync.Run(args.Skip(1));

// 画質の段階ごとの OBS のプロファイル「HEISO ○○」を作る(OBS を閉じた状態で): HeisoRecorder --setup-obs-profiles [--from <プロファイル>]
if (args.Length >= 1 && args[0] == "--setup-obs-profiles")
    return ObsProfileSetup.Run(args.Skip(1));

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // exe をどこから起動しても appsettings.json と wwwroot を exe の隣から読む
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});

// 個人設定(OBS のパスワードなど)は appsettings.Local.json に書く。Git には入れない
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

var opt = builder.Configuration.GetSection("Recorder").Get<RecorderOptions>() ?? new RecorderOptions();
builder.WebHost.UseUrls($"http://0.0.0.0:{opt.HttpPort}");

builder.Services.AddSingleton(opt);
builder.Services.AddSingleton<AccessKey>();
builder.Services.AddSingleton<TelemetryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelemetryService>());
builder.Services.AddSingleton<ObsClient>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ObsClient>());
builder.Services.AddSingleton<ObsQualityService>();
builder.Services.AddSingleton<ObsSceneService>();
builder.Services.AddSingleton<RaceWatcher>();
builder.Services.AddSingleton<SessionController>(sp => new SessionController(
    sp.GetRequiredService<RecorderOptions>(), sp.GetRequiredService<TelemetryService>(), sp.GetRequiredService<ObsClient>(),
    sp.GetRequiredService<ObsQualityService>(), sp.GetRequiredService<ILogger<SessionController>>(), sp.GetRequiredService<RaceWatcher>(),
    sp.GetRequiredService<ObsSceneService>()));
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(60));

var app = builder.Build();
var key = app.Services.GetRequiredService<AccessKey>();
var controller = app.Services.GetRequiredService<SessionController>();
var quality = app.Services.GetRequiredService<ObsQualityService>();
var scene = app.Services.GetRequiredService<ObsSceneService>();
var race = app.Services.GetRequiredService<RaceWatcher>();   // 記録していなくても、受信していれば車両とレースを判定する

// /api/* は合言葉必須(ヘッダ X-Key かクエリ k)
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        var k = ctx.Request.Headers["X-Key"].FirstOrDefault() ?? ctx.Request.Query["k"].FirstOrDefault();
        if (k != key.Value)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = Strings.T("合言葉が違います。PCの画面に出ているQRコードから開き直してください") });
            return;
        }
    }
    await next();
});
// 画面の言語の辞書(Fh6.Core の strings.json から)。i18n.js より前に読む。合言葉なしで読める(秘密ではない)
app.MapGet("/i18n-data.js", () => Results.Text(
    "window.HEISO_I18N = " + System.Text.Json.JsonSerializer.Serialize(new { lang = Strings.Language, strings = Strings.Dictionary(Strings.Language) }) + ";",
    "text/javascript; charset=utf-8"));
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
});

app.MapGet("/api/status", () => Results.Json(controller.GetStatus()));

app.MapPost("/api/start", async (StartRequest? req) =>
{
    var (ok, error) = await controller.StartAsync(req?.Video ?? true, req?.Label, req?.Quality);
    return ok ? Results.Json(controller.GetStatus()) : Results.Json(new { error }, statusCode: 409);
});

app.MapPost("/api/stop", async () =>
{
    var (ok, error) = await controller.StopAsync();
    return ok ? Results.Json(controller.GetStatus()) : Results.Json(new { error }, statusCode: 409);
});

// 録画の画質の段階を選ぶ(次の記録から使う。保存先フォルダに覚える)
app.MapPost("/api/quality", (QualityRequest? req) =>
{
    if (req?.Preset is not string id || !QualityPresets.IsKnown(id))
        return Results.Json(new { error = Strings.T("知らない画質の段階です") }, statusCode: 400);
    if (controller.IsActive)
        return Results.Json(new { error = Strings.T("記録中は変えられません") }, statusCode: 409);
    quality.SavePreset(id);
    return Results.Json(controller.GetStatus());
});

// 録画するシーンを選ぶ(空なら OBS で今選んでいるシーンのまま。次の記録から使う。保存先フォルダに覚える)
app.MapPost("/api/scene", (SceneRequest? req) =>
{
    if (controller.IsActive)
        return Results.Json(new { error = Strings.T("記録中は変えられません") }, statusCode: 409);
    scene.SaveScene(req?.Scene);
    return Results.Json(controller.GetStatus());
});

app.MapPost("/api/marker", (MarkerRequest? req) =>
{
    var (ok, count) = controller.AddMarker(req?.Label);
    return ok ? Results.Json(new { count }) : Results.Json(new { error = Strings.T("記録中ではありません") }, statusCode: 409);
});

// 確認カード(車両・コース)の操作。誤りは 400 で理由を返す
IResult RaceAction(Action act)
{
    try { act(); return Results.Json(controller.GetStatus()); }
    catch (UserError ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
}
app.MapPost("/api/car", (CarRequest req) => RaceAction(() => race.ResolveCar(req.Id, req.Car, req.SetupChoice, req.SetupName, req.Note)));
app.MapPost("/api/course", (CourseRequest req) => RaceAction(() => race.ResolveCourse(req.Id, req.Choice, req.Name, req.Kind, req.Note)));
app.MapPost("/api/dismiss", (DismissRequest req) => RaceAction(() => race.Dismiss(req.Id)));
app.MapPost("/api/variant", () => RaceAction(race.NewVariant));
app.MapPost("/api/last-race", (LastRaceRequest req) => RaceAction(() =>
    race.EditLastRace(req.CourseChoice, req.CourseName, req.Kind, req.Car, req.SetupChoice, req.SetupName)));

app.MapGet("/api/connect-info", (HttpContext ctx) =>
{
    var remote = ctx.Connection.RemoteIpAddress;
    bool isLocal = remote != null && (IPAddress.IsLoopback(remote) ||
        (remote.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remote.MapToIPv4())));
    var urls = NetworkUtil.GetCandidates().Select(a => new
    {
        address = a.Address,
        name = a.InterfaceName,
        url = $"http://{a.Address}:{opt.HttpPort}/?k={key.Value}",
    });
    return Results.Json(new { isLocal, urls });
});

var localUrl = $"http://localhost:{opt.HttpPort}/?k={key.Value}";

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine();
    Console.WriteLine("  " + Strings.T("HEISO Recorder を起動しました"));
    Console.WriteLine("  " + Strings.T("PC       : {url}", ("url", localUrl)));
    foreach (var a in NetworkUtil.GetCandidates().Take(3))
        Console.WriteLine("  " + Strings.T("スマホ   : {url}   ({name})", ("url", $"http://{a.Address}:{opt.HttpPort}/?k={key.Value}"), ("name", a.InterfaceName)));
    Console.WriteLine("  " + Strings.T("UDP      : {port}(FH6 の Data Out をここへ)", ("port", opt.UdpPort)));
    Console.WriteLine("  " + Strings.T("保存先   : {path}", ("path", opt.ResolveOutputDir())));
    Console.WriteLine("  " + Strings.T("終了するときは Ctrl+C(記録中なら停止・保存してから終わります)"));
    Console.WriteLine();

    if (opt.OpenBrowserOnStart)
    {
        try { Process.Start(new ProcessStartInfo(localUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Console.WriteLine("  " + Strings.T("ブラウザを開けませんでした: {error}", ("error", ex.Message))); }
    }
});

// Ctrl+C で終わるときも、記録中ならOBSを止めてファイルを閉じる
app.Lifetime.ApplicationStopping.Register(() =>
{
    if (!controller.IsActive) return;
    Console.WriteLine("  " + Strings.T("記録を停止しています…"));
    try { controller.StopAsync().Wait(TimeSpan.FromSeconds(45)); } catch { }
});

try
{
    app.Run();
}
catch (IOException ex) when (ex.InnerException is System.Net.Sockets.SocketException || ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("  " + Strings.T("TCPポート {port} が使用中です。appsettings.json の HttpPort を変えてください。", ("port", opt.HttpPort)));
    Console.WriteLine("  " + Strings.T("Enter で終了"));
    Console.ReadLine();
}
return 0;

record StartRequest(bool? Video, string? Label, string? Quality);
record QualityRequest(string? Preset);
record SceneRequest(string? Scene);
record MarkerRequest(string? Label);
record CarRequest(string Id, string? Car, string? SetupChoice, string? SetupName, string? Note);
record CourseRequest(string Id, string? Choice, string? Name, string? Kind, string? Note);
record DismissRequest(string Id);
record LastRaceRequest(string? CourseChoice, string? CourseName, string? Kind, string? Car, string? SetupChoice, string? SetupName);

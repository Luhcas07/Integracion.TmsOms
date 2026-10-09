using System.Text.Json.Serialization;
using Integracion.TmsOms.Api;
using Integracion.TmsOms.Application;
using Integracion.TmsOms.Domain;
using Integracion.TmsOms.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton(TimeProvider.System);

// Opciones: se leen de IConfiguration al resolverse (no al arrancar), para que las pruebas puedan sobrescribirlas.
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection("Queue").Get<QueueOptions>() ?? new QueueOptions());

builder.Services.AddSingleton(sp =>
{
    var c = sp.GetRequiredService<IConfiguration>();
    return new RetryOptions
    {
        MaxAttempts = c.GetValue("Retry:MaxAttempts", 3),
        InitialDelay = TimeSpan.FromMilliseconds(c.GetValue("Retry:InitialDelayMilliseconds", 200))
    };
});

builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection("Evidence").Get<EvidenceOptions>() ?? new EvidenceOptions());

builder.Services.AddSingleton(sp =>
{
    var c = sp.GetRequiredService<IConfiguration>();
    return new CircuitBreakerOptions
    {
        FailureThreshold = c.GetValue("CircuitBreaker:FailureThreshold", 3),
        OpenDuration = TimeSpan.FromSeconds(c.GetValue("CircuitBreaker:OpenDurationSeconds", 10))
    };
});

// Cola de eventos del TMS
builder.Services.AddSingleton(sp => new InMemoryMessageQueue<TrackingEvent>(sp.GetRequiredService<QueueOptions>()));
builder.Services.AddSingleton<IMessageSender<TrackingEvent>>(
    sp => sp.GetRequiredService<InMemoryMessageQueue<TrackingEvent>>());
builder.Services.AddSingleton<IMessageReceiver<TrackingEvent>>(
    sp => sp.GetRequiredService<InMemoryMessageQueue<TrackingEvent>>());

// Tópico de estados, con sus dos suscripciones
builder.Services.AddSingleton(sp =>
{
    var topic = new InMemoryTopic<StatusChangedMessage>(sp.GetRequiredService<QueueOptions>());
    topic.Subscribe(Subscriptions.Evidences, message => message.Status.RequiresEvidence());
    topic.Subscribe(Subscriptions.Notifications);
    return topic;
});
builder.Services.AddSingleton<IMessageSender<StatusChangedMessage>>(
    sp => sp.GetRequiredService<InMemoryTopic<StatusChangedMessage>>());

// OMS en memoria (también hace de almacén del Outbox)
builder.Services.AddSingleton<InMemoryOmsStore>();
builder.Services.AddSingleton<IOmsStore>(sp => sp.GetRequiredService<InMemoryOmsStore>());
builder.Services.AddSingleton<IOutboxStore>(sp => sp.GetRequiredService<InMemoryOmsStore>());

// Lógica de aplicación compartida
builder.Services.AddSingleton<IRetryPolicy>(sp => new RetryPolicy(sp.GetRequiredService<RetryOptions>()));
builder.Services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
builder.Services.AddSingleton<TrackingEventHandler>();
builder.Services.AddSingleton(sp =>
{
    var hours = sp.GetRequiredService<IConfiguration>().GetValue("Tms:SourceUtcOffsetHours", -5.0);
    return new TmsEventTranslator(TimeSpan.FromHours(hours));
});
builder.Services.AddSingleton<OutboxDispatcher>();
builder.Services.AddSingleton(sp =>
{
    var handler = sp.GetRequiredService<TrackingEventHandler>();
    return new MessageConsumer<TrackingEvent>(
        sp.GetRequiredService<IMessageReceiver<TrackingEvent>>(),
        (trackingEvent, correlationId, cancellationToken) =>
            handler.HandleAsync(trackingEvent, correlationId, cancellationToken));
});

// Evidencias
builder.Services.AddSingleton<IEvidenceDownloader, SimulatedEvidenceDownloader>();
builder.Services.AddSingleton<InMemoryEvidenceStorage>();
builder.Services.AddSingleton<IEvidenceStorage>(sp => sp.GetRequiredService<InMemoryEvidenceStorage>());
builder.Services.AddSingleton<InMemoryEvidenceCatalog>();
builder.Services.AddSingleton<IEvidenceCatalog>(sp => sp.GetRequiredService<InMemoryEvidenceCatalog>());
builder.Services.AddSingleton<EvidenceHandler>();

// Notificaciones
builder.Services.AddSingleton<IClientConfigProvider>(sp =>
{
    var clients = sp.GetRequiredService<IConfiguration>()
                      .GetSection("Clients").Get<Dictionary<string, ClientConfig>>()
                  ?? [];
    return new ConfiguredClientProvider(clients);
});
builder.Services.AddSingleton<INotificationAdapter, StandardNotificationAdapter>();
builder.Services.AddSingleton<INotificationAdapter, LegacyNotificationAdapter>();
builder.Services.AddSingleton<NotificationAdapterRegistry>();
builder.Services.AddSingleton(sp => new CircuitBreakerRegistry(sp.GetRequiredService<CircuitBreakerOptions>()));
builder.Services.AddSingleton(sp => new InMemoryClientEndpoint(sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IClientEndpoint>(sp => sp.GetRequiredService<InMemoryClientEndpoint>());
builder.Services.AddSingleton<NotificationHandler>();

// Workers en segundo plano
builder.Services.AddHostedService<EventProcessorWorker>();
builder.Services.AddHostedService<OutboxDispatcherWorker>();
builder.Services.AddHostedService<EvidenceWorker>();
builder.Services.AddHostedService<NotificationWorker>();

var app = builder.Build();

DemoData.Seed(app.Services.GetRequiredService<InMemoryOmsStore>());

app.MapWebhookEndpoints();
app.MapDiagnosticsEndpoints();
app.MapWorkerDiagnosticsEndpoints();

app.Run();
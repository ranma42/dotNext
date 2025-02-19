﻿using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using DotNext.Net.Cluster.Consensus.Raft.Membership;
using Microsoft.Extensions.Logging.Console;
using RaftNode;
using SslOptions = DotNext.Net.Security.SslOptions;

switch (args.LongLength)
{
    case 0:
    case 1:
        Console.WriteLine("Port number and protocol are not specified");
        break;
    case 2:
        await StartNode(args[0], int.Parse(args[1]));
        break;
    case 3:
        await StartNode(args[0], int.Parse(args[1]), args[2]);
        break;
}

static Task UseAspNetCoreHost(int port, string? persistentStorage = null)
{
    var configuration = new Dictionary<string, string>
            {
                {"partitioning", "false"},
                {"lowerElectionTimeout", "150" },
                {"upperElectionTimeout", "300" },
                {"requestTimeout", "00:10:00"},
                {"publicEndPoint", $"https://localhost:{port}"},
                {"coldStart", "false"},
                {"requestJournal:memoryLimit", "5" },
                {"requestJournal:expiration", "00:01:00" }
            };
    if (!string.IsNullOrEmpty(persistentStorage))
        configuration[SimplePersistentState.LogLocation] = persistentStorage;
    return new HostBuilder().ConfigureWebHost(webHost =>
    {
        webHost.UseKestrel(options =>
        {
            options.ListenLocalhost(port, static listener => listener.UseHttps(LoadCertificate()));
        })
        .UseStartup<Startup>();
    })
    .ConfigureLogging(static builder => builder.AddConsole().SetMinimumLevel(LogLevel.Error))
    .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(configuration))
    .JoinCluster()
    .Build()
    .RunAsync();
}

static async Task UseConfiguration(RaftCluster.NodeConfiguration config, string? persistentStorage)
{
    AddMembersToCluster(config.UseInMemoryConfigurationStorage());
    var loggerFactory = new LoggerFactory();
    var loggerOptions = new ConsoleLoggerOptions
    {
        LogToStandardErrorThreshold = LogLevel.Warning
    };
    loggerFactory.AddProvider(new ConsoleLoggerProvider(new FakeOptionsMonitor<ConsoleLoggerOptions>(loggerOptions)));
    config.LoggerFactory = loggerFactory;
    using var cluster = new RaftCluster(config);
    cluster.LeaderChanged += ClusterConfigurator.LeaderChanged;
    var modifier = default(DataModifier?);
    if (!string.IsNullOrEmpty(persistentStorage))
    {
        var state = new SimplePersistentState(persistentStorage, new AppEventSource());
        cluster.AuditTrail = state;
        modifier = new DataModifier(cluster, state);
    }
    await cluster.StartAsync(CancellationToken.None);
    await (modifier?.StartAsync(CancellationToken.None) ?? Task.CompletedTask);
    using var handler = new CancelKeyPressHandler();
    Console.CancelKeyPress += handler.Handler;
    await handler.WaitAsync();
    Console.CancelKeyPress -= handler.Handler;
    await (modifier?.StopAsync(CancellationToken.None) ?? Task.CompletedTask);
    await cluster.StopAsync(CancellationToken.None);

    // NOTE: this way of adding members to the cluster is not recommended in production code
    static void AddMembersToCluster(InMemoryClusterConfigurationStorage<IPEndPoint> storage)
    {
        var builder = storage.CreateActiveConfigurationBuilder();

        var address = new IPEndPoint(IPAddress.Loopback, 3262);
        builder.Add(ClusterMemberId.FromEndPoint(address), address);

        address = new(IPAddress.Loopback, 3263);
        builder.Add(ClusterMemberId.FromEndPoint(address), address);

        address = new(IPAddress.Loopback, 3264);
        builder.Add(ClusterMemberId.FromEndPoint(address), address);

        builder.Build();
    }
}

static Task UseUdpTransport(int port, string? persistentStorage)
{
    var configuration = new RaftCluster.UdpConfiguration(new IPEndPoint(IPAddress.Loopback, port))
    {
        LowerElectionTimeout = 150,
        UpperElectionTimeout = 300,
        DatagramSize = 1024
    };
    return UseConfiguration(configuration, persistentStorage);
}

static Task UseTcpTransport(int port, string? persistentStorage, bool useSsl)
{
    var configuration = new RaftCluster.TcpConfiguration(new IPEndPoint(IPAddress.Loopback, port))
    {
        LowerElectionTimeout = 150,
        UpperElectionTimeout = 300,
        TransmissionBlockSize = 4096,
        SslOptions = useSsl ? CreateSslOptions() : null
    };

    return UseConfiguration(configuration, persistentStorage);

    static SslOptions CreateSslOptions()
    {
        var options = new SslOptions();
        options.ServerOptions.ServerCertificate = LoadCertificate();
        options.ClientOptions.TargetHost = "localhost";
        options.ClientOptions.RemoteCertificateValidationCallback = RaftClientHandlerFactory.AllowCertificate;
        return options;
    }
}

static Task StartNode(string protocol, int port, string? persistentStorage = null)
{
    switch (protocol.ToLowerInvariant())
    {
        case "http":
        case "https":
            return UseAspNetCoreHost(port, persistentStorage);
        case "udp":
            return UseUdpTransport(port, persistentStorage);
        case "tcp":
            return UseTcpTransport(port, persistentStorage, false);
        case "tcp+ssl":
            return UseTcpTransport(port, persistentStorage, true);
        default:
            Console.Error.WriteLine("Unsupported protocol type");
            Environment.ExitCode = 1;
            return Task.CompletedTask;
    }
}

static X509Certificate2 LoadCertificate()
{
    using var rawCertificate = Assembly.GetCallingAssembly().GetManifestResourceStream(typeof(Program), "node.pfx");
    using var ms = new MemoryStream(1024);
    rawCertificate?.CopyTo(ms);
    ms.Seek(0, SeekOrigin.Begin);
    return new X509Certificate2(ms.ToArray(), "1234");
}
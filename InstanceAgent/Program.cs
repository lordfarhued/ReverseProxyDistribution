using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;
using Newtonsoft.Json;
using System.Xml;


Console.WriteLine("=== Reverse Proxy Instance Agent ===");
Console.Write("Enter Instance ID (e.g., instance-001): ");
string instanceId = Console.ReadLine() ?? "instance-" + Guid.NewGuid().ToString().Substring(0, 8);

Console.Write("Enter Master Server URL (default: https://localhost:7001): ");
string serverUrl = Console.ReadLine();
if (string.IsNullOrWhiteSpace(serverUrl))
    serverUrl = "https://localhost:7001";

var connection = new HubConnectionBuilder()
    .WithUrl($"{serverUrl}/configHub", options =>
    {
        options.HttpMessageHandlerFactory = (handler) =>
        {
            if (handler is HttpClientHandler clientHandler)
            {
                // Игнорируем SSL ошибки для разработки
                clientHandler.ServerCertificateCustomValidationCallback =
                    (sender, certificate, chain, sslPolicyErrors) => true;
            }
            return handler;
        };
    })
    .WithAutomaticReconnect()
    .Build();

// Локальное хранилище конфигураций
var localConfig = new Dictionary<string, ConfigItem>();
var blockedIps = new HashSet<string>();

// Обработчики событий
connection.On<ConfigItem>("ConfigurationUpdated", (config) =>
{
    Console.WriteLine($"[UPDATE] Config received: {config.Key} (v{config.Version})");
    localConfig[config.Key] = config;
    SaveConfigToFile();
});

connection.On<int>("ConfigurationDeleted", (id) =>
{
    Console.WriteLine($"[DELETE] Config ID: {id}");
    var toRemove = localConfig.FirstOrDefault(x => x.Value.Id == id);
    if (toRemove.Key != null)
    {
        localConfig.Remove(toRemove.Key);
        SaveConfigToFile();
    }
});

connection.On<BlockedIpInfo>("IpBlocked", (ipInfo) =>
{
    Console.WriteLine($"[BLOCK] IP: {ipInfo.IpAddress} - {ipInfo.Reason}");
    blockedIps.Add(ipInfo.IpAddress);
    SaveBlockedIpsToFile();
});

connection.Reconnecting += error =>
{
    Console.WriteLine($"[RECONNECTING] Connection lost. Attempting to reconnect...");
    return Task.CompletedTask;
};

connection.Reconnected += connectionId =>
{
    Console.WriteLine($"[RECONNECTED] Connection restored: {connectionId}");
    return Task.CompletedTask;
};

connection.Closed += error =>
{
    Console.WriteLine($"[CLOSED] Connection closed. {error?.Message}");
    return Task.CompletedTask;
};

try
{
    Console.WriteLine("Connecting to master server...");
    await connection.StartAsync();
    Console.WriteLine("✓ Connected successfully!");

    // Регистрируем инстанс
    await connection.InvokeAsync("RegisterInstance", instanceId, GetLocalIpAddress());
    Console.WriteLine($"✓ Instance registered: {instanceId}");

    // Загружаем начальные конфигурации
    await LoadInitialConfigurations(serverUrl);

    // Запускаем отправку метрик
    var metricsTask = SendMetricsPeriodically(connection, instanceId, serverUrl);

    Console.WriteLine("\n=== Agent is running ===");
    Console.WriteLine("Commands:");
    Console.WriteLine("  status - Show current status");
    Console.WriteLine("  configs - Show loaded configurations");
    Console.WriteLine("  blocked - Show blocked IPs");
    Console.WriteLine("  exit - Stop agent");
    Console.WriteLine();

    while (true)
    {
        var command = Console.ReadLine()?.ToLower();

        switch (command)
        {
            case "status":
                Console.WriteLine($"Instance: {instanceId}");
                Console.WriteLine($"Connection: {connection.State}");
                Console.WriteLine($"Configurations: {localConfig.Count}");
                Console.WriteLine($"Blocked IPs: {blockedIps.Count}");
                break;

            case "configs":
                Console.WriteLine("\n=== Loaded Configurations ===");
                foreach (var cfg in localConfig.Values)
                {
                    Console.WriteLine($"  [{cfg.Type}] {cfg.Key} = {cfg.Value.Substring(0, Math.Min(50, cfg.Value.Length))}... (v{cfg.Version})");
                }
                break;

            case "blocked":
                Console.WriteLine("\n=== Blocked IPs ===");
                foreach (var ip in blockedIps)
                {
                    Console.WriteLine($"  {ip}");
                }
                break;

            case "exit":
                Console.WriteLine("Stopping agent...");
                await connection.StopAsync();
                return;

            default:
                Console.WriteLine("Unknown command. Type 'status', 'configs', 'blocked', or 'exit'");
                break;
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR: {ex.Message}");
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey();
}

void SaveConfigToFile()
{
    try
    {
        var json = JsonConvert.SerializeObject(localConfig, Formatting.Indented);
        File.WriteAllText($"config_{instanceId}.json", json);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error saving config: {ex.Message}");
    }
}

void SaveBlockedIpsToFile()
{
    try
    {
        var json = JsonConvert.SerializeObject(blockedIps, Formatting.Indented);
        File.WriteAllText($"blocked_ips_{instanceId}.json", json);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error saving blocked IPs: {ex.Message}");
    }
}

async Task LoadInitialConfigurations(string serverUrl)
{
    try
    {
        using var httpClient = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
        });

        var response = await httpClient.GetStringAsync($"{serverUrl}/api/configuration");
        var configs = JsonConvert.DeserializeObject<List<ConfigItem>>(response);

        if (configs != null)
        {
            foreach (var config in configs)
            {
                localConfig[config.Key] = config;
            }
            Console.WriteLine($"✓ Loaded {configs.Count} initial configurations");
            SaveConfigToFile();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Warning: Could not load initial configs: {ex.Message}");
    }
}

async Task SendMetricsPeriodically(HubConnection conn, string instId, string serverUrl)
{
    var random = new Random();

    while (conn.State == HubConnectionState.Connected)
    {
        try
        {
            await Task.Delay(10000); // Каждые 10 секунд

            var metrics = new
            {
                InstanceId = instId,
                IpAddress = GetLocalIpAddress(),
                ConfigVersion = localConfig.Values.Max(c => (int?)c.Version) ?? 0,
                Status = "online",
                RequestCount = random.Next(1000, 10000),
                AvgLatency = random.NextDouble() * 100
            };

            await conn.InvokeAsync("ReportMetrics", metrics);

            // Также отправляем через HTTP API
            using var httpClient = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            });

            var json = JsonConvert.SerializeObject(metrics);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            // Создаем простой endpoint для метрик
            // await httpClient.PostAsync($"{serverUrl}/api/configuration/metrics", content);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending metrics: {ex.Message}");
        }
    }
}

string GetLocalIpAddress()
{
    try
    {
        var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
        foreach (var ip in host.AddressList)
        {
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return ip.ToString();
            }
        }
    }
    catch { }
    return "127.0.0.1";
}

class ConfigItem
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string Type { get; set; } = "";
    public int Version { get; set; }
    public string? Domain { get; set; }
}

class BlockedIpInfo
{
    public string IpAddress { get; set; } = "";
    public string Reason { get; set; } = "";
} 
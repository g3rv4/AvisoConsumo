using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

var healthChecksClient = new HttpClient
    { BaseAddress = new Uri("https://hc-ping.com/"), Timeout = TimeSpan.FromSeconds(10) };

var proxy_addresses = Environment.GetEnvironmentVariable("PROXY_ADDRESSES").Split(',');
var proxy_usernames = Environment.GetEnvironmentVariable("PROXY_USERNAMES").Split(',');
var proxy_passwords = Environment.GetEnvironmentVariable("PROXY_PASSWORDS").Split(',');

var clients = new List<HttpClient>();
for (var i = 0; i < proxy_addresses.Length; i++)
{
    var client = new HttpClient(new HttpClientHandler
    {
        UseCookies = true, Proxy = new WebProxy
        {
            Address = new Uri(proxy_addresses[i]),
            BypassProxyOnLocal = false,
            Credentials = new NetworkCredential(proxy_usernames[i], proxy_passwords[i])
        },
        UseProxy = true
    })
    {
        BaseAddress = new Uri("https://brou.e-sistarbanc.com.uy/")
    };
    client.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:109.0) Gecko/20100101 Firefox/114.0");
    clients.Add(client);
}
var rng = new Random();
clients = clients.OrderBy(x => rng.Next()).ToList();
var currentClientId = 0;

var telegramClient = new HttpClient()
{
    BaseAddress =
        new Uri($"https://api.telegram.org/bot{Environment.GetEnvironmentVariable("TELEGRAM_BOT_KEY")}/")
};

var numberRegex = new Regex("[0-9]", RegexOptions.Compiled);
var endsInColonRegex = new Regex("[a-z]:$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
var remainingRetries = 5;
do
{
    try
    {
        currentClientId++;
        var currentClient = clients[currentClientId % clients.Count];
        Console.WriteLine("Getting the token to log in...");
        Console.WriteLine("Using proxy: " + currentClientId % clients.Count);
        var r = await currentClient.GetAsync("");
        var content = await r.Content.ReadAsStringAsync();

        var doc = new HtmlDocument();
        doc.LoadHtml(content);
        var tks = doc.DocumentNode.SelectNodes("//input[@name='tks']");

        if (tks == null)
        {
            throw new Exception("Could not find tks");
        }

        var tksValue = tks.First().Attributes["value"].Value;

        var values = new Dictionary<string, string>
        {
            { "documento", Environment.GetEnvironmentVariable("DOC") },
            { "password", Environment.GetEnvironmentVariable("PASS") },
            { "tks", tksValue },
            { "button1", "Ingresar" },
        };
        var requestContent = new FormUrlEncodedContent(values);
        Console.WriteLine("Logging in...");
        r = await currentClient.PostAsync("", requestContent);
        content = await r.Content.ReadAsStringAsync();

        doc = new HtmlDocument();
        doc.LoadHtml(content);
        var messagesDivs = doc.DocumentNode.SelectNodes("//div[@class='mensaje info']");
        if (messagesDivs != null)
        {
            var messages = messagesDivs
                .Where(n => !string.IsNullOrWhiteSpace(n.InnerText))
                .Select(n => n.InnerText)
                .ToArray();

            if (messages.Length > 0)
            {
                throw new Exception("Could not login. Message: " + messages[0]);
            }
        }

        values = new Dictionary<string, string>
        {
            { "p3", Environment.GetEnvironmentVariable("CARD_P3") },
        };
        requestContent = new FormUrlEncodedContent(values);

        Console.WriteLine("Getting p3...");
        r = await currentClient.PostAsync("movimientos/", requestContent);
        content = await r.Content.ReadAsStringAsync();

        doc = new HtmlDocument();
        doc.LoadHtml(content);

        var p3 = doc.DocumentNode.SelectNodes("//input[@name='p3']");
        if (p3 == null)
        {
            throw new Exception("Could not find p3");
        }

        var p3Value = p3.First().Attributes["value"].Value;
        while (true)
        {
            Console.WriteLine("Retrieving movements...");
            r = await currentClient.GetAsync("url.php?id=movimientosajax&p3=" + p3Value);
            content = await r.Content.ReadAsStringAsync();

            if (string.IsNullOrWhiteSpace(content))
            {
                Console.WriteLine("Could not get movements. Logging in again");
                break;
            }

            doc = new HtmlDocument();
            doc.LoadHtml(content);

            var movements = doc.DocumentNode.SelectNodes("//div[@class='col-12 cont-movimiento']");
            if (movements != null)
            {
                var pendingMovements = movements.Where(n => n.InnerHtml.Contains("A confirmar")).ToArray();
                if (pendingMovements.Length > 0)
                {
                    var alreadyNotified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var currentNotified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var historyOfNotifications = new Dictionary<DateTime, HashSet<string>>();
                    var notifications = new List<string>();
                    if (File.Exists(Environment.GetEnvironmentVariable("STATE_FILE_PATH")))
                    {
                        var fileContentstate = File.ReadAllText(Environment.GetEnvironmentVariable("STATE_FILE_PATH"));
                        historyOfNotifications =
                            JsonSerializer.Deserialize<Dictionary<DateTime, HashSet<string>>>(fileContentstate);
                        alreadyNotified =
                            new HashSet<string>(
                                historyOfNotifications.SelectMany(n => n.Value),
                                StringComparer.OrdinalIgnoreCase);
                    }

                    foreach (var movement in pendingMovements)
                    {
                        var movementDetails = movement.SelectSingleNode(".//div[@class='col-6 ml-2']");
                        var title = movementDetails.SelectSingleNode(".//strong").ChildNodes
                            .Where(n => n.NodeType == HtmlNodeType.Text && !string.IsNullOrWhiteSpace(n.InnerText))
                            .Select(n => n.InnerText.Trim())
                            .Aggregate((a, b) => a + " " + b)
                            .Trim();
                        if (alreadyNotified.Contains(title))
                        {
                            continue;
                        }

                        var desc = movementDetails.ChildNodes
                            .Where(n => n.NodeType == HtmlNodeType.Text && !string.IsNullOrWhiteSpace(n.InnerText))
                            .Select(n => n.InnerText.Trim())
                            .Where(n => !endsInColonRegex.IsMatch(n))
                            .Aggregate((a, b) => a + "\n" + b);

                        var monto = string.Join(" - ", movement.SelectNodes(".//strong[@class='monto_movimiento']")
                            .Select(n => n.InnerText.Trim())
                            .Where(m => numberRegex.IsMatch(m)));

                        notifications.Add(desc + "\nMonto: " + monto);
                        currentNotified.Add(title);
                    }

                    if (notifications.Count > 0)
                    {
                        var msg = new TelegramMessage
                        {
                            chat_id = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID"),
                            text = string.Join("\n----------------\n", notifications),
                            disable_web_page_preview = true
                        };
                        var json = JsonSerializer.Serialize(msg);
                        var data = new StringContent(json, Encoding.UTF8, "application/json");
                        r = await telegramClient.PostAsync("sendMessage", data);
                        if (!r.IsSuccessStatusCode)
                        {
                            Console.WriteLine("Could not post message to telegram, got " + r.StatusCode);
                            continue;
                        }

                        if (!historyOfNotifications.ContainsKey(DateTime.UtcNow.Date))
                        {
                            historyOfNotifications[DateTime.UtcNow.Date] =
                                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        }

                        historyOfNotifications[DateTime.UtcNow.Date].UnionWith(currentNotified);

                        foreach (var key in historyOfNotifications.Keys.Where(
                                     k => k < DateTime.UtcNow.Date.AddDays(-15)))
                        {
                            historyOfNotifications.Remove(key);
                        }

                        File.WriteAllText(Environment.GetEnvironmentVariable("STATE_FILE_PATH"),
                            JsonSerializer.Serialize(historyOfNotifications));
                    }
                }
            }

            try
            {
                await healthChecksClient.GetAsync("ef58ef4e-9ed7-4b69-ae75-701714d5e7ee");
            }
            catch
            {
                // ignore it, it will trigger an alert if it doesn't get pinged
            }

            Console.WriteLine("Waiting 120s...");
            await Task.Delay(120000);
        }

        Console.WriteLine("Waiting 10s...");
        await Task.Delay(10000);
        remainingRetries = 5;
    } catch (Exception e)
    {
        Console.WriteLine("Error: " + e.Message);
        remainingRetries--;
        if (remainingRetries == 0)
        {
            break;
        }
        
        Console.WriteLine($"Retrying in 1 minute... ({remainingRetries} retries left)");
        await Task.Delay(60000);
    }
} while (true);

public class TelegramMessage
{
    public string chat_id { get; set; }
    public string text { get; set; }
    public bool disable_web_page_preview { get; set; }
}